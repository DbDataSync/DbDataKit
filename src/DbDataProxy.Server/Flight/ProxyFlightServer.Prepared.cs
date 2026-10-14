using System.Collections.Concurrent;
using System.Security.Cryptography;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using DbDataProxy.Server.Auth;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DbDataProxy.Server.Flight;

/// <summary>Server-side record of a prepared statement. The proxy keeps the text and runs it on demand (no parameters yet).</summary>
public sealed record PreparedStatement(string Owner, string DataSource, string Sql, DateTimeOffset LastUsed);

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

// Prepared statements without parameters. Some clients (notably the .NET Flight SQL client) prepare every statement,
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

    protected override Task<FlightInfo> GetPreparedStatementQueryFlightInfo(
        CommandPreparedStatementQuery command, FlightDescriptor descriptor, ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var statement = LookUp(command.PreparedStatementHandle, principal);
        var source = catalog.Resolve(principal, statement.DataSource);
        return ExecuteForFlightInfoAsync(statement.Sql, principal, source, descriptor, context);
    }

    protected override async Task PutPreparedStatementUpdate(
        CommandPreparedStatementUpdate command,
        FlightServerRecordBatchStreamReader requestStream,
        IAsyncStreamWriter<FlightPutResult> responseStream,
        ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var statement = LookUp(command.PreparedStatementHandle, principal);

        // Parameter batches are not supported yet; drain whatever the client sends so the call completes cleanly.
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            if (requestStream.Current.Length > 0)
                throw new RpcException(new Status(StatusCode.Unimplemented, "Statement parameters are not supported by this proxy."));
        }

        var source = catalog.Resolve(principal, statement.DataSource);
        long affected = await RunUpdateAsync(statement.Sql, principal, source, context).ConfigureAwait(false);
        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = affected }.ToByteString())).ConfigureAwait(false);
    }

    private PreparedStatement LookUp(ByteString handle, DbDataProxy.Server.Auth.ProxyPrincipal principal) =>
        prepared.Get(handle.ToByteArray(), principal.Name)
        ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown or closed prepared statement."));
}
