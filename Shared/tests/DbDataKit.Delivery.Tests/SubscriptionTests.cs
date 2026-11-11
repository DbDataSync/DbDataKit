using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Delivery.Tests;

public sealed class FakeDirectory : ISubscriberDirectory
{
    public Dictionary<string, SubscriberContact> People { get; } = new()
    {
        ["ana"] = new("ana", "Ana Lopez", "ana@example.com"),
        ["bo"] = new("bo", "Bo Chen", "bo@example.com"),
        ["admin"] = new("admin", "Ada Admin", "admin@example.com"),
    };

    public Task<SubscriberContact?> FindAsync(string person, CancellationToken cancellationToken) => Task.FromResult(People.GetValueOrDefault(person));
}

public sealed class FakeRenderer(Func<SubscriptionRender, RenderResult> render) : ISubscriptionRenderer
{
    public List<SubscriptionRender> Asked { get; } = [];

    public Task<RenderResult> RenderAsync(SubscriptionRender request, CancellationToken cancellationToken)
    {
        lock (Asked) Asked.Add(request);
        return Task.FromResult(render(request));
    }
}

/// <summary>Plan 22a GP17: subscriptions on every state engine (SQLite always; SQL Server and PostgreSQL when the shared engines are up).</summary>
public sealed class SubscriptionTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static readonly DateTimeOffset T0 = DeliveryHarness.Start;

    private static SubscriptionFields Fields(string name = "Weekly sales", string channel = "mail", string cron = "0 7 * * *", string zone = "UTC", string format = "pdf", string? widget = null) =>
        new(name, "dashboard", "sales", new JsonObject { ["region"] = new JsonArray("east") }, "print", format, widget, channel, null, cron, zone);

    private static RenderResult.Rendered Rendered(string text, string title = "Sales") => new(Encoding.UTF8.GetBytes(text), "application/pdf", "sales.pdf", title, $"Sales for the week: {text}", "https://console.example/views/sales", []);

    private static (SubscriptionService Service, SubscriptionStore Store, FakeRenderer Renderer, FakeDirectory Directory, ScriptedChannel Channel, ChannelSet Channels) Build(DeliveryHarness h, Func<SubscriptionRender, RenderResult> render)
    {
        var channel = new ScriptedChannel("mail", _ => DeliveryResult.Sent());
        var channels = new ChannelSet(channel);
        var delivery = h.Service(channels, "sender");
        var renderer = new FakeRenderer(render);
        var directory = new FakeDirectory();
        var store = new SubscriptionStore(h.Database);
        return (new SubscriptionService(store, delivery, renderer, directory, h.Clock), store, renderer, directory, channel, channels);
    }

    // ---- the request ----

    [Fact]
    public void A_request_is_read_strictly()
    {
        var spec = SubscriptionSpec.Read(JsonNode.Parse("""
            {"name": "Weekly sales", "kind": "dashboard", "view": "sales", "parameters": {"region": ["east", "west"], "year": 2026}, "layout": "print", "channel": "mail",
             "schedule": {"cron": "0 7 * * 1", "time_zone": "Europe/Berlin"}}
            """)!.AsObject(), "request");
        Assert.Equal(("Weekly sales", "dashboard", "sales", "pdf", "print", "mail", "0 7 * * 1", "Europe/Berlin"), (spec.Fields.Name, spec.Fields.Kind, spec.Fields.ViewName, spec.Fields.ExportFormat, spec.Fields.Layout, spec.Fields.Channel, spec.Fields.Cron, spec.Fields.TimeZone));
        Assert.Equal(2026, (int)spec.Fields.Parameters!["year"]!);

        static string Refused(string json) => Assert.Throws<SubscriptionSpecException>(() => SubscriptionSpec.Read(JsonNode.Parse(json)!.AsObject(), "request")).Message;
        const string Ok = "\"name\": \"n\", \"kind\": \"report\", \"view\": \"v\", \"channel\": \"mail\", \"schedule\": {\"cron\": \"0 7 * * *\"}";
        Assert.Contains("a subscription has no key `when`", Refused("{" + Ok + ", \"when\": \"now\"}"));
        Assert.Contains("kind is `dashboard` or `report`, not `chart`", Refused("""{"name": "n", "kind": "chart", "view": "v", "channel": "mail", "schedule": {"cron": "0 7 * * *"}}"""));
        Assert.Contains("format `csv` is one widget's rows: name it with `widget`", Refused("{" + Ok + ", \"format\": \"csv\"}"));
        Assert.Contains("`widget` is for `csv` and `xlsx`", Refused("{" + Ok + ", \"widget\": \"w\"}"));
        Assert.Contains("format is `pdf`, `csv` or `xlsx`, not `docx`", Refused("{" + Ok + ", \"format\": \"docx\"}"));
        Assert.Contains("schedule is required", Refused("""{"name": "n", "kind": "report", "view": "v", "channel": "mail"}"""));
        Assert.Contains("schedule: 'not a cron' is not a cron expression", Refused("""{"name": "n", "kind": "report", "view": "v", "channel": "mail", "schedule": {"cron": "not a cron"}}"""));
        Assert.Contains("is not a time zone this machine knows", Refused("""{"name": "n", "kind": "report", "view": "v", "channel": "mail", "schedule": {"cron": "0 7 * * *", "time_zone": "Mars/Olympus"}}"""));
        Assert.Contains("runs every 15 minutes; a subscription is at most hourly", Refused("""{"name": "n", "kind": "report", "view": "v", "channel": "mail", "schedule": {"cron": "*/15 * * * *"}}"""));
        Assert.Contains("parameters.region is a value or a list of values", Refused("{" + Ok + ", \"parameters\": {\"region\": {\"a\": 1}}}"));
        Assert.Contains("recipient is an email address", Refused("{" + Ok + ", \"recipient\": \"nobody\"}"));
        Assert.Contains("view `../etc` is not a name", Refused("""{"name": "n", "kind": "report", "view": "../etc", "channel": "mail", "schedule": {"cron": "0 7 * * *"}}"""));
        Assert.Contains("`layout` is for a PDF", Refused("{" + Ok + ", \"format\": \"xlsx\", \"layout\": \"print\"}"));
    }

    [Fact]
    public void The_schedule_is_read_in_the_zone_and_follows_daylight_saving_in_two_zones()
    {
        // Berlin changes on 2026-03-29 (01:00 UTC): 07:00 is 06:00 UTC before and 05:00 UTC after. New York changes on 2026-03-08 (02:00 local): 07:00 is 12:00 UTC on the 7th (EST) and 11:00 UTC from the 8th (EDT).
        var berlinBefore = SubscriptionSpec.Next("0 7 * * *", "Europe/Berlin", DateTimeOffset.Parse("2026-03-27T10:00:00Z"));
        var berlinAcross = SubscriptionSpec.Next("0 7 * * *", "Europe/Berlin", DateTimeOffset.Parse("2026-03-28T10:00:00Z"));
        var berlinAfter = SubscriptionSpec.Next("0 7 * * *", "Europe/Berlin", DateTimeOffset.Parse("2026-03-29T10:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-03-28T06:00:00Z"), berlinBefore);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T05:00:00Z"), berlinAcross);
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T05:00:00Z"), berlinAfter);
        Assert.Equal(DateTimeOffset.Parse("2026-03-07T12:00:00Z"), SubscriptionSpec.Next("0 7 * * *", "America/New_York", DateTimeOffset.Parse("2026-03-06T13:00:00Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T11:00:00Z"), SubscriptionSpec.Next("0 7 * * *", "America/New_York", DateTimeOffset.Parse("2026-03-07T13:00:00Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T11:00:00Z"), SubscriptionSpec.Next("0 7 * * *", "America/New_York", DateTimeOffset.Parse("2026-03-08T13:00:00Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T11:00:00Z"), SubscriptionSpec.Next("0 7 * * *", "America/New_York", DateTimeOffset.Parse("2026-03-08T04:00:00Z")));   // 07:00 EDT on the day it changes
        // the same instant is two different days in two zones
        Assert.NotEqual(SubscriptionSpec.Next("0 7 * * *", "Europe/Berlin", T0)!.Value.TimeOfDay, SubscriptionSpec.Next("0 7 * * *", "America/New_York", T0)!.Value.TimeOfDay);
    }

    // ---- the store ----

    [Fact]
    public void The_module_has_the_subscription_tables_in_its_second_migration_and_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(DeliveryModule.Module));
        Assert.Equal(3, DeliveryModule.Module.Version);
        Assert.Contains(DeliveryModule.Module.Retention, r => r is { Table: "subscription_run", TimeColumn: "started_at", DefaultDays: 90 });
        Assert.DoesNotContain(DeliveryModule.Module.Retention, r => r.Table == "subscription");
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_subscription_is_stored_edited_paused_and_resumed_and_its_runs_are_listed(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var store = new SubscriptionStore(h.Database);
        var first = SubscriptionSpec.Next("0 7 * * *", "UTC", T0)!.Value;
        var id = store.Create(Fields(), "ana", "Ana Lopez", "ana", first);
        var row = store.Find(id)!;
        Assert.Equal(("Weekly sales", "ana", "ana", "active", (DateTimeOffset?)first, 1L), (row.Fields.Name, row.Person, row.Owner, row.Status, row.NextRunAt, row.Version));
        Assert.Equal("east", (string)row.Fields.Parameters!["region"]![0]!);
        Assert.Equal(("print", "pdf", "mail", "0 7 * * *"), (row.Fields.Layout, row.Fields.ExportFormat, row.Fields.Channel, row.Fields.Cron));

        var edited = Fields("Weekly sales EU", cron: "0 8 * * 1", zone: "Europe/Berlin");
        Assert.False(store.Update(id, edited, first, expectedVersion: 99));
        Assert.True(store.Update(id, edited, first, row.Version));
        Assert.Equal(("Weekly sales EU", "Europe/Berlin"), (store.Find(id)!.Fields.Name, store.Find(id)!.Fields.TimeZone));

        Assert.True(store.SetStatus(id, "paused", null, null));
        Assert.Null(store.Find(id)!.NextRunAt);
        Assert.Empty(store.ClaimDue(T0 + TimeSpan.FromDays(30), _ => T0));
        Assert.True(store.SetStatus(id, "active", null, first));
        Assert.Equal("active", store.Find(id)!.Status);
        Assert.Throws<ArgumentException>(() => store.SetStatus(id, "active", null, null));

        store.RecordRun(id, T0, SubscriptionOutcomes.Sent, "sales.pdf is in the outbox", Guid.NewGuid(), "run-1");
        store.RecordRun(id, T0 + TimeSpan.FromDays(1), SubscriptionOutcomes.Failed, "no browser", problem: "the host has no browser");
        var runs = store.Runs(id);
        Assert.Equal(["failed", "sent"], runs.Select(r => r.Outcome));
        Assert.Equal("the host has no browser", store.Find(id)!.StatusDetail);
        Assert.Equal("failed", store.Find(id)!.LastOutcome);
        Assert.Single(store.List(person: "ana"));
        Assert.Empty(store.List(person: "bo"));
        Assert.True(store.Delete(id));
        Assert.Null(store.Find(id));
        Assert.Empty(store.Runs(id));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Two_runners_take_a_due_subscription_once_and_a_runner_that_was_down_takes_it_once(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var store = new SubscriptionStore(h.Database);
        var ids = Enumerable.Range(0, 6).Select(i => store.Create(Fields($"s{i}"), "ana", null, "ana", T0 - TimeSpan.FromDays(i))).ToList();
        var taken = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Parallel.For(0, 4, _ =>
        {
            foreach (var row in store.ClaimDue(T0, r => SubscriptionSpec.Next(r.Fields.Cron, r.Fields.TimeZone, T0), max: 10)) taken.Add(row.Id);
        });
        Assert.Equal(6, taken.Count);
        Assert.Equal(ids.Order(), taken.Order());                               // each once, though one of them was due six days ago
        Assert.All(ids, id => Assert.True(store.Find(id)!.NextRunAt > T0));
        Assert.Empty(store.ClaimDue(T0, r => T0.AddDays(1)));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_runs_of_a_subscription_expire_after_ninety_days_and_the_subscription_stays(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var store = new SubscriptionStore(h.Database);
        var id = store.Create(Fields(), "ana", null, "ana", T0);
        store.RecordRun(id, T0, SubscriptionOutcomes.Sent, "ok");
        h.Clock.Advance(TimeSpan.FromDays(91));
        new RetentionJob(h.Database).Run();
        Assert.Empty(store.Runs(id));
        Assert.NotNull(store.Find(id));
    }

    // ---- the runs ----

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_subscribers_of_one_view_with_different_rules_receive_different_rows_and_the_outbox_shows_both(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, renderer, _, channel, _) = Build(h, r => Rendered($"rows for {r.Subscriber}: " + (r.Subscriber == "ana" ? "east" : "west")));
        var ana = store.Create(Fields("Sales (Ana)"), "ana", "Ana Lopez", "ana", T0);
        var bo = store.Create(Fields("Sales (Bo)"), "bo", "Bo Chen", "bo", T0);

        var results = await service.RunDueAsync(default, runId: "run-7");
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("sent", r.Outcome));
        Assert.Equal(["ana", "bo"], renderer.Asked.Select(a => a.Subscriber).Order());      // each rendered as that person, by id: never as the runner
        Assert.All(renderer.Asked, a => Assert.Equal(("dashboard", "sales", "print", "pdf"), (a.Kind, a.View, a.Layout, a.Format)));
        Assert.Equal("east", (string)renderer.Asked.Single(a => a.Subscriber == "ana").Parameters["region"]![0]!);

        var messages = h.Store.List(source: null).Where(m => m.Source!.StartsWith("subscription:")).ToList();
        Assert.Equal(2, messages.Count);
        var byPerson = messages.ToDictionary(m => m.OwnerPerson!);
        Assert.Equal(["ana@example.com"], byPerson["ana"].Recipients);
        Assert.Equal(["bo@example.com"], byPerson["bo"].Recipients);
        string Attached(MessageRow m) { using var reader = h.Payloads.OpenRead(m.Attachments[0].Payload); using var ms = new MemoryStream(); reader.CopyTo(ms); return Encoding.UTF8.GetString(ms.ToArray()); }
        Assert.Equal("rows for ana: east", Attached(byPerson["ana"]));
        Assert.Equal("rows for bo: west", Attached(byPerson["bo"]));
        Assert.Contains("rows for ana: east", byPerson["ana"].BodyText);
        Assert.DoesNotContain("west", byPerson["ana"].BodyText + byPerson["ana"].BodyHtml);
        Assert.Contains("You receive this because you subscribed to 'Sales (Ana)'", byPerson["ana"].BodyText);
        Assert.Equal("https://console.example/views/sales", byPerson["ana"].Link);

        await h.Service(new ChannelSet(channel), "sender").RunOnceAsync();
        Assert.Equal(2, channel.Received.Count);
        var runs = store.Runs(ana);
        var run = Assert.Single(runs);
        Assert.Equal(((string?)"sent", (string?)"run-7", (Guid?)byPerson["ana"].Id), (run.Outcome, run.RunId, run.MessageId));
        Assert.Equal("sent", store.Find(bo)!.LastOutcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_subscriber_who_lost_access_gets_nothing_the_subscription_stops_and_says_why_to_them_and_the_owner(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, _, _, _, _) = Build(h, r => r.Subscriber == "bo" ? new RenderResult.AccessLost("Bo Chen is no longer in the group `sales-east`, which the model's rule grants.") : Rendered("rows"));
        var bo = store.Create(Fields("Sales (Bo)"), "bo", "Bo Chen", "admin", T0);
        var ana = store.Create(Fields("Sales (Ana)"), "ana", "Ana Lopez", "ana", T0);

        var results = (await service.RunDueAsync(default)).ToDictionary(r => r.Name);
        Assert.Equal("access-lost", results["Sales (Bo)"].Outcome);
        Assert.Contains("no longer in the group `sales-east`", results["Sales (Bo)"].Detail);
        Assert.Equal("sent", results["Sales (Ana)"].Outcome);

        var stopped = store.Find(bo)!;
        Assert.Equal(("stopped", null), (stopped.Status, stopped.NextRunAt));
        Assert.StartsWith("Stopped: Bo Chen is no longer in the group", stopped.StatusDetail);
        var all = h.Store.List();
        Assert.DoesNotContain(all, m => m.Attachments.Count > 0 && m.OwnerPerson == "bo");                // no document went to Bo
        var notice = all.Where(m => m.Subject == "Subscription 'Sales (Bo)' was stopped").Single();
        Assert.Equal(["bo@example.com", "admin@example.com"], notice.Recipients);                          // the subscriber and the owner
        Assert.Contains("no longer in the group `sales-east`", notice.BodyText);
        Assert.Equal("access-lost", Assert.Single(store.Runs(bo)).Outcome);

        h.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Single(await service.RunDueAsync(default));                                                  // only Ana's: a stopped subscription is not due
        Assert.Equal("stopped", store.Find(bo)!.Status);
        Assert.Equal("active", store.Find(ana)!.Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_person_the_identity_home_no_longer_knows_stops_their_subscription_without_rendering(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, renderer, directory, _, _) = Build(h, _ => Rendered("rows"));
        var id = store.Create(Fields(), "ghost", "Gus Host", "admin", T0);
        var result = Assert.Single(await service.RunDueAsync(default));
        Assert.Equal("access-lost", result.Outcome);
        Assert.Contains("Gus Host is not known to the identity home any more", result.Detail);
        Assert.Empty(renderer.Asked);
        Assert.Equal("stopped", store.Find(id)!.Status);
        Assert.Equal(["admin@example.com"], Assert.Single(h.Store.List()).Recipients);                    // the owner is told (the subscriber has no address any more)

        directory.People["off"] = new("off", "Olga Off", "olga@example.com", Active: false);
        var disabled = store.Create(Fields("Disabled"), "off", "Olga Off", "off", T0);
        Assert.Equal("access-lost", Assert.Single(await service.RunDueAsync(default)).Outcome);
        Assert.Equal("stopped", store.Find(disabled)!.Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_render_that_fails_sends_nothing_tells_the_owner_and_the_subscription_tries_again_at_its_next_time(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var fail = true;
        var (service, store, _, _, _, _) = Build(h, _ => fail ? new RenderResult.Failed("unavailable", "export unavailable: the browser is not installed (views.export.path)") : Rendered("rows"));
        var id = store.Create(Fields("Sales (Ana)"), "ana", "Ana Lopez", "admin", T0);

        var first = Assert.Single(await service.RunDueAsync(default));
        Assert.Equal("failed", first.Outcome);
        Assert.Contains("the browser is not installed", first.Detail);
        var notice = Assert.Single(h.Store.List());
        Assert.Equal(["admin@example.com"], notice.Recipients);
        Assert.Empty(notice.Attachments);
        Assert.Contains("Nothing was sent", notice.BodyText);
        var row = store.Find(id)!;
        Assert.Equal("active", row.Status);
        Assert.True(row.NextRunAt > T0);

        fail = false;
        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("sent", Assert.Single(await service.RunDueAsync(default)).Outcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_removed_channel_is_a_problem_the_subscription_shows_and_a_good_run_clears_it(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, renderer, _, _, channels) = Build(h, _ => Rendered("rows"));
        var id = store.Create(Fields(), "ana", "Ana Lopez", "ana", T0);
        channels.Remove("mail");

        var result = Assert.Single(await service.RunDueAsync(default));
        Assert.Equal("failed", result.Outcome);
        Assert.Contains("The delivery channel 'mail' is not configured", result.Detail);
        Assert.Empty(renderer.Asked);                                                                      // nothing was rendered for a channel that is gone
        var row = store.Find(id)!;
        Assert.Equal("active", row.Status);
        Assert.Contains("The delivery channel 'mail' is not configured", row.StatusDetail);
        Assert.Empty(h.Store.List());

        channels.Remove("mail");
        // the administrator adds the channel back
        var again = new ChannelSet(new ScriptedChannel("mail", _ => DeliveryResult.Sent()));
        var service2 = new SubscriptionService(store, h.Service(again, "sender"), renderer, new FakeDirectory(), h.Clock);
        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("sent", Assert.Single(await service2.RunDueAsync(default)).Outcome);
        Assert.Null(store.Find(id)!.StatusDetail);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Send_now_runs_a_stopped_subscription_to_see_whether_access_is_back_and_does_not_move_its_schedule(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var lost = true;
        var (service, store, _, _, _, _) = Build(h, _ => lost ? new RenderResult.AccessLost("not in the group") : Rendered("rows"));
        var id = store.Create(Fields(), "ana", "Ana Lopez", "ana", T0);
        Assert.Equal("access-lost", (await service.RunNowAsync(id, default)).Outcome);
        Assert.Equal("stopped", store.Find(id)!.Status);

        lost = false;
        Assert.Equal("sent", (await service.RunNowAsync(id, default)).Outcome);
        Assert.Equal("stopped", store.Find(id)!.Status);                                                   // a person resumes it; a successful trial does not
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RunNowAsync(Guid.NewGuid(), default));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Subscriptions_in_two_time_zones_come_due_at_their_own_local_morning_across_a_daylight_change(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, _, _, _, _) = Build(h, _ => Rendered("rows"));
        var newYork = store.Create(Fields("New York", cron: "0 7 * * *", zone: "America/New_York"), "bo", null, "bo", DateTimeOffset.Parse("2026-03-07T12:00:00Z"));
        var berlin = store.Create(Fields("Berlin", cron: "0 7 * * *", zone: "Europe/Berlin"), "ana", null, "ana", DateTimeOffset.Parse("2026-03-28T06:00:00Z"));
        void At(string utc) => h.Clock.Advance(DateTimeOffset.Parse(utc) - h.Clock.GetUtcNow());

        // New York changes on the night of the 7th to the 8th: the morning after is due at 11:00 UTC, an hour earlier than the morning before was
        At("2026-03-07T12:00:05Z");
        Assert.Equal(["New York"], (await service.RunDueAsync(default)).Select(r => r.Name));
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T11:00:00Z"), store.Find(newYork)!.NextRunAt);
        At("2026-03-08T11:00:05Z");
        Assert.Equal(["New York"], (await service.RunDueAsync(default)).Select(r => r.Name));
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T11:00:00Z"), store.Find(newYork)!.NextRunAt);

        // Berlin changes on the night of the 28th to the 29th: the morning after is due at 05:00 UTC; New York was not asked for 19 days and is due once, not once for each missed morning
        At("2026-03-28T06:00:05Z");
        var results = await service.RunDueAsync(default);
        Assert.Equal(["Berlin", "New York"], results.Select(r => r.Name).Order());
        Assert.All(results, r => Assert.Equal("sent", r.Outcome));
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T05:00:00Z"), store.Find(berlin)!.NextRunAt);
        Assert.Equal(DateTimeOffset.Parse("2026-03-28T11:00:00Z"), store.Find(newYork)!.NextRunAt);
        At("2026-03-29T05:00:05Z");
        Assert.Contains("Berlin", (await service.RunDueAsync(default)).Select(r => r.Name));
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T05:00:00Z"), store.Find(berlin)!.NextRunAt);
        Assert.Equal(1, store.Runs(berlin).Count(r => r.StartedAt < DateTimeOffset.Parse("2026-03-29T00:00:00Z")));
    }
}
