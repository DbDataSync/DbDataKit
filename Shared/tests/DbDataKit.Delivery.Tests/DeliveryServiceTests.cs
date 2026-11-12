using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.State.Snapshots;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Delivery.Tests;

/// <summary>Plan 22a GP07: the outbox, the senders, the retries and the settings, on every state engine (SQLite always; SQL Server and PostgreSQL when the shared engines are up).</summary>
public sealed class DeliveryServiceTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static DeliveryRequest Request(string channel = "mail", string subject = "Weekly sales", string? source = "test") =>
        new(channel, subject, ["ana@example.com"], "Plain body", "<p>Html body</p>", "https://console.example/views/sales", Source: source, OwnerPerson: "person-1");

    private static readonly DeliveryPolicy Fast = new(3, [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

    [Fact]
    public void The_module_follows_the_conventions_and_declares_its_retention()
    {
        Assert.Empty(StateConventions.Violations(DeliveryModule.Module));
        var rule = Assert.Single(DeliveryModule.Module.Retention, r => r.Table == "message");
        Assert.Equal(("message", "settled_at", 30d), (rule.Table, rule.TimeColumn, rule.DefaultDays));
        Assert.Equal(["attempt"], rule.Children!.Select(c => c.Table));
        Assert.Equal("delivery", DeliveryModule.Module.Name);
        Assert.Equal(StateScopeKind.Install, DeliveryModule.Module.Scope);
        Assert.Equal(["attempt", "message", "subscription", "subscription_run"], StateTableDefinitions.Read(DeliveryModule.Module).Select(t => t.Name).Order());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_message_is_written_with_both_bodies_a_link_and_an_attachment_and_read_back(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var mail = new ScriptedChannel("mail", _ => DeliveryResult.Sent("250 ok"));
        var service = h.Service(new ChannelSet(mail), "sender-a");
        var id = await service.SendAsync(Request() with { Attachments = [new DeliveryAttachment("sales.pdf", "application/pdf", [1, 2, 3, 4])] });

        var row = h.Store.Find(id)!;
        Assert.Equal(("mail", "Weekly sales", "Plain body", "<p>Html body</p>", "https://console.example/views/sales", "test", "person-1", "pending", 0), (row.Channel, row.Subject, row.BodyText, row.BodyHtml, row.Link, row.Source, row.OwnerPerson, row.Status, row.Attempts));
        Assert.Equal(["ana@example.com"], row.Recipients);
        var attachment = Assert.Single(row.Attachments);
        Assert.Equal(("sales.pdf", "application/pdf", 4L), (attachment.Name, attachment.MediaType, attachment.Size));
        Assert.Equal("delivery", h.Payloads.Get(attachment.Payload)!.OwnerModule);
        Assert.Equal(id.ToString("D"), h.Payloads.Get(attachment.Payload)!.OwnerRow);

        Assert.Equal(1, await service.RunOnceAsync());
        var sent = Assert.Single(mail.Received);
        Assert.Equal([1, 2, 3, 4], Assert.Single(sent.Attachments).Content);
        Assert.Equal(("Plain body", "<p>Html body</p>", 1), (sent.Text, sent.Html, sent.Attempt));
        Assert.Equal("sent", h.Store.Find(id)!.Status);
        var attempt = Assert.Single(h.Store.Attempts(id));
        Assert.Equal((1, "sender-a", "sent", "250 ok"), (attempt.Number, attempt.Sender, attempt.Outcome, attempt.Detail));
    }

    [Fact]
    public async Task A_message_for_a_channel_that_is_not_configured_is_refused_to_the_caller()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var service = h.Service(new ChannelSet(new ScriptedChannel("mail", _ => DeliveryResult.Sent())), "a");
        var ex = await Assert.ThrowsAsync<UnknownChannelException>(() => service.SendAsync(Request("teams")));
        Assert.Contains("'teams' is not configured", ex.Message);
        Assert.Contains("mail", ex.Message);
        Assert.Equal(0, h.Count("message"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_message_is_taken_by_one_of_two_senders_only_once(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var shared = new ScriptedChannel("mail", _ => DeliveryResult.Sent());
        var a = h.Service(new ChannelSet(shared), "sender-a");
        var b = h.Service(new ChannelSet(shared), "sender-b");
        for (var i = 0; i < 12; i++) await a.SendAsync(Request(subject: $"m{i}"));

        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            var total = 0;
            for (var round = 0; round < 6; round++) total += await (i % 2 == 0 ? a : b).RunOnceAsync(batch: 3);
            return total;
        })));

        Assert.Equal(12, counts.Sum());
        Assert.Equal(12, shared.Received.Count);
        Assert.Equal(12, shared.Received.Select(p => p.MessageId).Distinct().Count());
        Assert.Equal(12, h.Store.Counts()["sent"]);
        Assert.Equal(12, h.Count("attempt"));
        Assert.All(h.Store.List(limit: 50), m => Assert.Equal(1, m.Attempts));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_temporary_failure_is_retried_with_back_off_and_parked_after_the_limit(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var failing = new ScriptedChannel("mail", p => DeliveryResult.Temporary($"451 try later ({p.Attempt})"));
        var service = h.Service(new ChannelSet(failing), "a", Fast);
        var id = await service.SendAsync(Request());

        Assert.Equal(1, await service.RunOnceAsync());
        var first = h.Store.Find(id)!;
        Assert.Equal(("pending", 1, "temporary"), (first.Status, first.Attempts, first.LastOutcome));
        Assert.Equal(DeliveryHarness.Start + TimeSpan.FromSeconds(30), first.NextAttemptAt);
        Assert.Equal(0, await service.RunOnceAsync());                       // the back-off has not passed

        h.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await service.RunOnceAsync());
        Assert.Equal(TimeSpan.FromMinutes(5), h.Store.Find(id)!.NextAttemptAt - h.Clock.GetUtcNow());

        h.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await service.RunOnceAsync());
        var parked = h.Store.Find(id)!;
        Assert.Equal(("dead", 3, "temporary"), (parked.Status, parked.Attempts, parked.LastOutcome));
        Assert.Contains("Gave up after 3 tries", parked.LastDetail);
        Assert.Contains("451 try later (3)", parked.LastDetail);
        Assert.NotNull(parked.SettledAt);
        Assert.Equal(["temporary", "temporary", "temporary"], h.Store.Attempts(id).Select(a => a.Outcome));

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await service.RunOnceAsync());                       // parked messages are not tried again by themselves
        Assert.True(h.Store.Requeue(id));
        Assert.Equal(1, await service.RunOnceAsync());                       // a person sent it again: another try
        Assert.Equal(4, h.Store.Attempts(id).Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_permanent_failure_parks_the_message_at_once_with_the_servers_words_and_a_throw_is_a_temporary_error(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var refusing = new ScriptedChannel("mail", _ => DeliveryResult.Permanent("550 5.1.1 no such user ana@example.com"));
        var throwing = new ScriptedChannel("odd", _ => throw new InvalidOperationException("the stream broke"));
        var service = h.Service(new ChannelSet(refusing, throwing), "a", Fast);
        var refused = await service.SendAsync(Request("mail"));
        var thrown = await service.SendAsync(Request("odd"));

        Assert.Equal(2, await service.RunOnceAsync());
        var dead = h.Store.Find(refused)!;
        Assert.Equal(("dead", 1, "permanent", "550 5.1.1 no such user ana@example.com"), (dead.Status, dead.Attempts, dead.LastOutcome, dead.LastDetail));
        var error = h.Store.Find(thrown)!;
        Assert.Equal(("pending", "temporary"), (error.Status, error.LastOutcome));
        Assert.Contains("InvalidOperationException: the stream broke", error.LastDetail);
        Assert.Equal("error", Assert.Single(h.Store.Attempts(thrown)).Outcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_sender_that_loses_its_lease_cannot_settle_what_another_sender_took(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var channel = new ScriptedChannel("mail", _ => DeliveryResult.Sent());
        var a = h.Service(new ChannelSet(channel), "a", Fast);
        var b = h.Service(new ChannelSet(channel), "b", Fast);
        var id = await a.SendAsync(Request());

        var taken = Assert.Single(h.Store.Claim("a", TimeSpan.FromMinutes(1)));
        Assert.Equal(id, taken.Id);
        Assert.Empty(h.Store.Claim("b", TimeSpan.FromMinutes(1)));            // held by a
        h.Clock.Advance(TimeSpan.FromMinutes(2));                             // a died
        Assert.Equal(1, await b.RunOnceAsync());
        Assert.False(h.Store.Complete(id, "a", AttemptOutcomes.Sent, "late", DeliveryHarness.Start));
        Assert.Equal(1, h.Count("attempt"));
        Assert.Equal("b", Assert.Single(h.Store.Attempts(id)).Sender);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_removed_channel_parks_what_waits_for_it_and_says_so(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var channels = new ChannelSet(new ScriptedChannel("mail", _ => DeliveryResult.Sent()));
        var service = h.Service(channels, "a");
        var id = await service.SendAsync(Request());
        channels.Remove("mail");

        Assert.Equal(1, await service.RunOnceAsync());
        var dead = h.Store.Find(id)!;
        Assert.Equal(("dead", "permanent"), (dead.Status, dead.LastOutcome));
        Assert.Contains("'mail' is not configured any more", dead.LastDetail);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_limit_per_minute_holds_a_message_back_without_counting_a_try(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var channel = new ScriptedChannel("mail", _ => DeliveryResult.Sent(), limitPerMinute: 2);
        var service = h.Service(new ChannelSet(channel), "a");
        for (var i = 0; i < 3; i++) await service.SendAsync(Request(subject: $"m{i}"));

        Assert.Equal(3, await service.RunOnceAsync());
        Assert.Equal(2, channel.Received.Count);
        var waiting = Assert.Single(h.Store.List(status: "pending"));
        Assert.Equal(0, waiting.Attempts);
        Assert.Equal(DeliveryHarness.Start + TimeSpan.FromMinutes(1), waiting.NextAttemptAt);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await service.RunOnceAsync());
        Assert.Equal(3, channel.Received.Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Rows_past_retention_go_and_their_attachments_with_them_while_open_work_stays(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var channel = new ScriptedChannel("mail", p => p.Subject == "keep" ? DeliveryResult.Temporary("later") : DeliveryResult.Sent());
        var service = h.Service(new ChannelSet(channel), "a", new DeliveryPolicy(9, [TimeSpan.FromDays(60)], TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
        var old = await service.SendAsync(Request(subject: "old") with { Attachments = [new DeliveryAttachment("a.csv", "text/csv", [9, 9])] });
        var open = await service.SendAsync(Request(subject: "keep"));
        await service.RunOnceAsync();
        Assert.Equal(("sent", "pending"), (h.Store.Find(old)!.Status, h.Store.Find(open)!.Status));
        var payload = h.Store.Find(old)!.Attachments[0].Payload;

        h.Clock.Advance(TimeSpan.FromDays(31));
        var settings = new RetentionSettings { OrphanGrace = TimeSpan.Zero };
        new RetentionJob(h.Database, settings).Run();
        new RetentionJob(h.Database, settings).Run();

        Assert.Null(h.Store.Find(old));
        Assert.Empty(h.Store.Attempts(old));
        Assert.Null(h.Payloads.Get(payload));
        Assert.NotNull(h.Store.Find(open));                                   // still pending: current work has no age limit
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_secret_never_reaches_the_outbox_or_the_log_even_when_a_server_echoes_it(string engine)
    {
        const string canary = "CANARY-pa55w0rd-7f3a91";
        using var h = new DeliveryHarness(engine);
        var scrubber = new SecretScrubber();
        var resolver = new MapResolver(new() { ["secret:dbdata/delivery/mail/password"] = canary });
        var channels = EchoKind.Channels(resolver, scrubber, h.Clock);
        var log = new CollectingLogger<DeliveryService>();
        var service = h.Service(channels, "a", DeliveryPolicy.Default with { MaxAttempts = 1 }, scrubber, log);
        var id = await service.SendAsync(Request("mail"));
        await service.RunOnceAsync();

        var row = h.Store.Find(id)!;
        Assert.Equal("dead", row.Status);
        Assert.Contains("535 authentication failed for ***", row.LastDetail);
        Assert.DoesNotContain(canary, row.LastDetail);
        Assert.All(h.Store.Attempts(id), a => Assert.DoesNotContain(canary, a.Detail));
        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, line => Assert.DoesNotContain(canary, line));
        var everything = string.Join('\n', new[] { row.Subject, row.BodyText, row.BodyHtml, row.Link, row.LastDetail, row.Source }.Concat(row.Recipients));
        Assert.DoesNotContain(canary, everything);
    }

    [Fact]
    public async Task A_secret_that_cannot_be_read_is_a_temporary_failure_naming_the_key_and_the_reference()
    {
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var scrubber = new SecretScrubber();
        var channels = EchoKind.Channels(new MapResolver([]), scrubber, h.Clock);
        var service = h.Service(channels, "a", scrubber: scrubber);
        var id = await service.SendAsync(Request("mail"));
        await service.RunOnceAsync();
        var row = h.Store.Find(id)!;
        Assert.Equal(("pending", "temporary"), (row.Status, row.LastOutcome));
        Assert.Contains("`password` of the channel 'mail' (secret:dbdata/delivery/mail/password) could not be read", row.LastDetail);
    }

    // ---- the settings section ----

    [Fact]
    public void The_section_is_read_strictly_and_an_unknown_key_fails_naming_the_file()
    {
        var read = TestConnections.Section("""
            {"retry": {"max_attempts": 4, "backoff_seconds": [10, 60]}, "lease_seconds": 90,
             "channels": {"mail": "smtp-main", "alerts": {"prod": "slack-ops", "test": "slack-sandbox", "*": "slack-sandbox"}}, "limits": {"alerts": 20}}
            """);
        Assert.Equal(4, read.Policy.MaxAttempts);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)], read.Policy.Backoff);
        Assert.Equal(TimeSpan.FromSeconds(90), read.Policy.Lease);
        var mail = read.Channels[0];
        Assert.Equal(("mail", "smtp-main", null, null), (mail.Name, mail.For("prod"), mail.For("test"), mail.LimitPerMinute));   // a name alone binds prod, and prod only
        var alerts = read.Channels[1];
        Assert.Equal(("slack-ops", "slack-sandbox", "slack-sandbox", 20), (alerts.For("prod"), alerts.For("test"), alerts.For("anything-else"), alerts.LimitPerMinute));
        Assert.True(alerts.IsOwn("test"));
        Assert.False(alerts.IsOwn("anything-else"));

        static string Refused(string json) => Assert.Throws<DeliverySettingsException>(() => TestConnections.Section(json)).Message;
        Assert.StartsWith("settings.yaml: delivery has no key `queue`", Refused("""{"queue": {}}"""));
        Assert.StartsWith("settings.yaml: delivery.retry has no key `attempts`", Refused("""{"retry": {"attempts": 3}}"""));
        Assert.Contains("delivery.limits.nope names a channel that is not bound in bindings.yml", Refused("""{"channels": {"mail": "a"}, "limits": {"nope": 5}}"""));
        Assert.Contains("delivery.limits.mail is a whole number", Refused("""{"channels": {"mail": "a"}, "limits": {"mail": 0}}"""));
        Assert.Contains("delivery.retry.max_attempts is a whole number", Refused("""{"retry": {"max_attempts": 0}}"""));
    }

    [Fact]
    public void Channels_in_the_section_are_refused_naming_the_file_and_the_place_they_moved_to()
    {
        var message = Assert.Throws<DeliverySettingsException>(() => DeliverySettings.Read(JsonNode.Parse("""{"channels": {"mail": "smtp-main"}}""")!.AsObject(), "/etc/console/settings.yaml")).Message;
        Assert.StartsWith("/etc/console/settings.yaml: delivery.channels is not kept here any more", message, StringComparison.Ordinal);
        Assert.Contains("bindings.yml", message, StringComparison.Ordinal);
        Assert.Contains("{ prod: <connection>, test: <connection> }", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"mail": {"kind": "email", "host": "smtp.example.com", "from": "a@b.example"}}""", "`kind`")]
    [InlineData("""{"mail": {"host": "smtp.example.com"}}""", "`host`")]
    [InlineData("""{"hook": {"url": "secret:x/url"}}""", "`url`")]
    [InlineData("""{"hook": {"limit_per_minute": 5}}""", "`limit_per_minute`")]
    public void A_channel_in_the_old_form_is_refused_naming_the_file_and_the_new_form(string channels, string key)
    {
        var read = DbDataKit.Connections.Bindings.BindingsLoader.Load("bindings: 1\ndelivery: " + channels, "/etc/console/bindings.yml");
        Assert.False(read.Ok);
        var d = Assert.Single(read.Diagnostics, x => x.Code == DbDataKit.Connections.Bindings.BindingsLoader.EarlierForm && x.Message.Contains(key, StringComparison.Ordinal));
        Assert.Equal("/etc/console/bindings.yml", d.Path);
        Assert.Contains("Channels are connections now", d.Message, StringComparison.Ordinal);
        Assert.Contains("{ prod: <connection>, test: <connection> }", d.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_section_there_are_no_channels_and_the_default_policy()
    {
        var read = DeliverySettings.Read(null, "settings.yaml");
        Assert.Empty(read.Channels);
        Assert.Equal(6, read.Policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromHours(6), read.Policy.WaitAfter(99));
        Assert.Equal(TimeSpan.FromSeconds(30), read.Policy.WaitAfter(1));
    }
}

/// <summary>A kind of channel for the tests that gives back what a login failure of a real server gives: the password, quoted.</summary>
internal sealed class EchoChannel(ChannelContext context) : IDeliveryChannel
{
    public string Name => context.Config.Name;

    public string Kind => "echo";

    public int? LimitPerMinute => context.Config.LimitPerMinute;

    public async Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken)
    {
        var password = await context.SecretAsync("password", cancellationToken);
        return DeliveryResult.Permanent($"535 authentication failed for {password}");
    }
}

internal static class EchoKind
{
    /// <summary>One channel, <c>mail</c>, that echoes the secret <c>password</c> it resolves (the reference <c>secret:dbdata/delivery/mail/password</c>).</summary>
    public static IDeliveryChannels Channels(ISecretResolver resolver, SecretScrubber scrubber, TimeProvider time)
    {
        var config = new ChannelConfig("mail", "echo", new JsonObject(), new Dictionary<string, DbDataKit.Secrets.SecretRef> { ["password"] = DbDataKit.Secrets.SecretRef.Parse("secret:dbdata/delivery/mail/password") }, null);
        return new ChannelSet(new EchoChannel(new ChannelContext(config, resolver, scrubber, time)));
    }
}
