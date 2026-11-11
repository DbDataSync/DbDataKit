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
/// A model's tables and rows through the management API on a real PostgreSQL and a real SQL Server (plan 19, Z09; <c>testenv/testenv.sh up postgres mssql</c>, then <c>eval "$(testenv/testenv.sh env dbdatakit)"</c>; a class skips without its engine): the storage is planned and created,
/// a row is written and read back, a stale version and a broken rule are refused with their own statuses, and each role does only its part.
/// </summary>
public abstract class StoreDataTests(string engine) : IAsyncLifetime
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
    private string subject = "ada";
    private IReadOnlyList<string>? scope;
    private WebApplication? app;
    private StoreSettings settings = null!;
    private readonly string? server = Environment.GetEnvironmentVariable(engine == "sqlserver" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");

    private bool SqlServer => engine == "sqlserver";

    /// <summary>The server's connection string pointed at another database.</summary>
    private string ConnectionTo(string name) => SqlServer ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString : new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

    private async Task RunAsync(string database, string sql)
    {
        if (SqlServer)
        {
            await using var connection = new SqlConnection(ConnectionTo(database));
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            await using var connection = new NpgsqlConnection(ConnectionTo(database));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> CountAsync(string database, string table)
    {
        if (SqlServer)
        {
            await using var connection = new SqlConnection(ConnectionTo(database));
            await connection.OpenAsync();
            await using var command = new SqlCommand($"SELECT count(*) FROM {table}", connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        await using var pg = new NpgsqlConnection(ConnectionTo(database));
        await pg.OpenAsync();
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", pg);
        return Convert.ToInt64(await count.ExecuteScalarAsync());
    }

    private async Task CreateDatabaseAsync(string name) => await RunAsync(SqlServer ? "master" : "postgres", $"CREATE DATABASE {name}");

    private async Task DropDatabaseAsync(string name) =>
        await RunAsync(SqlServer ? "master" : "postgres", SqlServer ? $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {name}; END" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");

    private string ConnectionFile(string name, string variableName) =>
        $"schema: connection/1\nname: {name}\nengine: {engine}\nconnection_string_secret: 'env:{variableName}'\n" + "tls:\n  encrypt: false\n";

    public async ValueTask InitializeAsync()
    {
        if (server is null) return;
        await CreateDatabaseAsync(database);
        Environment.SetEnvironmentVariable(variable, ConnectionTo(database));
        Directory.CreateDirectory(Path.Combine(directory, StoreConnections.Folder));
        File.WriteAllText(StoreConnections.FileOf(directory, "mdm"), ConnectionFile("mdm", variable));
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
            foreach (var name in extraDatabases.Prepend(database)) await DropDatabaseAsync(name);
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
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, subject, subject, role, App: scope is null ? null : "acme", Scope: scope)));
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
        Assert.Equal(engine, (string)plan["engine"]!);
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
    public async Task An_agents_write_waits_as_a_change_request_a_person_approves_unless_the_model_lets_agents_write()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author);
        await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin);

        // an agent proposes: nothing is written, the request waits
        subject = "agent:helper";
        var proposed = await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "1").Replace("{\"changes\"", "{\"description\":\"add North\",\"changes\""), ServerRole.Author);
        Assert.Equal(HttpStatusCode.Accepted, proposed.StatusCode);
        var id = (string)(await Json(proposed))["id"]!;
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/row?key.code=NA", null, ServerRole.Viewer)).StatusCode);
        var pending = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/requests?state=pending", null, ServerRole.Viewer));
        Assert.Equal([id], pending["requests"]!.AsArray().Select(r => (string)r!["id"]!).ToArray());

        // an agent cannot approve its own request
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, $"/api/v1/models/territories/requests/{id}/approve", null, ServerRole.Author)).StatusCode);

        // a person approves: the rows are written once, by the writer, and the request is decided
        subject = "ada";
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, $"/api/v1/models/territories/requests/{id}/approve", null, ServerRole.Viewer)).StatusCode);
        var approved = await send(HttpMethod.Post, $"/api/v1/models/territories/requests/{id}/approve", "{\"note\":\"ok\"}", ServerRole.Author);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("approved", (string)(await Json(approved))["state"]!);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/row?key.code=NA", null, ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await send(HttpMethod.Post, $"/api/v1/models/territories/requests/{id}/approve", null, ServerRole.Author)).StatusCode);

        // a rejected request writes nothing
        subject = "agent:helper";
        var second = (string)(await Json(await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("EU", "Europe", "2"), ServerRole.Author)))["id"]!;
        subject = "ada";
        Assert.Equal("rejected", (string)(await Json(await send(HttpMethod.Post, $"/api/v1/models/territories/requests/{second}/reject", null, ServerRole.Author)))["state"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/row?key.code=EU", null, ServerRole.Viewer)).StatusCode);

        // a model agents manage themselves takes their writes directly
        subject = "ada";
        await send(HttpMethod.Put, "/api/v1/models/robots", Model.Replace("name: territories", "name: robots").Replace("storage: {connection: mdm}", "storage: {connection: mdm}\napproval: {agents: direct}"), ServerRole.Author);
        await send(HttpMethod.Post, "/api/v1/models/robots/storage/create", null, ServerRole.Admin);
        subject = "agent:helper";
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/robots/changes", Insert("AS", "Asia", "3"), ServerRole.Author)).StatusCode);
    }

    [Fact]
    public async Task A_proposal_is_checked_when_it_is_made_a_broken_one_is_refused_and_kept_nowhere_and_a_proposal_that_cannot_be_checked_says_so()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author);

        // before the tables exist the check cannot be made: the request is kept, and says it was not checked
        subject = "agent:helper";
        var early = await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "1"), ServerRole.Author);
        Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);
        var earlyBody = await Json(early);
        Assert.False((bool)earlyBody["checked"]!);
        Assert.NotNull((string?)earlyBody["check_problem"]);
        subject = "ada";
        await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin);

        // with the tables there, a rule broken at proposal time is refused as a direct write would be, with each violation, and no request is kept
        subject = "agent:helper";
        var before = (await Json(await send(HttpMethod.Get, "/api/v1/models/territories/requests", null, ServerRole.Viewer)))["requests"]!.AsArray().Count;
        var broken = await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("EU", "Europe", "-5"), ServerRole.Author);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, broken.StatusCode);
        Assert.NotEmpty((await Json(broken))["violations"]!.AsArray());
        Assert.Equal(before, (await Json(await send(HttpMethod.Get, "/api/v1/models/territories/requests", null, ServerRole.Viewer)))["requests"]!.AsArray().Count);

        // a good proposal is checked and kept, and writes nothing; one that names a row that is not there is refused too
        var good = await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("AS", "Asia", "3"), ServerRole.Author);
        Assert.Equal(HttpStatusCode.Accepted, good.StatusCode);
        Assert.True((bool)(await Json(good))["checked"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/row?key.code=AS", null, ServerRole.Viewer)).StatusCode);
        var missing = """{"changes":[{"action":"update","entity":"territory","key":{"code":"ZZ"},"expected_version":1,"values":{"name":"x"}}]}""";
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", missing, ServerRole.Author)).StatusCode);

        // a version that has moved on since the proposer read the row is a conflict at proposal, not at approval
        subject = "ada";
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "1"), ServerRole.Author)).StatusCode);
        subject = "agent:helper";
        var stale = """{"changes":[{"action":"update","entity":"territory","key":{"code":"NA"},"expected_version":9,"values":{"name":"x"}}]}""";
        Assert.Equal(HttpStatusCode.Conflict, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", stale, ServerRole.Author)).StatusCode);
    }

    [Fact]
    public async Task An_applied_change_is_an_event_a_consumer_reads_after_the_last_one_it_saw_and_a_refused_or_rejected_one_is_not()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author);
        await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin);
        Assert.Empty((await Json(await send(HttpMethod.Get, "/api/v1/events", null, ServerRole.Viewer)))["events"]!.AsArray());

        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "10"), ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("EU", "Europe", "-5"), ServerRole.Author)).StatusCode);
        var first = await Json(await send(HttpMethod.Get, "/api/v1/events", null, ServerRole.Viewer));
        var only = Assert.Single(first["events"]!.AsArray());
        Assert.Equal("territories", (string)only!["model"]!);
        Assert.Equal("territory", (string)only["entities"]![0]!);
        Assert.Equal(1, (int)only["changes"]!);
        Assert.Equal("ada", (string)only["by"]!);
        Assert.Null(only["request"]);
        Assert.False(string.IsNullOrEmpty((string)only["change_id"]!));
        var seen = (long)first["last"]!;

        // an agent's proposal is no event until a person approves it; the event carries the request's id as its change id, and a rejected one never becomes one
        subject = "agent:helper";
        var approved = (string)(await Json(await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("AS", "Asia", "3"), ServerRole.Author)))["id"]!;
        var rejected = (string)(await Json(await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("AF", "Africa", "4"), ServerRole.Author)))["id"]!;
        Assert.Empty((await Json(await send(HttpMethod.Get, $"/api/v1/events?after={seen}", null, ServerRole.Viewer)))["events"]!.AsArray());
        subject = "ada";
        await send(HttpMethod.Post, $"/api/v1/models/territories/requests/{rejected}/reject", null, ServerRole.Author);
        Assert.Empty((await Json(await send(HttpMethod.Get, $"/api/v1/events?after={seen}", null, ServerRole.Viewer)))["events"]!.AsArray());
        await send(HttpMethod.Post, $"/api/v1/models/territories/requests/{approved}/approve", null, ServerRole.Author);
        var next = Assert.Single((await Json(await send(HttpMethod.Get, $"/api/v1/events?after={seen}", null, ServerRole.Viewer)))["events"]!.AsArray());
        Assert.Equal(approved, (string)next!["change_id"]!);
        Assert.Equal(approved, (string)next["request"]!);
        Assert.True((long)next["seq"]! > seen);
        Assert.Equal(HttpStatusCode.BadRequest, (await send(HttpMethod.Get, "/api/v1/events?after=x", null, ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task An_apps_token_reaches_only_the_entities_its_scope_names_and_nothing_else()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Author);
        await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "North", "1"), ServerRole.Author)).StatusCode);
        const string Row = "/api/v1/models/territories/entities/territory/row?key.code=NA";

        // read scope: the row is read; a write is refused even though the person is an author; the admin routes are not reachable
        scope = ["territories/territory:read"];
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Get, Row, null, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Get, "/api/v1/models/territories/entities/territory/rows", null, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("EU", "Europe", "2"), ServerRole.Author)).StatusCode);
        foreach (var path in new[] { "/api/v1/models", "/api/v1/models/territories", "/api/v1/models/territories/storage", "/api/v1/models/territories/requests", "/api/v1/models/territories/schema" })
            Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Get, path, null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Put, "/api/v1/models/territories", Model, ServerRole.Admin)).StatusCode);

        // an entity the scope does not name, and another model's, are refused
        scope = ["territories/other:read", "elsewhere/territory:write"];
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Get, Row, null, ServerRole.Author)).StatusCode);

        // write scope: the write goes through, as the person, and the person's own roles still decide (a viewer cannot write)
        scope = ["territories/territory:write"];
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("EU", "Europe", "2"), ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("AS", "Asia", "3"), ServerRole.Viewer)).StatusCode);
        scope = [];
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Get, Row, null, ServerRole.Author)).StatusCode);   // an empty scope reaches nothing

        // without a scope (a console) the same calls work
        scope = null;
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Get, "/api/v1/models/territories/storage", null, ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task The_command_line_saves_a_model_and_creates_its_tables_so_a_new_server_needs_no_console_for_its_first_one()
    {
        var send = await Client();
        async Task<(int Exit, string Out, string Error)> Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await StoreCli.Router().RunAsync([.. args, "--repo", directory], new DbDataKit.Cli.CliContext(output, error, TextReader.Null));
            return (exit, output.ToString(), error.ToString());
        }

        var good = Path.Combine(directory, "cli_territories.yaml");
        File.WriteAllText(good, Model.Replace("name: territories", "name: cli_territories"));
        var bad = Path.Combine(directory, "broken.yaml");
        File.WriteAllText(bad, "schemaVersion: 1\nname: broken\nentities: nope\n");

        Assert.NotEqual(0, (await Run("model", "put", "--file", bad)).Exit);                      // checked first: nothing saved
        Assert.False(File.Exists(StoreModels.FileOf(directory, "broken")));
        Assert.Equal(1, (await Run("storage", "create", "--model", "cli_territories")).Exit);       // no such model yet

        var saved = await Run("model", "put", "--file", good);
        Assert.True(saved.Exit == 0, saved.Error);
        Assert.Contains("Saved the model cli_territories", saved.Out);
        var created = await Run("storage", "create", "--model", "cli_territories");
        Assert.Equal(0, created.Exit);
        Assert.Contains("Created the tables", created.Out);

        // the tables are there: a write and a read through the API
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/cli_territories/changes", Insert("NA", "North", "1"), ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Get, "/api/v1/models/cli_territories/entities/territory/row?key.code=NA", null, ServerRole.Viewer)).StatusCode);
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
        await CreateDatabaseAsync(second);
        var other = variable + "_2";
        Environment.SetEnvironmentVariable(other, ConnectionTo(second));
        try
        {
            File.WriteAllText(StoreConnections.FileOf(directory, "region-eu"), ConnectionFile("region-eu", other));
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
            Assert.Equal(1L, await CountAsync(second, "territories.territory"));
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

    [Fact]
    public async Task Instances_in_shared_tables_are_split_by_an_instance_id_the_server_stamps()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Model.Replace("storage: {connection: mdm}", "storage: {connection: mdm, instances: column}"), ServerRole.Author);
        foreach (var instance in new[] { "acme", "globex" })
            Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Put, $"/api/v1/models/territories/instances/{instance}", null, ServerRole.Admin)).StatusCode);

        // the tables are shared: made once, through the model's own path
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/storage/create", null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Post, "/api/v1/models/territories/storage/create", null, ServerRole.Admin)).StatusCode);

        await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/changes", Insert("NA", "Acme North", "1"), ServerRole.Author);
        await send(HttpMethod.Post, "/api/v1/models/territories/instances/globex/changes", Insert("NA", "Globex North", "2"), ServerRole.Author);
        await send(HttpMethod.Post, "/api/v1/models/territories/changes", Insert("NA", "Default North", "3"), ServerRole.Author);

        foreach (var (path, expected) in new[] { ("instances/acme/", "Acme North"), ("instances/globex/", "Globex North"), ("", "Default North") })
            Assert.Equal(expected, (string)(await Json(await send(HttpMethod.Get, $"/api/v1/models/territories/{path}entities/territory/row?key.code=NA", null, ServerRole.Viewer)))["values"]!["name"]!);
        var page = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/acme/entities/territory/rows", null, ServerRole.Viewer));
        Assert.Single(page["rows"]!.AsArray());
        Assert.Equal(3L, await CountAsync(database, "territories.territory"));

        // a body cannot name the instance, and an unregistered one is not there
        var forged = """{"changes":[{"action":"insert","entity":"territory","values":{"code":"ZZ","name":"Z","_instance":"globex"}}]}""";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/changes", forged, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/instances/initech/entities/territory/rows", null, ServerRole.Viewer)).StatusCode);
        Assert.Equal("column", (string)(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances", null, ServerRole.Viewer)))["mode"]!);
    }

    [Fact]
    public async Task An_instance_behind_the_model_keeps_working_and_one_that_is_migrated_gets_the_new_structure()
    {
        var send = await Client();
        var v1 = Model.Replace("storage: {connection: mdm}", "storage: {connection: mdm, instances: schema}");
        var v2 = v1.Replace("quota: {type: \"DECIMAL(10,2)\", min: 0}", "quota: {type: \"DECIMAL(10,2)\", min: 0}\n      region: {type: VARCHAR(20), required: true}");
        await send(HttpMethod.Put, "/api/v1/models/territories", v1, ServerRole.Author);
        foreach (var instance in new[] { "acme", "globex" })
        {
            await send(HttpMethod.Put, $"/api/v1/models/territories/instances/{instance}", null, ServerRole.Admin);
            Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Post, $"/api/v1/models/territories/instances/{instance}/storage/create", null, ServerRole.Admin)).StatusCode);
        }
        string[] Status(JsonNode report) => report["instances"]!.AsArray().Select(i => $"{(string)i!["name"]!}:{(string)i["status"]!}").ToArray();
        var report = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/migrations", null, ServerRole.Viewer));
        Assert.Equal(["default:unknown", "acme:current", "globex:current"], Status(report));

        // the model moves on: a required column the instances do not have yet
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Put, "/api/v1/models/territories", v2, ServerRole.Author)).StatusCode);
        Assert.Equal(["default:unknown", "acme:behind", "globex:behind"], Status(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/migrations", null, ServerRole.Viewer))));

        // both still work as they were: the version they run has no region, and says so
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/globex/changes", Insert("NA", "Globex North", "1"), ServerRole.Author)).StatusCode);
        string[] Attributes(JsonNode schema) => schema["entities"]![0]!["attributes"]!.AsArray().Select(a => (string)a!["name"]!).ToArray();
        Assert.DoesNotContain("region", Attributes(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/globex/schema", null, ServerRole.Viewer))));
        Assert.Contains("region", Attributes(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/schema", null, ServerRole.Viewer))));

        // a dry run says what it would add, and changes nothing; only an administrator migrates
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/migrate?apply=true", null, ServerRole.Author)).StatusCode);
        var dry = await Json(await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/migrate", null, ServerRole.Admin));
        Assert.False((bool)dry["up_to_date"]!);
        Assert.False((bool)dry["applied"]!);
        Assert.Contains(dry["statements"]!.AsArray(), st => (string)st!["description"]! == "add column territory.region");
        Assert.Equal("behind", Status(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/migrations", null, ServerRole.Viewer)))[1].Split(':')[1]);

        var applied = await Json(await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/migrate?apply=true", null, ServerRole.Admin));
        Assert.True((bool)applied["applied"]!);
        Assert.Equal(["default:unknown", "acme:current", "globex:behind"], Status(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/migrations", null, ServerRole.Viewer))));

        // acme now runs the new version: region is required there; globex, still behind, does not know it
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/changes", Insert("NA", "Acme North", "1"), ServerRole.Author)).StatusCode);
        var withRegion = """{"changes":[{"action":"insert","entity":"territory","values":{"code":"NA","name":"Acme North","region":"EMEA"}}]}""";
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/changes", withRegion, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Post, "/api/v1/models/territories/instances/globex/changes", Insert("EU", "Globex Europe", "2"), ServerRole.Author)).StatusCode);
        Assert.Equal(2, (await Json(await send(HttpMethod.Get, "/api/v1/models/territories/instances/globex/entities/territory/rows", null, ServerRole.Viewer)))["rows"]!.AsArray().Count);
        Assert.True((bool)(await Json(await send(HttpMethod.Post, "/api/v1/models/territories/instances/acme/migrate", null, ServerRole.Admin)))["up_to_date"]!);

        // and globex migrates later on its own
        Assert.True((bool)(await Json(await send(HttpMethod.Post, "/api/v1/models/territories/instances/globex/migrate?apply=true", null, ServerRole.Admin)))["applied"]!);
        Assert.Equal(["default:unknown", "acme:current", "globex:current"], Status(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/migrations", null, ServerRole.Viewer))));
    }
}


/// <summary>On PostgreSQL (<c>DBDATAKIT_TEST_POSTGRES</c>).</summary>
public sealed class StoreDataOnPostgresTests() : StoreDataTests("postgres");

/// <summary>On SQL Server (<c>DBDATAKIT_TEST_SQLSERVER</c>).</summary>
public sealed class StoreDataOnSqlServerTests() : StoreDataTests("sqlserver");
