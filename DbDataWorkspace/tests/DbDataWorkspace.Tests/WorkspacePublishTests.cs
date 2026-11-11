using DbDataKit.Authoring.Server;
using System.Net;
using System.Text.Json.Nodes;
using DbDataGateway.Tests.Support;
using DbDataKit.Apps.Bundling;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>
/// Publishing a build to a paired Gateway (plan 22a GP35b2): the publisher key is a reference to a secret kept in the server's own store and read at the moment of publishing; the app goes through the Gateway's apps API as the paired
/// client; a refusal is the Gateway's own words; and every step is audited with the person's name. The Gateway here is a fake with real pairing; the real one is in the Playwright story.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspacePublishTests
{
    private static WorkspaceAppSettings Real() => new(AppStoreSupport.Require(), null, null, NetworkIsolator.IsAvailable ? NetworkIsolation.Required : NetworkIsolation.IfAvailable, 120);

    private static async Task<(string Workspace, string Build)> BuiltAsync(WorkspaceHarness server)
    {
        await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders","title":"Orders"}""");
        var workspace = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var build = (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{workspace}/builds")).Body!["id"]!;
        for (var i = 0; i < 600; i++)
        {
            var status = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}")).Body!["build"]!["status"]!;
            if (status == "succeeded") return (workspace, build);
            Assert.NotEqual("failed", status);
            await Task.Delay(200);
        }
        throw new TimeoutException("the build did not finish");
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Approver(WorkspaceHarness server, string path, string json) =>
        server.Send("grace", ServerRole.Approver, HttpMethod.Post, path, json);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Approver2(WorkspaceHarness server, string path) => server.Send("grace", ServerRole.Approver, HttpMethod.Get, path);

    [Fact]
    public async Task A_key_is_paired_with_a_gateway_kept_in_the_secret_store_and_listed_only_as_a_reference()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();

        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString());

        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        var reference = (string)made.Body!["secret_ref"]!;
        Assert.Equal("secret:dbdata:workspace:publisher-key:edge", reference);
        Assert.StartsWith(gateway.Url.ToString().TrimEnd('/'), (string)made.Body["gateway"]!["url"]!);
        Assert.Contains("publisher", made.Body["gateway"]!["roles"]!.AsArray().Select(r => (string)r!));
        // the key is in the server's secret store (a file of the store), and no call returns it or the signing seed
        var names = new List<string>();
        await foreach (var name in WorkspaceSecrets.Open(server.Directory).ListAsync("")) names.Add(name.ToString());
        Assert.Contains("dbdata:workspace:publisher-key:edge", names);
        var listed = await server.AsAdmin(HttpMethod.Get, "/api/v1/publisher-keys");
        Assert.Equal(["edge"], listed.Body!["keys"]!.AsArray().Select(k => (string)k!["name"]!));
        Assert.DoesNotContain("seed", listed.Body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(reference, server.Store().PublisherKeys().Single().SecretRef);
        Assert.Contains(server.Audit(), a => a.Command == "publisher-key.pair" && a.Target!.StartsWith("edge", StringComparison.Ordinal) && a.Outcome == "ok");
        // whoever publishes sees the targets (name and Gateway, never a reference); an author does not
        var targets = await Approver2(server, "/api/v1/publish-targets");
        Assert.Equal(("edge", true), ((string)targets.Body!["targets"]![0]!["name"]!, (bool)targets.Body["targets"]![0]!["can_publish"]!));
        Assert.DoesNotContain("secret:", targets.Body.ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Get, "/api/v1/publish-targets")).Response.StatusCode);
        // an administrator's: an author is refused, and a code that was used is refused in the Gateway's words
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Get, "/api/v1/publisher-keys")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", "{}")).Response.StatusCode);
        var again = await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/second/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = "AAAAABBBBBCCCCCDDDDD" }.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, again.Response.StatusCode);
        Assert.Single(server.Store().PublisherKeys());
    }

    [Fact]
    public async Task A_key_that_is_pasted_in_place_of_a_reference_is_refused_and_the_reference_of_a_missing_secret_is_named_when_publishing()
    {
        await using var server = await WorkspaceHarness.StartAsync(apps: Real());
        var (workspace, build) = await BuiltAsync(server);

        var pasted = await server.AsAdmin(HttpMethod.Put, "/api/v1/publisher-keys/edge", """{"secret_ref":"workspace-1a2b3c:c2VlZA"}""");
        Assert.Equal(HttpStatusCode.BadRequest, pasted.Response.StatusCode);
        Assert.Empty(server.Store().PublisherKeys());

        var noKey = await Approver(server, $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}/publish", """{"target":"edge"}""");
        Assert.Equal(HttpStatusCode.Conflict, noKey.Response.StatusCode);
        Assert.Contains("no publisher key called 'edge'", (string)noKey.Body!["error"]!);

        await server.AsAdmin(HttpMethod.Put, "/api/v1/publisher-keys/edge", """{"secret_ref":"secret:dbdata:workspace:publisher-key:edge"}""");
        var unpaired = await Approver(server, $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}/publish", """{"target":"edge"}""");
        Assert.Equal(HttpStatusCode.Conflict, unpaired.Response.StatusCode);
        Assert.Contains("has no Gateway paired", (string)unpaired.Body!["error"]!);
    }

    [Fact]
    public async Task A_build_is_sent_to_the_preview_channel_with_a_preview_instance_by_an_author_and_the_link_is_the_gateways_address()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(apps: Real());
        var (workspace, build) = await BuiltAsync(server);
        await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString());
        var path = $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}/preview-data";

        Assert.Equal(HttpStatusCode.BadRequest, (await server.AsAda(HttpMethod.Post, path, """{"target":"edge"}""")).Response.StatusCode);   // names no instance
        var sent = await server.AsAda(HttpMethod.Post, path, """{"target":"edge","instance":"orders"}""");   // an author may: nothing goes to production

        Assert.Equal(HttpStatusCode.OK, sent.Response.StatusCode);
        Assert.Equal("https://apps.fake.test:8818/apps/orders~preview/", (string)sent.Body!["preview"]!["preview_address"]!);
        Assert.Equal("orders", (string)sent.Body["preview"]!["instance"]!);
        Assert.Collection(gateway.Calls,
            c => Assert.StartsWith("POST /api/v1/apps/check as client:workspace-", c),
            c => Assert.StartsWith("PUT /api/v1/apps/orders/channels/preview as client:workspace-", c),
            c => Assert.StartsWith("PUT /api/v1/app-instances/orders/channels/preview as client:workspace-", c));
        Assert.DoesNotContain(gateway.Calls, c => c.StartsWith("PUT /api/v1/apps/orders as ", StringComparison.Ordinal));   // production was not touched
        Assert.Contains("Preview of workspace", (string)gateway.Published.First()["note"]!);
        Assert.Contains(server.Audit(), a => a.Command == "build.preview-data" && a.Outcome == "ok");

        // an instance production does not have is the Gateway's refusal, in its words
        var ghost = await server.AsAda(HttpMethod.Post, path, """{"target":"edge","instance":"ghost"}""");
        Assert.Equal(HttpStatusCode.Conflict, ghost.Response.StatusCode);
        Assert.Contains("no instance 'ghost' in production", (string)ghost.Body!["error"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Send("bob", ServerRole.Viewer, HttpMethod.Post, path, """{"target":"edge","instance":"orders"}""")).Response.StatusCode);
    }

    [Fact]
    public async Task A_build_is_published_as_the_paired_client_with_the_key_read_from_its_reference_and_the_gateway_checks_first()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(apps: Real());
        var (workspace, build) = await BuiltAsync(server);
        await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString());
        var path = $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}/publish";

        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, path, """{"target":"edge"}""")).Response.StatusCode);       // an author does not publish
        Assert.Equal(HttpStatusCode.BadRequest, (await Approver(server, path, "{}")).Response.StatusCode);
        var published = await Approver(server, path, """{"target":"edge","note":"First"}""");

        Assert.Equal(HttpStatusCode.OK, published.Response.StatusCode);
        Assert.Equal(1, (int)published.Body!["published"]!["version"]!);
        Assert.Equal("orders", (string)published.Body["published"]!["app"]!);
        Assert.Equal(["Read the built app.", "Read the publisher key 'edge' from the secret store."], published.Body["steps"]!.AsArray().Select(s => (string)s!).Take(2));
        Assert.Contains(published.Body["steps"]!.AsArray(), s => ((string)s!).StartsWith("Published orders", StringComparison.Ordinal));
        // the Gateway was asked to check, then to publish, as the paired client with the publisher role
        Assert.Collection(gateway.Calls, c => Assert.StartsWith("POST /api/v1/apps/check as client:workspace-", c), c => Assert.StartsWith("PUT /api/v1/apps/orders as client:workspace-", c));
        Assert.All(gateway.Calls, c => Assert.Contains("with publisher", c));
        var sent = Assert.Single(gateway.Published);
        Assert.Contains("name: orders", (string)sent["manifest"]!);
        Assert.Contains("main.js", sent["files"]!.AsObject().Select(f => f.Key));
        Assert.Contains("First", (string)sent["note"]!);
        Assert.Contains($"build {build}", (string)sent["note"]!);
        Assert.Contains("grace", (string)sent["note"]!);

        // recorded against the build and in the audit, with the person
        var detail = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}")).Body!;
        var record = Assert.Single(detail["publishes"]!.AsArray())!;
        Assert.True((bool)record["ok"]!);
        Assert.Equal(("grace", "edge"), ((string)record["by"]!, (string)record["target"]!));
        Assert.Contains(server.Audit(), a => a.Command == "build.publish" && a.Outcome == "ok" && a.Target!.EndsWith("-> edge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_of_the_gateway_comes_back_in_its_words_and_is_recorded_and_a_key_whose_secret_is_gone_says_so()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(apps: Real());
        var (workspace, build) = await BuiltAsync(server);
        await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString());
        var path = $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}/publish";

        gateway.RefuseWith = "The app orders is trusted code and no approval stands for these exact files.";
        var refused = await Approver(server, path, """{"target":"edge"}""");
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("no approval stands for these exact files", (string)refused.Body!["error"]!);
        Assert.Contains("The Gateway refused", (string)refused.Body["error"]!);
        Assert.Empty(gateway.Published);
        var record = Assert.Single((await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}")).Body!["publishes"]!.AsArray())!;
        Assert.False((bool)record["ok"]!);
        Assert.Contains("no approval stands", (string)record["error"]!);
        Assert.Contains(server.Audit(), a => a.Command == "build.publish" && a.Outcome.StartsWith("refused: ", StringComparison.Ordinal));

        // the secret the reference names is gone: the sentence names the reference and what to do
        await WorkspaceSecrets.Open(server.Directory).DeleteAsync(new DbDataKit.Secrets.SecretName("workspace", "publisher-key", "edge"));
        gateway.RefuseWith = null;
        var gone = await Approver(server, path, """{"target":"edge"}""");
        Assert.Equal(HttpStatusCode.Conflict, gone.Response.StatusCode);
        Assert.Contains("secret:dbdata:workspace:publisher-key:edge", (string)gone.Body!["error"]!);
        Assert.Contains("not in this server's secret store", (string)gone.Body["error"]!);
        Assert.Empty(gateway.Published);
    }

    [Fact]
    public async Task A_pairing_that_may_not_publish_is_refused_before_anything_is_sent()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(apps: Real());
        var (workspace, build) = await BuiltAsync(server);
        await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/viewing/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode(ServerRole.Viewer) }.ToJsonString());

        var refused = await Approver(server, $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}/publish", """{"target":"viewing"}""");

        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("needs the publisher role", (string)refused.Body!["error"]!);
        Assert.Empty(gateway.Calls);
    }
}
