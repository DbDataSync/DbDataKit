using System.Net;
using System.Net.Sockets;
using DbDataKit.Connections;
using DbDataKit.Delivery.Webhooks;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Delivery.Tests;

/// <summary>
/// Plan 22a GP18 fold (owner decision) and plan 21: the delivery channels are connections, bound per release channel. SMTP through the in-process fake server and a webhook through the local listener, both now through connections; dev and prod resolve different connections;
/// sending on the test channel never reaches production's fake; and the secret parts of a webhook's address are in no failure text, row or log line, whatever way the call failed.
/// </summary>
public sealed class ConnectionChannelTests : IDisposable
{
    private const string Part = "CANARY-token-9f3a/part2-77aa";
    private const string Sig = "CANARY-sig+value=7f";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private LocalHttp Listener(Func<HttpSeen, (int Status, string Body, Dictionary<string, string>? Headers)>? answer = null)
    {
        var http = new LocalHttp(answer);
        _disposables.Add(http);
        return http;
    }

    private static MapResolver Secrets() => new(new() { ["secret:hooks/token"] = Part, ["secret:hooks/sig"] = Sig, ["secret:hooks/u"] = "in" });

    /// <summary>A Slack webhook connection to a listener (loopback listed on purpose): the address is a template whose path and query hold the secret parts.</summary>
    private static ConnectionDefinition Slack(string name, string origin) => TestConnections.Load(
        $"schema: connection/2\nkind: http\nname: {name}\nendpoint: \"{origin}/hook/{{token}}?sig={{sig}}\"\nendpoint_secrets:\n  token: secret:hooks/token\n  sig: secret:hooks/sig\nwebhook:\n  format: slack\n  allow_private: [ 127.0.0.1 ]\n  timeout_seconds: 1\n");

    private static DeliveryRequest Request(string channel, string? release = null) =>
        new(channel, "Disk is full", ["ops@example.com"], "The disk of db-1 is at 99%.", "<p>The disk of db-1 is at 99%.</p>", Source: "alert:disk", OwnerPerson: "person-1", ReleaseChannel: release);

    // ---- one name, a connection per release channel ----

    [Fact]
    public void Dev_test_and_prod_resolve_different_connections_and_a_name_alone_binds_prod_only()
    {
        var prod = Listener();
        var sandbox = Listener();
        var connections = new FixedDeliveryConnections([Slack("slack-ops", $"http://127.0.0.1:{prod.Port}"), Slack("slack-sandbox", $"http://127.0.0.1:{sandbox.Port}")]);
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": {"prod": "slack-ops", "test": "slack-sandbox", "dev": "slack-sandbox"}, "mail": "slack-ops"}"""), connections, Secrets());

        Assert.Equal("slack-ops", channels.Resolve("alerts", "prod").Connection);
        Assert.Equal("slack-ops", channels.Resolve("alerts", null).Connection);
        Assert.Equal("slack-sandbox", channels.Resolve("alerts", "test").Connection);
        Assert.Equal("slack-sandbox", channels.Resolve("alerts", "dev").Connection);
        Assert.NotSame(channels.Resolve("alerts", "prod").Channel, channels.Resolve("alerts", "test").Channel);
        // a channel made from a connection is kept until the connection changes
        Assert.Same(channels.Resolve("alerts", "prod").Channel, channels.Resolve("alerts", "prod").Channel);

        var unbound = channels.Resolve("alerts", "staging");
        Assert.Null(unbound.Channel);
        Assert.False(unbound.Retryable);
        Assert.Contains("not bound in the release channel 'staging' (it is bound in: dev, prod, test)", unbound.Problem);
        Assert.Null(channels.Resolve("mail", "test").Channel);                                     // a name alone is prod's: a test never inherits it
        Assert.NotNull(channels.Resolve("mail", "prod").Channel);
        Assert.Equal("slack-ops", channels.Bindings.Single(b => b.Name == "alerts").For("prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Sending_on_the_test_channel_never_reaches_the_prod_listener(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var prod = Listener();
        var sandbox = Listener();
        var connections = new FixedDeliveryConnections([Slack("slack-ops", $"http://127.0.0.1:{prod.Port}"), Slack("slack-sandbox", $"http://127.0.0.1:{sandbox.Port}")]);
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": {"prod": "slack-ops", "test": "slack-sandbox"}}"""), connections, Secrets(), time: h.Clock);
        var service = h.Service(channels, "a");

        var testMessage = await service.SendAsync(Request("alerts", "test"));
        await service.RunOnceAsync();
        Assert.Single(sandbox.Seen);
        Assert.Empty(prod.Seen);                                                                  // the point: the test never notified production's channel
        Assert.Equal("test", h.Store.Find(testMessage)!.ReleaseChannel);
        Assert.Equal("sent", h.Store.Find(testMessage)!.Status);

        var prodMessage = await service.SendAsync(Request("alerts"));
        await service.RunOnceAsync();
        Assert.Single(prod.Seen);
        Assert.Single(sandbox.Seen);
        Assert.Null(h.Store.Find(prodMessage)!.ReleaseChannel);
        Assert.Equal("prod", h.Store.Find(prodMessage)!.Release);
        Assert.Equal("sent", h.Store.Find(prodMessage)!.Status);
    }

    public static TheoryData<string> Engines => StateEngines.Engines;

    [Fact]
    public async Task A_release_channel_with_no_binding_is_refused_to_the_caller_and_nothing_is_sent_anywhere()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var prod = Listener();
        var connections = new FixedDeliveryConnections([Slack("slack-ops", $"http://127.0.0.1:{prod.Port}")]);
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": "slack-ops"}"""), connections, Secrets(), time: h.Clock);
        var service = h.Service(channels, "a");

        var e = await Assert.ThrowsAsync<ChannelNotBoundException>(() => service.SendAsync(Request("alerts", "test")));
        Assert.Equal(("alerts", "test"), (e.Channel, e.ReleaseChannel));
        Assert.Contains("never uses another's connection", e.Message);
        await Assert.ThrowsAsync<UnknownChannelException>(() => service.SendAsync(Request("nope", "test")));
        Assert.Empty(h.Store.List());
        await service.RunOnceAsync();
        Assert.Empty(prod.Seen);
    }

    [Fact]
    public async Task A_star_binding_is_explicit_and_covers_the_release_channels_that_have_none_of_their_own()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var prod = Listener();
        var sandbox = Listener();
        var connections = new FixedDeliveryConnections([Slack("slack-ops", $"http://127.0.0.1:{prod.Port}"), Slack("slack-sandbox", $"http://127.0.0.1:{sandbox.Port}")]);
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": {"prod": "slack-ops", "*": "slack-sandbox"}}"""), connections, Secrets(), time: h.Clock);
        var service = h.Service(channels, "a");
        await service.SendAsync(Request("alerts", "qa"));
        await service.RunOnceAsync();
        Assert.Single(sandbox.Seen);
        Assert.Empty(prod.Seen);
    }

    [Fact]
    public async Task An_edited_connection_is_used_by_the_next_message_and_a_missing_one_is_a_problem_the_message_reports_then_recovers()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var first = Listener();
        var second = Listener();
        var connections = new FixedDeliveryConnections([Slack("slack-ops", $"http://127.0.0.1:{first.Port}")]);
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": "slack-ops"}"""), connections, Secrets(), time: h.Clock);
        var service = h.Service(channels, "a");

        await service.SendAsync(Request("alerts"));
        await service.RunOnceAsync();
        Assert.Single(first.Seen);

        connections.Set(Slack("slack-ops", $"http://127.0.0.1:{second.Port}"));                  // an edit: the next message goes to the new address
        await service.SendAsync(Request("alerts"));
        await service.RunOnceAsync();
        Assert.Equal((1, 1), (first.Seen.Count, second.Seen.Count));

        connections.Remove("slack-ops");
        var id = await service.SendAsync(Request("alerts"));                                      // written: fixing the connection makes the next try work
        await service.RunOnceAsync();
        var row = h.Store.Find(id)!;
        Assert.Equal(("pending", "temporary"), (row.Status, row.LastOutcome));
        Assert.Contains("uses the connection 'slack-ops', which cannot be used: There is no connection named 'slack-ops'", row.LastDetail);

        connections.Set(Slack("slack-ops", $"http://127.0.0.1:{second.Port}"));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await service.RunOnceAsync();
        Assert.Equal("sent", h.Store.Find(id)!.Status);
    }

    [Fact]
    public async Task A_connection_that_is_not_a_delivery_kind_is_a_problem_naming_what_it_needs()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var api = TestConnections.Load("schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\n");
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": "crm"}"""), new FixedDeliveryConnections([api]), Secrets(), time: h.Clock);
        var resolution = channels.Resolve("alerts", null);
        Assert.Null(resolution.Channel);
        Assert.Contains("`kind: smtp`, or `kind: http` with `webhook:", resolution.Problem);
        var service = h.Service(channels, "a");
        var id = await service.SendAsync(Request("alerts"));
        await service.RunOnceAsync();
        Assert.Equal("temporary", h.Store.Find(id)!.LastOutcome);
    }

    [Fact]
    public async Task A_limit_per_minute_is_kept_per_connection_so_a_test_never_uses_up_prods()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var prod = Listener();
        var sandbox = Listener();
        var connections = new FixedDeliveryConnections([Slack("slack-ops", $"http://127.0.0.1:{prod.Port}"), Slack("slack-sandbox", $"http://127.0.0.1:{sandbox.Port}")]);
        var channels = TestConnections.Channels(TestConnections.Section("""{"channels": {"alerts": {"prod": "slack-ops", "test": "slack-sandbox"}}, "limits": {"alerts": 1}}"""), connections, Secrets(), time: h.Clock);
        var service = h.Service(channels, "a");
        await service.SendAsync(Request("alerts", "test"));
        await service.SendAsync(Request("alerts"));
        await service.SendAsync(Request("alerts"));
        await service.RunOnceAsync();
        Assert.Equal((1, 1), (prod.Seen.Count, sandbox.Seen.Count));                              // the second prod message waits; the test message did not count against prod
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await service.RunOnceAsync();
        Assert.Equal(2, prod.Seen.Count);
    }

    // ---- SMTP and a signed webhook through connections ----

    [Fact]
    public async Task Mail_is_sent_through_a_smtp_connection_with_both_bodies()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        using var server = new FakeSmtpServer();
        var connection = TestConnections.Load($"schema: connection/2\nkind: smtp\nname: smtp-main\nsmtp:\n  host: 127.0.0.1\n  port: {server.Port}\n  tls: none\n  sender: \"DbData <noreply@example.com>\"\n");
        var channels = TestConnections.Channels(TestConnections.Settings("""{"mail": "smtp-main"}"""), new FixedDeliveryConnections([connection]), new MapResolver(new() { ["secret:mail/password"] = "s3cr3t-pw" }), time: h.Clock);
        var service = h.Service(channels, "a");
        var id = await service.SendAsync(new DeliveryRequest("mail", "Weekly sales", ["ana@example.com"], "Plain body", "<p>Html body</p>", Source: "test"));
        await service.RunOnceAsync();
        Assert.Equal("sent", h.Store.Find(id)!.Status);
        var mail = Assert.Single(server.Received);
        Assert.Equal(["ana@example.com"], mail.To);
        Assert.Contains("text/plain", mail.Raw);
        Assert.Contains("text/html", mail.Raw);
    }

    [Fact]
    public async Task A_signed_webhook_connection_signs_with_its_shared_secret_and_the_header_it_names()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var http = Listener();
        var connection = TestConnections.Load($"schema: connection/2\nkind: http\nname: erp-hook\nendpoint: \"http://127.0.0.1:{http.Port}/in/{{id}}\"\nendpoint_secrets: {{ id: \"secret:hooks/u\" }}\nwebhook: {{ format: signed, allow_private: [ 127.0.0.1 ] }}\nsign_in: {{ method: signed_webhook, key_secret: \"secret:hooks/signing\", name: X-Erp-Signature }}\n");
        var channels = TestConnections.Channels(TestConnections.Settings("""{"erp": "erp-hook"}"""), new FixedDeliveryConnections([connection]), new MapResolver(new() { ["secret:hooks/u"] = "abc", ["secret:hooks/signing"] = "shared-secret" }), time: h.Clock);
        var service = h.Service(channels, "a");
        var id = await service.SendAsync(Request("erp"));
        await service.RunOnceAsync();
        Assert.Equal("sent", h.Store.Find(id)!.Status);
        var seen = Assert.Single(http.Seen);
        Assert.Equal("/in/abc", seen.Path);
        Assert.Equal(SignatureCheck.Valid, WebhookSignature.Verify("shared-secret", seen.Headers["X-Erp-Signature"], seen.Body, h.Clock.GetUtcNow()));
        Assert.Equal(SignatureCheck.Mismatch, WebhookSignature.Verify("other-secret", seen.Headers["X-Erp-Signature"], seen.Body, h.Clock.GetUtcNow()));
        Assert.False(seen.Headers.ContainsKey("X-DbData-Signature"));
    }

    // ---- the secret parts of the address are in no failure, row or log line ----

    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("redirect")]
    [InlineData("tls")]
    [InlineData("dns")]
    [InlineData("refused")]
    [InlineData("body-echo-4xx")]
    [InlineData("body-echo-5xx")]
    public async Task No_secret_part_of_the_address_is_in_the_failure_the_row_or_the_log(string failure)
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var http = failure switch
        {
            "timeout" => Listener(_ => (599, "late", null)),
            "redirect" => Listener(seen => (302, "moved: " + seen.Path, new() { ["Location"] = "http://127.0.0.1" + seen.Path + "/next" })),
            "body-echo-4xx" => Listener(seen => (404, "no route for " + seen.Path, null)),
            "body-echo-5xx" => Listener(seen => (503, "upstream failed at " + seen.Path, null)),
            _ => Listener(),
        };
        var origin = failure switch
        {
            "tls" => $"https://127.0.0.1:{http.Port}",
            "dns" => "https://no-such-host.invalid",
            "refused" => $"http://127.0.0.1:{ClosedPort()}",
            _ => $"http://127.0.0.1:{http.Port}",
        };
        var scrubber = new SecretScrubber();
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": "slack-ops"}"""), new FixedDeliveryConnections([Slack("slack-ops", origin)]), Secrets(), scrubber, h.Clock);
        var log = new CollectingLogger<DeliveryService>();
        var service = h.Service(channels, "a", DeliveryPolicy.Default with { MaxAttempts = 1 }, scrubber, log);

        var id = await service.SendAsync(Request("alerts"));
        await service.RunOnceAsync();

        var row = h.Store.Find(id)!;
        Assert.NotEqual("sent", row.Status);
        Assert.False(string.IsNullOrEmpty(row.LastDetail));
        var everything = string.Join('\n', new[] { row.Subject, row.BodyText, row.BodyHtml, row.Link, row.LastDetail, row.Source }.Concat(row.Recipients).Concat(h.Store.Attempts(id).Select(a => a.Detail)).Concat(log.Lines));
        foreach (var secret in new[] { "CANARY", "9f3a", "part2-77aa", "7f" + "&", "value=7f", "value%3D7f", "sig%3D" })
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        Assert.NotEmpty(log.Lines);
    }

    [Fact]
    public async Task The_sent_words_name_the_host_never_the_address_and_the_composed_address_is_used_only_in_the_call()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var http = Listener(seen => (200, "ok " + seen.Path, null));
        var scrubber = new SecretScrubber();
        var channels = TestConnections.Channels(TestConnections.Settings("""{"alerts": "slack-ops"}"""), new FixedDeliveryConnections([Slack("slack-ops", $"http://127.0.0.1:{http.Port}")]), Secrets(), scrubber, h.Clock);
        var service = h.Service(channels, "a", scrubber: scrubber);
        var id = await service.SendAsync(Request("alerts"));
        await service.RunOnceAsync();
        var row = h.Store.Find(id)!;
        Assert.Equal("sent", row.Status);
        Assert.Equal("/hook/CANARY-token-9f3a/part2-77aa", Assert.Single(http.Seen).Path);          // the call went to the composed address
        Assert.DoesNotContain("CANARY", row.LastDetail);                                            // the far side's echo of it is scrubbed
        Assert.Contains("200 OK from 127.0.0.1", row.LastDetail);
    }
}
