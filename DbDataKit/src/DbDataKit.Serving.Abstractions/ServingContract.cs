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
public sealed record ServingContext(ServingCaller Caller, string Channel = ServingContext.ProductionChannel, string? Preview = null, string? SessionId = null, string? TraceId = null)
{
    public const string ProductionChannel = "prod";
}

/// <summary>One request the query service runs; the kind says which language the text is in. A write is never a request here (see <see cref="IServingEntities"/>).</summary>
public abstract record ServingRequest(string Source, ServingLimits? Asked = null)
{
    /// <summary>SQL passed through to a data source, in that source's dialect.</summary>
    public sealed record Sql(string Source, string Text, IReadOnlyDictionary<string, object?>? Parameters = null, ServingLimits? Asked = null) : ServingRequest(Source, Asked);

    /// <summary>A semantic request: the JSON document of the semantic layer, against the named model.</summary>
    public sealed record Semantic(string Source, string Json, ServingLimits? Asked = null) : ServingRequest(Source, Asked);

    /// <summary>Semantic SQL (plan 14 R6) against the named model.</summary>
    public sealed record SemanticSql(string Source, string Text, ServingLimits? Asked = null) : ServingRequest(Source, Asked);

    /// <summary>MDX in the subset the semantic layer translates (plan 14 R7).</summary>
    public sealed record Mdx(string Source, string Text, ServingLimits? Asked = null) : ServingRequest(Source, Asked);
}

/// <summary>Where an answer came from, so what was asked and what was answered can always be compared (plan 17, R4).</summary>
/// <param name="Canonical">The query as it ran: the text itself for SQL, the canonical semantic SQL for a request in another language.</param>
/// <param name="DefinitionVersion">The published definition version the request was pinned to; null for a source with no published definition.</param>
/// <param name="Channel">The channel it was answered from.</param>
public sealed record ServingProvenance(string Canonical, string? DefinitionVersion, string Channel);

/// <summary>A query's answer: an Arrow result and where it came from.</summary>
public interface IServingAnswer : IServingResult
{
    ServingProvenance Provenance { get; }
}

/// <summary>Who the caller is. Credentials a protocol cannot check itself (a Postgres SCRAM exchange, an Windows identity) are passed here as they arrived.</summary>
public interface IServingIdentity
{
    /// <summary>The caller the credentials identify, or null when they identify nobody.</summary>
    ValueTask<ServingCaller?> AuthenticateAsync(ServingCredentials credentials, CancellationToken cancellationToken = default);
}

/// <summary>A core session (plan 20, section 6): any instance of a group resumes it by id. <see cref="State"/> is the adapter's own, opaque to the core.</summary>
public sealed record ServingSession(string Id, string Protocol, string CallerId, byte[] State, DateTimeOffset ExpiresAt);

public interface IServingSessions
{
    Task<ServingSession> OpenAsync(ServingCaller caller, string protocol, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>The session, or null when it does not exist, has expired, or belongs to another caller.</summary>
    Task<ServingSession?> GetAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default);

    /// <summary>Stores the adapter's state and extends the life of the session.</summary>
    Task SaveStateAsync(ServingCaller caller, string id, byte[] state, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    Task CloseAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default);
}

/// <summary>The kinds of object a catalog lists (plan 20, section 10.1 decision 7).</summary>
public enum ServingObjectKind { Catalog, Schema, Table, View, Column, Model, Dimension, Hierarchy, Level, Measure, Entity }

/// <summary>One object of the catalog a caller may see. <see cref="Path"/> is the names from the top, so a column is [schema, table, column].</summary>
/// <param name="Columns">The Arrow schema of a table, view, model or entity; null for the other kinds.</param>
/// <param name="Properties">What only some kinds have: <c>type</c>, <c>key</c>, <c>format</c> (a measure's format string), <c>as_of</c> (an entity supports as-of reads).</param>
public sealed record ServingObject(ServingObjectKind Kind, IReadOnlyList<string> Path, string? Description = null, Schema? Columns = null, IReadOnlyDictionary<string, string>? Properties = null)
{
    public string Name => Path[^1];
}

public interface IServingCatalog
{
    /// <summary>The objects under <paramref name="under"/> (the whole catalog when empty) that the caller may see. A caller sees nothing they could not query.</summary>
    IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, CancellationToken cancellationToken = default);
}

public interface IServingQueries
{
    /// <summary>Runs the request as the caller, in the context's channel, within the lower of their limits and the request's. Access rules are applied here and nowhere else.</summary>
    Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default);
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
public sealed class HostServices(IServingHost host) : IServingServices, IServingIdentity, IServingSessions, IServingQueries, IServingCatalog
{
    private readonly ConcurrentDictionary<string, ServingSession> _sessions = new();

    public IServingIdentity Identity => this;

    public IServingSessions Sessions => this;

    public IServingQueries Queries => this;

    public IServingCatalog Catalog => this;

    public IServingEntities? Entities => null;

    public ValueTask<ServingCaller?> AuthenticateAsync(ServingCredentials credentials, CancellationToken cancellationToken = default) => host.AuthenticateAsync(credentials, cancellationToken);

    public async Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
    {
        if (request is not ServingRequest.Sql sql) throw new ServingInvalidException($"This host does not run {request.GetType().Name.ToLowerInvariant()} requests.");
        var result = await host.QueryAsync(context.Caller, new ServingQuery(sql.Source, sql.Text, sql.Parameters, sql.Asked), cancellationToken).ConfigureAwait(false);
        return new Answer(result, new ServingProvenance(sql.Text, null, context.Channel));
    }

    public async IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var table in host.DescribeAsync(context.Caller, new ServingCatalogRequest(), cancellationToken).ConfigureAwait(false))
        {
            var path = new[] { table.Catalog, table.Schema, table.Name }.OfType<string>().ToArray();
            if (under.Count > 0 && !path.Take(under.Count).SequenceEqual(under)) continue;
            yield return new ServingObject(table.Kind == "VIEW" ? ServingObjectKind.View : ServingObjectKind.Table, path, Columns: table.Columns);
        }
    }

    public Task<ServingSession> OpenAsync(ServingCaller caller, string protocol, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        var session = new ServingSession(Guid.NewGuid().ToString("N"), protocol, caller.Id, [], DateTimeOffset.UtcNow + timeToLive);
        _sessions[session.Id] = session;
        return Task.FromResult(session);
    }

    public Task<ServingSession?> GetAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.TryGetValue(id, out var s) && s.CallerId == caller.Id && s.ExpiresAt > DateTimeOffset.UtcNow ? s : null);

    public Task SaveStateAsync(ServingCaller caller, string id, byte[] state, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(id, out var s) || s.CallerId != caller.Id) throw new ServingNotFoundException($"There is no session '{id}'.");
        _sessions[id] = s with { State = state, ExpiresAt = DateTimeOffset.UtcNow + timeToLive };
        return Task.CompletedTask;
    }

    public Task CloseAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(id, out var s) && s.CallerId == caller.Id) _sessions.TryRemove(id, out _);
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
