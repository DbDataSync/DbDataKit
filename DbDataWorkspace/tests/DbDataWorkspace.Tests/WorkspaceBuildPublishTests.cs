using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// Publishing a Build project's package to a paired Runner (plan 33 SC20d, plan 28c E07): a build of a Build project is the check (<c>project show metadata</c>) and <c>project publish --to runner --out</c>, run by a stand-in <c>dbdatabuild</c>
/// (<see cref="FakeBuild"/>); the package is then sent as the Sync set is, with the target's publisher key, to a fake Runner. The compiled semantic models go to a Gateway over the data sources it has (SC20e), with no login.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceBuildPublishTests
{
    private const string Project = "orders-set";

    private static string Fake()
    {
        var path = FakeBuild.Create();
        if (path is null) Assert.Skip("the stand-in dbdatabuild is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "build" }.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static async Task<string> BuildAsync(WorkspaceHarness server, string workspace, bool expectSuccess = true)
    {
        var build = (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds")).Body!["id"]!;
        for (var i = 0; i < 300; i++)
        {
            var status = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!["build"]!["status"]!;
            if (status is "succeeded" or "failed") { Assert.Equal(expectSuccess ? "succeeded" : "failed", status); return build; }
            await Task.Delay(100);
        }
        throw new TimeoutException("the build did not finish");
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Publish(WorkspaceHarness server, string workspace, string build, JsonObject body) =>
        server.Send("grace", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}/publish", body.ToJsonString());

    [Fact]
    public async Task The_package_of_a_build_project_is_staged_on_a_runner_channel_and_published_again_it_sends_nothing()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair", new JsonObject { ["url"] = runner.Url.ToString(), ["code"] = runner.NewCode() }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        var first = await BuildAsync(server, workspace);

        var published = await Publish(server, workspace, first, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" });
        Assert.Equal(HttpStatusCode.OK, published.Response.StatusCode);
        Assert.Equal(("orders-set", "stage", 1), ((string)published.Body!["published"]!["package"]!, (string)published.Body["published"]!["mode"]!, (int)published.Body["published"]!["version"]!));
        Assert.Contains("rendered/refresh.plan.yaml", Assert.Single(runner.Published)["artifacts"]!.AsArray().Select(a => (string)a!["path"]!));
        Assert.All(runner.Calls, c => { Assert.Contains("(grace)", c); Assert.Contains("with publisher", c); });

        var second = await BuildAsync(server, workspace);
        var again = (await Publish(server, workspace, second, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" })).Body!["published"]!;
        Assert.Equal((1, false, 0), ((int)again["version"]!, (bool)again["changed"]!, (int)again["uploaded"]!));
        Assert.DoesNotContain("seed", published.Body.ToJsonString() + again.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plan_with_a_bad_cron_is_refused_by_the_tool_in_its_words_naming_file_and_key_and_a_damaged_project_fails_the_check_first()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var workspace = await DraftAsync(server);
        var plan = $"/api/v1/projects/{Project}/workspaces/{workspace}/files/rendered/refresh.plan.yaml";
        Assert.True((await server.AsAda(HttpMethod.Put, plan, """{"content":"cron: bad\n"}""")).Response.IsSuccessStatusCode);

        var build = await BuildAsync(server, workspace, expectSuccess: false);
        var detail = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!;
        Assert.Contains("rendered/refresh.plan.yaml: schedule.cron", (string)detail["build"]!["message"]!);

        Assert.True((await server.AsAda(HttpMethod.Put, plan, """{"content":"cron: \"0 2 * * *\"\n"}""")).Response.IsSuccessStatusCode);
        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/{Project}/workspaces/{workspace}/files/dbdatabuild.yml", """{"content":"schema: dbdatabuild.project/2\ngarbage\n"}""")).Response.IsSuccessStatusCode);
        var damaged = await BuildAsync(server, workspace, expectSuccess: false);
        var finding = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{damaged}")).Body!["result"]!["diagnostics"]!.AsArray()[0]!;
        Assert.Equal(("DDB-101", "dbdatabuild.yml", 3), ((string)finding["code"]!, (string)finding["file"]!, (int)finding["line"]!));
        Assert.Empty(runner.Published);
    }

    [Fact]
    public async Task The_semantic_models_of_a_build_project_are_published_to_a_gateway_over_its_data_sources_with_no_login_and_published_again_unchanged_or_refused_when_a_data_source_is_not_the_gateways()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/{Project}/workspaces/{workspace}/files/semantic/sales.yml", """{"content":"name: sales\n"}""")).Response.IsSuccessStatusCode);
        var build = await BuildAsync(server, workspace);

        gateway.DataSources.Add("warehouse");
        var published = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge" });
        Assert.Equal(HttpStatusCode.OK, published.Response.StatusCode);
        var model = published.Body!["published"]!["models"]![0]!;
        Assert.Equal(("sales", 1, true), ((string)model["model"]!, (int)model["version"]!, (bool)model["created"]!));
        Assert.Equal("warehouse", (string)gateway.SemanticModels["sales"].Sources["default"]!);
        Assert.Contains("name: sales", gateway.SemanticModels["sales"].Catalog);
        Assert.All(gateway.Calls, c => { Assert.Contains("with publisher", c); });
        Assert.DoesNotContain("secret", gateway.Published.Single().ToJsonString(), StringComparison.OrdinalIgnoreCase);   // no login, no reference to one

        var again = (await Publish(server, workspace, await BuildAsync(server, workspace), new JsonObject { ["target"] = "edge" })).Body!["published"]!["models"]![0]!;
        Assert.Equal((1, true), ((int)again["version"]!, (bool)again["unchanged"]!));

        // a Gateway without the data source says so in its words, and nothing is claimed
        gateway.DataSources.Remove("warehouse");
        var refused = await Publish(server, workspace, await BuildAsync(server, workspace), new JsonObject { ["target"] = "edge" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("'warehouse', which is not a data source of this server", (string)refused.Body!["error"]!);
    }

    [Fact]
    public async Task A_build_project_with_no_semantic_model_has_nothing_to_publish_to_a_gateway_and_the_refusal_says_so()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        var build = await BuildAsync(server, workspace);

        var refused = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("no semantic model to publish", (string)refused.Body!["error"]!);
        Assert.Empty(gateway.SemanticModels);
    }
}
