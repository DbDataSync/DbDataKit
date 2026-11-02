using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace DbDataStore.Tests;

/// <summary>
/// A model's tables and rows through the management API on a real PostgreSQL (plan 19, Z09; <c>testenv/testenv.sh up postgres</c>, then <c>eval "$(testenv/testenv.sh env dbdatakit)"</c>): the storage is planned and created,
/// a row is written and read back, a stale version and a broken rule are refused with their own statuses, and each role does only its part.
/// </summary>
public sealed class StoreDataTests : IAsyncLifetime
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
              name: {type: VARCHAR(100), required: true}
              quota: {type: "DECIMAL(10,2)", min: 0}
        """;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-data-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string database = "dds_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string variable = "DDS_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly List<string> extraDatabases = [];
    private WebApplication? app;
    private StoreSettings settings = null!;
    private string? server = Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES");

    public async ValueTask InitializeAsync()
    {
        if (server is null) return;
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }
        Environment.SetEnvironmentVariable(variable, new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString);
        Directory.CreateDirectory(Path.Combine(directory, StoreConnections.Folder));
        File.WriteAllText(StoreConnections.FileOf(directory, "mdm"), $"schema: connection/1\nname: mdm\nengine: postgres\nconnection_string_secret: 'env:{variable}'\ntls:\n  encrypt: false\n");
        settings = new StoreSettings(directory, new Uri("https://127.0.0.1:0"));
        app = StoreServer.Build(settings, StoreServer.Certificate(directory), _ => { });
        await app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Environment.SetEnvironmentVariable(variable, null);
        if (server is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(server);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            foreach (var extra in extraDatabases)
            {
                await using var dropExtra = new NpgsqlCommand($"DROP DATABASE IF EXISTS {extra} WITH (FORCE)", admin);
                await dropExtra.ExecuteNonQueryAsync();
            }
        }
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<Func<HttpMethod, string, string?, ServerRole, Task<HttpResponseMessage>>> Client()
    {
        Assert.SkipWhen(server is null, "set DBDATA_TEST_POSTGRES to run on PostgreSQL");
        var url = new Uri(app!.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return (method, path, body, role) =>
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return http.SendAsync(request);
        };
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static string Insert(string code, string name, string quota) =>
        $$$"""{"changes":[{"action":"insert","entity":"territory","values":{"code":"{{{code}}}","name":"{{{name}}}","quota":{{{quota}}}}}]}""";

    [Fact]
    public async Task Storage_is_planned_and_created_then_rows_are_written_read_and_refused_by_the_models_rules()
    {
        var send = await Client();
        Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author)).StatusCode);

        var plan = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/storage", null, ServerRole.Viewer));
        Assert.Equal("postgres", (string)plan["engine"]!);
        Assert.NotEmpty(plan["statements"]!.AsArray());
        Assert.Empty(plan["existing"]!.AsArray());

        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin)).StatusCode);
        Assert.NotEmpty((await Json(await send(HttpMethod.Get, "/api/v1/models/territories/storage", null, ServerRole.Viewer)))["existing"]!.AsArray());

        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "10"), ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "10"), ServerRole.Author)).StatusCode);

        var row = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/row?key.code=NA", null, ServerRole.Viewer));
        Assert.Equal("North", (string)row["values"]!["name"]!);
        Assert.Equal(1, (long)row["version"]!);

        // a rule of the model is refused with its name and nothing is written
        var refused = await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("EU", "Europe", "-5"), ServerRole.Author);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.NotEmpty((await Json(refused))["violations"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/row?key.code=EU", null, ServerRole.Viewer)).StatusCode);

        // an update names the version it read: the right one applies, a stale one is a conflict that says what the row is at now
        var update = (long version) => $$$"""{"changes":[{"action":"update","entity":"territory","key":{"code":"NA"},"expected_version":{{{version}}},"values":{"name":"North America"}}]}""";
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", update(1), ServerRole.Author)).StatusCode);
        var stale = await send(HttpMethod.Post, "/api/v1/models/territories/changes", update(1), ServerRole.Author);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2, (long)(await Json(stale))["current_version"]!);
        // a page of rows in key order
        foreach (var (code, quota) in new[] { ("AA", "1"), ("BB", "2"), ("CC", "3") })
            Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert(code, code, quota), ServerRole.Author)).StatusCode);
        var page = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/rows?limit=2&offset=1", null, ServerRole.Viewer));
        Assert.Equal(["BB", "CC"], page["rows"]!.AsArray().Select(r => (string)r!["values"]!["code"]!).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/rows?limit=x", null, ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/rows?limit=5000", null, ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/nope/rows", null, ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task A_model_whose_connection_has_no_file_says_so_instead_of_failing()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model.Replace("connection: mdm", "connection: nowhere"), ServerRole.Author);

        var response = await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "1"), ServerRole.Author);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("nowhere", (string)(await Json(response))["error"]!);
    }

    [Fact]
    public async Task Instances_in_schemas_hold_their_own_rows_and_a_key_may_repeat_between_them()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model.Replace("storage: {connection: mdm}", "storage: {connection: mdm, instances: schema}"), ServerRole.Author);
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/acme", null, ServerRole.Author)).StatusCode);
        foreach (var instance in new[] { "acme", "globex" })
        {
            Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Put, $"/api/v1/models/territories/instances/{instance}", null, ServerRole.Admin)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Post, $"/api/v1/models/territories/instances/{instance}/storage/create", null, ServerRole.Admin)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/acme", null, ServerRole.Admin)).StatusCode);

        // the same business key in both, with different content; neither sees the other's
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/changes", Insert("NA", "Acme North", "1"), ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/globex/changes", Insert("NA", "Globex North", "2"), ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/globex/changes", Insert("EU", "Globex Europe", "3"), ServerRole.Author)).StatusCode);

        string[] Codes(JsonNode page) => page["rows"]!.AsArray().Select(r => (string)r!["values"]!["code"]!).ToArray();
        var acme = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/acme/entities/territory/rows", null, ServerRole.Viewer));
        var globex = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/globex/entities/territory/rows", null, ServerRole.Viewer));
        Assert.Equal(["NA"], Codes(acme));
        Assert.Equal(["EU", "NA"], Codes(globex));
        Assert.Equal("Acme North", (string)(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/acme/entities/territory/row?key.code=NA", null, ServerRole.Viewer)))["values"]!["name"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/instances/acme/entities/territory/row?key.code=EU", null, ServerRole.Viewer)).StatusCode);

        // the model's own copy is another one again, and is not created yet
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Get, "/api/v1/models/territories/storage", null, ServerRole.Viewer)).StatusCode);
        Assert.Empty((await Json(await send(HttpMethod.Get, "/api/v1/models/territories/storage", null, ServerRole.Viewer)))["existing"]!.AsArray());

        var listed = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances", null, ServerRole.Viewer));
        Assert.Equal("schema", (string)listed["mode"]!);
        Assert.Equal(["acme", "globex"], listed["instances"]!.AsArray().Select(i => (string)i!["name"]!).ToArray());
        Assert.Equal("territories__acme", (string)listed["instances"]![0]!["schema"]!);

        // an instance nobody registered is not there; forgetting one keeps its data
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/instances/initech/entities/territory/rows", null, ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await send(HttpMethod.Delete, "/api/v1/models/territories/instances/acme", null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/instances/acme/entities/territory/rows", null, ServerRole.Viewer)).StatusCode);
        await send(HttpMethod.Put, "/api/v1/models/territories/instances/acme", null, ServerRole.Admin);
        Assert.Equal(["NA"], Codes(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/acme/entities/territory/rows", null, ServerRole.Viewer))));
    }

    [Fact]
    public async Task Instances_on_connections_of_their_own_are_in_another_database()
    {
        var send = await Client();
        var second = "dds_" + Guid.NewGuid().ToString("N")[..12];
        extraDatabases.Add(second);
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {second}", admin);
            await create.ExecuteNonQueryAsync();
        }
        var other = variable + "_2";
        Environment.SetEnvironmentVariable(other, new NpgsqlConnectionStringBuilder(server) { Database = second }.ConnectionString);
        try
        {
            File.WriteAllText(StoreConnections.FileOf(directory, "region-eu"), $"schema: connection/1\nname: region-eu\nengine: postgres\nconnection_string_secret: 'env:{other}'\ntls:\n  encrypt: false\n");
            await send(HttpMethod.Put, "/api/v1/models/territories", Model.Replace("storage: {connection: mdm}", "storage: {connection: mdm, instances: connection}"), ServerRole.Author);

            // a connection must be named, and must exist
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/eu", "{}", ServerRole.Admin)).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/eu", """{"connection":"nowhere"}""", ServerRole.Admin)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/eu", """{"connection":"region-eu"}""", ServerRole.Admin)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/eu/storage/create", null, ServerRole.Admin)).StatusCode);

            await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "Own copy", "1"), ServerRole.Author);
            await send(HttpMethod.Post, "/api/v1/models/territories/instances/eu/changes", Insert("NA", "EU copy", "2"), ServerRole.Author);

            Assert.Equal("Own copy", (string)(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/row?key.code=NA", null, ServerRole.Viewer)))["values"]!["name"]!);
            Assert.Equal("EU copy", (string)(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/eu/entities/territory/row?key.code=NA", null, ServerRole.Viewer)))["values"]!["name"]!);
            await using var check = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = second }.ConnectionString);
            await check.OpenAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM territories.territory", check);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        finally { Environment.SetEnvironmentVariable(other, null); }
    }

    [Fact]
    public async Task A_model_that_keeps_one_copy_has_no_instances_and_a_bad_name_is_refused()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/acme", null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/instances/acme/entities/territory/rows", null, ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/default", null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await send(HttpMethod.Put, "/api/v1/models/territories/instances/Not%20Ok", null, ServerRole.Admin)).StatusCode);
        Assert.Equal("none", (string)(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances", null, ServerRole.Viewer)))["mode"]!);
        // the model's own copy answers under its own name too
        await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Get, "/api/v1/models/territories/instances/default/entities/territory/rows", null, ServerRole.Viewer)).StatusCode);
    }
}
