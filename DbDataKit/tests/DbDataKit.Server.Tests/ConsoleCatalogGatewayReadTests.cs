using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Catalog;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The refresh of a draft's catalog cache from a Gateway's data source, through the console (<see cref="ConsoleCatalogRead.GatewayAsync"/>): a real Workspace server in this process, paired with the console only and with no publisher key, so
/// it cannot reach a Gateway or a Runner; and a stand-in Gateway with real pairing that answers its schema call. The console asks the Workspace server whether the person may read the project, asks the Gateway as the person, and answers the
/// connection as <c>catalog-cache/2</c> recorded as read from the Gateway. A data source made by hand is read only once the person confirmed it; a table name that looks like a secret is refused before the screen holds it.
/// </summary>
public sealed class ConsoleCatalogGatewayReadTests : IAsyncLifetime
{
    private const string GatewayId = "gateway-cat";
    private const string Project = "crm-to-dw";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-catalog-gateway-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<string> asked = new();
    private WebApplication console = null!;
    private WebApplication workspace = null!;
    private PairableServer gateway = null!;
    private HttpClient http = null!;
    private string session = null!;
    private string workspaceId = null!;

    /// <summary>The name of the table the stand-in Gateway answers; a test sets one that looks like a connection string.</summary>
    private string table = "orders";

    public async ValueTask InitializeAsync()
    {
        gateway = await PairableServer.StartAsync(product: ServerProducts.Gateway, instanceId: GatewayId, map: MapGateway);
        var workspaceDirectory = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspaceDirectory);
        workspace = WorkspaceServer.Build(new WorkspaceSettings(workspaceDirectory, new Uri("https://127.0.0.1:0")), WorkspaceServer.Certificate(workspaceDirectory), _ => { });
        await workspace.StartAsync();

        var consoleDirectory = Path.Combine(directory, "console");
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
        session = console.Services.GetRequiredService<ISessionStore>().Create(console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id).Id;
        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        var code = WorkspaceState.Pairing(workspaceDirectory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
        workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), code.Code, "Workspace", default)).InstanceId;
        await instances.PairAsync(gateway.Url, gateway.NewCodeWith(ServerRole.Admin | ServerRole.Publisher), "Gateway one", default);

        var (made, body) = await As(HttpMethod.Post, $"/relay/{workspaceId}/api/v1/projects", new { id = Project, kind = "sync" });
        Assert.True(made == HttpStatusCode.Created, $"{made} {body}");
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await console.StopAsync();
        await console.DisposeAsync();
        await workspace.StopAsync();
        await workspace.DisposeAsync();
        await gateway.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The Gateway's schema call, as the real one answers it: <c>warehouse</c> was made by the console's deploy and is read at once; <c>legacy</c> was made by hand and is read only with <c>{"adopt": "legacy"}</c>, else refused with
    /// the data source to confirm. Every call is kept with the person the token names.
    /// </summary>
    private void MapGateway(WebApplication server, PairingCodes codes, PairedClientsFile clients) =>
        server.MapPost("/api/v1/data-sources/{name}/schema", async (string name, HttpContext c) =>
        {
            var body = JsonNode.Parse(await new StreamReader(c.Request.Body).ReadToEndAsync()) as JsonObject;
            asked.Enqueue($"schema {name} by {c.ManagementToken().SubjectName} adopt={(string?)body?["adopt"]}");
            if (name == "legacy" && (string?)body?["adopt"] != "legacy")
                return Results.Json(new JsonObject
                {
                    ["error"] = "The data source 'legacy' was made by hand or by an administrator, not by this client.",
                    ["adopt"] = new JsonObject { ["data_source"] = "legacy", ["made_by"] = "hand", ["field"] = "adopt" },
                }, statusCode: 409);
            if (name is not ("warehouse" or "legacy")) return Results.Json(new { error = $"There is no data source '{name}'." }, statusCode: 404);
            return Results.Json(new JsonObject
            {
                ["data_source"] = name, ["engine"] = "postgres", ["truncated"] = false,
                ["tables"] = new JsonArray(new JsonObject
                {
                    ["schema"] = "public", ["name"] = table, ["kind"] = "table",
                    ["columns"] = new JsonArray(
                        new JsonObject { ["name"] = "id", ["type"] = "int64", ["nullable"] = false, ["key"] = true },
                        new JsonObject { ["name"] = "placed_at", ["type"] = "timestamp", ["nullable"] = true, ["key"] = false }),
                }),
            });
        });

    private async Task<(HttpStatusCode Status, JsonObject Body)> As(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && JsonNode.Parse(text) is JsonObject o ? o : []);
    }

    private string Route => $"/api/workspace-catalog/{workspaceId}/{Project}/gateway-read";

    [Fact]
    public async Task A_refresh_from_a_gateway_reads_the_data_source_as_the_person_through_the_console_while_the_workspace_server_can_reach_no_gateway()
    {
        // the Workspace server holds no publisher key and lists no target: the relay passes no such call
        Assert.Equal(HttpStatusCode.Forbidden, (await As(HttpMethod.Get, $"/relay/{workspaceId}/api/v1/publish-targets")).Status);

        var (status, body) = await As(HttpMethod.Post, Route, new { gateway = GatewayId, connection = "dw", data_source = "warehouse" });
        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Equal(("done", "dw", "warehouse", "Gateway one"), ((string?)body["state"], (string?)body["connection"], (string?)body["data_source"], (string?)body["gateway_name"]));
        Assert.Equal(["schema warehouse by Ada Admin adopt="], asked.ToArray());

        var load = CatalogCache.Load((string)body["cache"]!);
        Assert.True(load.Ok, string.Join("\n", load.Diagnostics.Select(d => d.Message)));
        var connection = Assert.Single(load.Cache!.Connections);
        Assert.Equal("dw", connection.Key);
        Assert.Equal(("Gateway one", "gateway", (string?)null), (connection.Value.Read!.Server, connection.Value.Read.Product, connection.Value.Read.Channel));
        Assert.Equal(["id", "placed_at"], Assert.Single(connection.Value.Tables).Columns.Select(c => c.Name).ToArray());
        Assert.Equal((1, 2), ((int)body["tables"]!, (int)body["columns"]!));
    }

    [Fact]
    public async Task The_connections_name_is_the_data_source_when_none_is_given_and_a_data_source_made_by_hand_is_read_only_once_the_person_confirmed_it()
    {
        var (status, _) = await As(HttpMethod.Post, Route, new { gateway = GatewayId, connection = "warehouse" });
        Assert.Equal(HttpStatusCode.OK, status);

        var (refused, why) = await As(HttpMethod.Post, Route, new { gateway = GatewayId, connection = "old", data_source = "legacy" });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("made by hand", (string)why["error"]!);
        Assert.Equal(("legacy", "hand"), ((string?)why["adopt"]!["data_source"], (string?)why["adopt"]!["made_by"]));

        var (confirmed, read) = await As(HttpMethod.Post, Route, new { gateway = GatewayId, connection = "old", data_source = "legacy", adopt = true });
        Assert.True(confirmed == HttpStatusCode.OK, $"{confirmed} {read}");
        Assert.Equal(["schema warehouse by Ada Admin adopt=", "schema legacy by Ada Admin adopt=", "schema legacy by Ada Admin adopt=legacy"], asked.ToArray());
    }

    [Fact]
    public async Task An_answer_that_looks_like_a_secret_is_refused_and_a_server_that_is_not_a_gateway_or_a_project_it_does_not_have_reaches_no_gateway()
    {
        table = "Server=db1;User Id=sa;Password=hunter2";
        var (refused, why) = await As(HttpMethod.Post, Route, new { gateway = GatewayId, connection = "dw", data_source = "warehouse" });
        Assert.Equal(HttpStatusCode.BadGateway, refused);
        Assert.DoesNotContain("hunter2", why.ToJsonString());
        asked.Clear();

        var (notGateway, said) = await As(HttpMethod.Post, Route, new { gateway = workspaceId, connection = "dw" });
        Assert.Equal(HttpStatusCode.Conflict, notGateway);
        Assert.Contains("is not a Gateway", (string)said["error"]!);

        var (unknown, none) = await As(HttpMethod.Post, $"/api/workspace-catalog/{workspaceId}/unknown/gateway-read", new { gateway = GatewayId, connection = "dw" });
        Assert.Equal(HttpStatusCode.NotFound, unknown);
        Assert.Contains("no project 'unknown'", (string)none["error"]!);
        Assert.Empty(asked);
    }
}
