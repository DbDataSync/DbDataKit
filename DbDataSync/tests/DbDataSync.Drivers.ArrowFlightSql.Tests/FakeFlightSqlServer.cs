using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using Arrow.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Field = Apache.Arrow.Field;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataSync.Drivers.ArrowFlightSql.Tests;

public enum ServerAuth
{
    /// <summary>Anyone may call.</summary>
    None,

    /// <summary>Handshake with Basic credentials; the server answers with a bearer token that every later
    /// call must carry.</summary>
    BasicHandshake,

    /// <summary>A proxy that checks Basic credentials on every call and issues no token.</summary>
    BasicEveryCall,

    /// <summary>A fixed pre-issued token, no handshake needed.</summary>
    Bearer,
}

/// <summary>What the server was asked, and how it answers.</summary>
public sealed class ServerState
{
    public ServerAuth Auth { get; set; } = ServerAuth.None;
    public string User { get; set; } = "svc";
    public string Password { get; set; } = "s3cret";
    public string Token { get; set; } = "static-token-123";
    public string IssuedToken { get; } = "issued-" + Guid.NewGuid().ToString("N");

    /// <summary>Metadata calls answer Unimplemented, as a minimal proxy would.</summary>
    public bool NoMetadata { get; set; }

    /// <summary>The FlightInfo carries no schema, so it has to come from the first record batch.</summary>
    public bool OmitInfoSchema { get; set; }

    /// <summary>Statements can be prepared but values cannot be bound to them (Unimplemented), as in a proxy
    /// that only prepares parameterless statements.</summary>
    public bool RefuseParameters { get; set; }

    public List<string> Executed { get; } = [];
    public List<RecordBatch?> ExecutedParameters { get; } = [];
    public List<string?> AuthorizationSeen { get; } = [];

    /// <summary>Each metadata request as the server received it — which fields were set, not just their
    /// values, since a null the client turns into an empty string is a different request.</summary>
    public List<string> MetadataRequests { get; } = [];
    public int HandshakeCount;

    internal Dictionary<string, (Schema Schema, List<RecordBatch> Batches)> Results { get; } = [];
    internal Dictionary<string, (string Sql, RecordBatch? Parameters)> Prepared { get; } = [];

    public string BasicHeader => "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{User}:{Password}"));
}

/// <summary>The data every test reads: <c>sales.orders</c> in two record batches, so a read has to cross a
/// batch boundary, with nulls and one column of each interesting type.</summary>
internal static class TestData
{
    public static readonly Schema OrdersSchema = new Schema.Builder()
        .Field(new Field("id", Int64Type.Default, nullable: false))
        .Field(new Field("customer", StringType.Default, nullable: true))
        .Field(new Field("amount", new Decimal128Type(18, 2), nullable: true))
        .Field(new Field("placed", new TimestampType(TimeUnit.Microsecond, (string?)null), nullable: true))
        .Field(new Field("shipped", Date32Type.Default, nullable: true))
        .Field(new Field("flag", BooleanType.Default, nullable: true))
        .Build();

    public static List<RecordBatch> Orders() => [OrderBatch(1, 3), OrderBatch(4, 2)];

    private static RecordBatch OrderBatch(long firstId, int count)
    {
        var id = new Int64Array.Builder();
        var customer = new StringArray.Builder();
        var amount = new Decimal128Array.Builder(new Decimal128Type(18, 2));
        var placed = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, (string?)null));
        var shipped = new Date32Array.Builder();
        var flag = new BooleanArray.Builder();

        for (var i = 0; i < count; i++)
        {
            var n = firstId + i;
            id.Append(n);
            // Every third customer is null.
            if (n % 3 == 0) customer.AppendNull(); else customer.Append($"customer-{n}");
            amount.Append(n * 10.25m);
            placed.Append(new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero).AddDays(n));
            shipped.Append(new DateTime(2026, 2, 1).AddDays(n));
            flag.Append(n % 2 == 0);
        }

        return new RecordBatch(OrdersSchema,
            [id.Build(), customer.Build(), amount.Build(), placed.Build(), shipped.Build(), flag.Build()], count);
    }

    public static readonly Schema OneSchema = new Schema.Builder().Field(new Field("one", Int32Type.Default, true)).Build();

    public static RecordBatch One() =>
        new(OneSchema, [new Int32Array.Builder().Append(1).Build()], 1);

    public static readonly (string Catalog, string Schema, string Table, string Type, Schema Columns, string[] Keys)[] Tables =
    [
        ("main", "sales", "orders", "TABLE", OrdersSchema, ["id"]),
        ("main", "sales", "order_view", "VIEW", OrdersSchema, []),
        ("main", "sys", "tables", "SYSTEM TABLE", OneSchema, []),
        ("other", "public", "things", "TABLE", OneSchema, []),
    ];
}

/// <summary>A minimal Flight SQL server: just enough protocol to exercise a client for real — statement
/// queries, prepared statements with bound parameters, catalogs/tables/primary keys, the Basic handshake,
/// and bearer checks — over actual gRPC. It does not parse SQL: a statement returns the table it names.</summary>
internal sealed class FakeFlightSqlServer(ServerState state) : FlightServer
{
    private void Authorize(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue("authorization");
        lock (state) state.AuthorizationSeen.Add(header);

        var ok = state.Auth switch
        {
            ServerAuth.None => true,
            ServerAuth.BasicHandshake => header == $"Bearer {state.IssuedToken}",
            ServerAuth.BasicEveryCall => header == state.BasicHeader,
            ServerAuth.Bearer => header == $"Bearer {state.Token}",
            _ => false,
        };

        if (!ok)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid credentials"));
    }

    public override async Task Handshake(
        IAsyncStreamReader<FlightHandshakeRequest> requestStream,
        IAsyncStreamWriter<FlightHandshakeResponse> responseStream, ServerCallContext context)
    {
        Interlocked.Increment(ref state.HandshakeCount);
        var header = context.RequestHeaders.GetValue("authorization");
        lock (state) state.AuthorizationSeen.Add(header);

        switch (state.Auth)
        {
            case ServerAuth.BasicHandshake:
                if (header != state.BasicHeader)
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid credentials"));
                await context.WriteResponseHeadersAsync(new Metadata { { "authorization", $"Bearer {state.IssuedToken}" } });
                break;
            case ServerAuth.BasicEveryCall:
                if (header != state.BasicHeader)
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid credentials"));
                break;
        }

        while (await requestStream.MoveNext(context.CancellationToken)) { }
        await responseStream.WriteAsync(new FlightHandshakeResponse());
    }

    public override Task<FlightInfo> GetFlightInfo(FlightDescriptor descriptor, ServerCallContext context)
    {
        Authorize(context);
        var any = FlightSqlUtils.Parse(descriptor.Command);

        if (any.Is(CommandStatementQuery.Descriptor))
            return Task.FromResult(Run(any.Unpack<CommandStatementQuery>().Query, null, descriptor));

        if (any.Is(CommandPreparedStatementQuery.Descriptor))
        {
            var handle = any.Unpack<CommandPreparedStatementQuery>().PreparedStatementHandle.ToStringUtf8();
            var prepared = state.Prepared[handle];
            return Task.FromResult(Run(prepared.Sql, prepared.Parameters, descriptor));
        }

        MetadataGuard();

        if (any.Is(CommandGetCatalogs.Descriptor))
        {
            var catalogs = new StringArray.Builder();
            foreach (var c in TestData.Tables.Select(t => t.Catalog).Distinct())
                catalogs.Append(c);
            var schema = new Schema.Builder().Field(new Field("catalog_name", StringType.Default, false)).Build();
            return Task.FromResult(Store(schema, [new RecordBatch(schema, [catalogs.Build()], catalogs.Length)], descriptor));
        }

        if (any.Is(CommandGetTables.Descriptor))
            return Task.FromResult(Tables(any.Unpack<CommandGetTables>(), descriptor));

        if (any.Is(CommandGetPrimaryKeys.Descriptor))
            return Task.FromResult(PrimaryKeys(any.Unpack<CommandGetPrimaryKeys>(), descriptor));

        throw new RpcException(new Status(StatusCode.Unimplemented, "not implemented by the fake"));
    }

    public override async Task DoGet(FlightTicket ticket, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context)
    {
        Authorize(context);
        var (schema, batches) = state.Results[ticket.Ticket.ToStringUtf8()];
        await responseStream.SetupStream(schema);
        foreach (var batch in batches)
            await responseStream.WriteAsync(batch);
    }

    public override async Task DoAction(FlightAction action, IAsyncStreamWriter<FlightResult> responseStream, ServerCallContext context)
    {
        Authorize(context);
        switch (action.Type)
        {
            case "CreatePreparedStatement":
            {
                var request = FlightSqlUtils.ParseAndUnpack<ActionCreatePreparedStatementRequest>(action.Body);
                var handle = Guid.NewGuid().ToString("N");
                var (schema, _) = Resolve(request.Query);
                state.Prepared[handle] = (request.Query, null);

                var result = new ActionCreatePreparedStatementResult
                {
                    PreparedStatementHandle = ByteString.CopyFromUtf8(handle),
                    DatasetSchema = ByteString.CopyFrom(ArrowSerializationHelpers.SerializeSchema(schema)),
                    ParameterSchema = ByteString.CopyFrom(ArrowSerializationHelpers.SerializeSchema(new Schema([], null))),
                };
                await responseStream.WriteAsync(new FlightResult(Any.Pack(result).ToByteString()));
                break;
            }
            case "ClosePreparedStatement":
            {
                var request = FlightSqlUtils.ParseAndUnpack<ActionClosePreparedStatementRequest>(action.Body);
                state.Prepared.Remove(request.PreparedStatementHandle.ToStringUtf8());
                break;
            }
            default:
                throw new RpcException(new Status(StatusCode.Unimplemented, action.Type));
        }
    }

    public override async Task DoPut(
        FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream,
        ServerCallContext context)
    {
        Authorize(context);
        var descriptor = await requestStream.FlightDescriptor;
        var any = FlightSqlUtils.Parse(descriptor.Command);
        if (!any.Is(CommandPreparedStatementQuery.Descriptor))
            throw new RpcException(new Status(StatusCode.Unimplemented, "writes are not supported by the fake"));

        if (state.RefuseParameters)
            throw new RpcException(new Status(StatusCode.Unimplemented, "PutPreparedStatementQuery is not supported."));

        var handle = any.Unpack<CommandPreparedStatementQuery>().PreparedStatementHandle.ToStringUtf8();
        RecordBatch? parameters = null;
        while (await requestStream.MoveNext())
            parameters = requestStream.Current;

        state.Prepared[handle] = (state.Prepared[handle].Sql, parameters);
        await responseStream.WriteAsync(FlightPutResult.Empty);
    }

    public override Task ListFlights(FlightCriteria criteria, IAsyncStreamWriter<FlightInfo> responseStream, ServerCallContext context) =>
        Task.CompletedTask;

    public override Task ListActions(IAsyncStreamWriter<FlightActionType> responseStream, ServerCallContext context) =>
        Task.CompletedTask;

    // ---- Query handling ----

    private (Schema Schema, List<RecordBatch> Batches) Resolve(string sql)
    {
        if (Regex.IsMatch(sql, @"^\s*SELECT\s+1\b", RegexOptions.IgnoreCase))
            return (TestData.OneSchema, [TestData.One()]);

        if (sql.Contains("orders", StringComparison.OrdinalIgnoreCase))
            return (TestData.OrdersSchema, TestData.Orders());

        throw new RpcException(new Status(StatusCode.InvalidArgument, $"the fake does not know how to run: {sql}"));
    }

    private FlightInfo Run(string sql, RecordBatch? parameters, FlightDescriptor descriptor)
    {
        lock (state)
        {
            state.Executed.Add(sql);
            state.ExecutedParameters.Add(parameters);
        }

        var (schema, batches) = Resolve(sql);
        return Store(schema, batches, descriptor);
    }

    private FlightInfo Store(Schema schema, List<RecordBatch> batches, FlightDescriptor descriptor)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (state) state.Results[id] = (schema, batches);

        var endpoint = new FlightEndpoint(new FlightTicket(id), []);
        return new FlightInfo(state.OmitInfoSchema ? new Schema([], null) : schema, descriptor, [endpoint], -1, -1);
    }

    private void MetadataGuard()
    {
        if (state.NoMetadata)
            throw new RpcException(new Status(StatusCode.Unimplemented, "no metadata here"));
    }

    private FlightInfo Tables(CommandGetTables request, FlightDescriptor descriptor)
    {
        lock (state)
        {
            state.MetadataRequests.Add(
                $"GetTables catalog={(request.HasCatalog ? $"'{request.Catalog}'" : "<unset>")} " +
                $"schema={(request.HasDbSchemaFilterPattern ? $"'{request.DbSchemaFilterPattern}'" : "<unset>")} " +
                $"table={(request.HasTableNameFilterPattern ? $"'{request.TableNameFilterPattern}'" : "<unset>")} " +
                $"types=[{string.Join(",", request.TableTypes)}] includeSchema={request.IncludeSchema}");
        }

        var rows = TestData.Tables.Where(t =>
            (!request.HasCatalog || t.Catalog == request.Catalog)
            && (!request.HasDbSchemaFilterPattern || Like(t.Schema, request.DbSchemaFilterPattern))
            && (!request.HasTableNameFilterPattern || Like(t.Table, request.TableNameFilterPattern))).ToList();

        var catalog = new StringArray.Builder();
        var schema = new StringArray.Builder();
        var table = new StringArray.Builder();
        var type = new StringArray.Builder();
        var columns = new BinaryArray.Builder();
        foreach (var row in rows)
        {
            catalog.Append(row.Catalog);
            schema.Append(row.Schema);
            table.Append(row.Table);
            type.Append(row.Type);
            columns.Append(ArrowSerializationHelpers.SerializeSchema(row.Columns).AsSpan());
        }

        var fields = new Schema.Builder()
            .Field(new Field("catalog_name", StringType.Default, true))
            .Field(new Field("db_schema_name", StringType.Default, true))
            .Field(new Field("table_name", StringType.Default, false))
            .Field(new Field("table_type", StringType.Default, false));
        var arrays = new List<IArrowArray> { catalog.Build(), schema.Build(), table.Build(), type.Build() };
        if (request.IncludeSchema)
        {
            fields.Field(new Field("table_schema", BinaryType.Default, false));
            arrays.Add(columns.Build());
        }

        var result = fields.Build();
        return Store(result, [new RecordBatch(result, arrays, rows.Count)], descriptor);
    }

    private FlightInfo PrimaryKeys(CommandGetPrimaryKeys request, FlightDescriptor descriptor)
    {
        var table = TestData.Tables.First(t => t.Table == request.Table);
        var catalog = new StringArray.Builder();
        var schema = new StringArray.Builder();
        var name = new StringArray.Builder();
        var column = new StringArray.Builder();
        var key = new StringArray.Builder();
        var sequence = new Int32Array.Builder();

        for (var i = 0; i < table.Keys.Length; i++)
        {
            catalog.Append(table.Catalog);
            schema.Append(table.Schema);
            name.Append(table.Table);
            column.Append(table.Keys[i]);
            key.Append("pk");
            sequence.Append(i + 1);
        }

        var result = new Schema.Builder()
            .Field(new Field("catalog_name", StringType.Default, true))
            .Field(new Field("db_schema_name", StringType.Default, true))
            .Field(new Field("table_name", StringType.Default, false))
            .Field(new Field("column_name", StringType.Default, false))
            .Field(new Field("key_name", StringType.Default, true))
            .Field(new Field("key_sequence", Int32Type.Default, false))
            .Build();
        return Store(result,
            [new RecordBatch(result, [catalog.Build(), schema.Build(), name.Build(), column.Build(), key.Build(), sequence.Build()], table.Keys.Length)],
            descriptor);
    }

    private static bool Like(string value, string pattern) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.IgnoreCase);
}

/// <summary>The server on a real loopback socket (HTTP/2, over TLS with a throwaway self-signed
/// certificate when asked), started per test.</summary>
internal sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ServerState State { get; }
    public Uri Address { get; }

    private TestServerHost(WebApplication app, ServerState state, Uri address)
    {
        _app = app;
        State = state;
        Address = address;
    }

    public static async Task<TestServerHost> StartAsync(Action<ServerState>? configure = null, bool tls = false)
    {
        var state = new ServerState();
        configure?.Invoke(state);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(state);
        builder.Services.AddGrpc().AddFlightServer<FakeFlightSqlServer>();

        var certificate = tls ? SelfSigned() : null;
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                if (certificate is not null)
                    listen.UseHttps(certificate);
            }));

        var app = builder.Build();
        app.MapFlightEndpoint();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestServerHost(app, state, new Uri(address));
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
