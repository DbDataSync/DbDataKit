using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Hosting.Certificates;
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
}

public sealed class StoreDataChangedOnPostgresTests() : StoreDataChangedEngineTests("postgres");

public sealed class StoreDataChangedOnSqlServerTests() : StoreDataChangedEngineTests("sqlserver");
