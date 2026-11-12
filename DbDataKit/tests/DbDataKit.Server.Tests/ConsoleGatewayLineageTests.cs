using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 18 G11b: the console's graph shows a measure's path from the source table through the Sync replication and the Build model to the Gateway that serves it. Each server sends what it knows (Sync the replication of a column, Build the model
/// that reads it, the Gateway the measure bound to the column and the model it serves), and the three meet on the identity of the column.
/// </summary>
public sealed class ConsoleGatewayLineageTests : IAsyncLifetime
{
    private const string Crm = "sqlserver://sql01/crm.dbo.orders";
    private const string Warehouse = "postgres://pg01/dwh.public.orders";
    private const string Model = "postgres://pg01/dwh.marts.fct_orders";
    private const string Served = "suite://gateway-1/semantic-model/sales";
    private const string Measure = "suite://gateway-1/semantic/sales/orders.revenue";
    private const string Source = "suite://gateway-1/data-source/dwh";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-gwlineage-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer sync = null!;
    private PairableServer build = null!;
    private PairableServer gateway = null!;

    private static LineageGraph SyncGraph() => new(
        [new LineageNode(Crm, NodeKind.Dataset, "dbo.orders", true), new LineageNode(Crm + "#amount", NodeKind.Column, "amount", true), new LineageNode(Warehouse, NodeKind.Dataset, "public.orders"), new LineageNode(Warehouse + "#amount", NodeKind.Column, "amount")],
        [new LineageEdge(Crm, Warehouse, EdgeKind.Replicates, Granularity.Table, Confidence.Declared), new LineageEdge(Crm + "#amount", Warehouse + "#amount", EdgeKind.Replicates, Granularity.Column, Confidence.Declared)]);

    private static LineageGraph BuildGraph() => new(
        [new LineageNode(Warehouse, NodeKind.Dataset, "public.orders", true), new LineageNode(Warehouse + "#amount", NodeKind.Column, "amount", true), new LineageNode(Model, NodeKind.Dataset, "marts.fct_orders"), new LineageNode(Model + "#revenue", NodeKind.Column, "revenue")],
        [new LineageEdge(Warehouse, Model, EdgeKind.Derives, Granularity.Table, Confidence.Derived), new LineageEdge(Warehouse + "#amount", Model + "#revenue", EdgeKind.Derives, Granularity.Column, Confidence.Derived, "sum")]);

    private static LineageGraph GatewayGraph() => new(
        [new LineageNode(Model, NodeKind.Dataset, "marts.fct_orders", true), new LineageNode(Model + "#revenue", NodeKind.Column, "revenue", true), new LineageNode(Measure, NodeKind.SemanticMember, "sales: orders.revenue"),
            new LineageNode(Served, NodeKind.Serving, "sales"), new LineageNode(Source, NodeKind.Serving, "dwh")],
        [new LineageEdge(Model + "#revenue", Measure, EdgeKind.Binds, Granularity.Column, Confidence.Derived, "sum"), new LineageEdge(Measure, Served, EdgeKind.Serves, Granularity.Table, Confidence.Derived),
            new LineageEdge(Model, Source, EdgeKind.Serves, Granularity.Table, Confidence.Derived)]);

    public async ValueTask InitializeAsync()
    {
        sync = await PairableServer.StartAsync(product: "dbdatasync", instanceId: "sync-1", map: (server, _, _) => server.MapGet("/api/lineage", () => Results.Json(LineageJson.Write(SyncGraph()))));
        build = await PairableServer.StartAsync(product: "dbdatabuild", instanceId: "build-1", map: (server, _, _) => server.MapGet("/api/v1/lineage", () => Results.Json(LineageJson.Write(BuildGraph()))));
        gateway = await PairableServer.StartAsync(product: "dbdatagateway", instanceId: "gateway-1", map: (server, _, _) => server.MapGet("/api/v1/lineage", () =>
        {
            // what the Gateway sends: its graph and the last read of a served table as a run along its edge, with the lateness its product says (G10b)
            var document = LineageJson.Write(GatewayGraph());
            document["runs"] = LineageJson.WriteRuns([new EdgeRun(Model, Source, EdgeKind.Serves, new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero), RunOutcome.Succeeded, Late: true)]);
            return Results.Json(document);
        }));
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(sync.Url, sync.NewCodeWith(ServerRole.Viewer), "Sync one", default);
        await instances.PairAsync(build.Url, build.NewCodeWith(ServerRole.Viewer), "Build one", default);
        await instances.PairAsync(gateway.Url, gateway.NewCodeWith(ServerRole.Viewer), "Gateway one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { sync, build, gateway }) await s.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<JsonNode> Get(string path)
    {
        var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ana-" + Guid.NewGuid().ToString("N")[..6], null, UserRole.Viewer).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task A_measure_is_traced_from_the_source_table_through_the_replication_and_the_model_to_the_gateway_that_serves_it()
    {
        var body = await Get("/api/lineage");
        var graph = LineageJson.Read(body);

        // the three servers' columns meet: one node each
        Assert.Equal(1, graph.Nodes.Count(n => n.Id == Model + "#revenue"));
        var path = graph.Path(Crm + "#amount", Served);
        Assert.NotNull(path);
        Assert.Equal([EdgeKind.Replicates, EdgeKind.Derives, EdgeKind.Binds, EdgeKind.Serves], path!.Select(e => e.Kind));

        var provenance = await Get($"/api/lineage?node={Uri.EscapeDataString(Served)}&direction=upstream");
        Assert.Contains(Crm + "#amount", provenance["reached"]!.AsArray().Select(r => (string)r!["id"]!));
        var impact = await Get($"/api/lineage?node={Uri.EscapeDataString(Crm + "#amount")}&direction=downstream");
        Assert.Contains(Served, impact["reached"]!.AsArray().Select(r => (string)r!["id"]!));
        // the run overlay: the Gateway's run comes through the join with the lateness its server said
        var run = Assert.Single(LineageJson.ReadRuns(body));
        Assert.Equal((Model, Source, EdgeKind.Serves, true), (run.From, run.To, run.Kind, run.Late));
        Assert.Equal(["Build one", "Gateway one", "Sync one"], body["servers"]!.AsArray().Select(s => (string)s!["server"]!));
    }
}
