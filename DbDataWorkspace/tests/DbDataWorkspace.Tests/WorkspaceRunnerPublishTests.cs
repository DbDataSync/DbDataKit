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
    public async Task The_manual_task_of_a_staged_draft_runs_on_the_runner_for_the_person_and_its_run_is_read_back()
    {
        // plan 28c E21, step e: a draft's database setup is applied before the set is published, as the task provision of the version the build staged
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        await Pair(server, "jobs", runner.Url, runner.NewCode(ServerRole.Publisher | ServerRole.Operator));
        var (workspace, build) = await BuildAsync(server);
        var run = $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/run";
        var body = new JsonObject { ["task"] = "provision", ["inputs"] = new JsonObject { ["replication"] = "orders", ["mapping"] = "orders", ["action"] = "create" } };

        // nothing staged yet: said, and nothing reaches the runner
        var early = await server.Send("grace", ServerRole.Approver, HttpMethod.Post, run, body.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, early.Response.StatusCode);
        Assert.Contains("has not been staged on a Runner", (string)early.Body!["error"]!);
        Assert.Empty(runner.Runs);

        Assert.Equal(HttpStatusCode.OK, (await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" })).Response.StatusCode);
        // an author may not run it; a body without a task is refused in words
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, run, body.ToJsonString())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Send("grace", ServerRole.Approver, HttpMethod.Post, run, """{"inputs":{}}""")).Response.StatusCode);

        var started = await server.Send("grace", ServerRole.Approver, HttpMethod.Post, run, body.ToJsonString());
        Assert.Equal(HttpStatusCode.Accepted, started.Response.StatusCode);
        var sent = Assert.Single(runner.Runs);
        // the staged version, the person on whose behalf, the inputs as given
        Assert.Equal(("orders-set/prod/provision", "1", "grace"), ((string)sent["task"]!, (string)sent["version"]!, (string)sent["on_behalf_of"]!));
        Assert.Equal("create", (string)sent["inputs"]!["action"]!);
        var runId = (string)started.Body!["run"]!["run_id"]!;
        Assert.Equal(runId, (string)sent["run_id"]!);
        Assert.Contains(server.Audit(), a => a.Command == "build.run" && a.Outcome == "queued " + runId);

        var read = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/runs/{runId}?step=provision-applied");
        Assert.Equal(HttpStatusCode.OK, read.Response.StatusCode);
        Assert.Equal(("succeeded", "1"), ((string)read.Body!["run"]!["outcome"]!, (string)read.Body["run"]!["version"]!));
        // only the step recorded while this run ran
        var step = Assert.Single(read.Body["run"]!["steps"]!.AsArray());
        Assert.Equal("create-orders", (string)step!["result"]!["steps"]![0]!["id"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/runs/{Guid.NewGuid()}")).Response.StatusCode);
    }

    [Fact]
    public async Task A_replication_of_the_published_set_runs_as_served_from_the_project_page_and_the_version_names_its_workspace()
    {
        // plan 33 SC22l: the project's Runs tab starts a drain (a task with a schedule) of what the channel serves, for the person, with a bulk load as asked; the version the Runner keeps names the workspace it came from
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        await Pair(server, "jobs", runner.Url, runner.NewCode(ServerRole.Publisher | ServerRole.Operator));
        var (workspace, build) = await BuildAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs", ["channel"] = "prod", ["serve"] = true })).Response.StatusCode);
        var note = (string)Assert.Single(runner.Published)["note"]!;
        Assert.Contains($"/{Project}/{workspace}]", note);
        Assert.Contains("[workspace:", note);

        var requests = """[{"kind":"BulkLoad","mapping":"orders","segments":"[{\"mode\":\"auto\",\"column\":\"id\",\"bucketCount\":4}]"}]""";
        var body = new JsonObject { ["task"] = "orders", ["served"] = true, ["inputs"] = new JsonObject { ["requests"] = requests } };
        var started = await server.Send("grace", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/run", body.ToJsonString());

        Assert.Equal(HttpStatusCode.Accepted, started.Response.StatusCode);
        var sent = Assert.Single(runner.Runs);
        // no version: the Runner's Run now of what it serves, for the person, the inputs as given
        Assert.Null(sent["version"]);
        Assert.Equal(("orders-set/prod/orders", "grace", requests), ((string)sent["task"]!, (string)sent["on_behalf_of"]!, (string)sent["inputs"]!["requests"]!));
        Assert.Equal("served", (string)started.Body!["run"]!["version"]!);
        Assert.Contains(server.Audit(), a => a.Command == "build.run" && a.Target!.Contains("as served", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_runner_paired_without_the_operator_role_refuses_the_run_in_its_words()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        await Pair(server, "jobs", runner.Url, runner.NewCode());
        var (workspace, build) = await BuildAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" })).Response.StatusCode);
        var refused = await server.Send("grace", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/run", """{"task":"provision"}""");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Response.StatusCode);
        Assert.Contains("operator role", (string)refused.Body!["error"]!);
        Assert.Contains(server.Audit(), a => a.Command == "build.run" && a.Outcome!.StartsWith("refused:", StringComparison.Ordinal));
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
