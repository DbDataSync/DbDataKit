using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Lineage;
using DbDataKit.Serving.Notify;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 24a UC13, the producers against a real Gateway started in this process: a fake product server (its own identity, paired with the Gateway as a server client with the Publisher role) tells it that datasets changed with the shared notifier.
/// The dataset is named as the product names it (<c>LineageIdentity.Dataset</c>) and found in the Gateway's own lineage graph; a dataset the graph does not know is heard and ignored, not a clear of everything; the call is signed as the server (the audit names it) and a
/// forged, replayed or row-carrying message is refused; the console's cache screen (<c>GET /api/v1/cache</c>) shows what the cache heard.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GatewayDataChangedProducersTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dbgateway-changed-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int managementPort;
    private GatewayPairing pairing = null!;
    private readonly ConsoleIdentity product = new("sync-server-test", ConsoleSigningKey.Generate());

    private string DataDirectory => Path.Combine(root, "data");

    private Uri ManagementUrl => new($"https://127.0.0.1:{managementPort}");

    private PairingFiles Files => new(GatewayConfigFile.PathIn(DataDirectory), Path.Combine(DataDirectory, "pairing-codes.json"));

    private StateDatabase State => app!.Services.GetRequiredService<StateDatabase>();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(DataDirectory));
        File.WriteAllText(GatewayConfigFile.PathIn(DataDirectory), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        for (var attempt = 1; ; attempt++)
        {
            managementPort = FreePort();
            app = GatewayHost.Build([
                $"--Gateway:DataDirectory={DataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0",
                "--Gateway:Management:Enabled=true", "--Gateway:Management:Address=127.0.0.1", $"--Gateway:Management:Port={managementPort}", "--Gateway:Management:LocalChannel=false",
                "--Gateway:DataSources:erp:Provider=sqlserver", "--Gateway:DataSources:erp:ConnectionString=Server=Warehouse01,1433;Database=Sales;Integrated Security=true", "--Gateway:DataSources:erp:Everyone=true",
            ]);
            try { await app.StartAsync(); break; }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)) { await app.DisposeAsync(); }
        }

        // the Gateway has seen a statement read sales.orders: that is how a table is a node of its graph before any model names it
        var recorder = new TableReadRecorder(new TableReadStore(State));
        recorder.Record("erp", "SELECT * FROM sales.orders", "ada");
        Assert.True(recorder.Flush());

        // the product server pairs as a server client (the Publisher role: the pairing it publishes with)
        var code = Files.Codes().Create(ServerRoles.Close(ServerRole.Publisher | ServerRole.Viewer)).Code;
        pairing = await new GatewayPairingsFile(Path.Combine(root, "product", "gateways.json")).PairAsync(product, "edge", ManagementUrl, code, "Sync server test");
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(root);
    }

    /// <summary>The table as the product names it: its own definition of the same server and database the Gateway's data source points at.</summary>
    private static string Orders() => LineageIdentity.Dataset(new ConnectionDefinition { Name = "warehouse", Engine = "sqlserver", Address = new AddressSpec { Host = "Warehouse01", Port = 1433, Database = "Sales" } }, "sales", "orders").ToString();

    private DataChangeNotifier Notifier(IGatewayPairings? pairings = null, ConsoleIdentity? identity = null) =>
        new("sync", pairings ?? new GatewayPairingsFile(Path.Combine(root, "product", "gateways.json")), _ => Task.FromResult(identity ?? product), null, new DataChangeNotifierOptions { RetryAfter = TimeSpan.FromMilliseconds(50) });

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, object? body = null, string? token = null, ConsoleIdentity? identity = null, ServerRole role = ServerRole.Viewer)
    {
        using var http = new HttpClient(PairingClient.Pinned(pairing.Fingerprint)) { BaseAddress = ManagementUrl };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token ?? (identity ?? product).Issuer().Issue(new ConsoleTokenRequest(pairing.InstanceId, "test", "test", role)));
        if (body is string text) request.Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var answer = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, answer.Length > 0 ? JsonNode.Parse(answer) : null);
    }

    [Fact]
    public async Task The_name_a_product_gives_a_table_is_the_name_the_gateways_lineage_graph_holds()
    {
        var graph = await Call(HttpMethod.Get, "/api/v1/lineage");

        Assert.Equal(HttpStatusCode.OK, graph.Status);
        Assert.Contains(graph.Body!["nodes"]!.AsArray(), n => (string?)n!["id"] == Orders());
    }

    [Fact]
    public async Task A_dataset_the_gateway_serves_is_heard_and_one_it_does_not_is_ignored_and_the_screen_and_the_audit_say_so()
    {
        await using var notifier = Notifier();

        notifier.Publish([new DataChange(Orders(), "run-42"), new DataChange("mysql://elsewhere/shop.orders", "run-42")]);
        await notifier.FlushAsync();

        var stats = notifier.Stats;
        Assert.Equal((1L, 1L, 0L, 0L), (stats.Sent, stats.NotServed, stats.Retried, stats.Failed));
        var screen = (await Call(HttpMethod.Get, "/api/v1/cache")).Body!["events"]!;
        Assert.Equal((1L, 1L, "sync"), ((long)screen["received"]!, (long)screen["ignored"]!, (string)screen["last_product"]!));
        Assert.Contains((string)screen["last_dataset"]!, new[] { Orders(), "mysql://elsewhere/shop.orders" });   // the two messages are sent at once; either can be the last heard
        var audit = new StateAuditLog(State).Read(50);
        Assert.Contains(audit, a => a.Command == "cache changed" && a.Target == Orders() && a.UserName == "sync server");
        Assert.Contains(audit, a => a.Command == "cache changed" && a.Target == "mysql://elsewhere/shop.orders");
    }

    [Fact]
    public async Task A_message_without_the_flag_for_an_unknown_dataset_still_clears_everything_as_before()
    {
        var plain = await Call(HttpMethod.Post, "/api/v1/cache/changed", """{"message":"data.changed/1","dataset":"mysql://elsewhere/shop.orders","product":"build"}""", role: ServerRole.Publisher);

        Assert.Equal(HttpStatusCode.OK, plain.Status);
        Assert.Equal((true, true), ((bool)plain.Body!["everything"]!, (bool)plain.Body["served"]!));
    }

    [Fact]
    public async Task A_message_from_a_server_that_was_never_paired_a_replayed_token_and_a_body_with_rows_are_refused()
    {
        // a stranger: its token is signed by a key the Gateway never paired, so the channel refuses it before the message is read and the failure is counted, not thrown
        var stranger = new ConsoleIdentity("never-paired", ConsoleSigningKey.Generate());
        await using var forged = Notifier(identity: stranger);
        forged.Publish([new DataChange(Orders())]);
        await forged.FlushAsync();
        Assert.Equal((0L, 1L, 0L), (forged.Stats.Sent, forged.Stats.Failed, forged.Stats.Retried));
        Assert.Contains("401", forged.Stats.LastFailure);

        // the same token twice: the second is a replay
        var token = product.Issuer().Issue(new ConsoleTokenRequest(pairing.InstanceId, product.Id, "sync server", ServerRole.Publisher));
        var body = """{"message":"data.changed/1","dataset":"mysql://elsewhere/x.y","product":"sync","only_if_served":true}""";
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/cache/changed", body, token)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Post, "/api/v1/cache/changed", body, token)).Status);

        // no row data can travel in it
        var rows = await Call(HttpMethod.Post, "/api/v1/cache/changed", """{"message":"data.changed/1","dataset":"a/b","product":"sync","rows":[[1]]}""", role: ServerRole.Publisher);
        Assert.Equal(HttpStatusCode.BadRequest, rows.Status);
        Assert.Contains("`rows` is not a key", (string)rows.Body!["error"]!);
    }

    [Fact]
    public async Task A_gateway_that_is_stopped_is_a_counted_failure_after_one_retry_and_never_an_exception()
    {
        await using var notifier = Notifier();
        await app!.StopAsync();

        notifier.Publish([new DataChange(Orders())]);
        await notifier.FlushAsync();

        Assert.Equal((0L, 1L, 1L), (notifier.Stats.Sent, notifier.Stats.Failed, notifier.Stats.Retried));
        Assert.Contains("edge", notifier.Stats.LastFailure);
    }
}
