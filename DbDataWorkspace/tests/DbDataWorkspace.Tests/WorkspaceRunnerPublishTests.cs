using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// Publishing a replication set as a package to a paired Runner (plan 33 SC22b, plan 28c E08): the Sync kind builds the package (a stand-in <c>dbdatasync</c> writes it, see <see cref="FakeSync"/>) and the Workspace sends it with the target's publisher key through
/// the shared publishing client, to a fake Runner that pairs and checks tokens for real and keeps what it was sent. What the real Sync writes, and what a real Runner does with it, are in Sync's and the Runner's own suites.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceRunnerPublishTests
{
    private const string Project = "orders-set";

    private static string Fake()
    {
        var path = FakeSync.Create();
        if (path is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        return path;
    }

    private static async Task<(string Workspace, string Build)> BuildAsync(WorkspaceHarness server, bool create = true, string? workspace = null, bool expectSuccess = true)
    {
        if (create) Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "sync" }.ToJsonString())).Response.StatusCode);
        workspace ??= (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var made = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds");
        Assert.Equal(HttpStatusCode.Accepted, made.Response.StatusCode);
        var build = (string)made.Body!["id"]!;
        for (var i = 0; i < 300; i++)
        {
            var status = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!["build"]!["status"]!;
            if (status is "succeeded" or "failed") { Assert.Equal(expectSuccess ? "succeeded" : "failed", status); return (workspace, build); }
            await Task.Delay(100);
        }
        throw new TimeoutException("the build did not finish");
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Publish(WorkspaceHarness server, string workspace, string build, JsonObject body) =>
        server.Send("grace", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/publish", body.ToJsonString());

    private static async Task Pair(WorkspaceHarness server, string name, Uri url, string code) =>
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, $"/api/v1/publisher-keys/{name}/pair", new JsonObject { ["url"] = url.ToString(), ["code"] = code }.ToJsonString())).Response.StatusCode);

    [Fact]
    public async Task A_build_of_a_replication_set_is_a_package_and_is_staged_on_a_channel_of_a_runner_by_the_publisher_key()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        await Pair(server, "jobs", runner.Url, runner.NewCode());
        var (workspace, build) = await BuildAsync(server);

        var detail = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!;
        Assert.Equal("orders-set", (string)detail["result"]!["info"]!["package"]!);
        Assert.Equal("orders", (string)detail["result"]!["info"]!["tasks"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/publish", """{"target":"jobs","channel":"prod"}""")).Response.StatusCode);
        var published = await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs", ["channel"] = "prod", ["bind"] = new JsonObject { ["source_db"] = "crm", ["target_db"] = "dwh" } });

        Assert.Equal(HttpStatusCode.OK, published.Response.StatusCode);
        var answer = published.Body!["published"]!;
        Assert.Equal(("orders-set", "prod", "stage", 1, true), ((string)answer["package"]!, (string)answer["channel"]!, (string)answer["mode"]!, (int)answer["version"]!, (bool)answer["changed"]!));
        // the Runner got the files of the draft's configuration, the manifest and the bindings of the channel; it was told to stage, not to serve
        var sent = Assert.Single(runner.Published);
        Assert.Equal("stage", (string)sent["mode"]!);
        Assert.Contains("repo/config/replications/orders/task.yaml", sent["artifacts"]!.AsArray().Select(a => (string)a!["path"]!));
        Assert.Contains("name: orders-set", (string)sent["manifest"]!);
        var bound = Assert.Single(runner.Bindings);
        Assert.Equal("crm", (string)bound["slots"]!["source_db"]!);
        // as the paired client with the publisher role, and the person who published is the name the Runner records
        Assert.All(runner.Calls, c => { Assert.Contains("as client:workspace-", c); Assert.Contains("(grace)", c); Assert.Contains("with publisher", c); });
        // the key is in no answer, record or audit line
        var record = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!.ToJsonString();
        Assert.DoesNotContain("seed", record + published.Body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(server.Audit(), a => a.Command == "build.publish" && a.Outcome == "ok" && a.Target!.EndsWith("-> jobs", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Audit(), a => (a.Outcome ?? "").Contains("secret:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publishing_the_same_set_again_sends_no_file_and_a_changed_mapping_stages_one_new_version_with_one_file()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        await Pair(server, "jobs", runner.Url, runner.NewCode());
        var (workspace, first) = await BuildAsync(server);
        Assert.Equal(1, (int)(await Publish(server, workspace, first, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" })).Body!["published"]!["version"]!);
        var uploaded = runner.UploadsPerPublish.Single();
        Assert.True(uploaded > 0);

        var (_, second) = await BuildAsync(server, create: false, workspace: workspace);
        var again = (await Publish(server, workspace, second, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" })).Body!["published"]!;
        Assert.Equal((1, false, 0), ((int)again["version"]!, (bool)again["changed"]!, (int)again["uploaded"]!));

        var path = $"/api/v1/projects/{Project}/workspaces/{workspace}/files/config/replications/orders/table-mappings/orders.yaml";
        var mapping = (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!;
        Assert.True((await server.AsAda(HttpMethod.Put, path, new JsonObject { ["content"] = mapping.Replace("nativeType: bigint\n  isPrimaryKey: true\n  keyOrdinal: 1\ntargetColumns", "nativeType: bigint\n  isPrimaryKey: true\n  keyOrdinal: 1\n- name: amount\n  nativeType: decimal(14,2)\ntargetColumns") }.ToJsonString())).Response.IsSuccessStatusCode);
        var (_, third) = await BuildAsync(server, create: false, workspace: workspace);
        // a draft with changes not yet committed says so in the note the Runner is sent with
        var changed = (await Publish(server, workspace, third, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" })).Body!["published"]!;
        Assert.Equal((2, true, 1), ((int)changed["version"]!, (bool)changed["changed"]!, (int)changed["uploaded"]!));
    }

    [Fact]
    public async Task A_mapping_with_no_column_cache_fails_the_build_naming_the_file_and_nothing_is_published()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        await Pair(server, "jobs", runner.Url, runner.NewCode());
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "sync" }.ToJsonString())).Response.StatusCode);
        var workspace = (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var path = $"/api/v1/projects/{Project}/workspaces/{workspace}/files/config/replications/orders/table-mappings/orders.yaml";
        var mapping = (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!;
        await server.AsAda(HttpMethod.Put, path, new JsonObject { ["content"] = mapping.Replace("sourceColumns:\n- name: id\n  nativeType: bigint\n  isPrimaryKey: true\n  keyOrdinal: 1\n", "sourceColumns: []\n") }.ToJsonString());

        var (_, build) = await BuildAsync(server, create: false, workspace: workspace, expectSuccess: false);

        var detail = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!;
        var finding = detail["result"]!["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.Equal("config/replications/orders/table-mappings/orders.yaml", (string)finding["file"]!);
        Assert.Contains("has not been cached", (string)detail["build"]!["message"]!);
        var refused = await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("failed", (string)refused.Body!["error"]!);
        Assert.Empty(runner.Published);
    }

    [Fact]
    public async Task A_target_of_the_wrong_product_a_missing_channel_and_the_runners_refusal_are_each_answered_in_words()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        await Pair(server, "jobs", runner.Url, runner.NewCode());
        await Pair(server, "edge", gateway.Url, gateway.NewCode());
        var (workspace, build) = await BuildAsync(server);

        var wrong = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge", ["channel"] = "prod" });
        Assert.Equal(HttpStatusCode.Conflict, wrong.Response.StatusCode);
        Assert.Contains("sync kind of project is published to a runner (package)", (string)wrong.Body!["error"]!);
        Assert.Contains("'edge' is a gateway", (string)wrong.Body["error"]!);

        var noChannel = await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs" });
        Assert.Equal(HttpStatusCode.BadRequest, noChannel.Response.StatusCode);
        Assert.Contains("Name the channel", (string)noChannel.Body!["error"]!);

        runner.RefuseWith = "The package 'orders-set' declares a slot no channel binds.";
        var refused = await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("The runner refused the package orders-set (400): The package 'orders-set' declares a slot no channel binds.", (string)refused.Body!["error"]!);
        var recorded = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!["publishes"]!.AsArray();
        Assert.False((bool)recorded.Last()!["ok"]!);
    }
}
