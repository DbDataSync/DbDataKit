using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// Publishing a Store project's models to a paired Gateway (plan 33 SC21b): the models go in the current format through the Gateway's entity-model route as the paired client, on a data source the Gateway already has (the model's storage connection
/// unless the publish names another); the Workspace holds no connection and creates no table. The Gateway here is a fake with real pairing and the route's rules (a data source of its own, the version a publish is made over).
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceStorePublishTests
{
    private const string Project = "territories";

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "store" }.ToJsonString())).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static async Task<string> BuildAsync(WorkspaceHarness server, string workspace, bool expectSuccess = true)
    {
        var made = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds");
        Assert.Equal(HttpStatusCode.Accepted, made.Response.StatusCode);
        var build = (string)made.Body!["id"]!;
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

    private static async Task Pair(WorkspaceHarness server, FakeGateway gateway) =>
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString())).Response.StatusCode);

    [Fact]
    public async Task A_model_is_published_in_the_current_format_on_the_data_source_its_storage_names_and_published_again_unchanged_or_changed_over_the_version_the_gateway_has()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        var workspace = await DraftAsync(server);
        // a file of the previous format is published as the current one, and the draft's file stays as it was written
        var path = $"/api/v1/projects/{Project}/workspaces/{workspace}/files/models/{Project}.yaml";
        var text = (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!;
        Assert.True((await server.AsAda(HttpMethod.Put, path, new JsonObject { ["content"] = text.Replace("schemaVersion: 2", "schemaVersion: 1") }.ToJsonString())).Response.IsSuccessStatusCode);
        var first = await BuildAsync(server, workspace);

        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{first}/publish", """{"target":"edge"}""")).Response.StatusCode);
        var published = await Publish(server, workspace, first, new JsonObject { ["target"] = "edge" });
        Assert.Equal(HttpStatusCode.OK, published.Response.StatusCode);
        var model = published.Body!["published"]!["models"]![0]!;
        Assert.Equal((Project, "mdm", 1, true), ((string)model["model"]!, (string)model["data_source"]!, (int)model["version"]!, (bool)model["created"]!));
        Assert.StartsWith("schemaVersion: 2", gateway.Models[Project].Text);
        Assert.StartsWith("schemaVersion: 1", (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!);
        // through the shared publish API (plan 33 SW01): what the Gateway holds is read first; the data source the model names is not one this client published, so it is a slot, which the Gateway refuses until it is bound and the client,
        // which made it, binds itself before it publishes again
        Assert.Contains(gateway.Calls, c => c.StartsWith("GET /api/v1/definitions/data-source as client:workspace-", StringComparison.Ordinal));
        Assert.Collection(gateway.Calls.Where(c => c.Contains("entity-model") && !c.Contains("/trust/")),
            c => Assert.StartsWith("GET /api/v1/definitions/entity-model/territories as client:workspace-", c), c => Assert.StartsWith("PUT /api/v1/definitions/entity-model/territories as client:workspace-", c),
            c => Assert.StartsWith("PUT /api/v1/bindings/prod/entity-model/territories as client:workspace-", c), c => Assert.StartsWith("PUT /api/v1/definitions/entity-model/territories as client:workspace-", c));
        Assert.DoesNotContain(gateway.Calls, c => c.Contains("/api/v1/entity-models/", StringComparison.Ordinal));
        var sent = (string)gateway.Published.Single()["manifest"]!;
        Assert.Contains("\"managedBy\":\"workspace-", sent);   // the manifest is signed as sent, so it says whose it is
        Assert.Contains("\"slots\":[\"mdm\"]", sent);
        Assert.All(gateway.Calls, c => Assert.Contains("with publisher", c));

        // the same model again leaves the Gateway's version where it was; a change goes over the version it has
        var again = (await Publish(server, workspace, await BuildAsync(server, workspace), new JsonObject { ["target"] = "edge" })).Body!["published"]!["models"]![0]!;
        Assert.Equal((1, true), ((int)again["version"]!, (bool)again["unchanged"]!));
        var current = (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!;
        Assert.True((await server.AsAda(HttpMethod.Put, path, new JsonObject { ["content"] = current.TrimEnd() + "\n      note: {type: VARCHAR(200)}\n" }.ToJsonString())).Response.IsSuccessStatusCode);
        var changed = (await Publish(server, workspace, await BuildAsync(server, workspace), new JsonObject { ["target"] = "edge" })).Body!["published"]!["models"]![0]!;
        Assert.Equal((2, false), ((int)changed["version"]!, (bool)changed["unchanged"]!));
        Assert.DoesNotContain("seed", published.Body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_model_goes_on_a_hand_made_data_source_only_with_the_confirmation_the_refusal_names_the_data_source_and_who_made_it_and_the_gateway_records_the_adoption()
    {
        await using var gateway = await FakeGateway.StartAsync();
        gateway.HandMade.Add("mdm");
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        var workspace = await DraftAsync(server);
        var build = await BuildAsync(server, workspace);

        // refused with what a screen needs to ask: the data source and who made it
        var refused = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("adopt", (string)refused.Body!["error"]!);
        Assert.Equal(("mdm", "hand"), ((string)refused.Body["adopt"]!["data_source"]!, (string)refused.Body["adopt"]!["made_by"]!));
        Assert.Empty(gateway.Models);

        // a confirmation that names another data source is no confirmation, here and not only at the Gateway
        var wrong = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge", ["adopt"] = "elsewhere" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Response.StatusCode);
        Assert.Contains("confirm the data source it goes on", (string)wrong.Body!["error"]!);
        Assert.Empty(gateway.Models);

        var adopted = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge", ["adopt"] = "mdm" });
        Assert.Equal(HttpStatusCode.OK, adopted.Response.StatusCode);
        Assert.Equal("mdm", (string)adopted.Body!["published"]!["models"]![0]!["adopted"]!);
        Assert.Contains($"mdm for {Project} by ", Assert.Single(gateway.Adoptions));
        // the Workspace keeps the confirmation with the publish
        var recorded = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!["publishes"]!.ToJsonString();
        Assert.Contains("\"adopted\":\"mdm\"", recorded);
    }

    [Fact]
    public async Task A_model_goes_on_a_hand_made_data_source_the_group_bound_as_a_slot_with_no_confirmation_and_the_refusal_without_the_binding_says_who_binds_it()
    {
        await using var gateway = await FakeGateway.StartAsync();
        gateway.HandMade.Add("mdm");
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        var workspace = await DraftAsync(server);
        var build = await BuildAsync(server, workspace);

        // not bound: the Workspace tried to bind it itself (the Gateway lets a publishing client bind only what it made) and says what an administrator does
        var refused = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("An administrator of the Gateway binds it once (PUT /api/v1/bindings/prod/entity-model/territories", (string)refused.Body!["error"]!);
        Assert.Empty(gateway.Models);

        // an administrator binds the slot, once: the same publish needs no confirmation and the Workspace binds nothing
        gateway.Bindings.Clear();
        gateway.Bindings[$"entity-model/{Project}/mdm"] = "an administrator";
        gateway.Calls.Clear();
        var published = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge" });
        Assert.Equal(HttpStatusCode.OK, published.Response.StatusCode);
        Assert.Equal("mdm", (string)published.Body!["published"]!["models"]![0]!["slots_used"]![0]!);
        Assert.Contains($"mdm for {Project} by ", Assert.Single(gateway.SlotUses));
        Assert.Empty(gateway.Adoptions);
        Assert.DoesNotContain(gateway.Calls, c => c.Contains("/api/v1/bindings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_data_source_the_gateway_does_not_have_and_a_gateways_refusal_come_back_in_its_words_and_a_runner_is_not_a_target()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair", new JsonObject { ["url"] = runner.Url.ToString(), ["code"] = runner.NewCode() }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        var build = await BuildAsync(server, workspace);

        var missing = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge", ["data_source"] = "warehouse" });
        Assert.Equal(HttpStatusCode.Conflict, missing.Response.StatusCode);
        Assert.Contains("The Gateway refused the model territories on the data source 'warehouse' (400): 'warehouse' is not a data source of this server", (string)missing.Body!["error"]!);
        gateway.DataSources.Add("warehouse");
        Assert.Equal("warehouse", (string)(await Publish(server, workspace, build, new JsonObject { ["target"] = "edge", ["data_source"] = "warehouse" })).Body!["published"]!["models"]![0]!["data_source"]!);

        gateway.RefuseWith = "The model does not load, so nothing was published.";
        var refused = await Publish(server, workspace, build, new JsonObject { ["target"] = "edge" });
        Assert.Contains("(400): The model does not load, so nothing was published.", (string)refused.Body!["error"]!);

        var wrong = await Publish(server, workspace, build, new JsonObject { ["target"] = "jobs", ["channel"] = "prod" });
        Assert.Equal(HttpStatusCode.Conflict, wrong.Response.StatusCode);
        Assert.Contains("store kind of project is published to a gateway (model)", (string)wrong.Body!["error"]!);
    }

    [Fact]
    public async Task A_model_that_does_not_check_fails_the_build_with_its_file_and_line_and_nothing_is_published()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        var workspace = await DraftAsync(server);
        var path = $"/api/v1/projects/{Project}/workspaces/{workspace}/files/models/{Project}.yaml";
        var text = (string)(await server.AsAda(HttpMethod.Get, path)).Body!["content"]!;
        Assert.True((await server.AsAda(HttpMethod.Put, path, new JsonObject { ["content"] = text + "garbage: [\n" }.ToJsonString())).Response.IsSuccessStatusCode);

        var build = await BuildAsync(server, workspace, expectSuccess: false);
        var finding = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!["result"]!["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.Equal($"models/{Project}.yaml", (string)finding["file"]!);
        Assert.True((int)finding["line"]! > 0);
        Assert.Equal(HttpStatusCode.Conflict, (await Publish(server, workspace, build, new JsonObject { ["target"] = "edge" })).Response.StatusCode);
        Assert.Empty(gateway.Published);
    }
}
