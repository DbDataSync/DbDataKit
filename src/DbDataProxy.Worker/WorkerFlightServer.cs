using System.Security.Cryptography;
using System.Text;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
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
    private static readonly OdbcProvider Odbc = new();

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
            log.LogWarning(ex, "Worker query failed");
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        await using (result)
        {
            await responseStream.SetupStream(result.Schema).ConfigureAwait(false);
            await foreach (var batch in result.ReadBatchesAsync(context.CancellationToken).ConfigureAwait(false))
                await responseStream.WriteAsync(batch).ConfigureAwait(false);
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
        if (action.Type == WorkerProtocol.ListColumnsAction)
        {
            var columnsRequest = System.Text.Json.JsonSerializer.Deserialize<WorkerColumnsRequest>(action.Body.Span, WorkerProtocol.Json)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "Empty column request."));
            IReadOnlyList<ColumnEntry> columns;
            try
            {
                columns = await new DbQueryExecutor(new DelegateConnectionFactory(Odbc.CreateConnection), columnsRequest.ConnectionString)
                    .ListColumnsAsync(columnsRequest.Catalog, columnsRequest.Schema, columnsRequest.Table, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Worker column listing failed");
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
            await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(columns, WorkerProtocol.Json)))).ConfigureAwait(false);
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
                log.LogWarning(ex, "Worker dialect lookup failed");
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
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
            log.LogWarning(ex, "Worker statement failed");
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
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
            log.LogWarning(ex, "Worker table listing failed");
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tables, WorkerProtocol.Json);
        await responseStream.WriteAsync(new FlightResult(ByteString.CopyFrom(body))).ConfigureAwait(false);
    }

    private static DbQueryExecutor CreateExecutor(WorkerRequest request) =>
        new(new DelegateConnectionFactory(Odbc.CreateConnection), request.ConnectionString);

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
