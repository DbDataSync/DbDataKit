using System.Collections.Concurrent;
using System.Security.Cryptography;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DbDataProxy.Server.Flight;

/// <summary>Server-side record of a prepared statement: the text, plus the parameter rows the client last bound.</summary>
public sealed record PreparedStatement(
    string Owner, string DataSource, string Sql, DateTimeOffset LastUsed, IReadOnlyList<IReadOnlyList<QueryParameter>>? Parameters = null);

public sealed class PreparedStatementStore(TimeProvider time)
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, PreparedStatement> _statements = new();

    public byte[] Add(string owner, string dataSource, string sql)
    {
        byte[] handle = RandomNumberGenerator.GetBytes(16);
        _statements[Convert.ToHexString(handle)] = new PreparedStatement(owner, dataSource, sql, time.GetUtcNow());
        return handle;
    }

    public PreparedStatement? Get(byte[] handle, string owner)
    {
        string key = Convert.ToHexString(handle);
        if (!_statements.TryGetValue(key, out var statement)) return null;
        if (!string.Equals(statement.Owner, owner, StringComparison.OrdinalIgnoreCase)) return null;
        _statements[key] = statement with { LastUsed = time.GetUtcNow() };
        return statement;
    }

    public void Bind(byte[] handle, string owner, IReadOnlyList<IReadOnlyList<QueryParameter>> parameters)
    {
        string key = Convert.ToHexString(handle);
        if (_statements.TryGetValue(key, out var s) && string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase))
            _statements[key] = s with { Parameters = parameters, LastUsed = time.GetUtcNow() };
    }

    public void Remove(byte[] handle, string owner)
    {
        string key = Convert.ToHexString(handle);
        if (_statements.TryGetValue(key, out var s) && string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase))
            _statements.TryRemove(key, out _);
    }

    public void Sweep()
    {
        var cutoff = time.GetUtcNow() - IdleLifetime;
        foreach (var (key, statement) in _statements)
            if (statement.LastUsed < cutoff) _statements.TryRemove(key, out _);
    }
}

// Prepared statements. Some clients (notably the .NET Flight SQL client) prepare every statement,
// even plain updates, so this is required for them to work. The dataset schema is reported empty at prepare time;
// the real schema arrives with the FlightInfo returned when the statement is executed.
public sealed partial class ProxyFlightServer
{
    protected override async Task CreatePreparedStatement(
        ActionCreatePreparedStatementRequest request, FlightAction action,
        IAsyncStreamWriter<FlightResult> output, ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var source = catalog.Resolve(principal, context.RequestHeaders.GetValue(DataSourceHeader));
        byte[] handle = prepared.Add(principal.Name, source.Name, request.Query);

        byte[] emptySchema = SchemaExtensions.SerializeSchema(new Schema([], null));
        var result = new ActionCreatePreparedStatementResult
        {
            PreparedStatementHandle = ByteString.CopyFrom(handle),
            DatasetSchema = ByteString.CopyFrom(emptySchema),
            ParameterSchema = ByteString.CopyFrom(emptySchema),
        };
        await output.WriteAsync(new FlightResult(Any.Pack(result).ToByteString())).ConfigureAwait(false);
    }

    protected override Task ClosePreparedStatement(
        ActionClosePreparedStatementRequest request, FlightAction action,
        IAsyncStreamWriter<FlightResult> output, ServerCallContext context)
    {
        prepared.Remove(request.PreparedStatementHandle.ToByteArray(), context.GetPrincipal().Name);
        return Task.CompletedTask;
    }

    private const int MaxParameterRows = 10_000;

    protected override Task<FlightInfo> GetPreparedStatementQueryFlightInfo(
        CommandPreparedStatementQuery command, FlightDescriptor descriptor, ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var statement = LookUp(command.PreparedStatementHandle, principal);
        var source = catalog.Resolve(principal, statement.DataSource);

        if (statement.Parameters is { Count: > 1 })
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A query accepts one row of parameters; bind a single row."));
        return ExecuteForFlightInfoAsync(statement.Sql, principal, source, descriptor, context, statement.Parameters?.FirstOrDefault());
    }

    // The client binds parameters with a DoPut on the prepared statement; the rows are kept until it binds again.
    protected override async Task PutPreparedStatementQuery(
        CommandPreparedStatementQuery command,
        FlightServerRecordBatchStreamReader requestStream,
        IAsyncStreamWriter<FlightPutResult> responseStream,
        ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        _ = LookUp(command.PreparedStatementHandle, principal);

        var rows = await ReadParameterRowsAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
        prepared.Bind(command.PreparedStatementHandle.ToByteArray(), principal.Name, rows);

        // Returning the same handle tells the client the statement is unchanged.
        var result = new DoPutPreparedStatementResult { PreparedStatementHandle = command.PreparedStatementHandle };
        await responseStream.WriteAsync(new FlightPutResult(result.ToByteString())).ConfigureAwait(false);
    }

    protected override async Task PutPreparedStatementUpdate(
        CommandPreparedStatementUpdate command,
        FlightServerRecordBatchStreamReader requestStream,
        IAsyncStreamWriter<FlightPutResult> responseStream,
        ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var statement = LookUp(command.PreparedStatementHandle, principal);
        var rows = await ReadParameterRowsAsync(requestStream, context.CancellationToken).ConfigureAwait(false);

        var source = catalog.Resolve(principal, statement.DataSource);
        long affected = 0;
        if (rows.Count == 0)
        {
            affected = await RunUpdateAsync(statement.Sql, principal, source, context, null).ConfigureAwait(false);
        }
        else
        {
            // One execution per parameter row; the counts are summed (-1 when the driver reports none).
            foreach (var row in rows)
            {
                long n = await RunUpdateAsync(statement.Sql, principal, source, context, row).ConfigureAwait(false);
                affected = n < 0 || affected < 0 ? -1 : affected + n;
            }
        }
        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = affected }.ToByteString())).ConfigureAwait(false);
    }

    private static async Task<List<IReadOnlyList<QueryParameter>>> ReadParameterRowsAsync(
        FlightServerRecordBatchStreamReader requestStream, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<QueryParameter>>();
        try
        {
            while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var batch = requestStream.Current;
                for (int r = 0; r < batch.Length; r++)
                {
                    if (rows.Count >= MaxParameterRows)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, $"At most {MaxParameterRows} parameter rows are accepted per statement."));
                    rows.Add(ArrowParameters.ReadRow(batch, r));
                }
            }
        }
        catch (NotSupportedException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        return rows;
    }

    private PreparedStatement LookUp(ByteString handle, DbDataProxy.Server.Auth.ProxyPrincipal principal) =>
        prepared.Get(handle.ToByteArray(), principal.Name)
        ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown or closed prepared statement."));
}
