using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Catalog;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The console's read of a database's shape for a Workspace project (<see cref="ConsoleCatalogRead"/>), against a fake Workspace server and a fake Runner: the Workspace server says whether the person may read the project and which package
/// it becomes; the console starts the Runner's <c>discover</c> for the package's slot of the connection, naming the person; it waits, or answers the run for the screen to follow; the finished read is one connection of
/// <c>catalog-cache/2</c> recorded as read from the Runner and the channel. A read in production goes ahead. The real programs are in <c>DbDataKit.Server.Servers.Tests</c>.
/// </summary>
public sealed class ConsoleCatalogReadTests : IAsyncLifetime
{
    private const string WorkspaceId = "workspace-cat";
    private const string RunnerId = "runner-cat";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-catalog-read-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<string> reached = new();
    private readonly ConcurrentQueue<JsonObject> started = new();
    private readonly ConcurrentDictionary<string, int> looks = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer workspace = null!;
    private PairableServer runner = null!;
    private string admin = null!;

    /// <summary>How many looks at a run the fake Runner answers "running" before it says how the run ended.</summary>
    private int runningLooks;

    /// <summary>How the next run ends: null succeeds, any other text fails with it.</summary>
    private string? failure;

    /// <summary>The columns of <c>dbo.accounts</c> the next read answers; the table name, when a test wants a value that looks like a secret.</summary>
    private string[] columns = ["id", "name"];
    private string table = "accounts";

    public async ValueTask InitializeAsync()
    {
        workspace = await PairableServer.StartAsync(product: ServerProducts.Workspace, instanceId: WorkspaceId, map: MapWorkspace);
        runner = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: RunnerId, map: MapRunner);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id).Id;
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(workspace.Url, workspace.NewCodeWith(ServerRole.Admin | ServerRole.Author | ServerRole.Approver), "Workspaces", default);
        await instances.PairAsync(runner.Url, runner.NewCodeWith(ServerRole.Admin), "Runners", default);
        var reads = app.Services.GetRequiredService<ConsoleCatalogRead>();
        reads.Poll = TimeSpan.FromMilliseconds(20);
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

    /// <summary>A Workspace server's project route: <c>crm-to-dw</c> (a sync project, its package), <c>sales-dw</c> and <c>legacy-dw</c> (build projects the Runner holds no version of), <c>site</c> (an app, no package), <c>payroll</c> (the person may not read it).</summary>
    private void MapWorkspace(WebApplication server, PairingCodes codes, PairedClientsFile clients) =>
        server.MapGet("/api/v1/projects/{project}", (string project, HttpContext c) =>
        {
            reached.Enqueue($"workspace GET {project} by {c.ManagementToken().SubjectName}");
            return project switch
            {
                "crm-to-dw" => Results.Json(new { id = project, kind = "sync", package = "crm-to-dw" }),
                "sales-dw" or "legacy-dw" => Results.Json(new { id = project, kind = "build", package = project }),
                "site" => Results.Json(new { id = project, kind = "app", package = (string?)null }),
                "payroll" => Results.Json(new { error = "This needs the viewer role on the project payroll." }, statusCode: 403),
                _ => Results.NotFound(),
            };
        });

    /// <summary>A Runner's calls a read makes: the package's channels and bindings, the metadata route, a run, its step and the step's payload.</summary>
    private void MapRunner(WebApplication server, PairingCodes codes, PairedClientsFile clients)
    {
        server.MapGet("/api/v1/definitions/integration/{name}", (string name) => name != "crm-to-dw" ? Results.NotFound(new { error = "no such package" }) : Results.Json(new JsonObject
        {
            ["channels"] = new JsonArray(new JsonObject { ["channel"] = "test" }, new JsonObject { ["channel"] = "prod" }, new JsonObject { ["channel"] = "dev" }),
            ["bindings"] = new JsonArray(
                new JsonObject { ["channel"] = "dev", ["slot"] = "crm_db", ["bound_to"] = "crm_dev" },
                new JsonObject { ["channel"] = "prod", ["slot"] = "crm_db", ["bound_to"] = "crm" },
                new JsonObject { ["channel"] = "test", ["slot"] = "other", ["bound_to"] = "x" }),
        }));
        // what a Runner serves for a package it holds no version of: its channels and the bindings it holds for the package (an older Runner, here for legacy-dw, does not answer the call)
        server.MapGet("/api/v1/metadata/channels", (string package, HttpContext c) =>
        {
            reached.Enqueue($"runner GET metadata/channels {package} by {c.ManagementToken().SubjectName}");
            return package != "sales-dw" ? Results.NotFound() : Results.Json(new JsonObject
            {
                ["package"] = package, ["holds_version"] = false, ["channels"] = new JsonArray("prod", "dev"),
                ["bindings"] = new JsonArray(new JsonObject { ["channel"] = "dev", ["slot"] = "warehouse", ["bound_to"] = "sales-dw-warehouse-dev" }),
            });
        });
        server.MapPost("/api/v1/metadata/{channel}/{task}", async (string channel, string task, HttpContext c) =>
        {
            var body = (await JsonNode.ParseAsync(c.Request.Body))!.AsObject();
            body["channel"] = channel;
            body["task"] = task;
            body["token_subject"] = c.ManagementToken().SubjectName;
            started.Enqueue(body);
            var run = Guid.NewGuid().ToString("D");
            return Results.Json(new { run_id = run, key = run, step = task }, statusCode: 202);
        });
        server.MapGet("/api/v1/runs/{id}", (string id) =>
        {
            var seen = looks.AddOrUpdate(id, 1, (_, n) => n + 1);
            var channel = started.LastOrDefault()?["channel"]?.GetValue<string>() ?? "dev";
            var outcome = seen <= runningLooks ? "running" : failure is null ? "succeeded" : "failed";
            var task = started.LastOrDefault()?["task"]?.GetValue<string>() ?? "discover";
            return Results.Json(new JsonObject
            {
                ["id"] = id, ["package"] = "dbdatakit-metadata", ["channel"] = channel, ["task"] = task, ["outcome"] = outcome,
                ["failure"] = outcome == "failed" ? new JsonObject { ["kind"] = "error", ["message"] = failure } : null,
            });
        });
        server.MapGet("/api/v1/tasks/dbdatakit-metadata/{channel}/discover/steps/discover", (string channel, string key) => Results.Json(new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject { ["key"] = key, ["completed"] = "2026-11-22T10:00:04Z", ["payload"] = "6f1c2c8e-0000-4000-8000-000000000001" }),
        }));
        server.MapGet("/api/v1/tasks/dbdatakit-metadata/{channel}/describe/steps/describe", (string channel, string key) => Results.Json(new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject
            {
                ["key"] = key, ["completed"] = "2026-11-22T10:00:04Z",
                ["result"] = new JsonObject
                {
                    ["engine"] = "sqlserver", ["strategy"] = "SqlServerDescribeFirstResultSet",
                    ["columns"] = new JsonArray(new JsonObject { ["name"] = "id", ["type"] = "int", ["nullable"] = false, ["sample"] = "42" }, new JsonObject { ["name"] = "segment", ["type"] = "nvarchar(20)", ["nullable"] = true }),
                },
            }),
        }));
        server.MapGet("/api/v1/tasks/dbdatakit-metadata/{channel}/discover/steps/discover/payloads/{id}", () => Results.Json(new JsonObject
        {
            ["engine"] = "sqlserver",
            ["databases"] = new JsonArray(new JsonObject { ["name"] = "crm", ["schemas"] = new JsonArray("dbo", "staging") }),
            ["tables"] = new JsonArray(new JsonObject
            {
                ["database"] = "crm", ["schema"] = "dbo", ["name"] = table, ["kind"] = "table",
                ["columns"] = new JsonArray([.. columns.Select(n => (JsonNode?)new JsonObject { ["name"] = n, ["type"] = "int", ["nullable"] = n != "id", ["key"] = n == "id" })]),
            }),
        }));
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> Call(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={admin}");
        if (body is not null) request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private const string Base = $"/api/workspace-catalog/{WorkspaceId}/crm-to-dw";

    [Fact]
    public async Task The_channel_picker_lists_the_packages_channels_with_the_ones_that_bind_the_connections_slot_and_offers_the_first_that_is_not_production()
    {
        var (status, body) = await Call(HttpMethod.Get, $"{Base}/channels?runner={RunnerId}&connection=CRM-db");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("crm_db", (string?)body["slot"]);   // the slot Sync's package makes of the connection's name
        Assert.Equal(["dev", "prod", "test"], body["channels"]!.AsArray().Select(c => (string)c!["channel"]!).ToArray());
        Assert.Equal([true, true, false], body["channels"]!.AsArray().Select(c => (bool)c!["bound"]!).ToArray());
        Assert.True((bool)body["channels"]![1]!["production"]!);
        Assert.Equal("dev", (string?)body["default"]);
        Assert.Contains(reached, r => r == "workspace GET crm-to-dw by Ada Admin");

        // a project the Workspace server does not have is not listed
        var (_, none) = await Call(HttpMethod.Get, $"/api/workspace-catalog/{WorkspaceId}/unknown/channels?runner={RunnerId}&connection=crm");
        Assert.Contains("no project 'unknown'", (string)none["error"]!);
    }

    [Fact]
    public async Task A_package_the_runner_holds_no_version_of_lists_every_channel_the_runner_serves_with_the_bindings_it_holds_for_the_package()
    {
        var (status, body) = await Call(HttpMethod.Get, $"/api/workspace-catalog/{WorkspaceId}/sales-dw/channels?runner={RunnerId}&connection=warehouse");
        Assert.True(status == HttpStatusCode.OK, body.ToJsonString());
        Assert.Equal(["dev", "prod"], body["channels"]!.AsArray().Select(c => (string)c!["channel"]!).ToArray());
        Assert.Equal([true, false], body["channels"]!.AsArray().Select(c => (bool)c!["bound"]!).ToArray());
        Assert.Equal("sales-dw-warehouse-dev", (string?)body["channels"]![0]!["bound_to"]);
        Assert.True((bool)body["channels"]![1]!["production"]!);
        Assert.Equal("dev", (string?)body["default"]);
        Assert.Contains("holds no version of sales-dw", (string)body["note"]!);
        Assert.Contains(reached, r => r == "runner GET metadata/channels sales-dw by Ada Admin");

        // a connection nothing binds yet: every channel is listed, none offered first, and Set up for development is how to bind one
        var (_, unbound) = await Call(HttpMethod.Get, $"/api/workspace-catalog/{WorkspaceId}/sales-dw/channels?runner={RunnerId}&connection=staging");
        Assert.Equal([false, false], unbound["channels"]!.AsArray().Select(c => (bool)c!["bound"]!).ToArray());
        Assert.Null((string?)unbound["default"]);
        Assert.Contains("Set up for development", (string)unbound["note"]!);

        // a Runner that does not answer the call lists no channel and says a build published to a channel comes first
        var (_, older) = await Call(HttpMethod.Get, $"/api/workspace-catalog/{WorkspaceId}/legacy-dw/channels?runner={RunnerId}&connection=warehouse");
        Assert.Empty(older["channels"]!.AsArray());
        Assert.Contains("has no package legacy-dw yet", (string)older["note"]!);
    }

    [Fact]
    public async Task A_read_in_dev_names_the_person_and_answers_the_connection_as_a_catalog_cache_recorded_as_read_from_the_runner_and_the_channel()
    {
        var (status, body) = await Call(HttpMethod.Post, $"{Base}/read", new { runner = RunnerId, channel = "dev", connection = "CRM-db", schema = "dbo", table = "accounts" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("done", (string?)body["state"]);
        Assert.False((bool)body["production"]!);

        var asked = Assert.Single(started);
        Assert.Equal(("crm-to-dw", "crm_db", "Ada Admin", "dev", "discover"), ((string)asked["package"]!, (string)asked["slot"]!, (string)asked["on_behalf_of"]!, (string)asked["channel"]!, (string)asked["task"]!));
        Assert.Equal("accounts", (string?)asked["inputs"]!["table"]);
        Assert.Equal("""["dbo"]""", (string?)asked["inputs"]!["schemas"]);
        Assert.Equal("Ada Admin", (string?)asked["token_subject"]);

        var load = CatalogCache.Load((string)body["cache"]!);
        Assert.True(load.Ok, string.Join("\n", load.Diagnostics.Select(d => d.Message)));
        var connection = Assert.Single(load.Cache!.Connections);
        Assert.Equal("CRM-db", connection.Key);
        Assert.Equal(new CacheRead("Runners", "runner", "dev", "2026-11-22T10:00:04Z"), connection.Value.Read);
        Assert.Equal(["id", "name"], Assert.Single(connection.Value.Tables).Columns.Select(c => c.Name).ToArray());
        Assert.Equal("/servers/runner-cat/runs?run=" + (string)body["run_id"]!, (string?)body["link"]);
    }

    [Fact]
    public async Task A_read_in_production_goes_ahead_and_says_it_was_production()
    {
        var (status, body) = await Call(HttpMethod.Post, $"{Base}/read", new { runner = RunnerId, channel = "prod", connection = "CRM-db" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool)body["production"]!);
        Assert.Equal("prod", (string?)Assert.Single(started)["channel"]);
        Assert.Equal("prod", CatalogCache.Load((string)body["cache"]!).Cache!.Connections["CRM-db"].Read!.Channel);
    }

    [Fact]
    public async Task A_read_that_takes_longer_than_the_wait_answers_the_run_and_the_screen_follows_it_to_its_end()
    {
        app.Services.GetRequiredService<ConsoleCatalogRead>().Wait = TimeSpan.FromMilliseconds(100);
        runningLooks = 1_000;
        var (status, body) = await Call(HttpMethod.Post, $"{Base}/read", new { runner = RunnerId, channel = "dev", connection = "CRM-db" });
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("running", (string?)body["state"]);
        var run = (string)body["run_id"]!;
        Assert.Equal($"/servers/{RunnerId}/runs?run={run}", (string?)body["link"]);

        var follow = $"{Base}/read/{run}?runner={RunnerId}&channel=dev&connection=CRM-db";
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Get, follow)).Status);
        runningLooks = 0;
        var (ended, done) = await Call(HttpMethod.Get, follow);
        Assert.Equal(HttpStatusCode.OK, ended);
        Assert.Equal("done", (string?)done["state"]);
        Assert.Single(started);   // following starts nothing
    }

    [Fact]
    public async Task A_failed_read_answers_the_runners_reason_and_a_result_that_looks_like_a_secret_is_refused_before_the_screen_holds_it()
    {
        failure = "Login failed for the bound login.";
        var (status, body) = await Call(HttpMethod.Post, $"{Base}/read", new { runner = RunnerId, channel = "dev", connection = "CRM-db" });
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Contains("Login failed for the bound login.", (string)body["error"]!);
        Assert.Equal("failed", (string?)body["state"]);

        failure = null;
        table = "Server=db1;User Id=sa;Password=hunter2";
        var (refused, why) = await Call(HttpMethod.Post, $"{Base}/read", new { runner = RunnerId, channel = "dev", connection = "CRM-db" });
        Assert.Equal(HttpStatusCode.BadGateway, refused);
        Assert.DoesNotContain("hunter2", why.ToJsonString());
    }

    [Fact]
    public async Task Describe_starts_the_runners_describe_for_the_person_and_answers_the_querys_columns_with_nothing_else_of_them()
    {
        const string sql = "SELECT id, segment FROM dbo.accounts";
        var (status, body) = await Call(HttpMethod.Post, $"{Base}/describe", new { runner = RunnerId, channel = "dev", connection = "CRM-db", sql });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("done", (string?)body["state"]);
        var asked = Assert.Single(started);
        Assert.Equal(("crm-to-dw", "crm_db", "Ada Admin", "dev", "describe"), ((string)asked["package"]!, (string)asked["slot"]!, (string)asked["on_behalf_of"]!, (string)asked["channel"]!, (string)asked["task"]!));
        Assert.Equal(sql, (string?)asked["inputs"]!["sql"]);
        Assert.Equal("SqlServerDescribeFirstResultSet", (string?)body["strategy"]);
        Assert.Equal("""[{"name":"id","type":"int","nullable":false},{"name":"segment","type":"nvarchar(20)","nullable":true}]""", body["columns"]!.ToJsonString());
        Assert.Equal($"/servers/{RunnerId}/runs?run={(string)body["run_id"]!}", (string?)body["link"]);
    }

    [Fact]
    public async Task Describe_that_takes_longer_than_the_wait_is_followed_and_a_query_the_runner_refuses_answers_its_reason()
    {
        app.Services.GetRequiredService<ConsoleCatalogRead>().Wait = TimeSpan.FromMilliseconds(100);
        runningLooks = 1_000;
        var (status, body) = await Call(HttpMethod.Post, $"{Base}/describe", new { runner = RunnerId, channel = "dev", connection = "CRM-db", sql = "SELECT 1 AS one" });
        Assert.Equal(HttpStatusCode.Accepted, status);
        var follow = $"{Base}/describe/{(string)body["run_id"]!}?runner={RunnerId}&channel=dev&connection=CRM-db";
        runningLooks = 0;
        var (ended, done) = await Call(HttpMethod.Get, follow);
        Assert.Equal(HttpStatusCode.OK, ended);
        Assert.Equal(2, done["columns"]!.AsArray().Count);

        // a discover run is not a description: the follow of one names what it is not
        var (_, read) = await Call(HttpMethod.Post, $"{Base}/read", new { runner = RunnerId, channel = "dev", connection = "CRM-db" });
        var (wrong, said) = await Call(HttpMethod.Get, $"{Base}/describe/{(string)read["run_id"]!}?runner={RunnerId}&channel=dev&connection=CRM-db");
        Assert.Equal(HttpStatusCode.NotFound, wrong);
        Assert.Contains("not a description of a query", (string)said["error"]!);

        failure = "The query writes: UPDATE is not allowed in a description.";
        var (refused, why) = await Call(HttpMethod.Post, $"{Base}/describe", new { runner = RunnerId, channel = "dev", connection = "CRM-db", sql = "UPDATE dbo.accounts SET name = 'x'" });
        Assert.Equal((HttpStatusCode)422, refused);
        Assert.Contains("UPDATE is not allowed", (string)why["error"]!);

        var (noSql, _) = await Call(HttpMethod.Post, $"{Base}/describe", new { runner = RunnerId, channel = "dev", connection = "CRM-db", sql = " " });
        Assert.Equal(HttpStatusCode.BadRequest, noSql);
    }

    [Fact]
    public async Task The_workspace_servers_refusal_is_passed_on_and_nothing_reaches_the_runner_and_a_project_with_no_package_is_not_read()
    {
        var (status, body) = await Call(HttpMethod.Post, $"/api/workspace-catalog/{WorkspaceId}/payroll/read", new { runner = RunnerId, channel = "dev", connection = "crm" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("viewer role on the project payroll", (string)body["error"]!);

        var (app, why) = await Call(HttpMethod.Post, $"/api/workspace-catalog/{WorkspaceId}/site/read", new { runner = RunnerId, channel = "dev", connection = "crm" });
        Assert.Equal(HttpStatusCode.Conflict, app);
        Assert.Contains("app project", (string)why["error"]!);

        // the Runner is named by the screen and must be one: a Workspace server is not
        var (notRunner, said) = await Call(HttpMethod.Post, $"{Base}/read", new { runner = WorkspaceId, channel = "dev", connection = "crm" });
        Assert.Equal(HttpStatusCode.Conflict, notRunner);
        Assert.Contains("is not a Runner", (string)said["error"]!);
        Assert.Empty(started);
    }
}
