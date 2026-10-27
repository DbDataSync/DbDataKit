using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Serving.FlightSql;

/// <summary>
/// The Flight SQL wire protocol over the core services (plan 20, section 10): statements, prepared statements, updates and catalog metadata. It authenticates through identity and issues a core session's id as the token, runs statements through the
/// query service as the caller, and shapes answers into Flight streams. It does not know what a data source is, how a login is checked, which statements are allowed or how a query runs: a refusal, a missing source or a failure
/// comes back as a serving exception and goes out as the gRPC status that says so.
/// </summary>
public sealed partial class ServingFlightServer(ILoggerFactory loggerFactory, FlightSqlModule module) : FlightSqlServer(loggerFactory)
{
    private readonly ILogger _log = loggerFactory.CreateLogger<ServingFlightServer>();

    private IServingServices Services => module.Services;
    private FlightSqlOptions Options => module.Options;

    public override async Task Handshake(IAsyncStreamReader<FlightHandshakeRequest> requestStream, IAsyncStreamWriter<FlightHandshakeResponse> responseStream, ServerCallContext context)
    {
        module.EnsureServing();
        // Most Flight clients (C++, Java, Python, .NET) send Basic credentials in the "authorization" header; the Go clients, including the ADBC Flight SQL driver, send them as a BasicAuth message in the handshake payload.
        string? header = context.RequestHeaders.GetValue("authorization");
        string user = "", password = "";
        bool haveCredentials = BasicCredentials.TryParseBasic(header, out user, out password);
        if (!haveCredentials && await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            haveCredentials = BasicCredentials.TryParseBasicAuthPayload(requestStream.Current.Payload.ToByteArray(), out user, out password);
        if (!haveCredentials)
        {
            _log.LogWarning("Handshake without usable credentials from {Peer}: authorization scheme {Scheme} (length {Length}), headers [{Headers}]",
                context.Peer, header?.Split(' ')[0], header?.Length, string.Join(", ", context.RequestHeaders.Select(h => h.Key)));
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Basic credentials are required."));
        }

        ServingCaller? caller;
        try { caller = await Services.Identity.AuthenticateAsync(new ServingCredentials.Password(user, password), context.ClientAddress(), context.CancellationToken).ConfigureAwait(false); }
        catch (ServingException ex) { throw FlightFailures.ToRpcException(ex); }
        if (caller is null)
        {
            _log.LogWarning("Handshake rejected for user {User} from {Peer}", user, context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid credentials."));
        }

        // the token is the id of a core session: any later call presents it as a bearer token and identity resolves it
        var session = await Services.Sessions.OpenAsync(caller, FlightSqlModule.ModuleName, null, context.CancellationToken).ConfigureAwait(false);
        _log.LogInformation("Handshake accepted for {User} from {Peer}", caller.Name, context.Peer);

        // Standard Flight clients pick the token up from the response "authorization" header.
        await context.WriteResponseHeadersAsync(new Grpc.Core.Metadata { { "authorization", "Bearer " + session.Id } }).ConfigureAwait(false);
        await responseStream.WriteAsync(new FlightHandshakeResponse(ByteString.CopyFromUtf8(session.Id), 0)).ConfigureAwait(false);
    }

    protected override Task<FlightInfo> GetStatementQueryFlightInfo(CommandStatementQuery command, FlightDescriptor descriptor, ServerCallContext context) =>
        ExecuteForFlightInfoAsync(command.Query, context.RequestedSource(), descriptor, context);

    /// <summary>Runs the statement so its schema is known, parks the open result for DoGet and returns the FlightInfo.</summary>
    private async Task<FlightInfo> ExecuteForFlightInfoAsync(string sql, string source, FlightDescriptor descriptor, ServerCallContext context, IReadOnlyList<ServingParameter>? parameters = null)
    {
        var serving = context.GetServingContext();
        module.Pending.EnsureCapacity(serving.Caller.Name);
        _log.LogInformation("Query by {User} on {Source}: {Sql}", serving.Caller.Name, source.Length == 0 ? "<default>" : source, Describe(sql));

        IServingAnswer answer;
        try { answer = await Services.Queries.RunAsync(serving, new ServingRequest.Sql(source, sql, parameters), context.CancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException) { throw Fail(ex); }

        byte[] handle = module.Pending.Add(serving.Caller.Name, source, answer);
        var ticket = new TicketStatementQuery { StatementHandle = ByteString.CopyFrom(handle) };
        var endpoint = new FlightEndpoint(new FlightTicket(Any.Pack(ticket).ToByteString()), []);
        return new FlightInfo(answer.Schema, descriptor, [endpoint], -1, -1);
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

        var caller = context.GetCaller();
        var query = module.Pending.Take(statement.StatementHandle.ToByteArray(), caller.Name)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown or expired statement handle."));

        await using var answer = query.Answer;
        var plan = CompressionPlan.Resolve(Options.Compression(), Options.SourceCompression(query.Source), context.RequestHeaders.GetValue(CompressionPlan.ClientHeader));

        long rows = 0;
        try
        {
            await plan.StartStreamAsync(responseStream, answer.Schema, context).ConfigureAwait(false);
            await foreach (var batch in answer.ReadBatchesAsync(context.CancellationToken).ConfigureAwait(false))
            {
                rows += batch.Length;
                await responseStream.WriteAsync(batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException) { throw Fail(ex); }
        _log.LogInformation("Streamed {Rows} rows to {User} from {Source} (compression: {Compression})", rows, caller.Name, query.Source.Length == 0 ? "<default>" : query.Source, plan);
    }

    protected override async Task PutStatementUpdate(CommandStatementUpdate command, FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context)
    {
        long affected = await RunUpdateAsync(command.Query, context.RequestedSource(), context).ConfigureAwait(false);
        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = affected }.ToByteString())).ConfigureAwait(false);
    }

    private async Task<long> RunUpdateAsync(string sql, string source, ServerCallContext context, IReadOnlyList<ServingParameter>? parameters = null)
    {
        var serving = context.GetServingContext();
        if (Services.Updates is not { } updates) throw new RpcException(new Status(StatusCode.PermissionDenied, "This server takes no updates."));
        _log.LogInformation("Update by {User} on {Source}: {Sql}", serving.Caller.Name, source.Length == 0 ? "<default>" : source, Describe(sql));
        try { return (await updates.ExecuteAsync(serving, new ServingRequest.Sql(source, sql, parameters), context.CancellationToken).ConfigureAwait(false)).Rows; }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException) { throw Fail(ex); }
    }

    /// <summary>The client-facing status for <paramref name="ex"/>; a failure nobody classified (a bug, not a database error) is also logged in full.</summary>
    private RpcException Fail(Exception ex)
    {
        var rpc = FlightFailures.ToRpcException(ex);
        if (FlightFailures.IsUnclassified(rpc, ex)) _log.LogError(ex, "Unexpected failure while serving a request");
        return rpc;
    }

    private string Describe(string sql) => Options.LogSql() ? Truncate(sql) : "<sql hidden>";

    private static string Truncate(string sql)
    {
        string flat = sql.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "...";
    }
}
