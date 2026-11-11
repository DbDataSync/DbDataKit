using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;

namespace DbDataWorkspace.Tests;

/// <summary>Publish targets by product (plan 33 SC20b): a target is a Gateway or a Runner, known from the server's own /version, and a project's kind publishes only to the products it lists.</summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceTargetProductTests
{
    private static string Pair(FakeGateway to, string? product = null) =>
        new JsonObject { ["url"] = to.Url.ToString(), ["code"] = to.NewCode(), ["product"] = product }.ToJsonString();

    [Fact]
    public async Task A_target_knows_whether_it_is_a_gateway_or_a_runner_from_the_server_itself()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var runner = await FakeGateway.StartAsync("dbdatarunner");
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", Pair(gateway))).Response.StatusCode);
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair", Pair(runner));
        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        Assert.Equal("runner", (string)made.Body!["product"]!);

        var keys = (await server.AsAdmin(HttpMethod.Get, "/api/v1/publisher-keys")).Body!["keys"]!.AsArray();
        Assert.Equal("gateway", (string)keys.Single(k => (string)k!["name"]! == "edge")!["product"]!);
        Assert.Equal("runner", (string)keys.Single(k => (string)k!["name"]! == "jobs")!["product"]!);
    }

    [Fact]
    public async Task Naming_the_wrong_product_at_pairing_is_refused_and_nothing_is_kept()
    {
        await using var runner = await FakeGateway.StartAsync("dbdatarunner");
        await using var server = await WorkspaceHarness.StartAsync();
        var refused = await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair", Pair(runner, "gateway"));
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("is a runner, not a gateway", (string)refused.Body!["error"]!);
        Assert.Empty((await server.AsAdmin(HttpMethod.Get, "/api/v1/publisher-keys")).Body!["keys"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/x/pair", Pair(runner, "toaster"))).Response.StatusCode);
    }

    [Fact]
    public async Task The_targets_say_whether_a_project_of_a_kind_goes_there()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var runner = await FakeGateway.StartAsync("dbdatarunner");
        await using var server = await WorkspaceHarness.StartAsync();
        await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", Pair(gateway));
        await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair", Pair(runner));
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders"}""")).Response.StatusCode);

        var targets = (await server.Send("boss", DbDataKit.Web.Trust.ServerRole.Approver, HttpMethod.Get, "/api/v1/publish-targets?project=orders")).Body!["targets"]!.AsArray();
        Assert.True((bool)targets.Single(t => (string)t!["name"]! == "edge")!["accepts"]!);
        Assert.False((bool)targets.Single(t => (string)t!["name"]! == "jobs")!["accepts"]!);
        var plain = (await server.Send("boss", DbDataKit.Web.Trust.ServerRole.Approver, HttpMethod.Get, "/api/v1/publish-targets")).Body!["targets"]!.AsArray();
        Assert.Null(plain[0]!["accepts"]);
    }

    [Fact]
    public async Task A_kind_refuses_a_target_of_a_product_it_does_not_publish_to_and_names_both()
    {
        var app = new AppProjectKind();
        var runner = new PublishTarget("jobs", "https://runner:8495", "r1", "ff", ["publisher"], "ws", DateTimeOffset.UtcNow, PublishProducts.Runner);
        var gateway = runner with { Name = "edge", Product = PublishProducts.Gateway };
        Assert.Equal("app", WorkspacePublishing.RouteFor(app, gateway).What);
        var refused = Assert.Throws<WorkspacePublishException>(() => WorkspacePublishing.RouteFor(app, runner));
        Assert.Equal(409, refused.Status);
        Assert.Contains("kind of project is published to a gateway (app)", refused.Message);
        Assert.Contains("'jobs' is a runner", refused.Message);
    }

    [Fact]
    public void A_targets_file_from_before_products_reads_as_a_gateway()
    {
        var json = """{"Name":"edge","Url":"https://g:8816","InstanceId":"i","Fingerprint":"f","Roles":["publisher"],"ClientName":"c","PairedAt":"2026-01-01T00:00:00Z"}""";
        var target = System.Text.Json.JsonSerializer.Deserialize<PublishTarget>(json)!;
        Assert.Equal(PublishProducts.Gateway, target.Product);
    }
}
