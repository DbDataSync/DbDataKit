using System.Data.Common;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Flight;

/// <summary>
/// Flight SQL front end. Clients authenticate through the Flight handshake (Basic credentials in, bearer token out),
/// name a data source with the <c>x-datasource</c> header, and receive results as (optionally compressed) Arrow streams.
/// </summary>
public sealed partial class ProxyFlightServer(
    ILoggerFactory loggerFactory,
    DataSourceCatalog catalog,
    PendingQueryStore pending,
    PreparedStatementStore prepared,
    ChainedCredentialValidator credentialValidator,
    SessionStore sessions,
    IOptionsMonitor<ProxyOptions> options) : FlightSqlServer(loggerFactory)
{
    public const string DataSourceHeader = "x-datasource";

    private readonly ILogger _log = loggerFactory.CreateLogger<ProxyFlightServer>();

    public override async Task Handshake(
        IAsyncStreamReader<FlightHandshakeRequest> requestStream,
        IAsyncStreamWriter<FlightHandshakeResponse> responseStream,
        ServerCallContext context)
    {
        // Most Flight clients (C++, Java, Python, .NET) send Basic credentials in the "authorization" header; the Go
        // clients, including the ADBC Flight SQL driver, send them as a BasicAuth message in the handshake payload.
        string? header = context.RequestHeaders.GetValue("authorization");
        string user = "", password = "";
        bool haveCredentials = RequestAuthenticator.TryParseBasic(header, out user, out password);
        if (!haveCredentials && await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            haveCredentials = TryParseBasicAuthPayload(requestStream.Current.Payload.ToByteArray(), out user, out password);
        if (!haveCredentials)
        {
            _log.LogWarning("Handshake without usable credentials from {Peer}: authorization scheme {Scheme} (length {Length}), headers [{Headers}]",
                context.Peer, header?.Split(' ')[0], header?.Length, string.Join(", ", context.RequestHeaders.Select(h => h.Key)));
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Basic credentials are required."));
        }

        var principal = await credentialValidator.ValidateAsync(user, password, context.CancellationToken, context.ClientAddress()).ConfigureAwait(false);
        if (principal is null)
        {
            _log.LogWarning("Handshake rejected for user {User} from {Peer}", user, context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid credentials."));
        }

        string token = sessions.Issue(principal);
        _log.LogInformation("Handshake accepted for {User} ({Method}) from {Peer}", principal.Name, principal.Method, context.Peer);

        // Standard Flight clients pick the token up from the response "authorization" header.
        await context.WriteResponseHeadersAsync(new Metadata { { "authorization", "Bearer " + token } }).ConfigureAwait(false);
        await responseStream.WriteAsync(new FlightHandshakeResponse(ByteString.CopyFromUtf8(token), 0)).ConfigureAwait(false);
    }

    /// <summary>Parses the Flight <c>BasicAuth</c> protobuf message (username = field 2, password = field 3).</summary>
    internal static bool TryParseBasicAuthPayload(byte[] payload, out string user, out string password)
    {
        user = password = "";
        if (payload.Length == 0) return false;
        try
        {
            var input = new CodedInputStream(payload);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 2: user = input.ReadString(); break;
                    case 3: password = input.ReadString(); break;
                    default: input.SkipLastField(); break;
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
        return user.Length > 0;
    }

    protected override Task<FlightInfo> GetStatementQueryFlightInfo(
        CommandStatementQuery command, FlightDescriptor descriptor, ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var source = catalog.Resolve(principal, context.RequestHeaders.GetValue(DataSourceHeader));
        return ExecuteForFlightInfoAsync(command.Query, principal, source, descriptor, context);
    }

    /// <summary>Runs the statement so its schema is known, parks the open result for DoGet and returns the FlightInfo.</summary>
    private async Task<FlightInfo> ExecuteForFlightInfoAsync(
        string sql, ProxyPrincipal principal, ResolvedDataSource source, FlightDescriptor descriptor, ServerCallContext context,
        IReadOnlyList<QueryParameter>? parameters = null)
    {
        pending.EnsureCapacity(principal.Name);
        _log.LogInformation("Query by {User} on {Source}: {Sql}", principal.Name, source.Name, Describe(sql));

        IQueryResult result;
        try
        {
            result = await source.Executor
                .ExecuteQueryAsync(new QueryRequest(sql, source.Options.BatchSize, source.Options.CommandTimeoutSeconds, parameters), context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            throw ToRpcException(ex);
        }

        byte[] handle = pending.Add(principal.Name, source, result);
        var ticket = new TicketStatementQuery { StatementHandle = ByteString.CopyFrom(handle) };
        var endpoint = new FlightEndpoint(new FlightTicket(Any.Pack(ticket).ToByteString()), []);
        return new FlightInfo(result.Schema, descriptor, [endpoint], -1, -1);
    }

    public override async Task DoGet(FlightTicket ticket, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context)
    {
        var packed = FlightSqlUtils.Parse(ticket.Ticket);
        if (!packed.Is(TicketStatementQuery.Descriptor))
        {
            await base.DoGet(ticket, responseStream, context).ConfigureAwait(false);
            return;
        }
        var statement = packed.Unpack<TicketStatementQuery>();

        var principal = context.GetPrincipal();
        var query = pending.Take(statement.StatementHandle.ToByteArray(), principal.Name)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown or expired statement handle."));

        await using var result = query.Result;
        var plan = CompressionPlan.Resolve(
            options.CurrentValue.Compression, query.Source.Options.Compression, context.RequestHeaders.GetValue(CompressionPlan.ClientHeader));

        long rows = 0;
        try
        {
            await plan.StartStreamAsync(responseStream, result.Schema, context).ConfigureAwait(false);
            await foreach (var batch in result.ReadBatchesAsync(context.CancellationToken).ConfigureAwait(false))
            {
                rows += batch.Length;
                await responseStream.WriteAsync(batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            throw ToRpcException(ex);
        }
        _log.LogInformation("Streamed {Rows} rows to {User} from {Source} (compression: {Compression})",
            rows, principal.Name, query.Source.Name, plan);
    }

    protected override async Task PutStatementUpdate(
        CommandStatementUpdate command,
        FlightServerRecordBatchStreamReader requestStream,
        IAsyncStreamWriter<FlightPutResult> responseStream,
        ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var source = catalog.Resolve(principal, context.RequestHeaders.GetValue(DataSourceHeader));
        long affected = await RunUpdateAsync(command.Query, principal, source, context).ConfigureAwait(false);
        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = affected }.ToByteString())).ConfigureAwait(false);
    }

    private async Task<long> RunUpdateAsync(
        string sql, ProxyPrincipal principal, ResolvedDataSource source, ServerCallContext context, IReadOnlyList<QueryParameter>? parameters = null)
    {
        if (!source.Options.AllowWrites)
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"Data source '{source.Name}' is read-only."));

        _log.LogInformation("Update by {User} on {Source}: {Sql}", principal.Name, source.Name, Describe(sql));
        try
        {
            return await source.Executor
                .ExecuteNonQueryAsync(new QueryRequest(sql, source.Options.BatchSize, source.Options.CommandTimeoutSeconds, parameters), context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            throw ToRpcException(ex);
        }
    }

    /// <summary>Maps database failures to gRPC statuses clients can act on, without leaking connection strings.</summary>
    internal static RpcException ToRpcException(Exception ex)
    {
        if (ex is RpcException rpc) return rpc;
        if (IsLoginFailure(ex))
            return new RpcException(new Status(StatusCode.Unauthenticated, "The database rejected the credentials."));
        if (ex is DbException or NotSupportedException or ArgumentException)
            return new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        return new RpcException(new Status(StatusCode.Internal, "Query failed: " + ex.Message));
    }

    private static bool IsLoginFailure(Exception ex) => ex switch
    {
        Microsoft.Data.SqlClient.SqlException sql => sql.Number is 18456 or 18452 or 4060,
        System.Data.Odbc.OdbcException odbc => odbc.Errors.Cast<System.Data.Odbc.OdbcError>()
            .Any(e => e.SQLState is { } s && (s.StartsWith("28", StringComparison.Ordinal) || s == "42000" && e.NativeError is 18456)),
        _ => false,
    };

    private string Describe(string sql) => options.CurrentValue.LogSql ? Truncate(sql) : "<sql hidden>";

    private static string Truncate(string sql)
    {
        string flat = sql.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "...";
    }
}

