using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>Plan 21 CH02: releases hosted by the console. Items promote a package from a source runner to a target runner as the person who starts the release; the approval needs the administrator role on every item's target server.</summary>
public sealed class ConsoleReleasesTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeRunnerGroup test = new();
    private readonly FakeRunnerGroup prod = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer testServer = null!;
    private PairableServer prodServer = null!;
    private readonly FakeRunnerGroup gwTest = new();
    private readonly FakeRunnerGroup gwProd = new();
    private PairableServer gwTestServer = null!;
    private PairableServer gwProdServer = null!;
    private string admin = null!;
    private string viewer = null!;

    public async ValueTask InitializeAsync()
    {
        testServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-test", map: test.Map);
        prodServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-prod", map: prod.Map);
        gwTestServer = await PairableServer.StartAsync(product: ServerProducts.Gateway, instanceId: "gateway-test", map: gwTest.Map);
        gwProdServer = await PairableServer.StartAsync(product: ServerProducts.Gateway, instanceId: "gateway-prod", map: gwProd.Map);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = Session(UserRole.Admin);
        viewer = Session(UserRole.Viewer);
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(testServer.Url, testServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Test runner", default);
        await instances.PairAsync(prodServer.Url, prodServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Prod runner", default);
        await instances.PairAsync(gwTestServer.Url, gwTestServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Test gateway", default);
        await instances.PairAsync(gwProdServer.Url, gwProdServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Prod gateway", default);
        gwTest.Versions[3] = ("{\"title\":\"Sales\"}", [("page.json", [9, 9])], null);
        gwTest.Versions[7] = ("{\"catalog\":\"x\"}", [], null);
        test.Versions[4] = ("manifest-v4", [("app.dll", [1, 2, 3])], null);
        test.Versions[5] = ("manifest-v5", [], null);
        test.Active["test"] = 5;
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await testServer.DisposeAsync();
        await prodServer.DisposeAsync();
        await gwTestServer.DisposeAsync();
        await gwProdServer.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Session(UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(role + " person", null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private static object Item(string name, int version, string toChannel = "prod") => new { target = "runner-prod", source = "runner-test", name, version, fromChannel = "test", toChannel };

    private async Task<string> Draft(params object[] items)
    {
        var created = await Call(HttpMethod.Post, "/api/releases", admin, new { name = "november", items });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        return (string)created.Body!["id"]!;
    }

    [Fact]
    public async Task A_release_is_approved_once_runs_its_items_as_the_person_stops_at_a_refusal_naming_the_item_and_resumes_there()
    {
        var id = await Draft(Item("orders", 4), Item("orders", 5));
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/releases/{id}/approve", viewer)).Status);
        var notApproved = await Call(HttpMethod.Post, $"/api/releases/{id}/start", admin);
        Assert.Equal(HttpStatusCode.Conflict, notApproved.Status);
        Assert.Contains("draft", (string)notApproved.Body!["error"]!);

        var approved = await Call(HttpMethod.Post, $"/api/releases/{id}/approve", admin);
        Assert.Equal("approved", (string)approved.Body!["state"]!);
        Assert.Equal("Admin person", (string)approved.Body["approvedByName"]!);

        // the target refuses the second item (an unbound slot): the release stops there and says which item and why
        prod.Refuse = "The slot 'source' of orders is not bound in prod.";
        var stopped = await Call(HttpMethod.Post, $"/api/releases/{id}/start", admin);
        Assert.Equal("stopped", (string)stopped.Body!["state"]!);
        Assert.Equal(1, (int)stopped.Body["stoppedAt"]!);
        Assert.Contains("The slot 'source' of orders is not bound in prod.", (string)stopped.Body["stoppedReason"]!);
        Assert.Equal("failed", (string)stopped.Body["items"]![0]!["state"]!);

        prod.Refuse = null;
        var done = await Call(HttpMethod.Post, $"/api/releases/{id}/resume", admin);
        Assert.Equal("succeeded", (string)done.Body!["state"]!);
        Assert.All(done.Body["items"]!.AsArray(), i => Assert.Equal("done", (string)i!["state"]!));
        Assert.Equal("manifest-v5", prod.Published["prod"].Manifest);
        Assert.All(test.Calls.Concat(prod.Calls), c => Assert.EndsWith("by Admin person", c));   // the person's own calls, not the console's

        var list = await Call(HttpMethod.Get, "/api/releases", viewer);
        Assert.Single(list.Body!.AsArray());
    }

    [Fact]
    public async Task An_approver_who_lacks_the_administrator_role_on_a_target_is_refused_naming_the_item_and_a_release_is_abandoned_only_before_it_ends()
    {
        var id = await Draft(Item("orders", 4));
        // the console may grant the target nothing but reading: nobody holds the administrator role there through it
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(prodServer.Url, prodServer.NewCodeWith(ServerRole.Viewer), "Prod runner", default);
        var refused = await Call(HttpMethod.Post, $"/api/releases/{id}/approve", admin);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("does not hold the approval permission for item 1 (runner runner-prod: integration 'orders' version 4, test to prod)", (string)refused.Body!["error"]!);

        var abandoned = await Call(HttpMethod.Post, $"/api/releases/{id}/abandon", admin);
        Assert.Equal("abandoned", (string)abandoned.Body!["state"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/releases/{id}/abandon", admin)).Status);
    }

    [Fact]
    public async Task A_draft_names_real_servers_and_items_the_console_can_run()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/api/releases", admin, new { name = "x", items = new[] { new { target = "nowhere", name = "orders", version = 1, fromChannel = "test", toChannel = "prod" } } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/api/releases", admin, new { name = "x", items = new[] { new { product = "gateway", target = "runner-prod", name = "orders", version = 1, fromChannel = "test", toChannel = "prod" } } })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, "/api/releases", admin, new { name = "x", items = Array.Empty<object>() })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, "/api/releases/none", admin)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/releases", viewer, new { name = "x", items = new[] { Item("orders", 4) } })).Status);
    }

    private static object GatewayItem(string kind, string name, int version, string toChannel = "prod") =>
        new { product = "gateway", kind, target = "gateway-prod", source = "gateway-test", name, version, fromChannel = "test", toChannel };

    [Fact]
    public async Task A_release_of_gateway_definitions_runs_as_the_person_and_each_item_names_its_kind_to_both_gateways()
    {
        var created = await Call(HttpMethod.Post, "/api/releases", admin, new { name = "gateway november", items = new[] { GatewayItem("data-source", "warehouse", 7), GatewayItem("dashboard", "sales", 3) } });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = (string)created.Body!["id"]!;
        Assert.Equal("gateway", (string)created.Body["items"]![0]!["product"]!);
        Assert.Equal("dashboard", (string)created.Body["items"]![1]!["kind"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/releases/{id}/approve", viewer)).Status);
        Assert.Equal("approved", (string)(await Call(HttpMethod.Post, $"/api/releases/{id}/approve", admin)).Body!["state"]!);
        var done = await Call(HttpMethod.Post, $"/api/releases/{id}/start", admin);
        Assert.Equal("succeeded", (string)done.Body!["state"]!);

        Assert.Equal("{\"title\":\"Sales\"}", gwProd.Published["prod"].Manifest);   // the last item; the first went before it
        Assert.Contains("data-source", gwTest.Kinds);
        Assert.Contains("dashboard", gwProd.Kinds);
        Assert.Contains("data-source", gwProd.Kinds);
        Assert.All(gwTest.Calls.Concat(gwProd.Calls), c => Assert.EndsWith("by Admin person", c));   // the person's own calls, not the console's
        Assert.Contains(gwProd.Calls, c => c.StartsWith("PUT definitions/dashboard/sales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_gateway_item_needs_the_administrator_role_on_its_target_gateway_to_be_approved_and_a_gateway_that_refuses_stops_the_release_at_its_item()
    {
        var id = (string)(await Call(HttpMethod.Post, "/api/releases", admin, new { name = "gw", items = new[] { GatewayItem("report", "monthly", 7) } })).Body!["id"]!;
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(gwProdServer.Url, gwProdServer.NewCodeWith(ServerRole.Viewer), "Prod gateway", default);
        var refused = await Call(HttpMethod.Post, $"/api/releases/{id}/approve", admin);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("does not hold the approval permission for item 1 (gateway gateway-prod: report 'monthly' version 7, test to prod)", (string)refused.Body!["error"]!);

        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(gwProdServer.Url, gwProdServer.NewCodeWith(ServerRole.Admin), "Prod gateway", default);
        Assert.Equal("approved", (string)(await Call(HttpMethod.Post, $"/api/releases/{id}/approve", admin)).Body!["state"]!);
        gwProd.Refuse = "The report 'monthly' reads a semantic model that prod does not serve.";
        var stopped = await Call(HttpMethod.Post, $"/api/releases/{id}/start", admin);
        Assert.Equal("stopped", (string)stopped.Body!["state"]!);
        Assert.Contains("does not serve", (string)stopped.Body["stoppedReason"]!);
    }

    [Fact]
    public async Task A_gateway_item_names_a_gateway_kind_and_a_gateway_server_and_a_runner_item_a_runner()
    {
        async Task<HttpStatusCode> Make(object item) => (await Call(HttpMethod.Post, "/api/releases", admin, new { name = "x", items = new[] { item } })).Status;
        Assert.Equal(HttpStatusCode.Created, await Make(GatewayItem("app", "orders-app", 1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Make(GatewayItem("integration", "orders", 1)));                                    // a runner's kind
        Assert.Equal(HttpStatusCode.BadRequest, await Make(GatewayItem("app-instance", "orders-app", 1)));                              // an instance belongs to one group
        Assert.Equal(HttpStatusCode.BadRequest, await Make(new { product = "gateway", kind = "dashboard", target = "runner-prod", name = "sales", version = 1, fromChannel = "test", toChannel = "prod" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Make(new { product = "gateway", kind = "dashboard", target = ConsoleRunnerSettings.ServerId, name = "sales", version = 1, fromChannel = "test", toChannel = "prod" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Make(new { target = "gateway-prod", name = "orders", version = 1, fromChannel = "test", toChannel = "prod" }));   // a runner item to a Gateway
        Assert.Equal(HttpStatusCode.BadRequest, await Make(new { product = "gateway", kind = "dashboard", target = "gateway-prod", name = "sales", version = 0, fromChannel = "test", toChannel = "prod" }));
    }

    [Fact]
    public async Task The_relay_checks_a_persons_role_for_a_gateways_publish_calls_before_it_forwards_them()
    {
        var before = gwProd.Calls.Count;
        // a viewer of the console reads a Gateway's definitions and publishes nothing; the Gateway is not asked for the write
        using (var read = new HttpRequestMessage(HttpMethod.Get, "/relay/gateway-prod/api/v1/definitions/dashboard/sales"))
        {
            read.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={viewer}");
            Assert.NotEqual(HttpStatusCode.Forbidden, (await http.SendAsync(read)).StatusCode);
        }
        Assert.Equal(before + 1, gwProd.Calls.Count);
        using var write = new HttpRequestMessage(HttpMethod.Put, "/relay/gateway-prod/api/v1/definitions/dashboard/sales") { Content = JsonContent.Create(new { manifest = "{}" }) };
        write.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={viewer}");
        var refused = await http.SendAsync(write);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("admin", await refused.Content.ReadAsStringAsync());
        Assert.Equal(before + 1, gwProd.Calls.Count);
    }

    [Fact]
    public void Every_gateway_publish_route_matches_its_own_sample_and_no_other_row_with_the_same_method()
    {
        foreach (var route in ServerProducts.GatewayRoutes)
        {
            var sample = route.Pattern.Replace("{name}", "sample").Replace("{sha}", new string('a', 64));
            Assert.Same(route, ServerProducts.GatewayRouteOf(route.Method, sample));
        }
        Assert.Null(ServerProducts.GatewayRouteOf("GET", "api/v1/config"));
    }
}
