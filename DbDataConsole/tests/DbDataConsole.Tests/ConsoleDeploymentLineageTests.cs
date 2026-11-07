using System.Net;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Lineage;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>Plan 28c E24: a paired runner sends its deployment graph through the relay, and the console answers "where is this deployed" and "what is deployed on this runner" from it.</summary>
public sealed class ConsoleDeploymentLineageTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-deploy-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer runner = null!;

    private static LineageNode Node(string id, string type, string name, params (string, string)[] properties) =>
        new(id, NodeKind.Deployment, name, false, new Dictionary<string, string>(properties.ToDictionary(p => p.Item1, p => p.Item2)) { [DeploymentProperties.Type] = type });

    private static LineageGraph Graph()
    {
        var package = DeploymentIds.Package("main", "integration", "orders");
        var channel = DeploymentIds.Channel("main", "integration", "orders", "prod");
        var v2 = DeploymentIds.Version("main", "integration", "orders", 2);
        var (a, b) = (DeploymentIds.Instance("main", "aaa"), DeploymentIds.Instance("main", "bbb"));
        return new LineageGraph(
        [
            Node(package, DeploymentTypes.Package, "orders", ("kind", "integration"), ("name", "orders")),
            Node(v2, DeploymentTypes.Version, "orders v2", ("kind", "integration"), ("name", "orders"), ("version", "2")),
            Node(channel, DeploymentTypes.Channel, "orders on prod", ("kind", "integration"), ("name", "orders"), ("channel", "prod"), ("group", "main"), ("active", "2"), ("previous", "1"), ("last_event", "activate"), ("last_by", "grace"), ("last_at", At.ToString("O"))),
            Node(a, DeploymentTypes.Instance, "host-a", ("instance_id", "aaa"), ("host", "host-a"), ("group", "main")),
            Node(b, DeploymentTypes.Instance, "host-b", ("instance_id", "bbb"), ("host", "host-b"), ("group", "main")),
        ],
        [
            new LineageEdge(package, v2, EdgeKind.Deploys, Granularity.Table, Confidence.Declared, "version"),
            new LineageEdge(v2, channel, EdgeKind.Deploys, Granularity.Table, Confidence.Declared, "active"),
            new LineageEdge(channel, a, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, new DeploymentDetail(2, "active", At).Format()),
            new LineageEdge(channel, b, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, new DeploymentDetail(1, "ready", At).Format()),
        ]);
    }

    public async ValueTask InitializeAsync()
    {
        runner = await PairableServer.StartAsync(product: "dbdatarunner", instanceId: "runner-1", map: (server, _, _) =>
            server.MapGet("/api/v1/lineage", () => Results.Json(LineageJson.Write(Graph()))));
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(runner.Url, runner.NewCodeWith(ServerRole.Viewer), "Runner one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await runner.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(HttpStatusCode Status, JsonNode Body)> Get(string path)
    {
        var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ana-" + Guid.NewGuid().ToString("N")[..6], null, UserRole.Viewer).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task Where_is_this_deployed_and_what_is_deployed_on_this_runner_are_answered_from_the_runners_graph()
    {
        var (status, where) = await Get("/api/lineage/deployments?definition=orders");
        Assert.Equal(HttpStatusCode.OK, status);
        var channel = where["channels"]![0]!;
        Assert.Equal((2, 1, "grace"), ((int)channel["active"]!, (int)channel["previous"]!, (string)channel["last_by"]!));
        var behind = channel["instances"]!.AsArray().Single(i => (string)i!["host"]! == "host-b")!;
        Assert.Equal((1, "ready", false), ((int)behind["version"]!, (string)behind["state"]!, (bool)behind["on_active"]!));

        var (_, on) = await Get("/api/lineage/deployments?runner=host-a");
        Assert.Equal("active", (string)on["instances"]![0]!["holdings"]![0]!["state"]!);

        var (_, all) = await Get("/api/lineage/deployments");
        Assert.Equal("orders", (string)all["definitions"]![0]!["name"]!);
        Assert.Equal(2, all["runners"]!.AsArray().Count);

        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/lineage/deployments?definition=nothing")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/lineage/deployments?runner=nowhere")).Status);
    }

    [Fact]
    public async Task The_joined_graph_carries_the_deployment_nodes_of_the_runner()
    {
        var (_, graph) = await Get("/api/lineage");
        Assert.Contains(graph["nodes"]!.AsArray(), n => (string)n!["kind"]! == "deployment");
        Assert.Contains(graph["servers"]!.AsArray(), s => (string)s!["product"]! == "dbdatarunner");
    }
}
