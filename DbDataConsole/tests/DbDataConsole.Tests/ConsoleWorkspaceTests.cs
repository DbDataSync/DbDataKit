using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>
/// The console and a Workspace server (plan 22a GP31), against a fake one: the product kind, and the relay's table, route by route. Only the calls a screen makes pass, each with the role it needs, which the relay checks against what the signed-in
/// person holds before it forwards (the server checks again); every other call of the server's API is refused at the relay, whoever signed in, and never reaches the server.
/// </summary>
public sealed class ConsoleWorkspaceTests : IAsyncLifetime
{
    private const string Id = "workspace-1";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<string> reached = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer server = null!;
    private string admin = null!;
    private string viewer = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PairableServer.StartAsync(product: ServerProducts.Workspace, instanceId: Id, map: Map);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = Session(UserRole.Admin);
        viewer = Session(UserRole.Viewer);
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCodeWith(ServerRole.Admin | ServerRole.Author | ServerRole.Approver), "Workspaces", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await server.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    /// <summary>The calls of a Workspace server, each recording that it was reached, with the server's own role rules; and the calls a console must not reach.</summary>
    private void Map(WebApplication workspace, PairingCodes codes, PairedClientsFile clients)
    {
        IResult Needs(HttpContext context, ServerRole role, Func<IResult> then)
        {
            reached.Enqueue($"{context.Request.Method} {context.Request.Path} by {context.ManagementToken().SubjectName}");
            return ManagementAuthorization.Authorize(context.ManagementToken(), role).Allowed ? then() : Results.Json(new { error = $"This needs the {role.ToString().ToLowerInvariant()} role." }, statusCode: 403);
        }

        workspace.MapGet("/api/v1/projects", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { projects = new[] { new { id = "orders", kind = "app", title = "Orders", workspaces = 1 } } })));
        workspace.MapPost("/api/v1/projects", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Json(new { id = "made" }, statusCode: 201)));
        workspace.MapGet("/api/v1/projects/{project}/workspaces", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { workspaces = Array.Empty<object>() })));
        workspace.MapPost("/api/v1/projects/{project}/workspaces", (HttpContext c) => Needs(c, ServerRole.Author, () => Results.Json(new { id = "ada-draft" }, statusCode: 201)));
        workspace.MapDelete("/api/v1/projects/{project}/workspaces/{id}", (HttpContext c) => Needs(c, ServerRole.Author, () => Results.NoContent()));
        workspace.MapGet("/api/v1/projects/{project}/workspaces/{id}/events", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { events = Array.Empty<object>() })));
        // what a console does not call yet (plan 22a GP35 gives them screens), and what is for the publisher
        workspace.MapGet("/api/v1/projects/{project}/workspaces/{id}/changes", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Ok()));
        workspace.MapGet("/api/v1/projects/{project}/workspaces/{id}/history", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Ok()));
        workspace.MapPost("/api/v1/projects/{project}/workspaces/{id}/commit", (HttpContext c) => Needs(c, ServerRole.Author, () => Results.Ok()));
        workspace.MapPost("/api/v1/projects/{project}/workspaces/{id}/push", (HttpContext c) => Needs(c, ServerRole.Author, () => Results.Ok()));
        workspace.MapPost("/api/v1/projects/{project}/workspaces/{id}/merge", (HttpContext c) => Needs(c, ServerRole.Approver, () => Results.Ok()));
        workspace.MapGet("/api/v1/publisher-keys", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        workspace.MapPut("/api/v1/publisher-keys/{name}", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
    }

    private string Session(UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(role + " person", null, role).Id).Id;

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string session, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return http.SendAsync(request);
    }

    private static string Relay(string path) => $"/relay/{Id}/{path}";

    [Fact]
    public async Task A_workspace_server_is_listed_with_its_product_and_may_announce_itself()
    {
        var row = Assert.Single(JsonNode.Parse(await (await Send(HttpMethod.Get, "/api/instances", admin)).Content.ReadAsStringAsync())!.AsArray())!;
        Assert.Equal("dbdataworkspace", (string)row["product"]!);
        Assert.True((bool)row["reachable"]!);

        var pending = app.Services.GetRequiredService<PendingInstances>();
        var announcement = new Announcement(ServerProducts.Workspace, "workspace-2", "Second", 1, null, "https://127.0.0.1:1", "CODE", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Null(pending.Announce(announcement, IPAddress.Loopback));
        Assert.Equal(ServerProducts.Workspace, Assert.Single(pending.All()).Product);
    }

    [Fact]
    public async Task The_screens_calls_are_relayed_for_the_roles_they_need_and_an_administrator_is_named_there()
    {
        Assert.Equal("orders", (string)JsonNode.Parse(await (await Send(HttpMethod.Get, Relay("api/v1/projects"), viewer)).Content.ReadAsStringAsync())!["projects"]![0]!["id"]!);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Relay("api/v1/projects/orders/workspaces"), viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Relay("api/v1/projects/orders/workspaces/ada-draft/events"), viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, Relay("api/v1/projects"), admin, """{"id":"made"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, Relay("api/v1/projects/orders/workspaces"), admin, """{"name":"draft"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, Relay("api/v1/projects/orders/workspaces/ada-draft"), admin)).StatusCode);

        Assert.Contains("POST /api/v1/projects/orders/workspaces by Admin person", reached);
    }

    [Fact]
    public async Task A_viewer_is_refused_at_the_relay_for_what_needs_more_and_the_server_is_not_asked()
    {
        var made = await Send(HttpMethod.Post, Relay("api/v1/projects/orders/workspaces"), viewer, """{"name":"draft"}""");
        Assert.Equal(HttpStatusCode.Forbidden, made.StatusCode);
        Assert.Equal("This needs the author role.", (string)JsonNode.Parse(await made.Content.ReadAsStringAsync())!["error"]!);
        var project = await Send(HttpMethod.Post, Relay("api/v1/projects"), viewer, """{"id":"x"}""");
        Assert.Equal(HttpStatusCode.Forbidden, project.StatusCode);
        Assert.Contains("admin role", await project.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, Relay("api/v1/projects/orders/workspaces/ada-draft"), viewer)).StatusCode);

        Assert.Empty(reached);
    }

    [Fact]
    public async Task What_the_server_lets_the_console_grant_limits_an_administrator_there()
    {
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCodeWith(ServerRole.Viewer), "Workspaces", default);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Relay("api/v1/projects/orders/workspaces"), admin, """{"name":"draft"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Relay("api/v1/projects"), admin)).StatusCode);
        Assert.DoesNotContain(reached, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GET", "api/v1/projects/orders/workspaces/ada-draft/changes")]
    [InlineData("GET", "api/v1/projects/orders/workspaces/ada-draft/history")]
    [InlineData("POST", "api/v1/projects/orders/workspaces/ada-draft/commit")]
    [InlineData("POST", "api/v1/projects/orders/workspaces/ada-draft/push")]
    [InlineData("POST", "api/v1/projects/orders/workspaces/ada-draft/merge")]
    [InlineData("GET", "api/v1/publisher-keys")]
    [InlineData("PUT", "api/v1/publisher-keys/main-group")]
    [InlineData("DELETE", "api/v1/projects/orders")]
    [InlineData("PUT", "api/v1/projects/orders/workspaces")]
    [InlineData("GET", "hubs/echo")]
    public async Task Only_the_calls_in_the_table_are_relayed_even_for_an_administrator(string method, string path)
    {
        var response = await Send(new HttpMethod(method), Relay(path), admin, method is "PUT" or "POST" ? "{}" : null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(reached);   // the server was never asked
    }

    [Fact]
    public void The_table_names_each_route_with_its_method_and_role_and_the_project_it_is_in()
    {
        Assert.Equal(ServerRole.Viewer, ServerProducts.RoleNeeded("dbdataworkspace", "GET", "api/v1/projects"));
        Assert.Equal(ServerRole.Admin, ServerProducts.RoleNeeded("dbdataworkspace", "POST", "api/v1/projects/"));
        Assert.Equal(ServerRole.Author, ServerProducts.RoleNeeded("dbdataworkspace", "post", "api/v1/projects/orders/workspaces?x=1"));
        Assert.Equal(ServerRole.Author, ServerProducts.RoleNeeded("dbdataworkspace", "DELETE", "api/v1/projects/orders/workspaces/ada-draft"));
        Assert.Equal("orders", ServerProducts.ProjectOf("dbdataworkspace", "GET", "api/v1/projects/orders/workspaces/ada-draft/events"));
        Assert.Null(ServerProducts.ProjectOf("dbdataworkspace", "GET", "api/v1/projects"));
        Assert.Null(ServerProducts.RoleNeeded("dbdataworkspace", "POST", "api/v1/projects/orders/workspaces/ada-draft/merge"));
        Assert.Null(ServerProducts.RoleNeeded("dbdataworkspace", "GET", "api/v1/projects/orders/workspaces/a/b/events"));
        Assert.Null(ServerProducts.RoleNeeded("dbdatarunner", "GET", "api/v1/projects"));      // another product's table is its own
        Assert.True(ServerProducts.Relayable("dbdatabuild", "DELETE", "api/v1/projects/x"));   // a product with no table is not limited
        // every route of the table is a call a screen makes: the list is the whole of what passes
        Assert.Equal(6, ServerProducts.WorkspaceRoutes.Count);
        Assert.All(ServerProducts.WorkspaceRoutes, r => Assert.True(ServerProducts.Relayable("dbdataworkspace", r.Method, r.Route.Replace("{p}", "orders").Replace("{id}", "ada-draft"))));
    }
}
