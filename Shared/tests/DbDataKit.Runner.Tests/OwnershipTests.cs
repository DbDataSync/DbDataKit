using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>
/// Plan 28c E09, the runner's half: who schedules a task while a product's own scheduler is still in the picture. <c>old</c> leaves the task alone, <c>observe</c> evaluates the schedule against a copy of the position and records decisions without
/// queueing anything, <c>runner</c> schedules from the position the product handed over. Fake clock throughout (no waiting on time), on each state engine.
/// </summary>
public sealed class OwnershipTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static string Owner => $"{Package}/{Channel}";

    private static TaskRef Ref(string task) => new(Package, Channel, task);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>The 09:00 daily times a task of owner runner queues and the ones an observed twin of it would have, found by ticking hourly across the window.</summary>
    private static async Task<(List<DateTimeOffset> Queued, List<DateTimeOffset> Observed, int Requests)> RunnerVersusObserve(string engine, string zone, DateTimeOffset from, DateTimeOffset to)
    {
        using var h = new SchedulerHarness(engine);
        h.Clock.Set(from);
        h.Activate(Manifest(Package, Def("owned", Schedule.OnCron("0 9 * * *", zone)), Def("watched", Schedule.OnCron("0 9 * * *", zone))), "1");
        h.SeedHandled("owned");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        scheduler.SetOwner(Ref("watched"), TaskOwners.Observe, "ada", null, new ScheduleSeed(from, null, null));
        var queued = new List<DateTimeOffset>();
        for (; h.Clock.GetUtcNow() <= to; h.Clock.Advance(TimeSpan.FromHours(1)))
        {
            var report = await Tick(scheduler);
            if (report.Enqueued.Any(t => t.Task == "owned")) queued.Add(h.Clock.GetUtcNow());
            Assert.DoesNotContain(report.Enqueued, t => t.Task == "watched");
        }

        var decisions = scheduler.DecisionsOf(Ref("watched"));
        Assert.All(decisions, d => Assert.Equal((TaskDecisions.Runner, 0, false), (d.Source, d.Missed, d.Paused)));
        Assert.Empty(runner.Of("watched"));                                   // nothing of an observed task ever runs
        Assert.Null(h.Jobs.Get(Owner, "watched"));                            // and its real position is never written: the schedule is evaluated against a copy
        return (queued, [.. decisions.Select(d => d.DecidedAt)], runner.Requests.Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Observe_records_the_times_a_runner_owned_task_runs_for_cron_in_New_York_across_the_end_of_daylight_saving_time(string engine)
    {
        var (queued, observed, requests) = await RunnerVersusObserve(engine, "America/New_York", Utc(10, 30, 20), Utc(11, 2, 20));

        Assert.Equal([Utc(10, 31, 13), Utc(11, 1, 14), Utc(11, 2, 14)], queued);   // 09:00 local: 13:00 UTC before the change, 14:00 after it
        Assert.Equal(queued, observed);
        Assert.Equal(3, requests);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Observe_records_the_times_a_runner_owned_task_runs_for_cron_in_Berlin_across_the_end_of_daylight_saving_time(string engine)
    {
        var (queued, observed, _) = await RunnerVersusObserve(engine, "Europe/Berlin", Utc(10, 23, 20), Utc(10, 26, 20));

        Assert.Equal([Utc(10, 24, 7), Utc(10, 25, 8), Utc(10, 26, 8)], queued);
        Assert.Equal(queued, observed);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Observe_records_a_missed_span_as_one_decision_and_a_first_sight_as_none(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("hourly", Schedule.OnCron("0 * * * *"))), "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        scheduler.SetOwner(Ref("hourly"), TaskOwners.Observe, "ada", null, seed: null);      // the product had no position for it: a first sight

        await Tick(scheduler);                                                                // 12:00, noted
        Assert.Empty(scheduler.DecisionsOf(Ref("hourly")));
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        await Tick(scheduler);
        Assert.Empty(scheduler.DecisionsOf(Ref("hourly")));

        h.Clock.Advance(TimeSpan.FromHours(3));                                              // 15:30: 13:00, 14:00 and 15:00 passed while nobody looked
        var report = await Tick(scheduler);
        await Tick(scheduler);                                                                // the second tick of the same moment decides nothing more

        var decision = Assert.Single(scheduler.DecisionsOf(Ref("hourly")));
        Assert.Equal((Utc(10, 24, 15, 30), 2, false), (decision.DecidedAt, decision.Missed, decision.Paused));   // one decision, standing for one of three, with two missed
        Assert.Equal((1, 1, 0), (report.Observed, report.LeftToOldOwner, report.Enqueued.Count));
        Assert.Empty(runner.Requests);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Observe_passes_a_paused_time_over_and_says_so(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("hourly", Schedule.OnCron("0 * * * *"))), "1");
        var scheduler = h.NewScheduler(new FakeRunner(h.Database));
        scheduler.SetOwner(Ref("hourly"), TaskOwners.Observe, "ada", null, new ScheduleSeed(h.Clock.GetUtcNow(), "bob", h.Clock.GetUtcNow()));

        h.Clock.Advance(TimeSpan.FromMinutes(65));
        await Tick(scheduler);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await Tick(scheduler);                                                                // 13:15: the 13:00 time was handled, not decided again

        var decision = Assert.Single(scheduler.DecisionsOf(Ref("hourly")));
        Assert.True(decision.Paused);
        Assert.Equal("bob", scheduler.ScheduleStateOf(Ref("hourly")).PausedBy);               // the copy of the pause is what an observed task reports
        Assert.Null(h.Jobs.Get(Owner, "hourly"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_of_the_old_owner_is_left_alone_and_a_manual_run_still_works(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("hourly", Schedule.OnCron("0 * * * *"))), "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        scheduler.SetOwner(Ref("hourly"), TaskOwners.Old, "ada", "the product schedules it for now", null);

        h.Clock.Advance(TimeSpan.FromHours(5));
        var report = await Tick(scheduler);

        Assert.Equal((0, 1, 0), (report.Enqueued.Count, report.LeftToOldOwner, report.Observed));
        Assert.Null(h.Jobs.Get(Owner, "hourly"));                                             // no position was written: the schedule was not even looked at
        Assert.Equal(TaskOwners.Old, scheduler.Status().Tasks.Single().Owner);
        Assert.NotNull(scheduler.RunNow(Ref("hourly"), null, "ada"));                         // by hand is nobody's schedule
        await Tick(scheduler);
        Assert.Single(runner.Requests);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_nobody_handed_over_is_the_runners(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("tick", Schedule.Interval(TimeSpan.FromMinutes(10)))), "1");
        var scheduler = h.NewScheduler(new FakeRunner(h.Database));

        Assert.Equal((TaskOwners.Runner, null), (scheduler.OwnerOfTask(Ref("tick")).Owner, scheduler.OwnerOfTask(Ref("tick")).Record));
        Assert.Single((await Tick(scheduler)).Enqueued);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cutover_from_a_position_neither_runs_nor_skips_the_times_since(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("every10", Schedule.OnCron("*/10 * * * *"))), "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        var handed = h.Clock.GetUtcNow();                                                     // the product's position: 12:00 dealt with
        scheduler.SetOwner(Ref("every10"), TaskOwners.Old, "ada", null, null);
        h.Clock.Advance(TimeSpan.FromMinutes(25));                                            // the wait for the run in flight took 25 minutes: 12:10 and 12:20 passed, belonging to nobody

        scheduler.SetOwner(Ref("every10"), TaskOwners.Runner, "ada", "cutover", new ScheduleSeed(handed, null, null));
        var first = await Tick(scheduler);
        var second = await Tick(scheduler);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var third = await Tick(scheduler);                                                    // 12:30: the next time

        Assert.Equal([1, 0, 1], new[] { first.Enqueued.Count, second.Enqueued.Count, third.Enqueued.Count });   // the passed times ran once (not skipped, not twice); then the schedule goes on
        Assert.Equal(2, runner.Requests.Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cutover_hands_the_pause_over_and_a_paused_time_is_passed_over(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("hourly", Schedule.OnCron("0 * * * *"))), "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        scheduler.SetOwner(Ref("hourly"), TaskOwners.Runner, "ada", "cutover", new ScheduleSeed(h.Clock.GetUtcNow(), "bob", h.Clock.GetUtcNow()));
        Assert.Equal("bob", scheduler.ScheduleStateOf(Ref("hourly")).PausedBy);

        h.Clock.Advance(TimeSpan.FromMinutes(65));
        var report = await Tick(scheduler);

        Assert.Equal((0, 1), (report.Enqueued.Count, report.Paused));
        Assert.Empty(runner.Requests);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Handing_back_leaves_the_position_for_the_product_and_reports_what_is_still_in_flight(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("every10", Schedule.Interval(TimeSpan.FromMinutes(10)))), "1");
        h.SeedHandled("every10");
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(h.Database) { Behaviour = async (_, _) => { await release.Task; return new TaskEnd(TaskEndState.Succeeded); } };
        var scheduler = h.NewScheduler(runner);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await scheduler.TickAsync();                                                          // due: queued and running, held open by the test
        await Until(() => runner.Active == 1, "the run to start");
        var handled = h.Clock.GetUtcNow();

        scheduler.SetOwner(Ref("every10"), TaskOwners.Old, "ada", "rollback", null);
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        var state = scheduler.ScheduleStateOf(Ref("every10"));
        Assert.Equal((TaskOwners.Old, handled, 1, 0), (state.Owner, state.HandledUpTo, state.Running, state.Queued));   // the product waits for this run, then reads the position the runner left

        release.SetResult();
        await scheduler.WhenIdleAsync();
        await Tick(scheduler);                                                                // nothing is queued for the 30 minutes: the runner no longer looks at the schedule
        state = scheduler.ScheduleStateOf(Ref("every10"));
        Assert.Equal((handled, 0, 0), (state.HandledUpTo, state.Running, state.Queued));
        Assert.Single(runner.Requests);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_loops_over_one_state_queue_a_runner_owned_task_once_and_an_old_owned_one_never(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("owned", Schedule.Interval(TimeSpan.FromMinutes(10))), Def("theirs", Schedule.Interval(TimeSpan.FromMinutes(10)))), "1");
        h.SeedHandled("owned");
        h.SeedHandled("theirs");
        var first = h.NewScheduler(new FakeRunner(h.Database));
        var second = h.NewScheduler(new FakeRunner(h.OpenDatabase()), database: h.OpenDatabase());
        first.SetOwner(Ref("theirs"), TaskOwners.Old, "ada", null, null);
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        var a = await first.TickAsync();
        var b = await second.TickAsync();

        Assert.Equal(["owned"], a.Enqueued.Concat(b.Enqueued).Select(t => t.Task));            // one item for the owned task between the two loops, none for the other
        await first.WhenIdleAsync();
        await second.WhenIdleAsync();
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_owner_is_validated_and_a_task_can_be_named_before_it_has_a_version_or_a_channel_this_loop_serves(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var scheduler = h.NewScheduler(new FakeRunner(h.Database));

        Assert.Throws<ArgumentException>(() => scheduler.SetOwner(Ref("x"), "both", "ada", null, null));
        scheduler.SetOwner(new TaskRef(Package, "other", "x"), TaskOwners.Old, "ada", null, null);              // another instance of the group serves that channel: the owner is the group's state
        Assert.Equal(TaskOwners.Old, scheduler.OwnerOfTask(new TaskRef(Package, "other", "x")).Owner);
        scheduler.SetOwner(Ref("not-published-yet"), TaskOwners.Old, "ada", "before the first publish", null);      // a package need not have a version to be named

        var record = new TaskOwnerStore(h.Database).Get(Package, Channel, "not-published-yet")!;
        Assert.Equal((TaskOwners.Old, "ada", "before the first publish"), (record.Owner, record.ChangedBy, record.Reason));
        Assert.Equal(h.Clock.GetUtcNow(), record.ChangedAt);
        Assert.Single(new TaskOwnerStore(h.Database).List(Package, Channel));
        Assert.Equal(2, new TaskOwnerStore(h.Database).List(Package).Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Decisions_are_recorded_once_per_source_and_moment_and_old_ones_are_dropped(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var decisions = new TaskDecisions(h.Database);
        var scheduler = h.NewScheduler(new FakeRunner(h.Database));
        var at = h.Clock.GetUtcNow();

        scheduler.RecordOldDecision(Ref("t"), at, 0, false);
        scheduler.RecordOldDecision(Ref("t"), at, 0, false);
        decisions.Record(new TaskDecision(Package, Channel, "t", TaskDecisions.Runner, at, 1, true));
        Assert.Equal([TaskDecisions.Old, TaskDecisions.Runner], decisions.List(Package, Channel, "t").Select(d => d.Source));
        Assert.Single(decisions.List(Package, Channel, "t", source: TaskDecisions.Old));

        h.Clock.Advance(TaskDecisions.Retention + TimeSpan.FromDays(1));
        scheduler.RecordOldDecision(Ref("t"), h.Clock.GetUtcNow(), 0, false);                  // recording prunes this task's decisions that are older than the retention
        Assert.Equal([h.Clock.GetUtcNow()], decisions.List(Package, Channel, "t").Select(d => d.DecidedAt));
    }
}
