using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// A Store model deployed from the Workspace with no Store server (plan 33 SC24): a build of a <c>store</c> project makes each model's deploy package (<c>DbDataKit.DataModel.Runner</c>), a publish names the model and stages its package on a
/// channel of a Runner with the slot <c>storage</c> bound there, and the manual task <c>deploy</c> of that staged version runs for the person, a dry run first. What the task does to a database is <c>StoreDeployOnRunnerTests</c> (a real runner); the
/// Runner here is the fake with real pairing that keeps what it was sent.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceStoreDeployTests
{
    private const string Project = "territories";

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "store" }.ToJsonString())).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static async Task<string> BuildAsync(WorkspaceHarness server, string workspace)
    {
        var made = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds");
        Assert.Equal(HttpStatusCode.Accepted, made.Response.StatusCode);
        var build = (string)made.Body!["id"]!;
        for (var i = 0; i < 300; i++)
        {
            var status = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!["build"]!["status"]!;
            if (status is "succeeded" or "failed") { Assert.Equal("succeeded", status); return build; }
            await Task.Delay(100);
        }
        throw new TimeoutException("the build did not finish");
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> AsGrace(WorkspaceHarness server, HttpMethod method, string path, JsonObject? body = null) =>
        server.Send("grace", ServerRole.Approver, method, path, body?.ToJsonString());

    [Fact]
    public async Task A_model_is_staged_as_its_deploy_package_with_its_storage_bound_and_deploy_runs_dry_first_for_the_person()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair",
            new JsonObject { ["url"] = runner.Url.ToString(), ["code"] = runner.NewCode(ServerRole.Publisher | ServerRole.Operator) }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        // a second model in the project: a publish to a Runner then names the model it stages
        var first = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/files/models/{Project}.yaml")).Body!["content"]!;
        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/{Project}/workspaces/{workspace}/files/models/regions.yaml",
            new JsonObject { ["content"] = first.Replace($"name: {Project}", "name: regions") }.ToJsonString())).Response.IsSuccessStatusCode);
        var build = await BuildAsync(server, workspace);
        var builds = $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}";

        var unnamed = await AsGrace(server, HttpMethod.Post, builds + "/publish", new JsonObject { ["target"] = "jobs", ["channel"] = "prod" });
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.Response.StatusCode);
        Assert.Contains("Name the model to stage", (string)unnamed.Body!["error"]!);
        Assert.Contains("regions, territories", (string)unnamed.Body["error"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await AsGrace(server, HttpMethod.Post, builds + "/publish", new JsonObject { ["target"] = "jobs", ["channel"] = "prod", ["model"] = "nope" })).Response.StatusCode);
        Assert.Empty(runner.Published);

        foreach (var model in new[] { "territories", "regions" })
        {
            var staged = await AsGrace(server, HttpMethod.Post, builds + "/publish",
                new JsonObject { ["target"] = "jobs", ["channel"] = "prod", ["model"] = model, ["bind"] = new JsonObject { ["storage"] = "mdm" } });
            Assert.Equal(HttpStatusCode.OK, staged.Response.StatusCode);
            Assert.Equal(("model-" + model, "stage"), ((string)staged.Body!["published"]!["package"]!, (string)staged.Body["published"]!["mode"]!));
        }
        // the package is the model's: the manual task deploy, the dbdatastore runtime, the model in the current format; the slot storage bound on the Runner's channel, not held here
        var sent = runner.Published.First();
        Assert.Equal("stage", (string)sent["mode"]!);
        Assert.Contains("name: model-territories", (string)sent["manifest"]!);
        Assert.Contains("dbdatastore-runtime", (string)sent["manifest"]!);
        Assert.Contains("model/model.yaml", sent["artifacts"]!.AsArray().Select(a => (string)a!["path"]!));
        Assert.All(runner.Bindings, b => Assert.Equal("mdm", (string)b["slots"]!["storage"]!));

        // the deploy of the first model, though the second was staged last: the run names the package; a dry run first
        var started = await AsGrace(server, HttpMethod.Post, builds + "/run",
            new JsonObject { ["task"] = "deploy", ["package"] = "model-territories", ["inputs"] = new JsonObject { ["dry_run"] = "true" } });
        Assert.Equal(HttpStatusCode.Accepted, started.Response.StatusCode);
        var run = Assert.Single(runner.Runs);
        Assert.Equal(("model-territories/prod/deploy", "1", "grace", "true"), ((string)run["task"]!, (string)run["version"]!, (string)run["on_behalf_of"]!, (string)run["inputs"]!["dry_run"]!));
        var runId = (string)started.Body!["run"]!["run_id"]!;
        var read = await server.AsAda(HttpMethod.Get, $"{builds}/runs/{runId}?step=deploy-report&package=model-territories");
        Assert.Equal(HttpStatusCode.OK, read.Response.StatusCode);
        Assert.Equal("succeeded", (string)read.Body!["run"]!["outcome"]!);
        Assert.Single(read.Body["run"]!["steps"]!.AsArray());   // the steps named deploy-report that this run recorded
        // a package the build never staged is not run
        var other = await AsGrace(server, HttpMethod.Post, builds + "/run", new JsonObject { ["task"] = "deploy", ["package"] = "model-nope" });
        Assert.Equal(HttpStatusCode.Conflict, other.Response.StatusCode);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task A_project_with_one_model_stages_it_without_naming_it()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair",
            new JsonObject { ["url"] = runner.Url.ToString(), ["code"] = runner.NewCode() }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        var build = await BuildAsync(server, workspace);
        var staged = await AsGrace(server, HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/publish", new JsonObject { ["target"] = "jobs", ["channel"] = "prod" });
        Assert.Equal(HttpStatusCode.OK, staged.Response.StatusCode);
        Assert.Equal("model-territories", (string)staged.Body!["published"]!["package"]!);
    }
}
