using Apache.Arrow;

namespace DbDataKit.Serving;

/// <summary>Who a caller is once the host has accepted their credentials. <see cref="Roles"/> and <see cref="Claims"/> are the host's to define; a protocol only carries them back to the host.</summary>
public sealed record ServingCaller(string Id, string Name, IReadOnlySet<string> Roles, IReadOnlyDictionary<string, string> Claims)
{
    public ServingCaller(string id, string name) : this(id, name, new HashSet<string>(), new Dictionary<string, string>()) { }
}

/// <summary>What a protocol received to prove who the caller is. A protocol builds the kind its wire format carries and nothing else.</summary>
public abstract record ServingCredentials
{
    /// <summary>A user name and a password (Flight SQL basic authentication, the Postgres wire password message).</summary>
    public sealed record Password(string User, string Secret) : ServingCredentials;

    /// <summary>A bearer token (a JWT or an opaque session token).</summary>
    public sealed record Bearer(string Token) : ServingCredentials;

    /// <summary>A Windows identity the transport has already authenticated (Negotiate); the host maps it.</summary>
    public sealed record Windows(string Account) : ServingCredentials;
}

/// <summary>A table or view the caller may query, with its columns as Arrow reports them.</summary>
public sealed record ServingTable(string? Catalog, string? Schema, string Name, string Kind, Schema Columns);

/// <summary>What <see cref="IServingHost.DescribeAsync"/> is asked for; every part is optional, and a name is matched exactly unless the host documents more.</summary>
public sealed record ServingCatalogRequest(string? Catalog = null, string? Schema = null, string? Table = null);

/// <summary>The limits a caller is held to. Null is no limit from the host; the host may still stop a query for reasons of its own.</summary>
public sealed record ServingLimits(long? MaxRows = null, TimeSpan? Timeout = null, int? MaxBatchRows = null);

/// <summary>A query: the text in the dialect the host documents, against the named source (a data source, an entity set), with positional or named parameter values.</summary>
public sealed record ServingQuery(string Source, string Text, IReadOnlyDictionary<string, object?>? Parameters = null, ServingLimits? Asked = null);

/// <summary>An open result: its Arrow schema is known first, its batches stream, and disposing releases what the host holds for it.</summary>
public interface IServingResult : IAsyncDisposable
{
    Schema Schema { get; }

    IAsyncEnumerable<RecordBatch> ReadBatchesAsync(CancellationToken cancellationToken = default);
}

/// <summary>A write the host accepted: how many rows it affected, or -1 when it cannot say.</summary>
public sealed record ServingWriteResult(long Rows);

/// <summary>Writes, offered only by a host that has a write path of its own (validation, history, audit). A protocol that receives an update or an ingest asks for this and answers "not supported" when the host has none.</summary>
public interface IServingWrites
{
    Task<ServingWriteResult> ExecuteAsync(ServingCaller caller, ServingQuery statement, CancellationToken cancellationToken = default);

    Task<ServingWriteResult> IngestAsync(ServingCaller caller, string source, string table, IAsyncEnumerable<RecordBatch> batches, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a protocol front end needs from the server it is hosted in (plan 19, N01). The host decides who may do what; the protocol never reads a table, never skips <see cref="AuthenticateAsync"/> and never lets a limit pass unreported.
/// </summary>
public interface IServingHost
{
    /// <summary>The caller the credentials identify, or null when they do not identify anyone (wrong password, expired token). A host throws only for a failure of its own.</summary>
    ValueTask<ServingCaller?> AuthenticateAsync(ServingCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>The tables and views the caller may query that match the request. A caller sees nothing they could not query.</summary>
    IAsyncEnumerable<ServingTable> DescribeAsync(ServingCaller caller, ServingCatalogRequest request, CancellationToken cancellationToken = default);

    /// <summary>The limits this caller is held to for this source.</summary>
    ServingLimits LimitsFor(ServingCaller caller, string source);

    /// <summary>
    /// Runs the query as the caller, within <see cref="LimitsFor"/> (the lower of those and what the query asked for). Throws <see cref="ServingDeniedException"/> for a source the caller may not query, <see cref="ServingNotFoundException"/> for one that does not exist,
    /// <see cref="ServingInvalidException"/> for text the host will not run, and, when reading the result passes a limit, <see cref="ServingLimitException"/>.
    /// </summary>
    Task<IServingResult> QueryAsync(ServingCaller caller, ServingQuery query, CancellationToken cancellationToken = default);

    /// <summary>The write path, or null: a host that has none does not pretend.</summary>
    IServingWrites? Writes { get; }
}

/// <summary>A failure a protocol answers in its own words (a Flight status code, a Postgres error code). The message is safe to show the caller.</summary>
public abstract class ServingException(string message) : Exception(message);

public sealed class ServingDeniedException(string message) : ServingException(message);

public sealed class ServingNotFoundException(string message) : ServingException(message);

public sealed class ServingInvalidException(string message) : ServingException(message);

/// <summary>A query reached a limit; the result ends here and says so.</summary>
public sealed class ServingLimitException(string message, string limit) : ServingException(message)
{
    /// <summary><c>rows</c> or <c>time</c>.</summary>
    public string Limit { get; } = limit;
}
