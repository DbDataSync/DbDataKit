using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The provisioning plan of a Sync draft from its catalog cache (plan 33 SC22j): <c>dbdatasync provisioning plan --from-cache --json</c> on a copy of the draft, offline, read only. The stand-in <see cref="FakeSync"/> for the mechanics; the real program
/// when <c>DBDATAKIT_TEST_DBDATASYNC</c> is set.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceSyncProvisioningTests
{
    private static string Fake()
    {
        var path = FakeSync.Create();
        if (path is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders-set","kind":"sync"}""")).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders-set/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Put(WorkspaceHarness server, string id, string path, string text) =>
        server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders-set/workspaces/{id}/files/{path}", new JsonObject { ["content"] = text }.ToJsonString());

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Plan(WorkspaceHarness server, string id, string json) =>
        server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders-set/workspaces/{id}/provisioning-plan", json);

    private const string Cache = "schema: catalog-cache/1\ngenerated_at: 2026-11-12T10:00:00Z\nsource: {server: prod-gateway, product: gateway}\nconnections:\n  target-db:\n    engine: postgres\n    tables:\n      - schema: public\n        name: other\n        kind: table\n        columns:\n          - {name: x, type: integer, nullable: false}\n";

    [Fact]
    public async Task The_plan_comes_back_as_the_tool_printed_it_and_the_program_was_asked_for_the_cache_on_a_copy()
    {
        var fake = Fake();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: fake);
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);

        var planned = await Plan(server, id, """{"replication":"orders"}""");
        Assert.Equal(HttpStatusCode.OK, planned.Response.StatusCode);
        Assert.True((bool)planned.Body!["ok"]!, planned.Body.ToJsonString());
        var mapping = planned.Body["data"]!["mappings"]![0]!;
        Assert.Equal("missing", (string)mapping["target_plan"]!["state"]!);
        Assert.Contains("CREATE TABLE", (string)mapping["target_plan"]!["steps"]![0]!["command_text"]!);
        var arguments = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fake)!, "arguments.txt")).Last();
        Assert.Contains("provisioning plan orders --from-cache --json --repo ", arguments);
    }

    [Fact]
    public async Task A_draft_with_no_cache_is_answered_with_the_programs_words_as_a_finding()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);

        var planned = await Plan(server, id, """{"replication":"orders"}""");
        Assert.Equal(HttpStatusCode.OK, planned.Response.StatusCode);
        Assert.False((bool)planned.Body!["ok"]!);
        Assert.Contains("catalog-cache.yaml is not in", (string)planned.Body["diagnostics"]![0]!["message"]!);
        Assert.DoesNotContain("ddw-sync-", planned.Body.ToJsonString());
    }

    [Theory]
    [InlineData("""{"replication":"../x"}""")]
    [InlineData("""{"replication":"-x"}""")]
    [InlineData("""{}""")]
    public async Task The_replication_name_is_checked_before_anything_runs(string body)
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);

        Assert.Equal(HttpStatusCode.BadRequest, (await Plan(server, id, body)).Response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_ask_for_the_plan_because_it_reads_and_runs_nothing_of_the_drafts()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);

        var viewer = await server.Send("vic", ServerRole.Viewer, HttpMethod.Post, $"/api/v1/projects/orders-set/workspaces/{id}/provisioning-plan", """{"replication":"orders"}""");
        Assert.Equal(HttpStatusCode.OK, viewer.Response.StatusCode);
    }

    [Fact]
    public async Task The_real_program_plans_a_create_for_a_table_the_cache_does_not_have_and_says_what_it_cannot_know_of_the_source()
    {
        var real = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_DBDATASYNC");
        Assert.False(string.IsNullOrEmpty(real) && string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_SYNC"), "true", StringComparison.OrdinalIgnoreCase), "dbdatasync is required but DBDATAKIT_TEST_DBDATASYNC is not set.");
        if (string.IsNullOrEmpty(real) || !File.Exists(real)) Assert.Skip("DBDATAKIT_TEST_DBDATASYNC is not set (a build of DbDataSync/src/DbDataSync.Cli)");
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: real);
        var id = await DraftAsync(server);
        var path = "config/replications/orders/table-mappings/orders.yaml";
        var mapping = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/files/{path}")).Body!["content"]!;
        Assert.True((await Put(server, id, path, mapping.Replace("columnMappings: []", "columnMappings:\n- sourceColumn: id\n  targetColumn: id"))).Response.IsSuccessStatusCode);
        Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);

        var planned = await Plan(server, id, """{"replication":"orders"}""");
        Assert.True((bool)planned.Body!["ok"]!, planned.Body.ToJsonString());
        var found = planned.Body["data"]!["mappings"]![0]!;
        Assert.Equal("missing", (string)found["target_plan"]!["state"]!);
        Assert.Contains("CREATE TABLE", (string)found["target_plan"]!["steps"]![0]!["command_text"]!);
        Assert.Equal("unknown", (string)found["source_plan"]!["state"]!);
    }
}
