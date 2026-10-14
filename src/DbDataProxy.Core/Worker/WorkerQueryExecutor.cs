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
public sealed class WorkerQueryExecutor(IWorkerLink link, string connectionString) : IQueryExecutor, IMetadataProvider
{
    public async Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, request.Sql, request.BatchSize, request.CommandTimeoutSeconds, request.Parameters));

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

    public async Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, request.Sql, request.BatchSize, request.CommandTimeoutSeconds, request.Parameters));

        using var call = client.DoAction(new FlightAction(WorkerProtocol.NonQueryAction, payload), Headers(secret), null, cancellationToken);
        long affected = -1;
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            affected = System.Text.Json.JsonSerializer.Deserialize<WorkerNonQueryResult>(result.Body.Span, WorkerProtocol.Json)!.Affected;
        return affected;
    }

    public async Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, "", 1, commandTimeoutSeconds));

        using var call = client.DoAction(new FlightAction(WorkerProtocol.ListTablesAction, payload), Headers(secret), null, cancellationToken);
        var tables = new List<TableEntry>();
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            tables.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<TableEntry>>(result.Body.Span, WorkerProtocol.Json) ?? []);
        return tables;
    }

    public async Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new WorkerColumnsRequest(connectionString, catalog, schema, table), WorkerProtocol.Json);

        using var call = client.DoAction(new FlightAction(WorkerProtocol.ListColumnsAction, payload), Headers(secret), null, cancellationToken);
        var columns = new List<ColumnEntry>();
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            columns.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<ColumnEntry>>(result.Body.Span, WorkerProtocol.Json) ?? []);
        return columns;
    }

    public async Task<IdentifierQuote?> GetIdentifierQuoteAsync(CancellationToken cancellationToken)
    {
        var (client, secret) = await link.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = WorkerProtocol.Serialize(new WorkerRequest(connectionString, "", 1, null));

        using var call = client.DoAction(new FlightAction(WorkerProtocol.IdentifierQuoteAction, payload), Headers(secret), null, cancellationToken);
        IdentifierQuote? quote = null;
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            quote = System.Text.Json.JsonSerializer.Deserialize<IdentifierQuote?>(result.Body.Span, WorkerProtocol.Json);
        return quote;
    }

    private static Metadata Headers(string secret) => new() { { WorkerProtocol.SecretHeader, secret } };

    private sealed class WorkerQueryResult(FlightRecordBatchStreamingCall call, Schema schema) : IQueryResult
    {
        public Schema Schema { get; } = schema;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                yield return call.ResponseStream.Current;
        }

        public ValueTask DisposeAsync()
        {
            call.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
