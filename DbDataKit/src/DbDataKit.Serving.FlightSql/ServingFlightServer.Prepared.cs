using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DbDataKit.Serving.FlightSql;

// Prepared statements. Some clients (notably the .NET Flight SQL client) prepare every statement, even plain updates, so this is required for them to work. The dataset schema is reported empty at prepare time;
// the real schema arrives with the FlightInfo returned when the statement is executed.
public sealed partial class ServingFlightServer
{
    protected override async Task CreatePreparedStatement(ActionCreatePreparedStatementRequest request, FlightAction action, IAsyncStreamWriter<FlightResult> output, ServerCallContext context)
    {
        var serving = context.GetServingContext();
        var source = context.RequestedSource();
        var statement = new ServingRequest.Sql(source, request.Query);
        // the core refuses a source the caller may not use, and (on a read-only source) a statement it would refuse to run; prepare is also how some clients send updates, so a refusal here is only for reads
        ServingStatementInfo info;
        try { info = await Services.Queries.PrepareAsync(serving, statement, context.CancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException) { throw Fail(ex); }

        byte[] handle = module.Prepared.Add(serving.Caller.Name, source, request.Query);
        byte[] emptySchema = SchemaExtensions.SerializeSchema(new Schema([], null));
        var result = new ActionCreatePreparedStatementResult
        {
            PreparedStatementHandle = ByteString.CopyFrom(handle),
            DatasetSchema = ByteString.CopyFrom(emptySchema),
            ParameterSchema = ByteString.CopyFrom(SchemaExtensions.SerializeSchema(ParameterSchemaFor(info.ParameterCount))),
        };
        await output.WriteAsync(new FlightResult(Any.Pack(result).ToByteString())).ConfigureAwait(false);
    }

    /// <summary>
    /// One field per <c>?</c> marker, named by position. The server does not know the database's expected types without running the statement, so the fields are Arrow Null ("any"); any value type is accepted when binding.
    /// Statements with <c>@name</c> markers report no parameters, because those are matched by the names the client sends.
    /// </summary>
    internal static Schema ParameterSchemaFor(int count) =>
        new(Enumerable.Range(0, count).Select(i => new Apache.Arrow.Field("parameter_" + i, Apache.Arrow.Types.NullType.Default, true)).ToList(), null);

    protected override Task ClosePreparedStatement(ActionClosePreparedStatementRequest request, FlightAction action, IAsyncStreamWriter<FlightResult> output, ServerCallContext context)
    {
        module.Prepared.Remove(request.PreparedStatementHandle.ToByteArray(), context.GetCaller().Name);
        return Task.CompletedTask;
    }

    private const int MaxParameterRows = 10_000;

    protected override Task<FlightInfo> GetPreparedStatementQueryFlightInfo(CommandPreparedStatementQuery command, FlightDescriptor descriptor, ServerCallContext context)
    {
        var statement = LookUp(command.PreparedStatementHandle, context);
        if (statement.Parameters is { Count: > 1 })
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A query accepts one row of parameters; bind a single row."));
        return ExecuteForFlightInfoAsync(statement.Sql, statement.DataSource, descriptor, context, statement.Parameters?.FirstOrDefault());
    }

    // The client binds parameters with a DoPut on the prepared statement; the rows are kept until it binds again.
    protected override async Task PutPreparedStatementQuery(CommandPreparedStatementQuery command, FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context)
    {
        _ = LookUp(command.PreparedStatementHandle, context);

        var rows = await ReadParameterRowsAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
        module.Prepared.Bind(command.PreparedStatementHandle.ToByteArray(), context.GetCaller().Name, rows);

        // Returning the same handle tells the client the statement is unchanged.
        var result = new DoPutPreparedStatementResult { PreparedStatementHandle = command.PreparedStatementHandle };
        await responseStream.WriteAsync(new FlightPutResult(result.ToByteString())).ConfigureAwait(false);
    }

    protected override async Task PutPreparedStatementUpdate(CommandPreparedStatementUpdate command, FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context)
    {
        var statement = LookUp(command.PreparedStatementHandle, context);
        var rows = await ReadParameterRowsAsync(requestStream, context.CancellationToken).ConfigureAwait(false);

        long affected = 0;
        if (rows.Count == 0)
        {
            affected = await RunUpdateAsync(statement.Sql, statement.DataSource, context, null).ConfigureAwait(false);
        }
        else
        {
            // One execution per parameter row; the counts are summed (-1 when the source reports none).
            foreach (var row in rows)
            {
                long n = await RunUpdateAsync(statement.Sql, statement.DataSource, context, row).ConfigureAwait(false);
                affected = n < 0 || affected < 0 ? -1 : affected + n;
            }
        }
        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = affected }.ToByteString())).ConfigureAwait(false);
    }

    private static async Task<List<IReadOnlyList<ServingParameter>>> ReadParameterRowsAsync(FlightServerRecordBatchStreamReader requestStream, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<ServingParameter>>();
        try
        {
            while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var batch = requestStream.Current;
                for (int r = 0; r < batch.Length; r++)
                {
                    if (rows.Count >= MaxParameterRows)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, $"At most {MaxParameterRows} parameter rows are accepted per statement."));
                    rows.Add(ArrowParameterReader.ReadRow(batch, r));
                }
            }
        }
        catch (ServingInvalidException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        return rows;
    }

    private PreparedStatement LookUp(ByteString handle, ServerCallContext context) =>
        module.Prepared.Get(handle.ToByteArray(), context.GetCaller().Name)
        ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown or closed prepared statement."));
}
