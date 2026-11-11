using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

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
        try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // the artifact store's files are read-only on Windows
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
        server.MapGet("/api/v1/runs/{id}", (HttpContext c, string id) => Needs(c, ServerRole.Viewer, () => Results.Json(new { id })));
        server.MapGet("/api/v1/runs/{id}/logs", (HttpContext c, string id) => Needs(c, ServerRole.Viewer, () => Results.Json(new { run = id, lines = Array.Empty<object>() })));
        server.MapGet("/api/v1/definitions/integration", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { kind = "integration", definitions = Array.Empty<object>() })));
        server.MapGet("/api/v1/definitions/integration/{name}", (HttpContext c, string name) => Needs(c, ServerRole.Viewer, () => Results.Json(new { name })));
        server.MapPost("/api/v1/tasks/{package}/{channel}/{task}/{act}", (HttpContext c, string act) => Needs(c, ServerRole.Operator, () => Results.Json(new { act }, statusCode: act == "run" ? 202 : 200)));
        server.MapPost("/api/v1/definitions/integration/{name}/{act}", async (HttpContext c, string act) =>
        {
            var body = await new StreamReader(c.Request.Body).ReadToEndAsync();
            return Needs(c, ServerRole.Admin, () => Results.Json(new { act, body }));
        });
        // the rest of the table (RunnerRoutes), each with the role the real runner asks for (RunnerRoleTests pins them to the real runner)
        server.MapGet("/api/v1/connections", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { connections = Array.Empty<object>() })));
        server.MapGet("/api/v1/settings", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { require_signatures = false })));
        server.MapGet("/api/v1/lineage", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { edges = Array.Empty<object>() })));
        server.MapGet("/api/v1/secrets/overview", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Json(new { secrets = Array.Empty<object>() })));
        server.MapGet("/api/v1/certificate", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { present = true })));
        // a server upgrades itself when the console says so (plan 24a UC09): the status is a viewer's, prepare and apply an administrator's
        server.MapGet("/api/v1/upgrade", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { state = "none" })));
        server.MapPost("/api/v1/upgrade", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Json(new { state = "prepared" })));
        server.MapGet("/api/v1/instances/{id}/labels", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { labels = Array.Empty<object>() })));
        server.MapGet("/api/v1/tasks/{package}/{channel}/{task}/schedule", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { owner = "runner" })));
        server.MapGet("/api/v1/tasks/{package}/{channel}/{task}/decisions", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { decisions = Array.Empty<object>() })));
        server.MapMethods("/api/v1/artifacts/{sha}", ["HEAD"], (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.NotFound()));
        server.MapPut("/api/v1/tasks/{package}/{channel}/{task}/owner", (HttpContext c) => Needs(c, ServerRole.Operator, () => Results.Json(new { owner = "runner" })));
        server.MapPut("/api/v1/instances/{id}/labels", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Json(new { added = Array.Empty<string>() })));
        server.MapPut("/api/v1/artifacts/{sha}", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        server.MapPut("/api/v1/definitions/integration/{name}", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        server.MapPost("/api/v1/bindings/{channel}/integration/{name}/test", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Json(new { ok = true })));
        server.MapPut("/api/v1/bindings/{channel}/integration/{name}", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        server.MapPut("/api/v1/settings/require_signatures", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
        // what a product reports and the serving layer calls: a console never reaches these, whoever is signed in
        server.MapPut("/api/v1/settings/max_artifact_bytes", (HttpContext c) => Needs(c, ServerRole.Admin, () => Results.Ok()));
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
    public async Task A_viewer_reads_and_the_relays_refusal_of_an_act_names_the_role_it_needs()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, Relay("api/v1/status"), viewer)).StatusCode);
        foreach (var path in new[] { "api/v1/tasks/sample/dev/ok/run", "api/v1/tasks/sample/dev/ok/pause", "api/v1/tasks/sample/dev/ok/resume" })
        {
            var refused = await Send(HttpMethod.Post, Relay(path), viewer, "{}");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("This needs the operator role on Runner, and you hold viewer there.", (string)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["error"]!);
        }

        var activate = await Send(HttpMethod.Post, Relay("api/v1/definitions/integration/sample/activate"), viewer, """{"channel":"dev"}""");
        Assert.Equal(HttpStatusCode.Forbidden, activate.StatusCode);
        Assert.Contains("admin role on Runner", await activate.Content.ReadAsStringAsync());
        Assert.DoesNotContain(reached, r => r.Contains("tasks") || r.Contains("activate"));   // the relay refused them: the runner was never asked
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
    public void The_allowlist_is_for_a_runner_alone_and_is_a_table_of_method_and_route()
    {
        Assert.True(ServerProducts.Relayable("dbdatabuild", "PUT", "api/v1/anything"));
        Assert.True(ServerProducts.Relayable(null, "DELETE", "api/anything"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/runs"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/connections"));   // plan 28c E23: the inventory is read, never written
        Assert.False(ServerProducts.Relayable("dbdatarunner", "DELETE", "api/v1/connections"));
        Assert.True(ServerProducts.Relayable("dbdatarunner", "POST", "api/v1/tasks/orders-sync/prod/extract/run"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/tasks/orders-sync/prod/extract/run"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "POST", "api/v1/runs"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "POST", "api/v1/tasks/orders/prod/extract/decisions"));   // the product reports decisions, not a console
        Assert.False(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/tasks/orders/prod/extract/schedule"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/artifacts/abc"));                           // a hash is 64 digits
        Assert.False(ServerProducts.Relayable("dbdatarunner", "GET", "api/v1/runs/../artifacts/x"));
        Assert.False(ServerProducts.Relayable("dbdatarunner", "HEAD", "api/v1/status"));                                   // HEAD is the one artifact route, not a pass for every read
        Assert.False(ServerProducts.Relayable("dbdatarunner", "PUT", "api/v1/settings/max_artifact_bytes"));
    }

    /// <summary>The table, as the owner decision of 2026-11-08 fixed it: the routes UE06 and the screens before it use, and what UE08, UE10 and UE11 add, with the runner's own role for each. A route added or a role changed fails here until this list says so.</summary>
    public static readonly (string Method, string Pattern, ServerRole Role, ServerRole Or)[] Expected =
    [
        ("GET", "api/v1/status", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/instances", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/connections", ServerRole.Viewer, ServerRole.None),
        ("GET", "api/v1/runs", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/runs/{id}", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/runs/{id}/logs", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/settings", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/lineage", ServerRole.Viewer, ServerRole.None),
        ("GET", "api/v1/upgrade", ServerRole.Viewer, ServerRole.None),
        ("GET", "api/v1/certificate", ServerRole.Viewer, ServerRole.None),
        ("GET", "api/v1/secrets/overview", ServerRole.Admin, ServerRole.None),
        ("GET", "api/v1/definitions/integration", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/definitions/integration/{name}", ServerRole.Viewer, ServerRole.None),
        ("GET", "api/v1/instances/{id}/labels", ServerRole.Viewer, ServerRole.None),
        ("GET", "api/v1/tasks/{name}/{name}/{name}/schedule", ServerRole.Viewer, ServerRole.None), ("GET", "api/v1/tasks/{name}/{name}/{name}/decisions", ServerRole.Viewer, ServerRole.None),
        ("HEAD", "api/v1/artifacts/{sha}", ServerRole.Viewer, ServerRole.Publisher),
        ("POST", "api/v1/tasks/{name}/{name}/{name}/run", ServerRole.Operator, ServerRole.None), ("POST", "api/v1/tasks/{name}/{name}/{name}/pause", ServerRole.Operator, ServerRole.None),
        ("POST", "api/v1/tasks/{name}/{name}/{name}/resume", ServerRole.Operator, ServerRole.None), ("PUT", "api/v1/tasks/{name}/{name}/{name}/owner", ServerRole.Operator, ServerRole.None),
        ("POST", "api/v1/definitions/integration/{name}/activate", ServerRole.Admin, ServerRole.None), ("POST", "api/v1/definitions/integration/{name}/switch", ServerRole.Admin, ServerRole.None),
        ("POST", "api/v1/definitions/integration/{name}/rollback", ServerRole.Admin, ServerRole.None), ("POST", "api/v1/definitions/integration/{name}/retire", ServerRole.Admin, ServerRole.None),
        ("PUT", "api/v1/artifacts/{sha}", ServerRole.Admin, ServerRole.Publisher), ("PUT", "api/v1/definitions/integration/{name}", ServerRole.Admin, ServerRole.Publisher),
        ("PUT", "api/v1/bindings/{name}/integration/{name}", ServerRole.Admin, ServerRole.Publisher), ("POST", "api/v1/bindings/{name}/integration/{name}/test", ServerRole.Admin, ServerRole.None), ("PUT", "api/v1/settings/require_signatures", ServerRole.Admin, ServerRole.None),
        ("PUT", "api/v1/instances/{id}/labels", ServerRole.Admin, ServerRole.None), ("POST", "api/v1/upgrade", ServerRole.Admin, ServerRole.None),
    ];

    public static TheoryData<string> Rows => [.. ServerProducts.RunnerRoutes.Select(r => $"{r.Method} {r.Pattern}")];

    /// <summary>A path the route's pattern matches (the fake runner answers any name).</summary>
    private static string Sample(ServerProducts.RunnerRoute route) =>
        route.Pattern.Replace("{name}", "sample").Replace("{sha}", new string('a', 64)).Replace("{id}", "6f1c0f1e-1111-4222-8333-444455556666");

    private static ServerProducts.RunnerRoute RouteOf(string row) => ServerProducts.RunnerRoutes.Single(r => $"{r.Method} {r.Pattern}" == row);

    /// <summary>Pairs the runner again so that this console may grant at most <paramref name="ceiling"/>; the administrator then holds what both allow, so it stands for a person with exactly that role.</summary>
    private Task HoldOnly(ServerRole ceiling) =>
        app.Services.GetRequiredService<ConsoleInstances>().PairAsync(runner.Url, runner.NewCodeWith(ceiling), "Runner", default);

    [Fact]
    public void The_table_is_exactly_the_routes_of_the_owner_decision_with_the_runners_own_roles() =>
        Assert.Equal(Expected, ServerProducts.RunnerRoutes.Select(r => (r.Method, r.Pattern, r.Role, r.Or)).ToArray());

    [Fact]
    public void Every_route_of_the_table_matches_its_own_sample_and_no_other_row_with_the_same_method()
    {
        foreach (var route in ServerProducts.RunnerRoutes)
            Assert.Same(route, ServerProducts.RunnerRouteOf(route.Method, Sample(route)));
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task A_person_who_holds_the_role_of_a_route_passes_as_themselves(string row)
    {
        var route = RouteOf(row);
        await HoldOnly(route.Role);
        var response = await Send(new HttpMethod(route.Method), Relay(Sample(route)), admin, route.Method is "PUT" or "POST" ? "{}" : null);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        // the runner was asked, and the token named the signed-in person with the roles they hold there (never the console)
        var call = reached.Last(r => r.StartsWith($"{route.Method} /{Sample(route)} as ", StringComparison.Ordinal));
        Assert.EndsWith("by Admin person", call);
        Assert.Contains(route.Role.ToString().ToLowerInvariant(), call);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task A_person_without_the_role_of_a_route_is_refused_at_the_relay_naming_the_role_and_the_runner_is_not_asked(string row)
    {
        var route = RouteOf(row);
        if (route.Role == ServerRole.Viewer) return;   // a person signed in to the console always reads (the ceiling of a server is at least viewer)
        var below = route.Role == ServerRole.Admin ? ServerRole.Operator : ServerRole.Viewer;
        await HoldOnly(below);
        var response = await Send(new HttpMethod(route.Method), Relay(Sample(route)), admin, route.Method is "PUT" or "POST" ? "{}" : null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Contains($"This needs the {route.Needs} role on Runner", error);
        Assert.Contains($"you hold {string.Join(" and ", ServerRoles.Names(ServerRoles.Close(below)))} there", error);
        Assert.Empty(reached);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task A_viewer_of_the_console_is_refused_by_the_relay_on_every_route_above_reading(string row)
    {
        var route = RouteOf(row);
        var response = await Send(new HttpMethod(route.Method), Relay(Sample(route)), viewer, route.Method is "PUT" or "POST" ? "{}" : null);
        if (route.Role == ServerRole.Viewer) { Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode); return; }
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains($"This needs the {route.Needs} role on Runner, and you hold viewer there.", await response.Content.ReadAsStringAsync());
        Assert.Empty(reached);
    }

    [Theory]
    [InlineData("PUT", "api/v1/artifacts/0123456789abcdef")]            // a hash is 64 digits
    [InlineData("PUT", "api/v1/settings/max_artifact_bytes")]
    [InlineData("PUT", "api/v1/definitions/integration/sample/activate")]
    [InlineData("PUT", "api/v1/tasks/sample/dev/ok/schedule")]
    [InlineData("PUT", "api/v1/runs")]
    [InlineData("PUT", "api/v1/status")]
    [InlineData("PUT", "api/v1/bindings/dev/integration/sample/extra")]
    [InlineData("DELETE", "api/v1/instances/6f1c0f1e-1111-4222-8333-444455556666/labels")]
    [InlineData("DELETE", "api/v1/bindings/dev/integration/sample")]
    [InlineData("POST", "api/v1/instances/6f1c0f1e-1111-4222-8333-444455556666/labels")]
    [InlineData("PATCH", "api/v1/bindings/dev/integration/sample")]
    [InlineData("HEAD", "api/v1/status")]                                 // HEAD is only the artifact route
    [InlineData("HEAD", "api/v1/artifacts/0123456789abcdef")]
    [InlineData("HEAD", "api/v1/definitions/integration/sample")]
    [InlineData("GET", "api/v1/artifacts/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]   // an artifact is not read through a console
    public async Task A_call_that_is_not_a_row_of_the_table_is_refused_for_an_administrator_as_a_console_is_not_their_client(string method, string path)
    {
        var response = await Send(new HttpMethod(method), Relay(path), admin, method is "PUT" or "POST" or "PATCH" ? "{}" : null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        if (method != "HEAD") Assert.Contains("relays only its management calls", await response.Content.ReadAsStringAsync());
        Assert.Empty(reached);
    }
}
