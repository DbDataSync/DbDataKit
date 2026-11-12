using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Delivery.Tests;

/// <summary>An alert source a test scripts: what is wrong right now.</summary>
internal sealed class ScriptedAlertSource : IAlertSource
{
    public List<AlertReading> Now { get; } = [];

    public bool Throws { get; set; }

    public Task<IReadOnlyList<AlertReading>> ReadAsync(CancellationToken cancellationToken) =>
        Throws ? throw new InvalidOperationException("the run history is not readable") : Task.FromResult<IReadOnlyList<AlertReading>>([.. Now]);
}

/// <summary>Plan 22a GP16: alerts on every state engine (SQLite always; SQL Server and PostgreSQL when the shared engines are up).</summary>
public sealed class AlertTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static (AlertService Service, AlertStore Store, ScriptedAlertSource Source, ScriptedChannel Channel, RecordingFeed Feed) Build(DeliveryHarness h)
    {
        var channel = new ScriptedChannel("ops", _ => DeliveryResult.Sent());
        var delivery = h.Service(new ChannelSet(channel), "sender");
        var store = new AlertStore(h.Database);
        var source = new ScriptedAlertSource();
        var feed = new RecordingFeed();
        return (new AlertService(store, delivery, [source], feed, h.Clock), store, source, channel, feed);
    }

    private static AlertReading Failed(string job = "nightly") => new(AlertKinds.FailedRun, "failed:" + job, $"The last run of {job} failed.");

    [Fact]
    public void The_module_follows_the_conventions_and_the_rules_default_to_off()
    {
        Assert.Empty(StateConventions.Violations(DeliveryModule.Module));
        using var h = new DeliveryHarness("sqlite");
        var rules = new AlertStore(h.Database).Rules();
        Assert.Equal(AlertKinds.All, rules.Select(r => r.Kind));
        Assert.All(rules, r => Assert.False(r.Enabled));
        Assert.Throws<ArgumentException>(() => new AlertStore(h.Database).SetRule("mood", true, null, [], 60, null, 0));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_rule_is_set_edited_by_version_and_read_back(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var store = new AlertStore(h.Database);
        Assert.True(store.SetRule(AlertKinds.FailedRun, true, "ops", ["ana@example.com"], 30, null, 0));
        Assert.False(store.SetRule(AlertKinds.FailedRun, false, null, [], 5, null, 0));           // written already: a second "first write" loses
        var rule = store.Rules().Single(r => r.Kind == AlertKinds.FailedRun);
        Assert.Equal((true, "ops", 30, 1L), (rule.Enabled, rule.Channel, rule.CooldownMinutes, rule.Version));
        Assert.Equal(["ana@example.com"], rule.Recipients);
        Assert.False(store.SetRule(AlertKinds.FailedRun, false, null, [], 5, null, 7));           // a stale version
        Assert.True(store.SetRule(AlertKinds.FailedRun, false, null, [], 5, "test", rule.Version));
        var again = store.Rules().Single(r => r.Kind == AlertKinds.FailedRun);
        Assert.Equal((false, null, 5, "test", 2L), (again.Enabled, again.Channel, again.CooldownMinutes, again.ReleaseChannel, again.Version));
        await Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_failed_run_sends_once_not_every_minute_again_after_the_cooldown_and_one_recovered(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, source, channel, feed) = Build(h);
        store.SetRule(AlertKinds.FailedRun, true, "ops", [], 60, null, 0);
        var delivery = h.Service(new ChannelSet(channel), "sender");
        source.Now.Add(Failed());

        Assert.Equal(1, (await service.RunOnceAsync()).Sent);
        for (var minute = 0; minute < 5; minute++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            var quiet = await service.RunOnceAsync();
            Assert.Equal((0, 1), (quiet.Sent, quiet.Quiet));
        }

        Assert.Single(h.Store.List(source: "alert:failed-run"));

        h.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, (await service.RunOnceAsync()).Sent);                                    // still failing after the cool-down: told again
        Assert.Equal(2, h.Store.List(source: "alert:failed-run").Count);

        source.Now.Clear();
        var recovered = await service.RunOnceAsync();
        Assert.Equal(1, recovered.Recovered);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await service.RunOnceAsync()).Recovered);                               // one "recovered", not one per pass
        var messages = h.Store.List(source: "alert:failed-run");
        Assert.Equal(3, messages.Count);
        Assert.Single(messages, m => m.Subject.StartsWith("Recovered", StringComparison.Ordinal));
        Assert.Equal(AlertStateRow.Ok, store.States().Single().Status);
        Assert.Equal(2, feed.Raised.Count(r => r.Kind == AlertService.FeedKind));
        await delivery.RunOnceAsync();
        Assert.Equal(3, channel.Received.Count);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task An_alert_with_no_channel_is_shown_in_the_feed_only(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, source, _, feed) = Build(h);
        store.SetRule(AlertKinds.FailedRun, true, null, [], 60, null, 0);
        source.Now.Add(Failed());
        var result = await service.RunOnceAsync();
        Assert.Equal((0, 1), (result.Sent, result.FeedOnly));
        Assert.Empty(h.Store.List());
        Assert.Single(feed.Raised, r => r.Kind == AlertService.FeedKind && r.Message.Contains("nightly"));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await service.RunOnceAsync()).FeedOnly);                                // not every minute
        source.Now.Clear();
        Assert.Equal(1, (await service.RunOnceAsync()).Recovered);
        Assert.Contains(feed.Raised, r => r.Kind == AlertService.RecoveredFeedKind);
        Assert.Empty(h.Store.List());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_cooldown_is_each_recipients_own_and_a_kind_that_is_off_says_nothing(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, source, _, _) = Build(h);
        store.SetRule(AlertKinds.FailedRun, true, "ops", ["ana@example.com"], 60, null, 0);
        store.SetRule(AlertKinds.LateData, false, "ops", ["ana@example.com"], 60, null, 0);
        source.Now.Add(Failed("a"));
        source.Now.Add(new AlertReading(AlertKinds.LateData, "overdue:b", "b is late."));
        Assert.Equal(1, (await service.RunOnceAsync()).Sent);                                    // late data is off
        Assert.Equal("ana@example.com", Assert.Single(h.Store.List()).Recipients.Single());

        store.SetRule(AlertKinds.FailedRun, true, "ops", ["ana@example.com", "bo@example.com"], 60, null, store.Rules().Single(r => r.Kind == AlertKinds.FailedRun).Version);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var added = await service.RunOnceAsync();
        Assert.Equal((1, 1), (added.Sent, added.Quiet));                                         // bo is told now, ana is still in her cool-down
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_source_that_cannot_be_read_and_a_channel_that_is_gone_tell_nobody_anything_false(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (service, store, source, channel, _) = Build(h);
        store.SetRule(AlertKinds.FailedRun, true, "ops", [], 60, null, 0);
        source.Now.Add(Failed());
        await service.RunOnceAsync();
        source.Now.Clear();
        source.Throws = true;
        var broken = await service.RunOnceAsync();
        Assert.Equal(0, broken.Recovered);                                                       // not "recovered": the reading failed
        Assert.Contains("run history", Assert.Single(broken.Problems));
        Assert.Equal(AlertStateRow.Failing, store.States().Single().Status);

        source.Throws = false;
        source.Now.Add(Failed("other"));
        store.SetRule(AlertKinds.FailedRun, true, "gone", [], 60, null, store.Rules().Single(r => r.Kind == AlertKinds.FailedRun).Version);
        var channels = new ChannelSet(channel);
        channels.Remove("ops");
        var gone = new AlertService(store, h.Service(channels, "sender"), [source], null, h.Clock);
        var result = await gone.RunOnceAsync();
        Assert.Equal(0, result.Sent);
        Assert.Contains("'gone' is not configured", string.Join(" ", result.Problems));
        Assert.DoesNotContain(store.States(), s => s.Source == "failed:other");                 // nothing was sent, so nothing is remembered: the next pass tries again
    }

    [Fact]
    public async Task Health_findings_become_readings_of_the_kinds_the_rules_name()
    {
        var eval = new HealthEvaluator();
        var now = DateTimeOffset.Parse("2026-11-12T10:00:00Z");
        var failed = eval.Evaluate(new JobExpectation("nightly", TimeSpan.FromHours(1)), new RunRecord("nightly", "r1", now.AddMinutes(-5), now.AddMinutes(-4), RunOutcome.Failed));
        var source = new HealthAlertSource(_ => Task.FromResult<IReadOnlyList<JobHealth>>([failed]));
        var readings = await source.ReadAsync(default);
        Assert.Contains(readings, r => r.Kind == AlertKinds.FailedRun && r.Source == "failed:nightly");
        Assert.All(readings, r => Assert.Contains(r.Kind, AlertKinds.All));
    }
}
