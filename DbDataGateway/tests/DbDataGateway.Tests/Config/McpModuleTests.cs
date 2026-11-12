using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Support;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 20 O12: the <c>mcp</c> module through the real host. An agent reads a semantic model with its own access rules; its write waits for a person, who approves it on the module's route, once; the module has its line on <c>/health</c>.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class McpModuleTests : IAsyncLifetime
{
    private const string Request = """
        {"spec_version": "0.3", "kind": "query",
         "query": {"measures": ["orders.net_revenue", "orders.order_count"], "limit": 100, "attributes": ["customers.region"],
                   "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
                   "order": [["orders.net_revenue", "desc"]]}}
        """;

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int port;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var p = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return p;
    }

    public ValueTask InitializeAsync()
    {
        var config = GatewayConfigFile.DirectoryIn(dataDirectory);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(dataDirectory);
    }

    private async Task<(HttpClient Http, StateDatabase State, string Ada, string Bob, string Agent, string Bea, string Linus)> StartAsync(string minutes = "60")
    {
        port = FreePort();
        var warehouse = Path.Combine(dataDirectory, "warehouse.duckdb");
        using (var c = new DuckDBConnection($"Data Source={warehouse}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "seed.sql"));
            cmd.ExecuteNonQuery();
        }
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:DataDirectory"] = dataDirectory, ["Gateway:Management:Enabled"] = "false", ["Gateway:Modules:0"] = "mcp", ["Gateway:Listen:Port"] = "0",
            ["Gateway:Mcp:Address"] = "127.0.0.1", ["Gateway:Mcp:Port"] = port.ToString(), ["Gateway:Mcp:ApprovalMinutes"] = minutes,
            ["Gateway:DataSources:warehouse:Provider"] = "duckdb", ["Gateway:DataSources:warehouse:ConnectionString"] = $"Data Source={warehouse}",
            ["Gateway:DataSources:other:Provider"] = "duckdb", ["Gateway:DataSources:other:ConnectionString"] = "Data Source=:memory:",
            ["Gateway:Semantic:Models:0:Catalog"] = Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"),
            ["Gateway:Semantic:Models:0:Sources:sqlserver_sales"] = "warehouse",
        };
        HomeSeed.Open(settings, ["warehouse"], group: "analysts");
        HomeSeed.Open(settings, ["other"], group: "others");
        app = GatewayHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await app.StartAsync();

        var state = app.Services.GetRequiredService<StateDatabase>();
        var serverId = GatewayStoreModule.ServerIdOf(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, state);
        var directory = new HomeDirectory(state);
        string Person(string name, string group, ServerRole roles)
        {
            var (user, token) = HomeSeed.Person(state, name, $"{name.ToLowerInvariant()}@example.invalid", group);
            directory.Assign(AssignmentSubject.User(user.Id), serverId, roles);
            return token;
        }
        var ada = Person("Ada", "analysts", ServerRole.Viewer | ServerRole.Author);
        var bob = Person("Bob", "others", ServerRole.Viewer);
        var bea = Person("Bea", "others", ServerRole.Viewer | ServerRole.Approver);
        var linus = Person("Linus", "others", ServerRole.Viewer);
        var agentToken = HomeSeed.ServiceAccount(state, "loader", "analysts");
        var agentUser = new AccessTokens(state, new IdentityUserStore(state)).FindServiceAccount("loader")!.UserId;
        directory.Assign(AssignmentSubject.User(agentUser), serverId, ServerRole.Viewer | ServerRole.Author | ServerRole.Approver);
        var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };
        return (http, state, ada, bob, agentToken, bea, linus);
    }

    private static async Task<JsonObject> CallAsync(HttpClient http, string? token, string tool, JsonObject? arguments = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments?.DeepClone() ?? new JsonObject() } }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!;
    }

    private static async Task<(HttpStatusCode Status, JsonObject Body)> RouteAsync(HttpClient http, string? token, string path, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? [] : (JsonObject)JsonNode.Parse(text)!);
    }

    private static string Text(JsonObject result) => (string)result["content"]![0]!["text"]!;

    [Fact]
    public async Task A_read_tool_answers_at_once_with_the_callers_own_access_rules()
    {
        var (http, _, ada, bob, _, _, _) = await StartAsync();

        var models = await CallAsync(http, ada, "list_semantic_models");
        var asAda = await CallAsync(http, ada, "query_semantic", new() { ["model"] = "sales_catalog", ["request"] = JsonNode.Parse(Request) });
        var bobsModels = await CallAsync(http, bob, "list_semantic_models");
        var asBob = await CallAsync(http, bob, "query_semantic", new() { ["model"] = "sales_catalog", ["request"] = JsonNode.Parse(Request) });

        Assert.False((bool)models["isError"]!);
        Assert.Contains("sales_catalog", Text(models));
        Assert.False((bool)asAda["isError"]!, Text(asAda));
        Assert.NotEmpty((JsonArray)asAda["structuredContent"]!["rows"]!);
        // Bob's home groups reach no data source the model reads: it is not listed to him and a query is refused, not run
        Assert.DoesNotContain("sales_catalog", Text(bobsModels));
        Assert.True((bool)asBob["isError"]!);
        Assert.Null(asBob["structuredContent"]);
    }

    [Fact]
    public async Task An_agents_write_waits_a_person_approves_it_once_and_the_one_who_asked_cannot()
    {
        var (http, state, ada, _, agent, bea, linus) = await StartAsync();
        var write = new JsonObject { ["model"] = "nowhere", ["changes"] = new JsonArray(new JsonObject { ["action"] = "insert", ["entity"] = "territory", ["values"] = new JsonObject { ["code"] = "NO" } }), ["description"] = "add north" };

        var asked = await CallAsync(http, ada, "write_entity_changes", write);
        Assert.StartsWith("pending approval ", Text(asked));
        var id = Text(asked)["pending approval ".Length..];
        var audit = new AgentStore(state);
        Assert.DoesNotContain(audit.Audit(origin: "mcp"), a => a.Kind is "tool-called" or "tool-failed");

        // nobody answered: the one who asked, an agent (even with the role) and a person without the role are refused, and nothing ran
        Assert.Equal(HttpStatusCode.Forbidden, (await RouteAsync(http, ada, $"/api/v1/mcp/approvals/{id}/approve")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await RouteAsync(http, agent, $"/api/v1/mcp/approvals/{id}/approve")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await RouteAsync(http, linus, $"/api/v1/mcp/approvals/{id}/approve")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RouteAsync(http, null, $"/api/v1/mcp/approvals/{id}/approve")).Status);
        Assert.DoesNotContain(audit.Audit(origin: "mcp"), a => a.Kind is "tool-called" or "tool-failed");

        // an approver lists what waits and approves: the write runs as the one who asked (here it finds no such model, so it is recorded as failed, once)
        var waiting = await RouteAsync(http, bea, "/api/v1/mcp/approvals", HttpMethod.Get);
        Assert.Equal(id, (string)waiting.Body["approvals"]![0]!["id"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await RouteAsync(http, linus, "/api/v1/mcp/approvals", HttpMethod.Get)).Status);
        var approved = await RouteAsync(http, bea, $"/api/v1/mcp/approvals/{id}/approve");
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.False((bool)approved.Body["ran"]!);
        Assert.Contains("nowhere", (string)approved.Body["error"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await RouteAsync(http, bea, $"/api/v1/mcp/approvals/{id}/approve")).Status);
        Assert.Single(audit.Audit(origin: "mcp"), a => a.Kind == "tool-failed");

        var status = await CallAsync(http, ada, "approval_status", new() { ["id"] = id });
        Assert.Equal("approved", (string)status["structuredContent"]!["status"]!);
    }

    [Fact]
    public async Task A_denied_request_never_runs_and_a_caller_without_the_author_role_is_not_even_asked_about()
    {
        var (http, state, ada, _, _, bea, linus) = await StartAsync();
        var write = new JsonObject { ["model"] = "nowhere", ["changes"] = new JsonArray(new JsonObject { ["action"] = "insert", ["entity"] = "t", ["values"] = new JsonObject() }) };

        var id = Text(await CallAsync(http, ada, "write_entity_changes", write))["pending approval ".Length..];
        var denied = await RouteAsync(http, bea, $"/api/v1/mcp/approvals/{id}/deny");
        var late = await RouteAsync(http, bea, $"/api/v1/mcp/approvals/{id}/approve");
        var viewers = await CallAsync(http, linus, "write_entity_changes", write);

        Assert.Equal("denied", (string)denied.Body["status"]!);
        Assert.Equal(HttpStatusCode.Conflict, late.Status);
        Assert.DoesNotContain(new AgentStore(state).Audit(origin: "mcp"), a => a.Kind is "tool-called" or "tool-failed");
        Assert.True((bool)viewers["isError"]!);
        Assert.Single(new AgentStore(state).Approvals(false, null, 50));
    }

    [Fact]
    public async Task The_module_has_its_line_on_the_health_report_and_asks_for_a_token()
    {
        var (http, _, _, _, _, _, _) = await StartAsync();

        var report = await HealthEvaluator.EvaluateAsync(app!.Services.GetServices<IReadinessProbe>(), app.Services.GetRequiredService<IDrainCoordinator>(), default);
        var anonymous = await http.PostAsync("/mcp", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"));
        var stream = await http.GetAsync("/mcp");

        Assert.Equal(HealthStatus.Healthy, report.Components.Single(c => c.Name == "mcp").Status);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, stream.StatusCode);
        await app.Services.GetRequiredService<GatewayMcpModule>().StopAsync(default);
        var after = await HealthEvaluator.EvaluateAsync(app.Services.GetServices<IReadinessProbe>(), app.Services.GetRequiredService<IDrainCoordinator>(), default);
        Assert.Equal(HealthStatus.Unhealthy, after.Components.Single(c => c.Name == "mcp").Status);
    }
}
