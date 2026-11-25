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
/// and puts the login's password, and only the password, in the Runner's secret store as the signed-in person, under a name of the package, the channel and the slot; the user comes back as plain text. The fake Workspace
/// server fails any request that carries the login, in its address, a header or its body. Every channel is accepted, production too. The real programs are in <c>DbDataKit.Server.Servers.Tests</c>.
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
        // the draft d1 of crm-to-dw: a Sync set with the connection crm (its own user and secret) and the connection web (an HTTP API)
        server.MapGet("/api/v1/projects/{project}/workspaces/{id}/files", (string project, string id) =>
        {
            workspaceCalls.Enqueue($"GET /api/v1/projects/{project}/workspaces/{id}/files");
            return id == "d1" ? Results.Json(new { files = DraftFiles.Keys.Select(p => new { path = p, size = DraftFiles[p].Length }) }) : Results.NotFound();
        });
        server.MapGet("/api/v1/projects/{project}/workspaces/{id}/files/{**path}", (string project, string id, string path) =>
        {
            workspaceCalls.Enqueue($"GET /api/v1/projects/{project}/workspaces/{id}/files/{path}");
            return id == "d1" && DraftFiles.TryGetValue(path, out var text) ? Results.Json(new { path, content = text }) : Results.NotFound();
        });
    }

    private static readonly Dictionary<string, string> DraftFiles = new(StringComparer.Ordinal)
    {
        ["config/connections/crm.yaml"] = "# the CRM database\nschema: connection/1\nname: crm\nengine: sqlserver\naddress:\n  host: crm.example\n  database: crm\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdatakit:sync:connection:crm\n",
        ["config/connections/web.yaml"] = "schema: connection/2\nname: web\nkind: http\nendpoint: https://api.example\n",
        ["config/replications/orders/task.yaml"] = "name: orders\n",
    };

    /// <summary>
    /// A Runner's secret store, its connection copies and its bindings as its management API takes them: the admin role, kept under the name (the test reads what it holds). A bind in <c>shared</c> is held for an administrator's
    /// confirmation, as a channel that would use production's connection is.
    /// </summary>
    private void MapRunner(WebApplication server, PairingCodes codes, PairedClientsFile clients)
    {
        server.MapPut("/api/v1/secrets/{name}", async (string name, HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: 403);
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            held[name] = ((string)body["value"]!, token.SubjectName);
            return Results.Json(new { name });
        });
        server.MapPut("/api/v1/connection-copies/{name}", async (string name, HttpContext context) =>
        {
            if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: 403);
            var definition = (string)(await JsonNode.ParseAsync(context.Request.Body))!["definition"]!;
            copies[name] = definition;
            return Results.Json(new { name, version = "v1", kind = "database", secrets = new[] { new { path = "auth.password_secret", reference = "secret:x", resolves = true } } });
        });
        server.MapPut("/api/v1/bindings/{channel}/integration/{package}", async (string channel, string package, HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            var (slot, to) = body["slots"]!.AsObject().Select(p => (p.Key, (string)p.Value!)).Single();
            if (channel == "shared" && body["confirm"] is null)
                return Results.Json(new { error = "Binding this needs an administrator's confirmation.", needsConfirmation = new[] { new { scope = "shared/x", slot, release = channel, connection = to, effect = "shares prod's connection" } } }, statusCode: 409);
            bound[$"{package}/{channel}/{slot}"] = to;
            return Results.Json(new { bindings = new[] { new { channel, slot, bound_to = to } } });
        });
    }

    private readonly ConcurrentDictionary<string, string> copies = new();
    private readonly ConcurrentDictionary<string, string> bound = new();

    private async Task<(HttpStatusCode Status, JsonObject Body)> SetUpAsync(string session, string project, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspace-dev-logins/{WorkspaceId}/{project}") { Content = JsonContent(body) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private static StringContent JsonContent(object body) => new(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_login_for_dev_stores_its_password_only_for_the_package_channel_and_slot_and_no_request_to_the_workspace_server_carries_it()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "dev", connection = "crm", user = User, password = Password });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Empty(carried);
        Assert.Equal(["GET /api/v1/projects/crm-to-dw"], workspaceCalls.ToArray());
        Assert.Equal((Password, "Ada Admin"), Assert.Single(held, h => h.Key == "dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password").Value);
        Assert.Single(held);
        Assert.DoesNotContain(held, h => h.Value.Value.Contains(User, StringComparison.Ordinal));
        Assert.Equal("crm-to-dw", (string?)body["package"]);
        Assert.Equal("dev", (string?)body["channel"]);
        Assert.False((bool?)body["production"]);
        Assert.Equal("runners-eu", (string?)body["runner_name"]);
        Assert.Equal(User, (string?)body["user"]);
        Assert.Equal("dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password", (string?)Assert.Single(body["secrets"]!.AsObject()).Value);
        Assert.Equal("password", Assert.Single(body["secrets"]!.AsObject()).Key);
        Assert.DoesNotContain(Password, body.ToJsonString());
    }

    [Fact]
    public async Task A_login_for_prod_is_stored_the_same_way_under_the_name_of_prod()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "prod", connection = "crm", user = User, password = Password });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Equal((Password, "Ada Admin"), held["dbdatakit:runner:dev-login:crm-to-dw.prod.crm:password"]);
        Assert.Single(held);
        Assert.DoesNotContain(held, h => h.Value.Value.Contains(User, StringComparison.Ordinal));
        Assert.True((bool?)body["production"]);
        Assert.Equal(User, (string?)body["user"]);
        Assert.Equal("password", Assert.Single(body["secrets"]!.AsObject()).Key);
        Assert.Empty(carried);
    }

    [Fact]
    public async Task A_connection_name_becomes_its_slot_and_the_user_may_be_left_out()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = "runners-eu", channel = "test", connection = "CRM-Main", password = Password });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Equal("crm_main", (string?)body["slot"]);
        Assert.Equal(Password, held["dbdatakit:runner:dev-login:crm-to-dw.test.crm_main:password"].Value);
        Assert.Single(held);
        Assert.Null(body["user"]);
        Assert.Empty(carried);
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
    public async Task The_workspace_servers_guard_fails_a_request_that_carries_the_login()
    {
        // the guard the tests above rely on: the login in the address of a call to the Workspace server is caught there
        var (status, _) = await SetUpAsync(admin, Password, new { runner = RunnerId, channel = "dev", connection = "crm", password = "another" });

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Equal([$"GET /api/v1/projects/{Password}"], carried.ToArray());
        Assert.Empty(held);
    }

    [Fact]
    public async Task A_runner_that_is_a_workspace_server_is_refused()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = WorkspaceId, channel = "dev", connection = "crm", password = Password });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("is not a Runner", (string?)body["error"]);
        Assert.Empty(carried);
    }

    [Fact]
    public async Task With_the_draft_the_console_puts_the_copy_with_the_user_and_the_secret_and_binds_the_slot_and_the_workspace_server_is_asked_only_for_the_draft_s_file()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "dev", connection = "crm", user = User, password = Password, draft = "d1" });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.True((bool?)body["bound"]);
        Assert.Equal("crm-to-dw-crm-dev", (string?)body["copy"]);
        Assert.Equal("config/connections/crm.yaml", (string?)body["file"]);
        Assert.Equal(User, (string?)body["user"]);
        const string secret = "dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password";
        Assert.Equal(secret, (string?)body["secrets"]!["password"]);
        Assert.Equal(Password, held[secret].Value);
        Assert.Equal("crm-to-dw-crm-dev", bound["crm-to-dw/dev/crm"]);
        var copy = copies["crm-to-dw-crm-dev"];
        Assert.Contains($"user: '{User}'", copy);
        Assert.Contains($"password_secret: 'secret:{secret}'", copy);
        Assert.Contains("host: crm.example", copy);
        Assert.Contains("name: 'crm-to-dw-crm-dev'", copy);
        Assert.DoesNotContain("dbdatakit:sync:connection:crm", copy);
        Assert.DoesNotContain(Password, copy);
        Assert.DoesNotContain(Password, body.ToJsonString());
        Assert.Empty(carried);
        Assert.Equal(["GET /api/v1/projects/crm-to-dw", "GET /api/v1/projects/crm-to-dw/workspaces/d1/files", "GET /api/v1/projects/crm-to-dw/workspaces/d1/files/config/connections/crm.yaml"], workspaceCalls.ToArray());
    }

    [Fact]
    public async Task With_the_draft_and_no_user_typed_the_copy_takes_the_user_the_draft_names()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "dev", connection = "crm", password = Password, draft = "d1" });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Equal("sync", (string?)body["user"]);
        Assert.Contains("user: 'sync'", copies["crm-to-dw-crm-dev"]);
    }

    [Fact]
    public async Task A_bind_the_runner_holds_for_confirmation_answers_409_with_what_to_confirm_and_the_login_and_copy_are_kept()
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "shared", connection = "crm", user = User, password = Password, draft = "d1" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.False((bool?)body["bound"]);
        Assert.Equal("shared/x", (string?)body["needsConfirmation"]![0]!["scope"]);
        Assert.True(held.ContainsKey("dbdatakit:runner:dev-login:crm-to-dw.shared.crm:password"));
        Assert.True(copies.ContainsKey("crm-to-dw-crm-shared"));
        Assert.Empty(bound);
    }

    [Theory]
    [InlineData("d2", "crm", 404, "has no draft 'd2'")]
    [InlineData("d1", "erp", 404, "The draft has no file of the connection 'erp'")]
    [InlineData("d1", "web", 422, "is a connection of the kind http")]
    public async Task A_draft_or_connection_file_that_cannot_make_a_copy_is_refused_before_the_runner_is_called(string draft, string connection, int expected, string words)
    {
        var (status, body) = await SetUpAsync(admin, "crm-to-dw", new { runner = RunnerId, channel = "dev", connection, user = User, password = Password, draft });

        Assert.Equal(expected, (int)status);
        Assert.Contains(words, (string?)body["error"]);
        Assert.Empty(held);
        Assert.Empty(copies);
        Assert.Empty(carried);
    }
}
