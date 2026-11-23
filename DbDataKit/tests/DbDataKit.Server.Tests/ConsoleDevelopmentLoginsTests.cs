using System.Collections.Concurrent;
using System.Net;
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
/// A development login a Workspace screen sends to the console (<see cref="ConsoleDevelopmentLogins"/>), against a fake Workspace server and a fake Runner: the console asks the Workspace server only which package the project is,
/// and puts the login in the Runner's secret store as the signed-in person, under names of the package, the channel and the slot. The fake Workspace server fails any request that carries the login, in its address, a header
/// or its body. Production is refused before anything is sent. The real programs are in <c>DbDataKit.Server.Servers.Tests</c>.
/// </summary>
public sealed class ConsoleDevelopmentLoginsTests : IAsyncLifetime
{
    private const string WorkspaceId = "workspace-dev";
    private const string RunnerId = "runner-dev";
    private const string Password = "pw-dev-login-7731";
    private const string User = "ana_dev_login_4410";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-dev-login-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<string> workspaceCalls = new();
    private readonly ConcurrentQueue<string> carried = new();
    private readonly ConcurrentDictionary<string, (string Value, string? Person)> held = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer workspace = null!;
    private PairableServer runner = null!;
    private string admin = null!;
    private string viewer = null!;

    public async ValueTask InitializeAsync()
    {
        workspace = await PairableServer.StartAsync(product: ServerProducts.Workspace, instanceId: WorkspaceId, map: MapWorkspace);
        runner = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: RunnerId, map: MapRunner);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var accounts = app.Services.GetRequiredService<IUserAccounts>();
        var sessions = app.Services.GetRequiredService<ISessionStore>();
        admin = sessions.Create(accounts.CreateUser("Ada Admin", null, UserRole.Admin).Id).Id;
        viewer = sessions.Create(accounts.CreateUser("Vic Viewer", null, UserRole.Viewer).Id).Id;
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(workspace.Url, workspace.NewCodeWith(ServerRole.Admin | ServerRole.Author | ServerRole.Approver), "Workspaces", default);
        await instances.PairAsync(runner.Url, runner.NewCodeWith(ServerRole.Admin), "runners-eu", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await workspace.DisposeAsync();
        await runner.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    /// <summary>
    /// A Workspace server that fails any request carrying the login (its address, a header, its body), and answers the project route: <c>crm-to-dw</c> (a sync project, its package), <c>site</c> (an app, no package),
    /// <c>payroll</c> (the person may not read it).
    /// </summary>
    private void MapWorkspace(WebApplication server, PairingCodes codes, PairedClientsFile clients)
    {
        server.Use(async (context, next) =>
        {
            // the body of a management call is read whole and handed on as it came; the pairing route sets its own size limit before it reads, so its body is not read here (it is the console's pairing, not the login)
            var body = "";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var bytes = new MemoryStream();
                await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
                body = Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Position = 0;
                context.Request.Body = bytes;
            }
            var seen = new StringBuilder().Append(context.Request.Path).Append(context.Request.QueryString).Append('\n').Append(body);
            foreach (var (name, values) in context.Request.Headers) seen.Append('\n').Append(name).Append(": ").Append(values.ToString());
            var text = seen.ToString();
            if (text.Contains(Password, StringComparison.Ordinal) || text.Contains(User, StringComparison.Ordinal))
            {
                carried.Enqueue($"{context.Request.Method} {context.Request.Path}");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "This Workspace server received a login." });
                return;
            }
            await next(context);
        });
        server.MapGet("/api/v1/projects/{project}", (string project) =>
        {
            workspaceCalls.Enqueue($"GET /api/v1/projects/{project}");
            return project switch
            {
                "crm-to-dw" => Results.Json(new { id = project, kind = "sync", package = "crm-to-dw" }),
                "site" => Results.Json(new { id = project, kind = "app", package = (string?)null }),
                "payroll" => Results.Json(new { error = "This needs the viewer role on the project payroll." }, statusCode: 403),
                _ => Results.NotFound(),
            };
        });
    }

    /// <summary>A Runner's secret store as its management API takes it: the admin role, a value of the body, kept under the name (the test reads what it holds).</summary>
    private void MapRunner(WebApplication server, PairingCodes codes, PairedClientsFile clients) =>
        server.MapPut("/api/v1/secrets/{name}", async (string name, HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: 403);
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            held[name] = ((string)body["value"]!, token.SubjectName);
            return Results.Json(new { name });
        });

    private async Task<(HttpStatusCode Status, JsonObject Body)> SetUpAsync(string session, string project, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspace-dev-logins/{WorkspaceId}/{project}") { Content = JsonContent(body) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private static StringContent JsonContent(object body) => new(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_login_for_dev_reaches_the_runners_store_for_the_package_channel_and_slot_and_no_request_to_the_workspace_server_carries_it()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "dev", connection = "crm", user = User, password = Password });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Empty(carried);
        Assert.Equal(["GET /api/v1/projects/crm-to-dw"], workspaceCalls.ToArray());
        Assert.Equal((Password, "Ada Admin"), held["dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password"]);
        Assert.Equal((User, "Ada Admin"), held["dbdatakit:runner:dev-login:crm-to-dw.dev.crm:user"]);
        Assert.Equal(2, held.Count);
        Assert.Equal("crm-to-dw", (string?)body["package"]);
        Assert.Equal("dev", (string?)body["channel"]);
        Assert.Equal("runners-eu", (string?)body["runner_name"]);
        Assert.Equal("dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password", (string?)body["secrets"]?["password"]);
        Assert.DoesNotContain(Password, body.ToJsonString());
        Assert.DoesNotContain(User, body.ToJsonString());
    }

    [Fact]
    public async Task A_connection_name_becomes_its_slot_and_the_user_may_be_left_out()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = "runners-eu", channel = "test", connection = "CRM-Main", password = Password });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Equal("crm_main", (string?)body["slot"]);
        Assert.Equal(Password, held["dbdatakit:runner:dev-login:crm-to-dw.test.crm_main:password"].Value);
        Assert.Single(held);
        Assert.Empty(carried);
    }

    [Fact]
    public async Task Production_is_refused_naming_the_rule_and_nothing_is_sent_to_either_server()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "prod", connection = "crm", password = Password });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("channel other than prod", (string?)body["error"]);
        Assert.Contains("Packages page (Bindings)", (string?)body["error"]);
        Assert.Empty(workspaceCalls);
        Assert.Empty(held);
    }

    [Theory]
    [InlineData("", "crm", "pw", "Name the channel")]
    [InlineData("dev", "", "pw", "Name the project's connection to set up")]
    [InlineData("dev", "crm", "", "Type the login's password")]
    public async Task A_request_without_what_it_needs_is_refused_before_anything_is_sent(string channel, string connection, string password, string words)
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel, connection, password });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(words, (string?)body["error"]);
        Assert.Empty(workspaceCalls);
        Assert.Empty(held);
    }

    [Fact]
    public async Task A_project_the_person_may_not_read_or_one_without_a_package_is_refused_in_the_workspace_servers_words_and_the_runner_is_not_called()
    {
        var (refused, words) = await SetUpAsync(admin, "payroll", new { runner = RunnerId, channel = "dev", connection = "crm", password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal("This needs the viewer role on the project payroll.", (string?)words["error"]);

        var (none, why) = await SetUpAsync(admin, "site", new { runner = RunnerId, channel = "dev", connection = "crm", password = Password });
        Assert.Equal(HttpStatusCode.Conflict, none);
        Assert.Contains("makes no package of tasks on a Runner", (string?)why["error"]);

        Assert.Empty(held);
        Assert.Empty(carried);
    }

    [Fact]
    public async Task A_person_the_runner_does_not_let_store_a_secret_is_refused_in_the_runners_words()
    {
        var (status, body) = await SetUpAsync(viewer, "crm-to-dw", new { runner = RunnerId, channel = "dev", connection = "crm", password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("This needs the admin role.", (string?)body["error"]);
        Assert.Empty(held);
        Assert.Empty(carried);
    }

    [Fact]
    public async Task A_runner_that_is_a_workspace_server_is_refused()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = WorkspaceId, channel = "dev", connection = "crm", password = Password });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("is not a Runner", (string?)body["error"]);
        Assert.Empty(carried);
    }
}
