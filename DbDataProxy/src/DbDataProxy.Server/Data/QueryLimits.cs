using System.Collections.Concurrent;
using DbDataKit.Connections;
using DbDataProxy.Data;
using DbDataProxy.Server.Configuration;
using Grpc.Core;
using Apache.Arrow;

namespace DbDataProxy.Server.Data;

/// <summary>
/// Optional concurrency caps per data source and per user (S-6), off by default. One shared <see cref="ConcurrencyGate"/> per data source, rebuilt
/// when its limits change in the configuration. (The shared connection pool is not used: it limits pooled <c>DbConnection</c>s, while the Proxy has to limit
/// streaming results and work handed to the 32-bit worker, which hold no connection of ours.)
/// </summary>
public sealed class QueryLimits
{
    private readonly ConcurrentDictionary<string, (string Signature, ConcurrencyGate Gate)> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The gates now in use, by data source name: what the management API reports.</summary>
    public IReadOnlyDictionary<string, ConcurrencyGate> Gates => _gates.ToDictionary(g => g.Key, g => g.Value.Gate, StringComparer.OrdinalIgnoreCase);

    /// <summary>The gate for a data source, or null when it has no limits.</summary>
    public ConcurrencyGate? For(string name, DataSourceOptions options)
    {
        if (options.MaxConcurrentQueries <= 0 && options.MaxConcurrentQueriesPerUser <= 0)
        {
            _gates.TryRemove(name, out _);
            return null;
        }
        string signature = $"{options.MaxConcurrentQueries}/{options.MaxConcurrentQueriesPerUser}/{options.AcquireTimeoutSeconds}";
        if (_gates.TryGetValue(name, out var existing) && existing.Signature == signature) return existing.Gate;

        var gate = new ConcurrencyGate(
            options.MaxConcurrentQueries > 0 ? options.MaxConcurrentQueries : int.MaxValue / 2,
            options.MaxConcurrentQueriesPerUser > 0 ? options.MaxConcurrentQueriesPerUser : null,
            TimeSpan.FromSeconds(options.AcquireTimeoutSeconds));
        _gates[name] = (signature, gate);
        return gate;
    }
}

/// <summary>
/// Runs every query, update and catalog call of one caller through the data source's gate. A streaming result keeps its slot until it is disposed
/// (read to the end, or discarded by the pending-query sweeper). A caller that cannot get a slot within the acquire timeout is refused with
/// ResourceExhausted.
/// </summary>
public sealed class LimitedExecutor(IQueryExecutor inner, ConcurrencyGate gate, string user, string source) : IQueryExecutor, IMetadataProvider
{
    private async ValueTask<GateTicket> EnterAsync(CancellationToken cancellationToken)
    {
        try { return await gate.EnterAsync(user, cancellationToken).ConfigureAwait(false); }
        catch (PoolAcquireTimeoutException)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                $"Data source '{source}' is busy: too many queries are running. Try again shortly."));
        }
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        using var ticket = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await call().ConfigureAwait(false);
    }

    public async Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var ticket = await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new GatedResult(await inner.ExecuteQueryAsync(request, cancellationToken).ConfigureAwait(false), ticket);
        }
        catch
        {
            ticket.Dispose();
            throw;
        }
    }

    public Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ExecuteNonQueryAsync(request, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SchemaEntry>> ListSchemasAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken) =>
        RunAsync(() => Metadata.ListSchemasAsync(commandTimeoutSeconds, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken) =>
        RunAsync(() => Metadata.ListTablesAsync(commandTimeoutSeconds, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync(() => Metadata.ListColumnsAsync(catalog, schema, table, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync(() => Metadata.ListPrimaryKeysAsync(catalog, schema, table, cancellationToken), cancellationToken);

    public Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken) =>
        RunAsync(() => Metadata.GetDialectAsync(cancellationToken), cancellationToken);

    private IMetadataProvider Metadata => inner as IMetadataProvider ?? throw new UnsupportedValueException("This data source cannot list catalog metadata.");

    private sealed class GatedResult(IQueryResult result, GateTicket ticket) : IQueryResult
    {
        public Schema Schema => result.Schema;

        public IAsyncEnumerable<RecordBatch> ReadBatchesAsync(CancellationToken cancellationToken) => result.ReadBatchesAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try { await result.DisposeAsync().ConfigureAwait(false); }
            finally { ticket.Dispose(); }
        }
    }
}
