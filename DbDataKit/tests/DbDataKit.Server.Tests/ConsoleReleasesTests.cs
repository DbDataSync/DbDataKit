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
    private string admin = null!;
    private string viewer = null!;

    public async ValueTask InitializeAsync()
    {
        testServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-test", map: test.Map);
        prodServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-prod", map: prod.Map);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = Session(UserRole.Admin);
        viewer = Session(UserRole.Viewer);
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(testServer.Url, testServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Test runner", default);
        await instances.PairAsync(prodServer.Url, prodServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Prod runner", default);
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
}
