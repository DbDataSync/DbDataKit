using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Lineage;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataStore.Tests;

/// <summary>
/// Plan 24a UC13 with a real engine (<c>testenv/testenv.sh up postgres mssql</c>): the Store server writes rows to a model's tables, paired with a stand-in Gateway that checks tokens as the real one does. An applied write tells the Gateway which entity changed, named as the
/// model's lineage names it and signed as the Store server itself; a write the model's rules refuse tells no one.
/// </summary>
public abstract class StoreDataChangedEngineTests(string engine) : IAsyncLifetime
{
    private const string Model = """
        schemaVersion: 1
        name: territories
        storage: {connection: mdm}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              quota: {type: "DECIMAL(10,2)", min: 0}
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), "dds-changed-engine-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string database = "ddsc_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string variable = "DDS_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly List<(string Body, ValidatedToken Token)> seen = [];
    private readonly string? server = Environment.GetEnvironmentVariable(engine == "sqlserver" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");
    private WebApplication? store;
    private WebApplication? gateway;
    private string lineage = """{"nodes":[],"edges":[]}""";
    private int lineageStatus = 200;
    private StoreSettings settings = null!;

    private string StoreDir => Path.Combine(root, "store");

    private bool SqlServer => engine == "sqlserver";

    private string ConnectionTo(string name) => SqlServer ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString : new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

    private async Task RunAsync(string db, string sql)
    {
        if (SqlServer)
        {
            await using var connection = new SqlConnection(ConnectionTo(db));
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            await using var connection = new NpgsqlConnection(ConnectionTo(db));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (server is null) return;
        await RunAsync(SqlServer ? "master" : "postgres", $"CREATE DATABASE {database}");
        Environment.SetEnvironmentVariable(variable, ConnectionTo(database));
        Directory.CreateDirectory(Path.Combine(StoreDir, StoreConnections.Folder));
        File.WriteAllText(StoreConnections.FileOf(StoreDir, "mdm"), $"schema: connection/1\nname: mdm\nengine: {engine}\nconnection_string_secret: 'env:{variable}'\ntls:\n  encrypt: false\n");

        gateway = StartFakeGateway();
        await gateway.StartAsync();
        settings = new StoreSettings(StoreDir, new Uri("https://127.0.0.1:0"));
        store = StoreServer.Build(settings, StoreServer.Certificate(StoreDir), _ => { }, new DataChangeNotifierOptions { RetryAfter = TimeSpan.FromMilliseconds(50) });
        await store.StartAsync();

        // the Store server pairs with the Gateway as it does to publish: its own key, a code the Gateway made for a publisher
        var gatewayDirectory = Path.Combine(root, "gateway");
        var code = new PairingFiles(Path.Combine(gatewayDirectory, "server.yaml")).Codes().Create(ServerRoles.Close(ServerRole.Publisher));
        await StorePublisher.For(StoreDir).PairAsync("edge", new Uri(gateway.Urls.First()), code.Code, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (store is not null) { await store.StopAsync(); await store.DisposeAsync(); }
        if (gateway is not null) { await gateway.StopAsync(); await gateway.DisposeAsync(); }
        Environment.SetEnvironmentVariable(variable, null);
        if (server is not null)
        {
            NpgsqlConnection.ClearAllPools();
            SqlConnection.ClearAllPools();
            await RunAsync(SqlServer ? "master" : "postgres", SqlServer ? $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database}; END" : $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private WebApplication StartFakeGateway()
    {
        var dir = Path.Combine(root, "gateway");
        Directory.CreateDirectory(dir);
        var files = new PairingFiles(Path.Combine(dir, "server.yaml"));
        var instanceId = ServerIdentity.InstanceId(Path.Combine(dir, "server.yaml"), "gateway", "fake gateway");
        var certificate = DirectoryCertificate.Load(dir);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.UseManagementApiVersion();
        app.MapPairing(new PairingService(instanceId, PairingProof.CertificateFingerprint(certificate), files.Clients(), files.Codes()));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, files.Clients(), new MemoryReplayCache()), _ => { });
        app.MapPost("/api/v1/cache/changed", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            lock (seen) seen.Add((text, context.ManagementToken()));
            return Results.Json(new { served = true });
        });
        // the Gateway's lineage, which the contract step asks (plan 21 CH06); a Publisher may read it
        app.MapGet("/api/v1/lineage", (HttpContext context) =>
        {
            var token = context.ManagementToken();
            return !ServerRoles.Allows(token.Roles, ServerRole.Viewer) && !token.Roles.HasFlag(ServerRole.Publisher)
                ? Results.Json(new { error = "This call needs the viewer or publisher role." }, statusCode: 403)
                : lineageStatus != 200 ? Results.Json(new { error = "down" }, statusCode: lineageStatus) : Results.Content(lineage, "application/json");
        });
        return app;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body, ServerRole role)
    {
        var url = new Uri(store!.Urls.First());
        var code = StoreState.Pairing(settings.Directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private async Task Settle() => await ((DataChangeNotifier)store!.Services.GetRequiredService<StoreDataChanges>().Notifier).FlushAsync();

    private static string Insert(string code, string quota) => $$$"""{"changes":[{"action":"insert","entity":"territory","values":{"code":"{{{code}}}","quota":{{{quota}}}}}]}""";

    [Fact]
    public async Task An_applied_write_tells_the_paired_gateway_which_entity_changed_signed_as_the_store_server_and_a_refused_write_tells_no_one()
    {
        Assert.SkipWhen(server is null, $"set DBDATAKIT_TEST_{(SqlServer ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "10"), ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("EU", "-5"), ServerRole.Author)).StatusCode);
        await Settle();

        (string Body, ValidatedToken Token) call;
        lock (seen) call = Assert.Single(seen);        // the insert, not the refused one
        var sent = (JsonObject)JsonNode.Parse(call.Body)!;
        Assert.Equal(("data.changed/1", "store"), ((string)sent["message"]!, (string)sent["product"]!));
        var dataset = (string)sent["dataset"]!;
        Assert.StartsWith(SqlServer ? "sqlserver://" : "postgres://", dataset);
        Assert.EndsWith("territories.territory", dataset);
        Assert.Equal(("store server", ServerRole.Publisher), (call.Token.SubjectName, call.Token.Roles));
        Assert.NotEqual("ada", call.Token.SubjectId);      // the Store server's own client id, not the person who wrote
    }

    private const string Wider = """
        schemaVersion: 1
        name: territories
        storage: {connection: mdm}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
        """;

    /// <summary>
    /// Plan 21 CH06 on a real engine: the contract step that drops a column a published dashboard reads, in any channel the paired Gateway's lineage shows, is refused naming the dashboard and the channel, with nothing changed; naming each consumer
    /// allows it (and the audit says who); once the Gateway's graph no longer shows a reader the step applies; a Gateway that cannot be asked refuses it too, until it is named.
    /// </summary>
    [Fact]
    public async Task The_contract_step_is_refused_while_a_published_consumer_in_any_channel_reads_the_column_and_applies_once_none_does()
    {
        Assert.SkipWhen(server is null, $"set DBDATAKIT_TEST_{(SqlServer ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/v1/models/territories", Wider, ServerRole.Author)).StatusCode);        // the model no longer has `quota`

        // the table and column as the Gateway's graph names them: the Store's own identity of the copy
        var (address, problem) = await new StoreConnections(StoreDir).AddressOnlyAsync("mdm", CancellationToken.None);
        Assert.NotNull(address);
        Assert.Null(problem);
        var dataset = LineageIdentity.Dataset(address, "territories", "territory");
        var column = LineageIdentity.Column(address, dataset, "quota");
        var consumer = new LineageNode("suite://gw/dashboard/quota-board~test", NodeKind.Consumer, "quota-board", false, new Dictionary<string, string> { ["consumer"] = "dashboard", ["channel"] = "test" });
        var graph = new LineageGraph([new(dataset.ToString(), NodeKind.Dataset, "territories.territory"), new(column, NodeKind.Column, "quota"), new("suite://gw/semantic-model/quota", NodeKind.Serving, "quota"), consumer],
            [new(column, "suite://gw/semantic-model/quota", EdgeKind.Binds, Granularity.Column, Confidence.Derived), new("suite://gw/semantic-model/quota", consumer.Id, EdgeKind.Reads, Granularity.Table, Confidence.Declared)]);
        lineage = LineageJson.Write(graph).ToJsonString();

        const string contract = "/api/v1/models/territories/migrate/contract";
        var plan = JsonNode.Parse(await (await Send(HttpMethod.Post, contract, null, ServerRole.Admin)).Content.ReadAsStringAsync())!;
        Assert.Equal(["drop column territory.quota"], plan["steps"]!.AsArray().Select(s => (string)s!["description"]!));      // a dry run asks nothing and says what would happen
        var hash = (string)plan["hash"]!;

        var refused = await Send(HttpMethod.Post, contract + "?approve=" + hash, null, ServerRole.Admin);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (string)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["error"]!;
        Assert.Contains("the dashboard 'quota-board' published in channel 'test'", message);
        Assert.Contains("drop the column 'quota' of 'territories.territory'", message);
        Assert.Contains("allow_consumer= quota-board@test", message);
        Assert.False((bool)JsonNode.Parse(await (await Send(HttpMethod.Post, contract, null, ServerRole.Admin)).Content.ReadAsStringAsync())!["nothing"]!);          // nothing was dropped

        // a paired Gateway that cannot be asked is not "no consumers": the step is refused until it is named
        lineageStatus = 503;
        var down = await Send(HttpMethod.Post, contract + "?approve=" + hash + "&allow_consumer=quota-board@test", null, ServerRole.Admin);
        Assert.Equal(HttpStatusCode.Conflict, down.StatusCode);
        Assert.Contains("'edge' did not give its lineage", (string)JsonNode.Parse(await down.Content.ReadAsStringAsync())!["error"]!);
        lineageStatus = 200;

        // the consumer is allowed by name and channel: the step runs, and the audit says who allowed what
        var applied = await Send(HttpMethod.Post, contract + "?approve=" + hash + "&allow_consumer=quota-board@test", null, ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True((bool)JsonNode.Parse(await applied.Content.ReadAsStringAsync())!["applied"]!);
        var entry = Assert.Single(new FileAuditLog(Path.Combine(StoreDir, "management-audit.log")).Read(), e => e.Command == "model.contract.allow-consumer");
        Assert.Equal("ada", entry.UserId);
        Assert.Contains("quota-board@test", entry.Target);
    }

    [Fact]
    public async Task When_no_published_consumer_reads_the_column_any_more_the_contract_step_applies_without_an_allowance()
    {
        Assert.SkipWhen(server is null, $"set DBDATAKIT_TEST_{(SqlServer ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        await Send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author);
        await Send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin);
        await Send(HttpMethod.Put, "/api/v1/models/territories", Wider, ServerRole.Author);
        const string contract = "/api/v1/models/territories/migrate/contract";
        var hash = (string)JsonNode.Parse(await (await Send(HttpMethod.Post, contract, null, ServerRole.Admin)).Content.ReadAsStringAsync())!["hash"]!;
        var applied = await Send(HttpMethod.Post, contract + "?approve=" + hash, null, ServerRole.Admin);       // the republished graph is empty
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True((bool)JsonNode.Parse(await applied.Content.ReadAsStringAsync())!["applied"]!);
    }
}

public sealed class StoreDataChangedOnPostgresTests() : StoreDataChangedEngineTests("postgres");

public sealed class StoreDataChangedOnSqlServerTests() : StoreDataChangedEngineTests("sqlserver");
