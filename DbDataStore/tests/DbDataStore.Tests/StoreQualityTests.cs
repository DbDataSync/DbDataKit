using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataStore.Tests;

/// <summary>
/// The Store's data quality checks on a real PostgreSQL and a real SQL Server (plan 23a FQ11, FQ17; <c>testenv/testenv.sh up postgres mssql</c>, then <c>eval "$(testenv/testenv.sh env dbdatakit)"</c>; a class skips without its engine): rows made invalid outside the writer (a direct
/// update) are found by a <c>unique</c> and a <c>referential</c> check run on request, the block is set and the console's overview and history carry them, and the writer's own refusal at write time is what it was.
/// </summary>
public abstract class StoreQualityTests(string engine) : IAsyncLifetime
{
    private const string Model = """
        schemaVersion: 2
        name: territories
        storage: {connection: mdm}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100), required: true}
              quota: {type: "DECIMAL(10,2)", min: 0}
          city:
            key: [id]
            attributes:
              id: {type: INT, required: true}
              territory_code: {type: VARCHAR(10)}
        checks:
          - {id: territory_name_unique, kind: unique, dataset: territory, column: name, severity: block}
          - {id: city_has_territory, kind: referential, dataset: city, column: territory_code, references: {dataset: territory, column: code}}
          - {id: territory_has_name, kind: not_null, dataset: territory, column: name}
        """;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-quality-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string database = "ddsq_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string variable = "DDS_QTEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication? app;
    private StoreSettings settings = null!;
    private readonly string? server = Environment.GetEnvironmentVariable(engine == "sqlserver" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");

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
        Directory.CreateDirectory(Path.Combine(directory, StoreConnections.Folder));
        File.WriteAllText(StoreConnections.FileOf(directory, "mdm"), $"schema: connection/1\nname: mdm\nengine: {engine}\nconnection_string_secret: 'env:{variable}'\ntls:\n  encrypt: false\n");
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
            SqlConnection.ClearAllPools();
            await RunAsync(SqlServer ? "master" : "postgres", SqlServer ? $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database}; END" : $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<Func<HttpMethod, string, string?, ServerRole, Task<HttpResponseMessage>>> Client()
    {
        if (server is null && string.Equals(Environment.GetEnvironmentVariable(SqlServer ? "DBDATAKIT_TEST_REQUIRE_SQLSERVER" : "DBDATAKIT_TEST_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase)) Assert.Fail($"{engine} is required but its DBDATAKIT_TEST_* variable is not set");
        Assert.SkipWhen(server is null, $"set DBDATAKIT_TEST_{(SqlServer ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        var url = new Uri(app!.Urls.First());
        var code = StoreState.Pairing(settings.Directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return (method, path, body, role) =>
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "ada", role)));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return http.SendAsync(request);
        };
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static string Insert(string entity, string values) => $$$"""{"changes":[{"action":"insert","entity":"{{{entity}}}","values":{{{values}}}}]}""";

    [Fact]
    public async Task Rows_made_invalid_outside_the_writer_are_found_by_unique_and_referential_checks_and_the_writers_own_refusal_still_happens()
    {
        var send = await Client();
        Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin)).StatusCode);
        foreach (var (entity, values) in new[] { ("territory", """{"code":"NA","name":"North","quota":1}"""), ("territory", """{"code":"EU","name":"Europe","quota":2}"""), ("city", """{"id":1,"territory_code":"NA"}"""), ("city", """{"id":2,"territory_code":"EU"}""") })
            Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert(entity, values), ServerRole.Author)).StatusCode);

        // the writer still refuses at write time (a rule of the model, not a check)
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("territory", """{"code":"XX","name":"Nowhere","quota":-5}"""), ServerRole.Author)).StatusCode);

        // an evaluation is an administrator's call; an unknown dataset is named
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/quality/run", """{"dataset":null}""", ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Post, "/api/v1/quality/run", """{"dataset":"nowhere"}""", ServerRole.Admin)).StatusCode);

        // clean data: everything passes, no block
        var clean = await Json(await send(HttpMethod.Post, "/api/v1/quality/run", """{"dataset":null}""", ServerRole.Admin));
        Assert.Equal((3, 3, 0, 0, 0), ((int)clean["evaluated"]!, (int)clean["passed"]!, (int)clean["failed"]!, (int)clean["errors"]!, (int)clean["blocking"]!));

        // made invalid outside the writer: a direct update, which no write rule saw
        await RunAsync(database, "UPDATE territories.territory SET name = 'North' WHERE code = 'EU'");
        await RunAsync(database, "UPDATE territories.city SET territory_code = 'ZZ' WHERE id = 2");
        var found = await Json(await send(HttpMethod.Post, "/api/v1/quality/run", """{"dataset":null}""", ServerRole.Admin));
        Assert.Equal((3, 1, 2, 0, 1), ((int)found["evaluated"]!, (int)found["passed"]!, (int)found["failed"]!, (int)found["errors"]!, (int)found["blocking"]!));

        // what a console reads: the datasets by the identity their tables have, the checks, the block, the sample keys of the history
        var overview = await Json(await send(HttpMethod.Get, "/api/v1/quality", null, ServerRole.Viewer));
        Assert.True((bool)overview["can_run"]!);
        var datasets = overview["datasets"]!.AsArray().ToDictionary(d => (string)d!["dataset"]!, d => d!);
        Assert.Equal(2, datasets.Count);
        var territory = datasets.Single(d => d.Key.EndsWith(".territories.territory", StringComparison.Ordinal)).Value;
        var unique = territory["checks"]!.AsArray().Single(c => (string)c!["check_id"]! == "territory_name_unique")!;
        Assert.Equal(("fail", "block", true), (((string)unique["status"]!), (string)unique["severity"]!, (bool)unique["blocking"]!));
        var city = datasets.Single(d => d.Key.EndsWith(".territories.city", StringComparison.Ordinal)).Value;
        Assert.Equal("fail", (string)city["checks"]![0]!["status"]!);
        Assert.False((bool)city["checks"]![0]!["blocking"]!);                 // a warn check never blocks

        var history = await Json(await send(HttpMethod.Get, $"/api/v1/quality/history?dataset={Uri.EscapeDataString(territory["dataset"]!.GetValue<string>())}&check=territory_name_unique", null, ServerRole.Viewer));
        Assert.Equal(["name=North"], history["results"]![0]!["sample_keys"]!.AsArray().Select(k => (string)k!).ToArray());      // the duplicated value
        var cityHistory = await Json(await send(HttpMethod.Get, $"/api/v1/quality/history?dataset={Uri.EscapeDataString(city["dataset"]!.GetValue<string>())}&check=city_has_territory", null, ServerRole.Viewer));
        Assert.Equal(["id=2"], cityHistory["results"]![0]!["sample_keys"]!.AsArray().Select(k => (string)k!).ToArray());        // the row's key

        // an administrator releases the block (an author cannot): the check still fails, what depends on the entity is no longer held for it, and a further failing evaluation does not set it again
        var territoryId = territory["dataset"]!.GetValue<string>();
        var release = $$$"""{"dataset":"{{{territoryId}}}","check":"territory_name_unique"}""";
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/quality/release", release, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/quality/release", release, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await send(HttpMethod.Post, "/api/v1/quality/release", release, ServerRole.Admin)).StatusCode);        // nothing left to release
        Assert.Equal(HttpStatusCode.BadRequest, (await send(HttpMethod.Post, "/api/v1/quality/release", """{"dataset":"x"}""", ServerRole.Admin)).StatusCode);
        var releasedOverview = await Json(await send(HttpMethod.Get, "/api/v1/quality", null, ServerRole.Viewer));
        var releasedCheck = releasedOverview["datasets"]!.AsArray().Single(d => (string)d!["dataset"]! == territoryId)!["checks"]!.AsArray().Single(c => (string)c!["check_id"]! == "territory_name_unique")!;
        Assert.Equal(("fail", false, "ada"), ((string)releasedCheck["status"]!, (bool)releasedCheck["blocking"]!, (string)releasedCheck["released_by"]!));
        Assert.True((bool)releasedOverview["can_release"]!);
        await send(HttpMethod.Post, "/api/v1/quality/run", """{"dataset":"territory"}""", ServerRole.Admin);
        var stillReleased = await Json(await send(HttpMethod.Get, "/api/v1/quality", null, ServerRole.Viewer));
        Assert.Equal(0, (int)stillReleased["datasets"]!.AsArray().Single(d => (string)d!["dataset"]! == territoryId)!["blocking"]!);
        Assert.Contains("quality.release", File.ReadAllText(Path.Combine(directory, "management-audit.log")));

        // fixed outside the writer too: the next evaluation passes and the block clears
        await RunAsync(database, "UPDATE territories.territory SET name = 'Europe' WHERE code = 'EU'");
        await RunAsync(database, "UPDATE territories.city SET territory_code = 'EU' WHERE id = 2");
        var fixedRun = await Json(await send(HttpMethod.Post, "/api/v1/quality/run", """{"dataset":"territory"}""", ServerRole.Admin));
        Assert.Equal((2, 0, 0, 0), ((int)fixedRun["evaluated"]!, (int)fixedRun["failed"]!, (int)fixedRun["errors"]!, (int)fixedRun["blocking"]!));      // the entity named: its two checks
        var after = await Json(await send(HttpMethod.Get, "/api/v1/quality", null, ServerRole.Viewer));
        Assert.All(after["datasets"]!.AsArray().Where(d => ((string)d!["dataset"]!).EndsWith(".territory", StringComparison.Ordinal)), d => Assert.Equal(0, (int)d!["blocking"]!));
    }
}

/// <summary>On PostgreSQL (<c>DBDATAKIT_TEST_POSTGRES</c>).</summary>
public sealed class StoreQualityOnPostgresTests() : StoreQualityTests("postgres");

/// <summary>On SQL Server (<c>DBDATAKIT_TEST_SQLSERVER</c>).</summary>
public sealed class StoreQualityOnSqlServerTests() : StoreQualityTests("sqlserver");
