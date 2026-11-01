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

/// <summary>The suite's lineage in the console (plan 18, G09): a Sync server and a Build server each send their graph, and a table Sync writes and a Build model reads is one node.</summary>
public sealed class ConsoleLineageTests : IAsyncLifetime
{
    private const string Crm = "sqlserver://sql01/crm.dbo.customers";
    private const string Warehouse = "postgres://pg01/dwh.public.customers";
    private const string Model = "postgres://pg01/dwh.marts.dim_customer";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-lineage-" + Guid.NewGuid().ToString("N")[..8]);
    private int syncCalls;
    private bool syncFails;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer sync = null!;
    private PairableServer build = null!;
    private PairableServer proxy = null!;

    private static LineageGraph SyncGraph() => new(
        [new LineageNode(Crm, NodeKind.Dataset, "dbo.customers", UnknownUpstream: true), new LineageNode(Warehouse, NodeKind.Dataset, "public.customers")],
        [new LineageEdge(Crm, Warehouse, EdgeKind.Replicates, Granularity.Table, Confidence.Declared, "crm-to-dwh/customers")]);

    private static LineageGraph BuildGraph() => new(
        [new LineageNode(Warehouse, NodeKind.Dataset, "public.customers", UnknownUpstream: true), new LineageNode(Model, NodeKind.Dataset, "marts.dim_customer")],
        [new LineageEdge(Warehouse, Model, EdgeKind.Derives, Granularity.Table, Confidence.Derived)]);

    public async ValueTask InitializeAsync()
    {
        sync = await PairableServer.StartAsync(product: "dbdatasync", instanceId: "sync-1", map: (server, _, _) =>
            server.MapGet("/api/lineage", () =>
            {
                Interlocked.Increment(ref syncCalls);
                if (syncFails) return Results.Json(new { error = "the configuration cannot be read" }, statusCode: 500);
                var document = LineageJson.Write(SyncGraph());
                document["problems"] = new JsonArray("Replication 'old' is left out: its configuration could not be read (ConfigFileException).");
                return Results.Json(document);
            }));
        build = await PairableServer.StartAsync(product: "dbdatabuild", instanceId: "build-1", map: (server, _, _) =>
            server.MapGet("/api/v1/lineage", () => { var document = LineageJson.Write(BuildGraph()); document["problems"] = new JsonArray(); return Results.Json(document); }));
        proxy = await PairableServer.StartAsync(product: "dbdataproxy", instanceId: "proxy-1");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(sync.Url, sync.NewCodeWith(ServerRole.Viewer), "Sync one", default);
        await instances.PairAsync(build.Url, build.NewCodeWith(ServerRole.Viewer), "Build one", default);
        await instances.PairAsync(proxy.Url, proxy.NewCodeWith(ServerRole.Viewer), "Proxy one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { sync, build, proxy }) await s.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(HttpStatusCode Status, JsonNode Body)> Get(string path, UserRole role = UserRole.Viewer)
    {
        var session = app.Services.GetRequiredService<SessionStore>().Create(app.Services.GetRequiredService<UserStore>().CreateUser("Ana-" + Guid.NewGuid().ToString("N")[..6], null, role).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task A_table_one_server_writes_and_another_reads_is_one_node_and_the_flow_runs_through_it()
    {
        var (status, body) = await Get("/api/lineage");

        Assert.Equal(HttpStatusCode.OK, status);
        var graph = LineageJson.Read(body);
        Assert.Equal(3, graph.Nodes.Count);                                                  // the warehouse table is one node
        Assert.False(graph.Node(Warehouse)!.UnknownUpstream);                                // Build did not know its upstream; Sync does
        Assert.Equal(2, graph.Edges.Count);
        var servers = body["servers"]!.AsArray();
        Assert.Equal(["Build one", "Sync one"], servers.Select(s => (string)s!["server"]!));   // the Proxy keeps no lineage and is not part of it
        Assert.Contains(servers[1]!["problems"]!.AsArray(), p => ((string)p!).Contains("Replication 'old' is left out"));
    }

    [Fact]
    public async Task The_impact_of_a_source_table_reaches_the_model_across_both_servers_and_the_provenance_of_the_model_reaches_the_source()
    {
        var (_, impact) = await Get($"/api/lineage?node={Uri.EscapeDataString(Crm)}&direction=downstream");
        var (_, provenance) = await Get($"/api/lineage?node={Uri.EscapeDataString(Model)}&direction=upstream");

        Assert.Equal([Warehouse, Model], impact["reached"]!.AsArray().Select(r => (string)r!["id"]!));
        Assert.Contains(Crm, provenance["reached"]!.AsArray().Select(r => (string)r!["id"]!));
        Assert.Equal((HttpStatusCode.NotFound, true), ((await Get("/api/lineage?node=nowhere")).Status, true));
    }

    [Fact]
    public async Task A_server_that_fails_is_named_and_left_out_and_the_others_still_show_and_an_answer_is_kept_until_a_refresh_is_asked_for()
    {
        await Get("/api/lineage");
        await Get("/api/lineage");
        Assert.Equal(1, syncCalls);                                                          // the second answer came from what was kept

        syncFails = true;
        await Get("/api/lineage?refresh=true");
        Assert.Equal(2, syncCalls);
        var (_, body) = await Get("/api/lineage");
        var failed = body["servers"]!.AsArray().Single(s => (string)s!["server"]! == "Sync one");
        Assert.Contains("the configuration cannot be read", (string)failed!["error"]!);
        Assert.Equal(2, LineageJson.Read(body).Nodes.Count);                                 // Build's graph alone
    }
}
