using System.Net;
using System.Text.Json.Nodes;

namespace DbDataWorkspace.Tests;

/// <summary>
/// A build of a Sync, Build or Store project that the kind's check refuses fails with the finding's file and line, so there is nothing for the console to
/// deploy; a sound one names its package. The stand-in programs (<see cref="FakeSync"/>, <see cref="FakeBuild"/>) play <c>dbdatasync</c> and
/// <c>dbdatabuild</c>; a Store model is checked in process.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceKindBuildTests
{
    private static string Fake(string? path, string program)
    {
        if (path is null) Assert.Skip($"the stand-in {program} is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server, string project, string kind)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = project, ["kind"] = kind }.ToJsonString())).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static async Task<JsonNode> BuildAsync(WorkspaceHarness server, string project, string workspace, bool expectSuccess)
    {
        var made = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/workspaces/{workspace}/builds");
        Assert.Equal(HttpStatusCode.Accepted, made.Response.StatusCode);
        var build = (string)made.Body!["id"]!;
        for (var i = 0; i < 300; i++)
        {
            var detail = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{project}/workspaces/{workspace}/builds/{build}")).Body!;
            var status = (string)detail["build"]!["status"]!;
            if (status is "succeeded" or "failed")
            {
                Assert.Equal(expectSuccess ? "succeeded" : "failed", status);
                Assert.Null(detail["publishes"]);   // the server keeps no record of a publish: the console keeps the deploys
                return detail;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("the build did not finish");
    }

    [Fact]
    public async Task A_replication_set_builds_as_its_package_and_a_mapping_with_no_column_cache_fails_the_build_naming_the_file()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake(FakeSync.Create(), "dbdatasync"));
        const string project = "orders-set";
        var workspace = await DraftAsync(server, project, "sync");

        var built = await BuildAsync(server, project, workspace, expectSuccess: true);
        Assert.Equal(("orders-set", "orders"), ((string)built["result"]!["info"]!["package"]!, (string)built["result"]!["info"]!["tasks"]!));

        var path = $"/api/v1/projects/{project}/workspaces/{workspace}/files/config/replications/orders/table-mappings/orders.yaml";
        var mapping = (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!;
        await server.AsAda(HttpMethod.Put, path, new JsonObject { ["content"] = mapping.Replace("sourceColumns:\n- name: id\n  nativeType: bigint\n  isPrimaryKey: true\n  keyOrdinal: 1\n", "sourceColumns: []\n") }.ToJsonString());
        var failed = await BuildAsync(server, project, workspace, expectSuccess: false);
        var finding = failed["result"]!["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.Equal("config/replications/orders/table-mappings/orders.yaml", (string)finding["file"]!);
        Assert.Contains("has not been cached", (string)failed["build"]!["message"]!);
    }

    [Fact]
    public async Task A_plan_with_a_bad_cron_is_refused_by_the_tool_in_its_words_naming_file_and_key_and_a_damaged_project_fails_the_check_first()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake(FakeBuild.Create(), "dbdatabuild"));
        const string project = "orders-set";
        var workspace = await DraftAsync(server, project, "build");
        var plan = $"/api/v1/projects/{project}/workspaces/{workspace}/files/rendered/refresh.plan.yaml";
        Assert.True((await server.AsAda(HttpMethod.Put, plan, """{"content":"cron: bad\n"}""")).Response.IsSuccessStatusCode);

        var detail = await BuildAsync(server, project, workspace, expectSuccess: false);
        Assert.Contains("rendered/refresh.plan.yaml: schedule.cron", (string)detail["build"]!["message"]!);

        Assert.True((await server.AsAda(HttpMethod.Put, plan, """{"content":"cron: \"0 2 * * *\"\n"}""")).Response.IsSuccessStatusCode);
        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/{project}/workspaces/{workspace}/files/dbdatabuild.yml", """{"content":"schema: dbdatabuild.project/2\ngarbage\n"}""")).Response.IsSuccessStatusCode);
        var finding = (await BuildAsync(server, project, workspace, expectSuccess: false))["result"]!["diagnostics"]!.AsArray()[0]!;
        Assert.Equal(("DDB-101", "dbdatabuild.yml", 3), ((string)finding["code"]!, (string)finding["file"]!, (int)finding["line"]!));
    }

    [Fact]
    public async Task A_store_model_that_does_not_check_fails_the_build_with_its_file_and_line()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        const string project = "territories";
        var workspace = await DraftAsync(server, project, "store");
        var path = $"/api/v1/projects/{project}/workspaces/{workspace}/files/models/{project}.yaml";
        var text = (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!;
        Assert.True((await server.AsAda(HttpMethod.Put, path, new JsonObject { ["content"] = text + "garbage: [\n" }.ToJsonString())).Response.IsSuccessStatusCode);

        var finding = (await BuildAsync(server, project, workspace, expectSuccess: false))["result"]!["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.Equal($"models/{project}.yaml", (string)finding["file"]!);
        Assert.True((int)finding["line"]! > 0);
    }
}
