using System.Net;
using System.Text.Json.Nodes;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The Sync project kind (plan 33 SC22a): a new project starts as a replication set in Sync's repository layout, and a draft is checked by <c>dbdatasync package publish --to runner --check</c> run as a child process. Most tests run a stand-in program
/// (<see cref="FakeSync"/>, a shell script that answers the way Sync's verb does) so they need no Sync build; the one that runs the real program needs <c>DBDATAKIT_TEST_DBDATASYNC</c> (<c>DBDATAKIT_TEST_REQUIRE_SYNC=true</c> makes its absence a failure).
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceSyncKindTests
{
    private static string Fake()
    {
        var path = FakeSync.Create();
        if (path is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server, string project = "orders-set")
    {
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = project, ["kind"] = "sync", ["title"] = "Orders set" }.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static async Task<JsonNode> CheckAsync(WorkspaceHarness server, string id, string project = "orders-set") =>
        (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{project}/workspaces/{id}/check")).Body!;

    private static async Task Put(WorkspaceHarness server, string id, string path, string content) =>
        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders-set/workspaces/{id}/files/{path}", new JsonObject { ["content"] = content }.ToJsonString())).Response.IsSuccessStatusCode);

    private static async Task<string> Read(WorkspaceHarness server, string id, string path) =>
        (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/files/{path}")).Body!["content"]!;

    [Fact]
    public async Task A_sync_project_starts_as_a_replication_set_in_the_layout_of_a_sync_repository_with_references_and_no_secret()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);

        var files = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.Contains("config/replications/orders/task.yaml", files);
        Assert.Contains("config/replications/orders/table-mappings/orders.yaml", files);
        Assert.Contains("config/connections/source-db.yaml", files);
        Assert.Contains("config/connections/target-db.yaml", files);
        Assert.Contains("password_secret: secret:dbdata:sync:connection:source-db", await Read(server, id, "config/connections/source-db.yaml"));
        Assert.StartsWith("schemaVersion: 2", await Read(server, id, "config/replications/orders/task.yaml"));
        Assert.Contains("Orders set", await Read(server, id, "README.md"));
        Assert.Contains("secrets/", await Read(server, id, ".gitignore"));

        var check = await CheckAsync(server, id);
        Assert.Equal("sync", (string)check["kind"]!);
        Assert.True((bool)check["ok"]!, check.ToJsonString());
    }

    [Fact]
    public async Task A_mapping_with_no_column_cache_is_refused_with_its_file_and_the_programs_words()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);
        var mapping = await Read(server, id, "config/replications/orders/table-mappings/orders.yaml");
        await Put(server, id, "config/replications/orders/table-mappings/orders.yaml", mapping.Replace("sourceColumns:\n- name: id\n  nativeType: bigint\n  isPrimaryKey: true\n  keyOrdinal: 1\n", "sourceColumns: []\n"));

        var check = await CheckAsync(server, id);
        Assert.False((bool)check["ok"]!);
        var finding = check["diagnostics"]!.AsArray().Single(d => (string)d!["severity"]! == "error")!;
        Assert.Equal("DBD-WS-SYNC", (string)finding["code"]!);
        Assert.Equal("config/replications/orders/table-mappings/orders.yaml", (string)finding["file"]!);      // relative to the draft, not the server's folder
        Assert.Contains("has not been cached", (string)finding["message"]!);
        Assert.DoesNotContain("Nothing was sent", check.ToJsonString());
    }

    [Theory]
    [InlineData("  password_secret: secret:dbdata:sync:connection:source-db", "  password: hunter2", "holds a password value", 10)]
    [InlineData("  password_secret: secret:dbdata:sync:connection:source-db", "  password_secret: hunter2", "not a secret reference", 10)]
    public async Task A_connection_file_that_holds_a_password_in_place_of_a_reference_is_refused_with_its_line_whatever_the_program_says(string was, string now, string words, int line)
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);
        var text = await Read(server, id, "config/connections/source-db.yaml");
        await Put(server, id, "config/connections/source-db.yaml", text.Replace(was, now));

        var finding = (await CheckAsync(server, id))["diagnostics"]!.AsArray().First(d => (string)d!["code"]! == "DBD-WS-SYNC-SECRET")!;
        Assert.Equal("config/connections/source-db.yaml", (string)finding["file"]!);
        Assert.Equal(line, (int)finding["line"]!);
        Assert.Contains(words, (string)finding["message"]!);
        Assert.DoesNotContain("hunter2", (string)finding["message"]!);        // the value is not repeated
    }

    [Fact]
    public async Task A_password_in_a_connection_string_and_a_file_under_secrets_are_refused()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);
        await Put(server, id, "config/connections/odbc.yaml", "schema: connection/1\nname: odbc\nengine: odbc\nconnection_string: 'Driver=x;Server=s;Pwd=hunter2;'\n");
        await Put(server, id, "config/secrets/store.json", "{}");

        var findings = (await CheckAsync(server, id))["diagnostics"]!.AsArray().Where(d => (string)d!["code"]! == "DBD-WS-SYNC-SECRET").ToList();
        Assert.Contains(findings, f => (string)f!["file"]! == "config/connections/odbc.yaml" && (int)f["line"]! == 4);
        Assert.Contains(findings, f => (string)f!["file"]! == "config/secrets/");
        // a reference in the connection string is what is meant and is not refused
        await Put(server, id, "config/connections/odbc.yaml", "schema: connection/1\nname: odbc\nengine: odbc\nconnection_string: 'Driver=x;Server=s;Pwd=secret:dbdata:sync:connection:odbc;'\n");
        Assert.DoesNotContain(((await CheckAsync(server, id))["diagnostics"]!.AsArray()), f => (string)f!["file"]! == "config/connections/odbc.yaml");
    }

    [Fact]
    public async Task The_program_runs_in_the_draft_without_the_servers_environment_and_the_check_writes_nothing()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);
        Environment.SetEnvironmentVariable("DBDATAKIT_TEST_MARKER", "secret-of-the-server");
        try
        {
            var check = await CheckAsync(server, id);
            Assert.True((bool)check["ok"]!, "the child saw the server's environment: " + check.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("DBDATAKIT_TEST_MARKER", null); }
        Assert.Empty((await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/changes")).Body!["changes"]!.AsArray());
    }

    [Fact]
    public async Task Without_the_program_a_project_is_still_made_and_a_check_says_why_it_cannot_run()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Path.Combine(Path.GetTempPath(), "no-such-dbdatasync"));
        var id = await DraftAsync(server);        // the template needs no program

        var check = await CheckAsync(server, id);
        Assert.False((bool)check["ok"]!);
        var missing = check["diagnostics"]!.AsArray().Single()!;
        Assert.Equal("DBD-WS-SYNC-MISSING", (string)missing["code"]!);
        Assert.Contains("dbdatasync is not beside it or on its PATH", (string)missing["message"]!);
    }

    [Fact]
    public async Task The_real_program_checks_the_template_sound_and_refuses_a_mapping_with_no_cache()
    {
        var real = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_DBDATASYNC");
        Assert.False(string.IsNullOrEmpty(real) && string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_SYNC"), "true", StringComparison.OrdinalIgnoreCase), "dbdatasync is required but DBDATAKIT_TEST_DBDATASYNC is not set.");
        if (string.IsNullOrEmpty(real) || !File.Exists(real)) Assert.Skip("DBDATAKIT_TEST_DBDATASYNC is not set (a build of DbDataSync/src/DbDataSync.Cli)");
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: real);
        var id = await DraftAsync(server);
        Assert.True((bool)(await CheckAsync(server, id))["ok"]!);
        var mapping = await Read(server, id, "config/replications/orders/table-mappings/orders.yaml");
        await Put(server, id, "config/replications/orders/table-mappings/orders.yaml", mapping.Replace("sourceColumns:\n- name: id\n  nativeType: bigint\n  isPrimaryKey: true\n  keyOrdinal: 1\n", "sourceColumns: []\n"));
        var finding = (await CheckAsync(server, id))["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.Equal("config/replications/orders/table-mappings/orders.yaml", (string)finding["file"]!);
    }
}
