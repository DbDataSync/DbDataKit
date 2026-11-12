using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;

namespace DbDataKit.Serving.Notify.Tests;

/// <summary>
/// Plan 24a UC13, the producer side: a server that finished a dataset tells its paired gateways with <c>data.changed/1</c>, signed as itself (its client id, the Publisher role), only the dataset and no row data, with no pairing nothing is sent, and a failure is counted and
/// retried once, never thrown. The Gateway's side is tested against a real Gateway in <c>DbDataGateway.Tests</c> (<c>GatewayDataChangedProducersTests</c>).
/// </summary>
public sealed class DataChangeNotifierTests : IDisposable
{
    private const string Audience = "gateway-1";
    private readonly string _dir = Directory.CreateTempSubdirectory("notify-").FullName;
    private readonly ConsoleIdentity _self = new("sync-server-1", ConsoleSigningKey.Generate());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed record Seen(string Path, string Body, string Token);

    private sealed class FakeGateway(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Seen(request.RequestUri!.AbsolutePath, body, request.Headers.GetValues(ManagementAuthentication.TokenHeader).Single()));
            return answer(Calls.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class Pairings(params GatewayPairing[] all) : IGatewayPairings
    {
        public IReadOnlyList<GatewayPairing> All() => all;
    }

    private static GatewayPairing Pairing(string name = "edge") => new(name, "https://127.0.0.1:1", Audience, "ab", DateTimeOffset.UnixEpoch);

    private DataChangeNotifier Notifier(IGatewayPairings pairings, FakeGateway gateway, TimeSpan? retry = null) =>
        new("sync", pairings, _ => Task.FromResult(_self), null, new DataChangeNotifierOptions { RetryAfter = retry ?? TimeSpan.FromMilliseconds(20) }, handler: _ => gateway);

    [Fact]
    public async Task A_server_with_no_pairing_sends_nothing()
    {
        var gateway = new FakeGateway(_ => Json(HttpStatusCode.OK, "{}"));
        await using var notifier = Notifier(new Pairings(), gateway);

        notifier.Publish([new DataChange("sqlserver://wh/sales.dbo.orders")]);
        await notifier.FlushAsync();

        Assert.Empty(gateway.Calls);
        Assert.Equal(new DataChangeStats(0, 0, 0, 0, null, null, null, 0), notifier.Stats);
    }

    [Fact]
    public async Task The_message_names_the_dataset_the_product_and_the_run_with_nothing_else_and_asks_to_be_ignored_where_it_is_not_served()
    {
        var gateway = new FakeGateway(_ => Json(HttpStatusCode.OK, """{"served":true}"""));
        await using var notifier = Notifier(new Pairings(Pairing()), gateway);

        notifier.Publish([new DataChange("sqlserver://wh/sales.dbo.orders", "run-7", DateTimeOffset.Parse("2026-11-12T08:00:00Z")), new DataChange("sqlserver://wh/sales.dbo.orders")]);
        await notifier.FlushAsync();

        var call = Assert.Single(gateway.Calls);
        Assert.Equal("/api/v1/cache/changed", call.Path);
        var sent = (JsonObject)JsonNode.Parse(call.Body)!;
        Assert.Equal(["message", "dataset", "product", "at", "run", "only_if_served"], sent.Select(p => p.Key).ToArray());
        Assert.Equal(("data.changed/1", "sqlserver://wh/sales.dbo.orders", "sync", "run-7", true), ((string)sent["message"]!, (string)sent["dataset"]!, (string)sent["product"]!, (string)sent["run"]!, (bool)sent["only_if_served"]!));
        Assert.True(DataChangedMessage.TryRead(call.Body, DateTimeOffset.UtcNow, out var notice, out var problem), problem);
        Assert.True(notice.OnlyIfServed);
        Assert.Equal(1, notifier.Stats.Sent);
    }

    [Fact]
    public async Task The_call_is_signed_as_the_server_itself_with_the_publisher_role_for_that_gateway_only_and_cannot_be_replayed()
    {
        var gateway = new FakeGateway(_ => Json(HttpStatusCode.OK, "{}"));
        await using var notifier = Notifier(new Pairings(Pairing()), gateway);
        notifier.Publish([new DataChange("a/b"), new DataChange("a/c")]);
        await notifier.FlushAsync();

        var clients = new Clients(new PairedClient(_self.Id, "Sync server", _self.Key.PublicKey, ServerRole.Publisher, ClientKind.Server));
        var validator = new ConsoleTokenValidator(Audience, clients, new MemoryReplayCache());
        var first = validator.Validate(gateway.Calls[0].Token);
        Assert.True(first.Ok, first.Detail);
        Assert.Equal((_self.Id, "sync server", ServerRole.Publisher, false), (first.Token!.SubjectId, first.Token.SubjectName, first.Token.Roles, first.Token.IsConsole));
        Assert.NotEqual(gateway.Calls[0].Token, gateway.Calls[1].Token);               // a token of its own for each message, so the replay cache never meets one twice
        Assert.False(validator.Validate(gateway.Calls[0].Token).Ok);                    // the same token a second time is refused
        Assert.False(new ConsoleTokenValidator("another-gateway", clients, new MemoryReplayCache()).Validate(gateway.Calls[1].Token).Ok);
    }

    private sealed class Clients(params PairedClient[] all) : IPairedClients
    {
        public PairedClient? Find(string clientId) => all.FirstOrDefault(c => c.Id == clientId);
    }

    [Fact]
    public async Task Every_paired_gateway_is_told_and_one_that_does_not_serve_the_dataset_is_counted_apart_from_a_failure()
    {
        var one = new FakeGateway(_ => Json(HttpStatusCode.OK, """{"served":true}"""));
        var two = new FakeGateway(_ => Json(HttpStatusCode.OK, """{"served":false}"""));
        await using var notifier = new DataChangeNotifier("build", new Pairings(Pairing("one"), Pairing("two")), _ => Task.FromResult(_self), handler: p => p.Name == "one" ? one : two);

        notifier.Publish([new DataChange("a/b")]);
        await notifier.FlushAsync();

        Assert.Single(one.Calls);
        Assert.Single(two.Calls);
        var stats = notifier.Stats;
        Assert.Equal((1L, 1L, 0L, 0L, 2), (stats.Sent, stats.NotServed, stats.Retried, stats.Failed, stats.Pairings));
    }

    [Fact]
    public async Task A_gateway_that_is_down_is_tried_once_more_later_and_a_second_failure_is_a_counter_and_never_an_exception()
    {
        var down = new FakeGateway(_ => Json(HttpStatusCode.ServiceUnavailable, """{"error":"draining"}"""));
        await using var notifier = Notifier(new Pairings(Pairing()), down);

        notifier.Publish([new DataChange("a/b")]);
        await notifier.FlushAsync();

        Assert.Equal(2, down.Calls.Count);
        var stats = notifier.Stats;
        Assert.Equal((0L, 1L, 1L), (stats.Sent, stats.Failed, stats.Retried));
        Assert.Contains("503 draining", stats.LastFailure);
        Assert.NotNull(stats.LastFailureAt);

        var recovers = new FakeGateway(n => n == 1 ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, "{}"));
        await using var second = Notifier(new Pairings(Pairing()), recovers);
        second.Publish([new DataChange("a/b")]);
        await second.FlushAsync();
        Assert.Equal((1L, 0L, 1L), (second.Stats.Sent, second.Stats.Failed, second.Stats.Retried));
    }

    [Fact]
    public async Task A_refusal_is_not_retried_and_an_unreachable_gateway_does_not_throw()
    {
        var refused = new FakeGateway(_ => Json(HttpStatusCode.Forbidden, """{"error":"This call needs the publisher role."}"""));
        await using var notifier = Notifier(new Pairings(Pairing()), refused);
        notifier.Publish([new DataChange("a/b")]);
        await notifier.FlushAsync();
        Assert.Single(refused.Calls);
        Assert.Equal((0L, 1L, 0L), (notifier.Stats.Sent, notifier.Stats.Failed, notifier.Stats.Retried));

        // nothing listens on this address: the handler is the pinned one of a real call
        await using var unreachable = new DataChangeNotifier("sync", new Pairings(Pairing()), _ => Task.FromResult(_self), null,
            new DataChangeNotifierOptions { RetryAfter = TimeSpan.FromMilliseconds(20), CallTimeout = TimeSpan.FromSeconds(2) });
        unreachable.Publish([new DataChange("a/b")]);
        await unreachable.FlushAsync();
        Assert.Equal((0L, 1L, 1L), (unreachable.Stats.Sent, unreachable.Stats.Failed, unreachable.Stats.Retried));
        Assert.Contains("could not be reached", unreachable.Stats.LastFailure);
    }

    [Fact]
    public async Task A_pairings_file_that_is_damaged_fails_the_notice_and_not_the_caller()
    {
        var file = new GatewayPairingsFile(Path.Combine(_dir, "gateways.json"));
        File.WriteAllText(file.Path, "{ not a list");
        await using var notifier = Notifier(file, new FakeGateway(_ => Json(HttpStatusCode.OK, "{}")));

        notifier.Publish([new DataChange("a/b")]);

        Assert.Equal(1, notifier.Stats.Failed);
        Assert.Contains("gateways.json", notifier.Stats.LastFailure + file.Path);
    }

    [Fact]
    public void The_pairings_file_keeps_the_shape_of_the_publish_pairings_and_replaces_one_by_name()
    {
        var file = new GatewayPairingsFile(Path.Combine(_dir, "sub", "gateways.json"));
        Assert.Empty(file.All());
        file.Add(Pairing("b"));
        file.Add(Pairing("a"));
        file.Add(Pairing("b") with { Url = "https://other:2" });

        Assert.Equal(["a", "b"], file.All().Select(p => p.Name));
        Assert.Equal("https://other:2", file.Find("b")!.Url);
        var json = (JsonArray)JsonNode.Parse(File.ReadAllText(file.Path))!;
        Assert.Equal(["name", "url", "instanceId", "fingerprint", "pairedAt"], ((JsonObject)json[0]!).Select(p => p.Key).ToArray());
        Assert.True(file.Remove("a"));
        Assert.False(file.Remove("a"));
    }

    [Fact]
    public void A_message_without_the_new_key_reads_as_before_and_a_key_that_is_not_a_boolean_is_refused()
    {
        Assert.True(DataChangedMessage.TryRead("""{"message":"data.changed/1","dataset":"a/b","product":"sync"}""", DateTimeOffset.UtcNow, out var plain, out _));
        Assert.False(plain.OnlyIfServed);
        Assert.False(DataChangedMessage.TryRead("""{"message":"data.changed/1","dataset":"a/b","product":"sync","only_if_served":"yes"}""", DateTimeOffset.UtcNow, out _, out var problem));
        Assert.Contains("only_if_served", problem);
    }
}
