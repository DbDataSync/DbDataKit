using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
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
/// The console and a runner (plan 28a R19), against a fake runner: the product kind, the roles a signed-in person holds there (the runner's own check decides, from the token the relay signs), and the relay's allowlist, which passes the documented management
/// calls and nothing else of a runner's API (not the publisher's uploads and publishes, not a webhook, not a setting).
/// </summary>
public sealed class ConsoleRunnerTests : IAsyncLifetime
{
    private const string Id = "runner-1";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<string> reached = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer runner = null!;
    private string admin = null!;
    private string viewer = null!;

    public async ValueTask InitializeAsync()
    {
        runner = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: Id, map: Map);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = Session(UserRole.Admin);
        viewer = Session(UserRole.Viewer);
        // the runner lets this console grant what its administrators hold (what `server pair --max-role viewer,operator,admin` makes)
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(runner.Url, runner.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Runner", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await runner.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    /// <summary>The management calls of a runner as the screens use them, with its role rules, and the calls a console must never reach (they record that they were reached).</summary>
    private void Map(WebApplication server, PairingCodes codes, PairedClientsFile clients)
    {
        IResult Needs(HttpContext context, ServerRole role, Func<IResult> then)
        {
            reached.Enqueue($"{context.Request.Method} {context.Request.Path} as {string.Join('+', ServerRoles.Names(context.ManagementToken().Roles))} by {context.ManagementToken().SubjectName}");
            return ManagementAuthorization.Authorize(context.ManagementToken(), role).Allowed ? then() : Results.Json(new { error = $"This needs the {role.ToString().ToLowerInvariant()} role." }, statusCode: 403);
        }

        server.MapGet("/api/v1/status", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { product = "dbdatarunner", group = "main", tasks = new[] { new { package = "sample", channel = "dev", task = "ok", paused = false } } })));
        server.MapGet("/api/v1/instances", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { all = c.Request.Query["all"].ToString(), instances = Array.Empty<object>() })));
        server.MapGet("/api/v1/runs", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { query = c.Request.QueryString.Value, runs = Array.Empty<object>() })));
        server.MapGet("/api/v1/definitions/integration", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { kind = "integration", definitions = Array.Empty<object>() })));
        server.MapGet("/api/v1/definitions/integration/{name}", (HttpContext c, string name) => Needs(c, ServerRole.Viewer, () => Results.Json(new { name })));
        server.MapPost("/api/v1/tasks/{package}/{channel}/{task}/{act}", (HttpContext c, string act) => Needs(c, ServerRole.Operator, () => Results.Json(new { act }, statusCode: act == "run" ? 202 : 200)));
        server.MapPost("/api/v1/definitions/integration/{name}/{act}", async (HttpContext c, string act) =>
        {
            var body = await new StreamReader(c.Request.Body).ReadToEndAsync();
            return Needs(c, ServerRole.Admin, () => Results.Json(new { act, body }));
        });
        // what a publisher or the serving layer calls: a console never reaches these, whoever is signed in
        server.MapPut("/api/v1/artifacts/{sha}", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        server.MapPut("/api/v1/definitions/integration/{name}", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        server.MapPut("/api/v1/bindings/{channel}/integration/{name}", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        server.MapPut("/api/v1/settings/require_signatures", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        server.MapPost("/api/v1/webhooks/{package}/{channel}/{source}", (HttpContext c) => Needs(c, ServerRole.Operator, () => Results.Ok()));
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
    public async Task A_runner_is_listed_with_its_product_and_its_health_like_any_server()
    {
        var list = JsonNode.Parse(await (await Send(HttpMethod.Get, "/api/instances", admin)).Content.ReadAsStringAsync())!.AsArray();
        var row = Assert.Single(list)!;
        Assert.Equal("dbdatarunner", (string)row["product"]!);
        Assert.Equal("Runner", (string)row["name"]!);
        Assert.True((bool)row["reachable"]!);
        Assert.True((bool)row["ready"]!);
    }

    [Fact]
    public async Task A_runner_may_announce_itself_and_is_confirmed_as_any_server_is()
    {
        var pending = app.Services.GetRequiredService<PendingInstances>();
        var announcement = new Announcement(ServerProducts.Runner, "runner-2", "Second runner", 1, null, "https://127.0.0.1:1", "CODE", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Null(pending.Announce(announcement, IPAddress.Loopback));
        Assert.Equal(ServerProducts.Runner, Assert.Single(pending.All()).Product);
    }

    [Fact]
    public async Task The_status_runs_instances_and_packages_are_relayed_and_the_query_goes_through_untouched()
    {
        Assert.Equal("sample", (string)JsonNode.Parse(await (await Send(HttpMethod.Get, Relay("api/v1/status"), viewer)).Content.ReadAsStringAsync())!["tasks"]![0]!["package"]!);
        Assert.Equal("true", (string)JsonNode.Parse(await (await Send(HttpMethod.Get, Relay("api/v1/instances?all=true"), viewer)).Content.ReadAsStringAsync())!["all"]!);
        Assert.Equal("?package=sample&channel=dev&outcome=failed", (string)JsonNode.Parse(await (await Send(HttpMethod.Get, Relay("api/v1/runs?package=sample&channel=dev&outcome=failed"), viewer)).Content.ReadAsStringAsync())!["query"]!);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Relay("api/v1/definitions/integration"), viewer)).StatusCode);
        Assert.Equal("sample", (string)JsonNode.Parse(await (await Send(HttpMethod.Get, Relay("api/v1/definitions/integration/sample"), viewer)).Content.ReadAsStringAsync())!["name"]!);
    }

    [Fact]
    public async Task An_administrator_holds_the_operator_and_admin_roles_on_the_runner_and_is_named_there()
    {
        var run = await Send(HttpMethod.Post, Relay("api/v1/tasks/sample/dev/ok/run"), admin, "{}");
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Relay("api/v1/tasks/sample/dev/ok/pause"), admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Relay("api/v1/tasks/sample/dev/ok/resume"), admin)).StatusCode);

        var activate = await Send(HttpMethod.Post, Relay("api/v1/definitions/integration/sample/activate"), admin, """{"channel":"dev","expected_version":1,"confirm_losses":true,"losses":["tally: seen"]}""");
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        Assert.Contains("tally: seen", (string)JsonNode.Parse(await activate.Content.ReadAsStringAsync())!["body"]!);
        foreach (var act in new[] { "switch", "rollback", "retire" })
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Relay($"api/v1/definitions/integration/sample/{act}"), admin, """{"channel":"dev"}""")).StatusCode);

        Assert.Contains(reached, r => r.StartsWith("POST /api/v1/tasks/sample/dev/ok/run as ") && r.Contains("admin") && r.Contains("operator") && r.EndsWith("by Admin person"));
    }

    [Fact]
    public async Task A_viewer_reads_and_the_runners_own_refusal_of_an_act_comes_through()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Relay("api/v1/status"), viewer)).StatusCode);
        foreach (var path in new[] { "api/v1/tasks/sample/dev/ok/run", "api/v1/tasks/sample/dev/ok/pause", "api/v1/tasks/sample/dev/ok/resume" })
        {
            var refused = await Send(HttpMethod.Post, Relay(path), viewer, "{}");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("This needs the operator role.", (string)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["error"]!);
        }

        var activate = await Send(HttpMethod.Post, Relay("api/v1/definitions/integration/sample/activate"), viewer, """{"channel":"dev"}""");
        Assert.Equal(HttpStatusCode.Forbidden, activate.StatusCode);
        Assert.Contains("admin role", await activate.Content.ReadAsStringAsync());
        Assert.All(reached.Where(r => r.Contains("tasks") || r.Contains("activate")), r => Assert.Contains("as viewer by Viewer person", r));
    }

    [Fact]
    public async Task What_the_runner_lets_the_console_grant_limits_an_administrator_there()
    {
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(runner.Url, runner.NewCodeWith(ServerRole.Viewer), "Runner", default);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Relay("api/v1/tasks/sample/dev/ok/run"), admin, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Relay("api/v1/status"), admin)).StatusCode);
    }

    [Theory]
    [InlineData("PUT", "api/v1/artifacts/0123456789abcdef")]            // a hash is 64 digits
    [InlineData("PUT", "api/v1/settings/max_artifact_bytes")]
    [InlineData("PUT", "api/v1/definitions/integration/sample/activate")]
    [InlineData("DELETE", "api/v1/bindings/dev/integration/sample")]
    [InlineData("POST", "api/v1/tasks/sample/dev/ok/decisions")]       // what a product reports to a runner is not a console's call
    [InlineData("POST", "api/v1/webhooks/sample/dev/shop")]
    [InlineData("POST", "api/v1/definitions/integration/sample/publish")]
    [InlineData("POST", "api/v1/tasks/sample/dev/ok/delete")]
    [InlineData("DELETE", "api/v1/tasks/sample/dev/ok/run")]
    [InlineData("GET", "api/v1/artifacts/0123456789abcdef")]
    [InlineData("GET", "hubs/echo")]
    public async Task Only_the_documented_management_calls_of_a_runner_are_relayed_even_for_an_administrator(string method, string path)
    {
        var response = await Send(new HttpMethod(method), Relay(path), admin, method is "PUT" or "POST" ? "{}" : null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        if (method != "HEAD") Assert.Contains("relays only its management calls", await response.Content.ReadAsStringAsync());
        Assert.Empty(reached);   // the runner was never asked
    }

    [Fact]
    public void The_allowlist_is_for_a_runner_alone_and_names_nothing_a_publisher_does()
    {
        Assert.True(ServerProducts.Relayable("dbdatabuild", "PUT", "api/v1/anything"));
        Assert.True(ServerProducts.Relayable(null, "DELETE", "api/anything"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/runs"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/connections"));   // plan 28c E23: the inventory is read, never written
        Assert.False(ServerProducts.Relayable("dbdatarunner", "DELETE", "api/v1/connections"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "POST", "api/v1/tasks/orders-sync/prod/extract/run"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/tasks/orders-sync/prod/extract/run"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "POST", "api/v1/runs"));
        // who schedules a task (plan 33 UE06): exactly these three, and nothing beside them
        Assert.True(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/tasks/orders/prod/extract/schedule"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/tasks/orders/prod/extract/decisions"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/tasks/orders/prod/extract/owner"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "POST", "api/v1/tasks/orders/prod/extract/decisions"));   // the product reports decisions, not a console
        Assert.False(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/tasks/orders/prod/extract/schedule"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/bindings/prod/integration/orders"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/definitions/integration/orders"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/artifacts/abc"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/runs/../artifacts/x"));
    }

    [Theory]
    [InlineData("GET", "api/v1/instances/6f1c0f1e-1111-4222-8333-444455556666/labels")]
    [InlineData("PUT", "api/v1/instances/6f1c0f1e-1111-4222-8333-444455556666/labels")]
    [InlineData("GET", "api/v1/tasks/sample/dev/ok/schedule")]
    [InlineData("GET", "api/v1/tasks/sample/dev/ok/decisions")]
    [InlineData("PUT", "api/v1/tasks/sample/dev/ok/owner")]
    [InlineData("PUT", "api/v1/bindings/dev/integration/sample")]
    [InlineData("PUT", "api/v1/settings/require_signatures")]
    [InlineData("HEAD", "api/v1/artifacts/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("PUT", "api/v1/artifacts/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("PUT", "api/v1/definitions/integration/sample")]
    public void The_screens_of_plan_33_may_bind_publish_label_and_move_a_task_owner_and_the_runner_still_decides_who(string method, string path) =>
        Assert.True(ServerProducts.Relayable("dbdatarunner", method, path));
}
