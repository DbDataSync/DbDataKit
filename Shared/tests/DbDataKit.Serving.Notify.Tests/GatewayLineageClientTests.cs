using System.Net;
using DbDataKit.Lineage;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;

namespace DbDataKit.Serving.Notify.Tests;

/// <summary>Plan 21 CH06: how a product server reads the lineage of the serving groups it is paired with (fake gateways: the real Gateway's side is in <c>DbDataGateway.Tests</c>), and what the contract guard makes of it.</summary>
public sealed class GatewayLineageClientTests
{
    private const string Table = "sqlserver://dw/sales.orders";
    private readonly ConsoleIdentity _self = new("store-server-1", ConsoleSigningKey.Generate());

    private sealed class Pairings(params GatewayPairing[] all) : IGatewayPairings
    {
        public IReadOnlyList<GatewayPairing> All() => all;
    }

    private sealed class Fake(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Path, string Token)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsolutePath, request.Headers.GetValues(ManagementAuthentication.TokenHeader).Single()));
            return Task.FromResult(answer());
        }
    }

    private static GatewayPairing Pairing(string name) => new(name, "https://127.0.0.1:1", "gateway-" + name, "ab", DateTimeOffset.UnixEpoch);

    private static HttpResponseMessage Graph()
    {
        var consumer = new LineageNode("suite://g/dashboard/sales~test", NodeKind.Consumer, "sales", false, new Dictionary<string, string> { ["consumer"] = "dashboard", ["channel"] = "test" });
        var graph = new LineageGraph([new(Table, NodeKind.Dataset, "sales.orders"), new(Table + "#price", NodeKind.Column, "price"), new("suite://g/semantic/sales", NodeKind.Serving, "sales"), consumer],
            [new(Table + "#price", "suite://g/semantic/sales", EdgeKind.Binds, Granularity.Column, Confidence.Derived), new("suite://g/semantic/sales", consumer.Id, EdgeKind.Reads, Granularity.Table, Confidence.Declared)]);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LineageJson.Write(graph).ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }

    private GatewayLineageClient Client(IGatewayPairings pairings, Func<GatewayPairing, HttpMessageHandler> handler) =>
        new(pairings, _ => Task.FromResult(_self), product: "store", handler: handler);

    [Fact]
    public async Task The_graph_of_each_pairing_is_read_as_the_server_with_the_publisher_role_and_the_guard_names_the_channel()
    {
        var gateway = new Fake(Graph);
        var result = await Client(new Pairings(Pairing("edge")), _ => gateway).FetchAsync();

        var call = Assert.Single(gateway.Calls);
        Assert.Equal("/api/v1/lineage", call.Path);
        Assert.Empty(result.Unreachable);
        var verdict = ContractGuard.Check([new ContractedColumn("drop the column 'price' of 'sales.orders'", Table + "#price", Table)], result.Graphs, result.Unreachable, []);
        Assert.Contains("the dashboard 'sales' published in channel 'test'", Assert.Single(verdict.Refusals));
    }

    [Fact]
    public async Task No_pairing_asks_nothing_and_finds_nothing_unreachable()
    {
        var gateway = new Fake(Graph);
        var result = await Client(new Pairings(), _ => gateway).FetchAsync();
        Assert.Empty(gateway.Calls);
        Assert.Empty(result.Graphs);
        Assert.Empty(result.Unreachable);
    }

    [Fact]
    public async Task A_gateway_that_refuses_or_is_down_is_named_unreachable_and_the_others_are_still_read()
    {
        var refusing = new Fake(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":"This call needs the viewer or publisher role."}""") });
        var good = new Fake(Graph);
        var result = await Client(new Pairings(Pairing("bad"), Pairing("good")), p => p.Name == "bad" ? refusing : good).FetchAsync();
        Assert.Equal(["good"], result.Graphs.Select(g => g.Server));
        var (server, why) = Assert.Single(result.Unreachable);
        Assert.Equal("bad", server);
        Assert.Contains("403", why);
        Assert.Contains("viewer or publisher role", why);
    }

    [Fact]
    public async Task A_pairings_file_that_cannot_be_read_is_an_unreachable_source_not_a_silent_pass()
    {
        var dir = Directory.CreateTempSubdirectory("lineage-client-").FullName;
        try
        {
            var path = Path.Combine(dir, "gateways.json");
            File.WriteAllText(path, "not json");
            var result = await Client(new GatewayPairingsFile(path), _ => new Fake(Graph)).FetchAsync();
            Assert.Equal("pairings", Assert.Single(result.Unreachable).Server);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
