using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Lineage;
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

    [Fact]
    public async Task An_approved_contract_runs_only_when_no_published_consumer_reads_what_it_drops_or_a_person_allows_each_by_name()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair",
            new JsonObject { ["url"] = runner.Url.ToString(), ["code"] = runner.NewCode(ServerRole.Publisher | ServerRole.Operator) }.ToJsonString())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair",
            new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        var build = await BuildAsync(server, workspace);
        var builds = $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}";
        Assert.Equal(HttpStatusCode.OK, (await AsGrace(server, HttpMethod.Post, builds + "/publish", new JsonObject { ["target"] = "jobs", ["channel"] = "prod", ["bind"] = new JsonObject { ["storage"] = "mdm" } })).Response.StatusCode);

        // an approved contract is never started through the plain run route: that would skip the check
        var around = await AsGrace(server, HttpMethod.Post, builds + "/run", new JsonObject { ["task"] = "contract", ["package"] = "model-territories", ["inputs"] = new JsonObject { ["approve"] = "abc" } });
        Assert.Equal(HttpStatusCode.Conflict, around.Response.StatusCode);
        Assert.Contains(".../contract", (string)around.Body!["error"]!);

        // the plan: a run of contract without an approval, whose report has the hash and the dropped column as the lineage graph names it
        const string table = "postgres://db/mdm.territories.territory";
        const string column = table + "#note";
        var hash = new string('a', 64);
        var plan = await AsGrace(server, HttpMethod.Post, builds + "/run", new JsonObject { ["task"] = "contract", ["package"] = "model-territories" });
        Assert.Equal(HttpStatusCode.Accepted, plan.Response.StatusCode);
        var planRun = (string)plan.Body!["run"]!["run_id"]!;
        runner.StepResults["contract-report"] = new JsonObject
        {
            ["model"] = "territories", ["schema"] = "territories", ["engine"] = "postgres", ["hash"] = hash, ["dryRun"] = true, ["applied"] = false,
            ["steps"] = new JsonArray(new JsonObject { ["object"] = "territory.note", ["description"] = "drop column territory.note", ["text"] = "ALTER TABLE ...", ["requiresEveryRowToHaveAValue"] = false }),
            ["blocked"] = new JsonArray(), ["notes"] = new JsonArray(),
            ["dropped"] = new JsonArray(new JsonObject { ["description"] = "drop the column 'note' of 'territories.territory'", ["columnId"] = column, ["datasetId"] = table }),
        };
        // a dashboard published in prod reads the column
        var dashboard = new LineageNode("suite://g/dashboard/sales", NodeKind.Consumer, "sales", false, new Dictionary<string, string> { ["consumer"] = "dashboard" });
        gateway.Lineage = new LineageGraph([new(table, NodeKind.Dataset, "territory"), new(column, NodeKind.Column, "note"), dashboard],
            [new(column, dashboard.Id, EdgeKind.Reads, Granularity.Column, Confidence.Declared)]);

        JsonObject Contract(string approve, params string[] allow) => new()
        {
            ["package"] = "model-territories", ["plan_run"] = planRun, ["approve"] = approve, ["allow_consumer"] = new JsonArray([.. allow.Select(a => (JsonNode?)a)]),
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, builds + "/contract", Contract(hash).ToJsonString())).Response.StatusCode);
        var other = await AsGrace(server, HttpMethod.Post, builds + "/contract", Contract(new string('b', 64)));
        Assert.Equal(HttpStatusCode.Conflict, other.Response.StatusCode);
        Assert.Contains("did not read a plan with the hash", (string)other.Body!["error"]!);

        var refused = await AsGrace(server, HttpMethod.Post, builds + "/contract", Contract(hash));
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("the dashboard 'sales' published in channel 'prod'", (string)refused.Body!["error"]!);
        Assert.Contains("allow sales@prod", (string)refused.Body["error"]!);
        Assert.Contains(gateway.Calls, c => c.StartsWith("GET /api/v1/lineage as client:workspace-", StringComparison.Ordinal) && c.EndsWith("with publisher", StringComparison.Ordinal));
        Assert.Single(runner.Runs);   // the plan and nothing more

        // a Gateway that does not answer refuses the step too, until a person allows going on without it
        gateway.LineageStatus = 503;
        var silent = await AsGrace(server, HttpMethod.Post, builds + "/contract", Contract(hash, "sales@prod"));
        Assert.Equal(HttpStatusCode.Conflict, silent.Response.StatusCode);
        Assert.Contains("the paired server 'edge' did not give its lineage", (string)silent.Body!["error"]!);
        gateway.LineageStatus = 200;

        // allowed by name and channel: the approved plan runs on the Runner for the person, and who allowed what is in the audit
        var started = await AsGrace(server, HttpMethod.Post, builds + "/contract", Contract(hash, "sales@prod"));
        Assert.Equal(HttpStatusCode.Accepted, started.Response.StatusCode);
        var sent = runner.Runs.Last();
        Assert.Equal(("model-territories/prod/contract", "grace", hash, "territories"), ((string)sent["task"]!, (string)sent["on_behalf_of"]!, (string)sent["inputs"]!["approve"]!, (string)sent["inputs"]!["schema"]!));
        Assert.Contains(server.Audit(), a => a.Command == "build.contract.allow-consumer" && a.Outcome == "sales@prod");
        Assert.Contains(server.Audit(), a => a.Command == "build.contract" && a.Outcome!.StartsWith("queued ", StringComparison.Ordinal));
    }
}
