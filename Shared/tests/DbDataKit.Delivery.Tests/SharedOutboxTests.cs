using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Delivery.Tests;

/// <summary>
/// Plan 32 DBR18, the Gateway without delivery channels: it draws a subscription as the subscriber and writes the message to the <b>shared outbox</b> knowing the channel only by the kind the console stored on the subscription
/// (<see cref="EnqueueOnlyChannels"/>); the console's sender (a second <see cref="DeliveryService"/> over the same state) claims the row and sends it through its own channel. SQLite always; SQL Server and PostgreSQL when the shared engines are up.
/// </summary>
public sealed class SharedOutboxTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static SubscriptionFields Fields(string? kind, string channel = "mail", bool preview = false) =>
        new("Weekly sales", "dashboard", "sales", new JsonObject { ["region"] = new JsonArray("east") }, "print", "pdf", null, channel, null, "0 7 * * *", "UTC", preview, kind);

    private static FakeRenderer Renderer() => new(_ => new RenderResult.Rendered(Encoding.UTF8.GetBytes("%PDF gateway"), "application/pdf", "sales.pdf", "Sales", "Sales for the week", "https://console.example/views/sales", [], Encoding.UTF8.GetBytes("png")));

    /// <summary>The Gateway's side: channels known only by stored kinds, a delivery service that is never run as a sender.</summary>
    private static (SubscriptionService Gateway, SubscriptionStore Store) GatewaySide(DeliveryHarness h)
    {
        var store = new SubscriptionStore(h.Database);
        var delivery = h.Service(new EnqueueOnlyChannels(() => store.ChannelKinds()), "dbdatagateway");
        return (new SubscriptionService(store, delivery, Renderer(), new FakeDirectory(), h.Clock), store);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_message_the_gateway_enqueued_is_sent_by_the_consoles_sender_through_its_own_channel(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (gateway, store) = GatewaySide(h);
        var id = store.Create(Fields("email", preview: true), "ana", "Ana Lopez", "ana", h.Clock.GetUtcNow().AddMinutes(-1));

        var results = await gateway.RunDueAsync(default);

        var result = Assert.Single(results);
        Assert.Equal(SubscriptionOutcomes.Sent, result.Outcome);
        var queued = Assert.Single(h.Store.List(source: $"subscription:{id:D}"));
        Assert.Equal("pending", queued.Status);                                       // the gateway only wrote it
        Assert.Equal(["ana@example.com"], queued.Recipients);
        Assert.Equal(2, queued.Attachments.Count);                                    // the PDF, and the picture inline because the stored kind is email

        // the console: its own channel (never seen by the gateway), a sender with its own id, the same outbox
        var channel = new ScriptedChannel("mail", _ => DeliveryResult.Sent(), kind: "email");
        var console = h.Service(new ChannelSet(channel), "console");
        Assert.Equal(1, await console.RunOnceAsync(default));

        var payload = Assert.Single(channel.Received);
        Assert.Equal("Sales", payload.Subject);
        Assert.Equal("%PDF gateway", Encoding.UTF8.GetString(payload.Attachments.Single(a => a.MediaType == "application/pdf").Content));
        Assert.Equal("preview@dbdatakit", payload.Attachments.Single(a => a.MediaType == "image/png").ContentId);
        var sent = h.Store.Find(queued.Id)!;
        Assert.Equal("sent", sent.Status);
        Assert.Equal("console", Assert.Single(h.Store.Attempts(queued.Id)).Sender);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_kind_decides_what_the_gateway_puts_in_the_message_a_chat_channel_gets_no_picture(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (gateway, store) = GatewaySide(h);
        store.Create(Fields("slack", "chat", preview: true), "ana", "Ana Lopez", "ana", h.Clock.GetUtcNow().AddMinutes(-1));

        await gateway.RunDueAsync(default);

        var message = Assert.Single(h.Store.List(channel: "chat"));
        Assert.Single(message.Attachments);                                          // the PDF only: Teams, Slack and the webhook get the link
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_subscription_with_no_stored_kind_is_not_enqueued_and_says_why(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (gateway, store) = GatewaySide(h);
        var id = store.Create(Fields(null), "ana", "Ana Lopez", "ana", h.Clock.GetUtcNow().AddMinutes(-1));

        var result = Assert.Single(await gateway.RunDueAsync(default));

        Assert.Equal(SubscriptionOutcomes.Failed, result.Outcome);
        Assert.Contains("'mail' is not configured", result.Detail);
        Assert.Empty(h.Store.List(source: $"subscription:{id:D}"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_console_sync_fills_a_subscription_from_before_the_column_and_follows_a_rebinding_without_moving_its_version(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var store = new SubscriptionStore(h.Database);
        var id = store.Create(Fields(null), "ana", "Ana Lopez", "ana", h.Clock.GetUtcNow());
        var version = store.Find(id)!.Version;
        Assert.Empty(store.ChannelKinds());

        Assert.Equal(1, store.SetChannelKind("mail", "email"));
        Assert.Equal("email", store.ChannelKinds()["mail"]);
        Assert.Equal(0, store.SetChannelKind("mail", "email"));                       // nothing to change
        Assert.Equal(1, store.SetChannelKind("mail", "teams"));                       // the channel was bound to another kind of connection
        Assert.Equal("teams", store.Find(id)!.Fields.ChannelKind);
        Assert.Equal(version, store.Find(id)!.Version);                               // an editor holding the old version is not refused
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_temporary_failure_of_the_consoles_channel_is_retried_and_a_removed_channel_parks_the_message(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (gateway, store) = GatewaySide(h);
        var first = store.Create(Fields("email"), "ana", "Ana Lopez", "ana", h.Clock.GetUtcNow().AddMinutes(-1));
        await gateway.RunDueAsync(default);
        var message = Assert.Single(h.Store.List(source: $"subscription:{first:D}"));

        // the far side is down at the first try and up at the second: the console's sender keeps the retry, the gateway is not involved
        var tries = 0;
        var channel = new ScriptedChannel("mail", _ => ++tries == 1 ? DeliveryResult.Temporary("mailbox busy") : DeliveryResult.Sent(), kind: "email");
        var console = h.Service(new ChannelSet(channel), "console");
        await console.RunOnceAsync(default);
        Assert.Equal("pending", h.Store.Find(message.Id)!.Status);
        Assert.Equal(0, await console.RunOnceAsync(default));                         // not due yet
        h.Clock.Advance(console.Policy.WaitAfter(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await console.RunOnceAsync(default));
        Assert.Equal("sent", h.Store.Find(message.Id)!.Status);
        Assert.Equal(2, h.Store.Attempts(message.Id).Count);

        // a channel the console does not have (removed or renamed since the subscription was saved) is a dead letter with the words of the console's channels, not a lost message
        store.Create(Fields("email", "gone"), "bo", "Bo Chen", "bo", h.Clock.GetUtcNow().AddMinutes(-1));
        h.Clock.Advance(TimeSpan.FromDays(1));
        await gateway.RunDueAsync(default);
        var orphan = Assert.Single(h.Store.List(channel: "gone"));
        await console.RunOnceAsync(default);
        var dead = h.Store.Find(orphan.Id)!;
        Assert.Equal("dead", dead.Status);
        Assert.Contains("not configured", dead.LastDetail);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_console_senders_on_the_one_outbox_send_a_message_once(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (gateway, store) = GatewaySide(h);
        for (var i = 0; i < 4; i++) store.Create(Fields("email") with { Name = $"s{i}" }, "ana", "Ana Lopez", "ana", h.Clock.GetUtcNow().AddMinutes(-1));
        await gateway.RunDueAsync(default);
        Assert.Equal(4, h.Store.List(channel: "mail").Count);

        var channel = new ScriptedChannel("mail", _ => DeliveryResult.Sent(), kind: "email");
        var a = h.Service(new ChannelSet(channel), "console-1");
        var b = h.Service(new ChannelSet(channel), "console-2");
        await Task.WhenAll(Task.Run(() => a.RunOnceAsync(default)), Task.Run(() => b.RunOnceAsync(default)));
        await a.RunOnceAsync(default);

        Assert.Equal(4, channel.Received.Count);
        Assert.All(h.Store.List(channel: "mail"), m => Assert.Equal("sent", m.Status));
    }
}
