using System.Security.Cryptography;
using System.Text;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using DbDataKit.Connections;
using DbDataKit.Odbc.Metadata;
using DbDataProxy.Data;
using DbDataProxy.Worker;
using Google.Protobuf;
using Grpc.Core;

namespace DbDataProxy.WorkerHost;

/// <summary>
/// Executes ODBC statements on behalf of the proxy. It only listens on loopback and every call must present the
/// per-launch secret the proxy passed in its environment.
/// </summary>
public sealed class WorkerFlightServer(WorkerSecret secret, ILogger<WorkerFlightServer> log) : FlightServer
{
    private static readonly IDbProvider Odbc = new DbDataKit.Connections.Odbc.OdbcProvider();

    public override async Task DoGet(FlightTicket ticket, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context)
    {
        Authorize(context);
        var request = WorkerProtocol.Deserialize(ticket.Ticket.Span);
        var executor = CreateExecutor(request);

        IQueryResult result;
        try
        {
            result = await executor
                .ExecuteQueryAsync(new QueryRequest(request.Sql, request.BatchSize, request.TimeoutSeconds, request.Parameters), context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Fail(ex, "Worker query failed");
        }

        await using (result)
        {
            try
            {
                await responseStream.SetupStream(result.Schema).ConfigureAwait(false);
                await foreach (var batch in result.ReadBatchesAsync(context.CancellationToken).ConfigureAwait(false))
                    await responseStream.WriteAsync(batch).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
            {
                throw Fail(ex, "streaming");
            }
        }
    }

    public override async Task DoAction(FlightAction action, IAsyncStreamWriter<FlightResult> responseStream, ServerCallContext context)
    {
        Authorize(context);
        if (action.Type == WorkerProtocol.ListTablesAction)
        {
            await ListTablesAsync(action, responseStream, context).ConfigureAwait(false);
            return;
        }
        if (action.Type == WorkerProtocol.ListSchemasAction)
        {
            var schemasRequest = WorkerProtocol.Deserialize(action.Body.Span);
            IReadOnlyList<SchemaEntry> schemas;
            try { schemas = await CreateExecutor(schemasRequest).ListSchemasAsync(schemasRequest.TimeoutSeconds, context.CancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Fail(ex, "schema listing");
            }
            await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(schemas, WorkerProtocol.Json)))).ConfigureAwait(false);
            return;
        }
        if (action.Type == WorkerProtocol.ListColumnsAction)
        {
            var columnsRequest = System.Text.Json.JsonSerializer.Deserialize<WorkerColumnsRequest>(action.Body.Span, WorkerProtocol.Json)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "Empty column request."));
            IReadOnlyList<ColumnEntry> columns;
            try
            {
                columns = await CreateExecutor(columnsRequest.ConnectionString, columnsRequest.ReadOnly, columnsRequest.MetadataMode)
                    .ListColumnsAsync(columnsRequest.Catalog, columnsRequest.Schema, columnsRequest.Table, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Fail(ex, "Worker column listing failed");
            }
            await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(columns, WorkerProtocol.Json)))).ConfigureAwait(false);
            return;
        }
        if (action.Type == WorkerProtocol.ListPrimaryKeysAction)
        {
            var keysRequest = System.Text.Json.JsonSerializer.Deserialize<WorkerPrimaryKeysRequest>(action.Body.Span, WorkerProtocol.Json)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "Empty primary key request."));
            IReadOnlyList<PrimaryKeyEntry> keys;
            try
            {
                keys = await CreateExecutor(keysRequest.ConnectionString, keysRequest.ReadOnly, keysRequest.MetadataMode)
                    .ListPrimaryKeysAsync(keysRequest.Catalog, keysRequest.Schema, keysRequest.Table, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Fail(ex, "Worker primary key listing failed");
            }
            await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(keys, WorkerProtocol.Json)))).ConfigureAwait(false);
            return;
        }
        if (action.Type == WorkerProtocol.DialectAction)
        {
            DialectInfo dialect;
            try
            {
                dialect = await CreateExecutor(WorkerProtocol.Deserialize(action.Body.Span)).GetDialectAsync(context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Fail(ex, "Worker dialect lookup failed");
            }
            await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(dialect, WorkerProtocol.Json)))).ConfigureAwait(false);
            return;
        }
        if (action.Type != WorkerProtocol.NonQueryAction)
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Unknown action '{action.Type}'."));

        var request = WorkerProtocol.Deserialize(action.Body.Span);
        long affected;
        try
        {
            affected = await CreateExecutor(request)
                .ExecuteNonQueryAsync(new QueryRequest(request.Sql, request.BatchSize, request.TimeoutSeconds, request.Parameters), context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Fail(ex, "Worker statement failed");
        }

        byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new WorkerNonQueryResult(affected), WorkerProtocol.Json);
        await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(body))).ConfigureAwait(false);
    }

    private async Task ListTablesAsync(FlightAction action, IAsyncStreamWriter<FlightResult> responseStream, ServerCallContext context)
    {
        var request = WorkerProtocol.Deserialize(action.Body.Span);
        IReadOnlyList<TableEntry> tables;
        try
        {
            tables = await CreateExecutor(request).ListTablesAsync(request.TimeoutSeconds, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Fail(ex, "Worker table listing failed");
        }
        byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tables, WorkerProtocol.Json);
        await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(body))).ConfigureAwait(false);
    }

    // The proxy sends the administrator's string (secrets resolved, pass-through credentials injected); the shared provider composes it here
    // (TLS and timeout defaults, ODBC policy) and opens it, so the 32-bit worker runs the same code path as the in-process ODBC provider.
    private static SourceConnection Source(string connectionString, bool readOnly, string? metadataMode) =>
        SourceConnection.Create(Odbc, "worker", connectionString, readOnly, metadataMode: metadataMode);

    private DbQueryExecutor CreateExecutor(WorkerRequest request) => new(Source(request.ConnectionString, request.ReadOnly, request.MetadataMode), Translator());

    private DbQueryExecutor CreateExecutor(string connectionString, bool readOnly, string? metadataMode) => new(Source(connectionString, readOnly, metadataMode), Translator());

    // Driver errors never leave the worker as text: the executor classifies them (kind and code, from the shared ODBC mapper) and they
    // travel to the proxy as status trailers. The raw text goes to the worker's log, which the proxy folds into its own (redacted) log.
    private FailureTranslator Translator() => new(Odbc.FailureMapper, Odbc.Engine, "worker", redactor: null, includeRaw: true, sink: new LogSink(log));

    private sealed class LogSink(ILogger log) : IFailureSink
    {
        public void Failed(string source, ConnectionFailure failure) =>
            log.LogWarning("Worker {Source} failure {Failure}: {Raw}", source, FailureTranslator.Describe(failure), failure.Raw ?? "(raw text not kept)");
    }

    private RpcException Fail(Exception ex, string what)
    {
        log.LogWarning("Worker {What} failed: {Type}", what, ex.GetType().Name);
        return ex switch
        {
            ConnectionFailureException f => new RpcException(new Status(StatusCode.Unknown, f.Message), FailureTranslator.ToTrailers(f.Failure)),
            IClientVisible or OdbcPolicyViolationException or OdbcResultTruncatedException => new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)),
            _ => new RpcException(new Status(StatusCode.Internal, "The 32-bit worker failed.")),
        };
    }

    private void Authorize(ServerCallContext context)
    {
        string? presented = context.RequestHeaders.GetValue(WorkerProtocol.SecretHeader);
        if (presented is null || !secret.Matches(presented))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid worker secret."));
    }
}

/// <summary>The shared secret for this worker launch.</summary>
public sealed class WorkerSecret(string value)
{
    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(value);

    public bool Matches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(_bytes, Encoding.UTF8.GetBytes(candidate));
}
