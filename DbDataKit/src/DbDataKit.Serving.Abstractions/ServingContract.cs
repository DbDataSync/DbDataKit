using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Apache.Arrow;

namespace DbDataKit.Serving;

/// <summary>What every call to a core service carries (plan 20, section 10.4), so no module can leave a part of it out.</summary>
/// <param name="Caller">Who is asking, with the roles and attributes the access rules key on.</param>
/// <param name="Channel">The channel the request is answered from (plan 21); <c>prod</c> until channels are built.</param>
/// <param name="Preview">The preview whose draft layer the request sees, or null.</param>
/// <param name="SessionId">The core session the request belongs to, or null for a request outside one.</param>
/// <param name="TraceId">The trace the request belongs to, carried across modules.</param>
/// <param name="ClientAddress">Where the call came from, for the core's login throttle and audit.</param>
public sealed record ServingContext(ServingCaller Caller, string Channel = ServingContext.ProductionChannel, string? Preview = null, string? SessionId = null, string? TraceId = null, string? ClientAddress = null)
{
    public const string ProductionChannel = "prod";
}

/// <summary>
/// One statement parameter in a form every protocol can produce and the core can bind (the shape Flight SQL, the Postgres wire and a JSON request all reduce to). <see cref="Type"/> is one of <c>null</c>, <c>bool</c>, <c>int64</c>, <c>uint64</c>, <c>double</c>,
/// <c>decimal</c>, <c>string</c>, <c>date</c>, <c>timestamp</c>, <c>time</c> and <c>binary</c>; <see cref="Text"/> is the value as invariant text (round-trip format for dates and times, base64 for binary), null for SQL NULL.
/// Parameters are positional, in the order of the markers; <see cref="Name"/> is only what the client called it.
/// </summary>
public sealed record ServingParameter(string? Name, string Type, string? Text);

/// <summary>One request the query service runs; the kind says which language the text is in. A write is never a request here (see <see cref="IServingEntities"/>).</summary>
public abstract record ServingRequest(string Source, ServingLimits? Asked = null)
{
    /// <summary>SQL passed through to a data source, in that source's dialect.</summary>
    public sealed record Sql(string Source, string Text, IReadOnlyList<ServingParameter>? Parameters = null, ServingLimits? Asked = null) : ServingRequest(Source, Asked);

    /// <summary>A semantic request: the JSON document of the semantic layer, against the named model.</summary>
    public sealed record Semantic(string Source, string Json, ServingLimits? Asked = null) : ServingRequest(Source, Asked);

    /// <summary>Semantic SQL (plan 14 R6) against the named model.</summary>
    public sealed record SemanticSql(string Source, string Text, ServingLimits? Asked = null, IReadOnlyList<ServingParameter>? Parameters = null) : ServingRequest(Source, Asked);

    /// <summary>MDX in the subset the semantic layer translates (plan 14 R7).</summary>
    public sealed record Mdx(string Source, string Text, ServingLimits? Asked = null) : ServingRequest(Source, Asked);
}

/// <summary>Where an answer came from, so what was asked and what was answered can always be compared (plan 17, R4).</summary>
/// <param name="Canonical">The query as it ran: the text itself for SQL, the canonical semantic SQL for a request in another language.</param>
/// <param name="DefinitionVersion">The published definition version the request was pinned to; null for a source with no published definition.</param>
/// <param name="Channel">The channel it was answered from.</param>
/// <param name="Source">The source it ran against, by name, when the request left the choice to the core (an empty name is the caller's default); null when the core does not say.</param>
public sealed record ServingProvenance(string Canonical, string? DefinitionVersion, string Channel, string? Source = null);

/// <summary>A query's answer: an Arrow result and where it came from.</summary>
public interface IServingAnswer : IServingResult
{
    ServingProvenance Provenance { get; }
}

/// <summary>Who the caller is. Credentials a protocol cannot check itself (a Postgres SCRAM exchange, an Windows identity) are passed here as they arrived.</summary>
public interface IServingIdentity
{
    /// <summary>
    /// The caller the credentials identify, or null when they identify nobody. <paramref name="clientAddress"/> is where the call came from, for the core's login throttle (a rejected attempt counts against the user and the address);
    /// a bearer token is a core session's token or a token from an identity provider the core trusts.
    /// </summary>
    ValueTask<ServingCaller?> AuthenticateAsync(ServingCredentials credentials, string? clientAddress = null, CancellationToken cancellationToken = default);
}

/// <summary>A core session (plan 20, section 6): any instance of a group resumes it by id. <see cref="State"/> is the adapter's own, opaque to the core.</summary>
public sealed record ServingSession(string Id, string Protocol, string CallerId, byte[] State, DateTimeOffset ExpiresAt);

public interface IServingSessions
{
    /// <param name="timeToLive">How long the session lives without use; null leaves it to the core's configuration.</param>
    Task<ServingSession> OpenAsync(ServingCaller caller, string protocol, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default);

    /// <summary>The session, or null when it does not exist, has expired, or belongs to another caller.</summary>
    Task<ServingSession?> GetAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default);

    /// <summary>Stores the adapter's state and extends the life of the session.</summary>
    Task SaveStateAsync(ServingCaller caller, string id, byte[] state, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default);

    Task CloseAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default);
}

/// <summary>The kinds of object a catalog lists (plan 20, section 10.1 decision 7).</summary>
public enum ServingObjectKind { Catalog, Schema, Table, View, Column, Model, Dimension, Hierarchy, Level, Measure, Entity }

/// <summary>One object of the catalog a caller may see. <see cref="Path"/> is the names from the top, so a column is [schema, table, column].</summary>
/// <param name="Columns">The Arrow schema of a table, view, model or entity; null for the other kinds.</param>
/// <param name="Properties">What only some kinds have: <c>type</c>, <c>key</c>, <c>format</c> (a measure's format string), <c>as_of</c> (an entity supports as-of reads).</param>
/// <param name="PrimaryKey">The columns of a table's primary key in key order; null where the source cannot say, empty where there is none.</param>
/// <param name="Catalog">For a schema, table or view: the catalog it is in, or null when the source has none (<see cref="Path"/> leaves out a part a source does not have, so a path alone cannot say which part is missing).</param>
/// <param name="SchemaName">For a table or view: the schema it is in, or null when the source has none.</param>
/// <param name="PrimaryKeyName">The name of the primary key constraint, or null when the source has none to name.</param>
public sealed record ServingObject(ServingObjectKind Kind, IReadOnlyList<string> Path, string? Description = null, Schema? Columns = null, IReadOnlyDictionary<string, string>? Properties = null, IReadOnlyList<string>? PrimaryKey = null, string? Catalog = null, string? SchemaName = null, string? PrimaryKeyName = null)
{
    public string Name => Path[^1];
}

/// <summary>What a source says about itself, for the protocols that report server facts (Flight SQL's <c>GetSqlInfo</c>, the Postgres wire's parameter status).</summary>
/// <param name="Product">The database product, as the source reports it (<c>Microsoft SQL Server</c>, <c>PostgreSQL</c>), or the kind of source for a semantic model.</param>
/// <param name="Version">The product's version as it reports it, or null when it was not asked or would not say.</param>
/// <param name="ReadOnly">The source takes no writes through this server.</param>
/// <param name="IdentifierQuote">The character that opens a quoted identifier, or null when the source does not say.</param>
/// <param name="IdentifiersCaseInsensitive">Identifiers compare without regard to case; null when the source does not say.</param>
/// <param name="NullsSortLow">A null sorts before every value; false sorts it at the end; null when the source does not say.</param>
public sealed record ServingSourceFacts(string Product, string? Version, bool ReadOnly, string? IdentifierQuote = null, bool? IdentifiersCaseInsensitive = null, bool? NullsSortLow = null);

public interface IServingCatalog
{
    /// <summary>What the source says about itself; an empty name is the caller's default source. Throws <see cref="ServingNotFoundException"/> for a source the caller cannot see and <see cref="ServingInvalidException"/> when no default can be chosen.</summary>
    Task<ServingSourceFacts> FactsAsync(ServingContext context, string source, CancellationToken cancellationToken = default);

    /// <summary>The objects under <paramref name="under"/> (the whole catalog when empty) that the caller may see. A caller sees nothing they could not query.</summary>
    /// <param name="withPrimaryKeys">Fill in <see cref="ServingObject.PrimaryKey"/>; reading keys can cost a catalog call per table, so a protocol asks with <paramref name="under"/> naming the one table it wants them for.</param>
    /// <param name="source">The source whose objects are listed; empty is the caller's default source, as for <see cref="FactsAsync"/>. A host with one catalog for every source ignores it.</param>
    /// <param name="withColumns">Fill in <see cref="ServingObject.Columns"/> of tables and views; reading them can cost a query each, so they are asked for only when the protocol needs them (Flight SQL's <c>GetTables</c> with <c>include_schema</c>).</param>
    IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, bool withColumns = false, bool withPrimaryKeys = false, string source = "", CancellationToken cancellationToken = default);
}

/// <summary>What a client is told when it prepares a statement.</summary>
/// <param name="ParameterCount">How many positional parameters the statement takes, by the source's own marker rules (<c>?</c>, <c>$1</c>). A statement with named markers reports none, because those are matched by the names the client sends.</param>
public sealed record ServingStatementInfo(int ParameterCount, IReadOnlyList<ServingParameterInfo>? Parameters = null);

/// <summary>One parameter a prepared statement takes, for a source that knows more than a count (a semantic model): its name, the attribute it is for, its type (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, or null) and whether it takes a list.</summary>
public sealed record ServingParameterInfo(string Name, string? For, string? Type, bool Many);

public interface IServingQueries
{
    /// <summary>Runs the request as the caller, in the context's channel, within the lower of their limits and the request's. Access rules are applied here and nowhere else.</summary>
    Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a statement the way running it would, without running it: the source must be one the caller may use, and a statement the source would refuse (a write on a read-only source) is refused with <see cref="ServingDeniedException"/>.
    /// Returns what a client needs to prepare it.
    /// </summary>
    Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default);
}

/// <summary>
/// A statement that changes a writable data source (an UPDATE, INSERT or DELETE sent by a client, as Flight SQL's <c>CommandStatementUpdate</c> does). A read-only source refuses it with <see cref="ServingDeniedException"/>; a host with no writable
/// source offers none. This is not the way managed entities are written (<see cref="IServingEntities"/>), which validates and records history.
/// </summary>
public interface IServingUpdates
{
    /// <summary>Runs the statement as the caller and returns the rows it affected, or -1 when the source cannot say.</summary>
    Task<ServingWriteResult> ExecuteAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes of managed entities (the <c>store</c> module's services); a host without them offers none.</summary>
public interface IServingEntities
{
    /// <summary>Rows of an entity, as they were at <paramref name="asOf"/> or now when null, with the context's draft on top.</summary>
    Task<IServingAnswer> ReadAsync(ServingContext context, string entity, DateTimeOffset? asOf, CancellationToken cancellationToken = default);

    /// <summary>A validated write: it becomes a row change, or a change request when the entity requires one. Never a table write.</summary>
    Task<ServingWriteResult> WriteAsync(ServingContext context, string entity, IAsyncEnumerable<RecordBatch> rows, CancellationToken cancellationToken = default);
}

/// <summary>What a module is given when it starts. A service the host does not offer is null.</summary>
public interface IServingServices
{
    IServingIdentity Identity { get; }

    IServingSessions Sessions { get; }

    IServingQueries Queries { get; }

    IServingCatalog Catalog { get; }

    IServingEntities? Entities { get; }

    /// <summary>Statements that change a writable source; null when the host has none.</summary>
    IServingUpdates? Updates { get; }
}

public enum ServingHealthState { Ok, Degraded, Down }

public sealed record ServingHealth(ServingHealthState State, string? Detail = null)
{
    public static ServingHealth Healthy { get; } = new(ServingHealthState.Ok);
}

/// <summary>An endpoint of the serving layer, enabled per instance (plan 20, section 10.2).</summary>
public interface IServingModule
{
    /// <summary>The name used in <c>server.yaml</c>.</summary>
    string Name { get; }

    Task StartAsync(IServingServices services, CancellationToken cancellationToken = default);

    /// <summary>Stops taking new work and finishes what is running, so an instance can be upgraded (plan 24).</summary>
    Task DrainAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    ServingHealth Health();
}

/// <summary>
/// Offers an <see cref="IServingHost"/> as core services: SQL requests go to <see cref="IServingHost.QueryAsync"/>, the catalog is its tables, identity is its authentication, and sessions are held in memory (a group needs the shared-state sessions of plan 20 O02).
/// A request kind the host cannot run fails with <see cref="ServingInvalidException"/> naming the kind.
/// </summary>
public sealed class HostServices(IServingHost host) : IServingServices, IServingIdentity, IServingSessions, IServingQueries, IServingCatalog, IServingUpdates
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, ServingSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ServingCaller> _callers = new();

    public IServingIdentity Identity => this;

    public IServingSessions Sessions => this;

    public IServingQueries Queries => this;

    public IServingCatalog Catalog => this;

    public IServingEntities? Entities => null;

    public IServingUpdates? Updates => host.Writes is null ? null : this;

    public ValueTask<ServingCaller?> AuthenticateAsync(ServingCredentials credentials, string? clientAddress = null, CancellationToken cancellationToken = default)
    {
        // a bearer token that is a session's id means the caller who opened the session
        if (credentials is ServingCredentials.Bearer(var token) && _sessions.TryGetValue(token, out var session) && session.ExpiresAt > DateTimeOffset.UtcNow && _callers.TryGetValue(token, out var caller))
            return ValueTask.FromResult<ServingCaller?>(caller);
        return host.AuthenticateAsync(credentials, cancellationToken);
    }

    public async Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
    {
        if (request is not ServingRequest.Sql sql) throw new ServingInvalidException($"This host does not run {request.GetType().Name.ToLowerInvariant()} requests.");
        var result = await host.QueryAsync(context.Caller, QueryOf(sql), cancellationToken).ConfigureAwait(false);
        return new Answer(result, new ServingProvenance(sql.Text, null, context.Channel, sql.Source));
    }

    // the host's query takes parameters by name; a positional one is named by its position (1-based), as the markers are counted
    private static ServingQuery QueryOf(ServingRequest.Sql sql) =>
        new(sql.Source, sql.Text, sql.Parameters?.Select((p, i) => (Name: p.Name ?? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Value: (object?)p.Text)).ToDictionary(x => x.Name, x => x.Value), sql.Asked);

    public Task<ServingWriteResult> ExecuteAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) =>
        host.Writes is { } writes ? writes.ExecuteAsync(context.Caller, QueryOf(statement), cancellationToken) : throw new ServingDeniedException("This host takes no writes.");

    public async Task<ServingSourceFacts> FactsAsync(ServingContext context, string source, CancellationToken cancellationToken = default)
    {
        await foreach (var _ in host.DescribeAsync(context.Caller, new ServingCatalogRequest(), cancellationToken).ConfigureAwait(false)) break;       // a caller who sees nothing has no source to ask about
        return new ServingSourceFacts("host", null, ReadOnly: host.Writes is null);
    }

    public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default)
    {
        // the host knows no dialect: count the question marks outside quotes, which is what a plain positional statement has
        var count = 0;
        char? quote = null;
        foreach (var c in statement.Text)
        {
            if (quote is not null) { if (c == quote) quote = null; }
            else if (c is '\'' or '"') quote = c;
            else if (c == '?') count++;
        }
        return Task.FromResult(new ServingStatementInfo(count));
    }

    public async IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, bool withColumns = false, bool withPrimaryKeys = false, string source = "", [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var table in host.DescribeAsync(context.Caller, new ServingCatalogRequest(), cancellationToken).ConfigureAwait(false))
        {
            var path = new[] { table.Catalog, table.Schema, table.Name }.OfType<string>().ToArray();
            if (under.Count > 0 && !path.Take(under.Count).SequenceEqual(under)) continue;
            yield return new ServingObject(table.Kind == "VIEW" ? ServingObjectKind.View : ServingObjectKind.Table, path, Columns: table.Columns, Catalog: table.Catalog, SchemaName: table.Schema);
        }
    }

    public Task<ServingSession> OpenAsync(ServingCaller caller, string protocol, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        var session = new ServingSession(Guid.NewGuid().ToString("N"), protocol, caller.Id, [], DateTimeOffset.UtcNow + (timeToLive ?? DefaultLifetime));
        _sessions[session.Id] = session;
        _callers[session.Id] = caller;
        return Task.FromResult(session);
    }

    public Task<ServingSession?> GetAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.TryGetValue(id, out var s) && s.CallerId == caller.Id && s.ExpiresAt > DateTimeOffset.UtcNow ? s : null);

    public Task SaveStateAsync(ServingCaller caller, string id, byte[] state, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(id, out var s) || s.CallerId != caller.Id) throw new ServingNotFoundException($"There is no session '{id}'.");
        _sessions[id] = s with { State = state, ExpiresAt = DateTimeOffset.UtcNow + (timeToLive ?? DefaultLifetime) };
        return Task.CompletedTask;
    }

    public Task CloseAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(id, out var s) && s.CallerId == caller.Id) { _sessions.TryRemove(id, out _); _callers.TryRemove(id, out _); }
        return Task.CompletedTask;
    }

    private sealed class Answer(IServingResult inner, ServingProvenance provenance) : IServingAnswer
    {
        public Schema Schema => inner.Schema;

        public ServingProvenance Provenance { get; } = provenance;

        public IAsyncEnumerable<RecordBatch> ReadBatchesAsync(CancellationToken cancellationToken = default) => inner.ReadBatchesAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
