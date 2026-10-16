using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using DbDataProxy.Data;
using Grpc.Core;

namespace DbDataProxy.Worker;

/// <summary>Gives access to a running worker process (starting it on demand).</summary>
public interface IWorkerLink
{
    ValueTask<(FlightClient Client, string Secret)> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Runs ODBC queries in the 32-bit worker process and streams the Arrow results back.</summary>
public sealed class WorkerQueryExecutor(IWorkerLink link, string connectionString, bool readOnly = false, string? metadataMode = null) : IQueryExecutor, IMetadataProvider
{
    private static readonly DbDataKit.Dialect.EngineId Odbc = new("odbc");

    /// <summary>A worker status that carries a classified failure becomes that failure again (kind and code); anything else passes through.</summary>
    internal static Exception Map(Exception ex) =>
        ex is RpcException rpc && FailureTranslator.FromTrailers(rpc.Trailers, Odbc) is { } failure ? new DbDataKit.Connections.ConnectionFailureException(failure) : ex;

    private static async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (RpcException ex) when (Map(ex) is var mapped && !ReferenceEquals(mapped, ex)) { throw mapped; }
    }

    public Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken) => GuardAsync(() => ExecuteQueryAsyncCore(request, cancellationToken));

    private async Task<IQueryResult> ExecuteQueryAsyncCore(QueryRequest request, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, request.Sql, request.BatchSize, request.CommandTimeoutSeconds, request.Parameters, readOnly, metadataMode));

        var call = client.GetStream(new FlightTicket(payload), Headers(secret), null, cancellationToken);
        try
        {
            // The first message carries the schema; reading it also surfaces connection and SQL errors from the worker.
            var schema = await call.ResponseStream.Schema.ConfigureAwait(false);
            return new WorkerQueryResult(call, schema);
        }
        catch
        {
            call.Dispose();
            throw;
        }
    }

    public Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken) => GuardAsync(() => ExecuteNonQueryAsyncCore(request, cancellationToken));

    private async Task<long> ExecuteNonQueryAsyncCore(QueryRequest request, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, request.Sql, request.BatchSize, request.CommandTimeoutSeconds, request.Parameters, readOnly, metadataMode));

        using var call = client.DoAction(new FlightAction(WorkerProtocol.NonQueryAction, payload), Headers(secret), null, cancellationToken);
        long affected = -1;
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            affected = System.Text.Json.JsonSerializer.Deserialize<WorkerNonQueryResult>(result.Body.Span, WorkerProtocol.Json)!.Affected;
        return affected;
    }

    public Task<IReadOnlyList<SchemaEntry>> ListSchemasAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken) => GuardAsync(() => ListSchemasAsyncCore(commandTimeoutSeconds, cancellationToken));

    private async Task<IReadOnlyList<SchemaEntry>> ListSchemasAsyncCore(int? commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, "", 1, commandTimeoutSeconds, null, readOnly, metadataMode));

        using var call = client.DoAction(new FlightAction(WorkerProtocol.ListSchemasAction, payload), Headers(secret), null, cancellationToken);
        var schemas = new List<SchemaEntry>();
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            schemas.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<SchemaEntry>>(result.Body.Span, WorkerProtocol.Json) ?? []);
        return schemas;
    }

    public Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken) => GuardAsync(() => ListTablesAsyncCore(commandTimeoutSeconds, cancellationToken));

    private async Task<IReadOnlyList<TableEntry>> ListTablesAsyncCore(int? commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, "", 1, commandTimeoutSeconds, null, readOnly, metadataMode));

        using var call = client.DoAction(new FlightAction(WorkerProtocol.ListTablesAction, payload), Headers(secret), null, cancellationToken);
        var tables = new List<TableEntry>();
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            tables.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<TableEntry>>(result.Body.Span, WorkerProtocol.Json) ?? []);
        return tables;
    }

    public Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) => GuardAsync(() => ListColumnsAsyncCore(catalog, schema, table, cancellationToken));

    private async Task<IReadOnlyList<ColumnEntry>> ListColumnsAsyncCore(string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new WorkerColumnsRequest(connectionString, catalog, schema, table, readOnly, metadataMode), WorkerProtocol.Json);

        using var call = client.DoAction(new FlightAction(WorkerProtocol.ListColumnsAction, payload), Headers(secret), null, cancellationToken);
        var columns = new List<ColumnEntry>();
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            columns.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<ColumnEntry>>(result.Body.Span, WorkerProtocol.Json) ?? []);
        return columns;
    }

    public Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) => GuardAsync(() => ListPrimaryKeysAsyncCore(catalog, schema, table, cancellationToken));

    private async Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsyncCore(string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new WorkerPrimaryKeysRequest(connectionString, catalog, schema, table, readOnly, metadataMode), WorkerProtocol.Json);

        using var call = client.DoAction(new FlightAction(WorkerProtocol.ListPrimaryKeysAction, payload), Headers(secret), null, cancellationToken);
        var keys = new List<PrimaryKeyEntry>();
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            keys.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<PrimaryKeyEntry>>(result.Body.Span, WorkerProtocol.Json) ?? []);
        return keys;
    }

    public Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken) => GuardAsync(() => GetDialectAsyncCore(cancellationToken));

    private async Task<DialectInfo> GetDialectAsyncCore(CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, "", 1, null, null, readOnly, metadataMode));

        using var call = client.DoAction(new FlightAction(WorkerProtocol.DialectAction, payload), Headers(secret), null, cancellationToken);
        DialectInfo? dialect = null;
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            dialect = System.Text.Json.JsonSerializer.Deserialize<DialectInfo>(result.Body.Span, WorkerProtocol.Json);
        return dialect ?? new DialectInfo(null, null, null);
    }

    private static Metadata Headers(string secret) => new() { { WorkerProtocol.SecretHeader, secret } };

    private sealed class WorkerQueryResult(FlightRecordBatchStreamingCall call, Schema schema) : IQueryResult
    {
        public Schema Schema { get; } = schema;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                bool more;
                try { more = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false); }
                catch (RpcException ex) when (Map(ex) is var mapped && !ReferenceEquals(mapped, ex)) { throw mapped; }
                if (!more) yield break;
                yield return call.ResponseStream.Current;
            }
        }

        public ValueTask DisposeAsync()
        {
            call.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
