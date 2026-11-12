using DbDataKit.Monitoring;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State.Tests;
using DbDataKit.Scheduling;
using DbDataKit.State;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>Plan 28a R09: the runner's loop on each engine, with a fake clock and a fake executor: the matrix of 13a's M03 scheduler tests, the projection, the lease, pooled limits and the stop.</summary>
public sealed class SchedulerTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static string Owner => $"{Package}/{Channel}";

    // ---- when a task is due

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_that_never_ran_is_due_and_runs_once_with_its_run_recorded(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("report", Schedule.Interval(TimeSpan.FromMinutes(10)))), "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        var report = await Tick(scheduler);
        var second = await Tick(scheduler);

        Assert.Equal(["pkg/prod/report"], report.Enqueued.Select(t => t.ToString()));
        Assert.Empty(second.Enqueued);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(("1", 1), (request.PackageVersion, request.Attempt));
        var run = h.Runs.LastRun("pkg/prod/report")!;
        Assert.Equal(RunOutcome.Succeeded, run.Run.Outcome);
        Assert.Equal((Package, "1", Channel, 1), (run.Package, run.PackageVersion, run.Channel, run.Attempt));
        Assert.Empty(h.Queue.ListInFlight(Owner));
        var status = scheduler.Status().Tasks.Single();
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromMinutes(10), status.NextDue);
        Assert.Equal(RunOutcome.Succeeded, status.LastRun!.Run.Outcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task An_interval_task_starts_every_interval_from_the_start_of_its_last_run(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("tick", Schedule.Interval(TimeSpan.FromMinutes(10)))), "1");
        h.SeedHandled("tick");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty((await Tick(scheduler)).Enqueued);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Single((await Tick(scheduler)).Enqueued);
        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty((await Tick(scheduler)).Enqueued);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single((await Tick(scheduler)).Enqueued);
        Assert.Equal(2, runner.Requests.Count);
    }

    /// <summary>The daily 09:00 times of a cron in a zone, found by ticking hourly across <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static async Task<List<DateTimeOffset>> DailyRuns(string engine, string zone, DateTimeOffset from, DateTimeOffset to)
    {
        using var h = new SchedulerHarness(engine);
        h.Clock.Set(from);
        h.Activate(Manifest(Package, Def("daily", Schedule.OnCron("0 9 * * *", zone))), "1");
        h.SeedHandled("daily");
        var scheduler = h.NewScheduler(new FakeRunner(h.Database));
        var times = new List<DateTimeOffset>();
        for (; h.Clock.GetUtcNow() <= to; h.Clock.Advance(TimeSpan.FromHours(1)))
            if ((await Tick(scheduler)).Enqueued.Count == 1) times.Add(h.Clock.GetUtcNow());
        return times;
    }

    private static DateTimeOffset Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    [Theory, MemberData(nameof(Engines))]
    public async Task Cron_in_New_York_keeps_nine_local_across_the_end_of_daylight_saving_time(string engine) =>
        // New York leaves daylight saving at 02:00 on 1 November: 09:00 is 13:00 UTC before it and 14:00 UTC after
        Assert.Equal([Utc(10, 31, 13), Utc(11, 1, 14), Utc(11, 2, 14)], await DailyRuns(engine, "America/New_York", Utc(10, 30, 20), Utc(11, 2, 20)));

    [Theory, MemberData(nameof(Engines))]
    public async Task Cron_in_Berlin_keeps_nine_local_across_the_end_of_daylight_saving_time(string engine) =>
        // Berlin leaves daylight saving at 03:00 on 25 October: 09:00 is 07:00 UTC before it and 08:00 UTC after
        Assert.Equal([Utc(10, 24, 7), Utc(10, 25, 8), Utc(10, 26, 8)], await DailyRuns(engine, "Europe/Berlin", Utc(10, 23, 20), Utc(10, 26, 20)));

    [Theory, MemberData(nameof(Engines))]
    public async Task A_missed_time_is_run_once_or_let_pass_by_the_misfire_policy(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Clock.Set(Utc(10, 24, 12));
        h.Activate(Manifest(Package,
            Def("run-once", Schedule.OnCron("0 * * * *", misfire: MisfirePolicy.RunOnce)),
            Def("skip", Schedule.OnCron("0 * * * *", misfire: MisfirePolicy.Skip))), "1");
        h.SeedHandled("run-once");
        h.SeedHandled("skip");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        // the loop was down for five and a half hours: five hourly times passed
        h.Clock.Advance(TimeSpan.FromMinutes(330));
        var report = await Tick(scheduler);

        Assert.Equal(["pkg/prod/run-once"], report.Enqueued.Select(t => t.ToString()));
        Assert.Single(runner.Requests);
        // the skipped times are dealt with: the next tick, a minute later, does not judge them again
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty((await Tick(scheduler)).Enqueued);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_paused_task_is_not_queued_at_its_times_but_can_be_run_by_hand_and_resumes_without_a_catch_up(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromMinutes(10)))), "1");
        h.SeedHandled("job");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        var job = new TaskRef(Package, Channel, "job");

        scheduler.Pause(job, "alice");
        Assert.True(scheduler.Status().Tasks.Single().Paused);
        Assert.Equal("alice", scheduler.Status().Tasks.Single().PausedBy);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        var report = await Tick(scheduler);
        Assert.Equal(1, report.Paused);
        Assert.Empty(runner.Requests);

        Assert.NotNull(scheduler.RunNow(job, requestedBy: "alice"));    // a person asked: paused or not
        await Tick(scheduler);
        Assert.Single(runner.Requests);

        scheduler.Resume(job);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty((await Tick(scheduler)).Enqueued);                 // the time passed while paused is not replayed
        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Single((await Tick(scheduler)).Enqueued);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_never_overlaps_itself_unless_its_manifest_allows_it(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package,
            Def("strict", Schedule.Interval(TimeSpan.FromMinutes(1))),
            Def("loose", Schedule.Interval(TimeSpan.FromMinutes(1)), allowOverlap: true)), "1");
        h.SeedHandled("strict");
        h.SeedHandled("loose");
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(h.Database) { Behaviour = async (_, _) => { await release.Task; return new TaskEnd(TaskEndState.Succeeded); } };
        var scheduler = h.NewScheduler(runner);

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var first = await scheduler.TickAsync();
        await FakeStarted(runner, 2);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await scheduler.TickAsync();

        Assert.Equal(2, first.Enqueued.Count);
        Assert.Equal(1, second.OverlapRefused);                         // strict: one run going, none queued
        Assert.Equal(["pkg/prod/loose"], second.Enqueued.Select(t => t.ToString()));
        await FakeStarted(runner, 3);
        Assert.Equal(2, runner.Of("loose").Count);
        Assert.Single(runner.Of("strict"));
        release.SetResult();
        await scheduler.WhenIdleAsync();
    }

    private static Task FakeStarted(FakeRunner runner, int count) => Until(() => runner.Requests.Count >= count, $"{count} runs to have started");

    // ---- the projection

    [Theory, MemberData(nameof(Engines))]
    public async Task A_broken_projection_row_is_skipped_and_reported_and_the_others_run(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var manifest = Manifest(Package, Def("good", Schedule.Interval(TimeSpan.FromMinutes(1))));
        h.Versions.Add(Package, "1", manifest);
        var rows = new List<TaskProjectionRow>(ProjectionDocuments.RowsOf(manifest, "1"))
        {
            new("bad-schedule", "1", """{"cron":"not a cron"}""", null, null, [], null),
            new("bad-json", "1", "{ this is not json", null, null, [], null),
            new("bad-limits", "1", null, null, """{"attempts":0}""", [], null),
        };
        h.Projections.Replace(Package, Channel, rows);
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        var report = await Tick(scheduler);

        Assert.Equal(["pkg/prod/good"], report.Enqueued.Select(t => t.ToString()));
        Assert.Equal(3, report.SkippedRows);
        Assert.Equal("pkg/prod/good", Assert.Single(runner.Requests).Task.ToString());
        var status = scheduler.Status();
        Assert.Equal(["bad-json", "bad-limits", "bad-schedule", "good"], status.Tasks.Select(t => t.Task.Task).Order());
        Assert.Contains("not a cron", status.Tasks.Single(t => t.Task.Task == "bad-schedule").Problem);
        Assert.Contains("JSON", status.Tasks.Single(t => t.Task.Task == "bad-json").Problem);
        Assert.Contains("above zero", status.Tasks.Single(t => t.Task.Task == "bad-limits").Problem);
        Assert.Null(status.Tasks.Single(t => t.Task.Task == "good").Problem);
        Assert.Equal(3, status.Problems.Count(p => p.Subject.StartsWith("pkg/prod/bad", StringComparison.Ordinal)));
        // once the row is repaired the problem goes away
        h.Projections.Replace(Package, Channel, ProjectionDocuments.RowsOf(manifest, "1"));
        await Tick(scheduler);
        Assert.Empty(scheduler.Status().Problems);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Only_the_projection_rows_of_the_served_channels_are_read(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var manifest = Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromMinutes(1))));
        h.Activate(manifest, "1");
        h.Activate(manifest, "2", "preview");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, channels: ["preview"]);

        await Tick(scheduler);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(("preview", "2"), (request.Task.Channel, request.PackageVersion));
        Assert.Throws<InvalidOperationException>(() => scheduler.RunNow(new TaskRef(Package, Channel, "job")));
    }

    // ---- the lease

    [Theory, MemberData(nameof(Engines))]
    public async Task A_running_task_keeps_its_lease_with_heartbeats(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("long", Schedule.Interval(TimeSpan.FromHours(1)))), "1");
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(h.Database) { Behaviour = async (_, _) => { await release.Task; return new TaskEnd(TaskEndState.Succeeded); } };
        var scheduler = h.NewScheduler(runner, heartbeat: TimeSpan.FromMilliseconds(40));

        await scheduler.TickAsync();
        await FakeStarted(runner, 1);
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        var expected = h.Clock.GetUtcNow() + RunnerSchedulerOptions.DefaultLease;     // the default lease: 90 seconds
        await Until(() => h.Queue.ListInFlight(Owner).Single().LeaseExpiresAt == expected, "the lease to be renewed from the new time");

        Assert.Equal(WorkItemStatus.Running, h.Queue.ListInFlight(Owner).Single().Item.Status);
        release.SetResult();
        await scheduler.WhenIdleAsync();
        Assert.Empty(h.Queue.ListInFlight(Owner));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_loop_that_stalled_and_wakes_up_with_its_tick_due_before_its_heartbeat_keeps_its_own_running_item(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromHours(1)))), "1");
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(h.Database) { Behaviour = async (_, _) => { await release.Task; return new TaskEnd(TaskEndState.Succeeded); } };
        var scheduler = h.NewScheduler(runner, worker: "loop-a");

        await scheduler.TickAsync();
        await FakeStarted(runner, 1);
        var run = runner.Requests.Single().RunId!.Value;

        // the whole process stalls for longer than the lease (a loaded machine): no renewal happens
        scheduler.SuspendHeartbeats = true;
        h.Clock.Advance(TimeSpan.FromSeconds(100));
        scheduler.SuspendHeartbeats = false;
        // it wakes up and its tick runs before the item's heartbeat does: the loop is alive and the task is going on here, nobody else took the item, so it is not taken back
        var report = await scheduler.TickAsync();

        Assert.Equal(0, report.ExpiredLeases);
        Assert.Single(runner.Requests);                                   // no second attempt was started
        var inFlight = h.Queue.ListInFlight(Owner).Single();
        Assert.Equal(WorkItemStatus.Running, inFlight.Item.Status);
        Assert.Equal(run, inFlight.Item.RunId);
        Assert.Equal(h.Clock.GetUtcNow() + RunnerSchedulerOptions.DefaultLease, inFlight.LeaseExpiresAt);
        release.SetResult();
        await scheduler.WhenIdleAsync();

        var runs = h.Runs.Read(new RunQuery(Service, "pkg/prod/job"));
        Assert.Equal(RunOutcome.Succeeded, Assert.Single(runs).Run.Outcome);
        Assert.Empty(h.Queue.ListInFlight(Owner));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_tick_that_finds_the_lease_of_an_item_it_runs_itself_expired_leaves_it_alone_even_when_nothing_renewed_it(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromHours(1)))), "1");
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(h.Database) { Behaviour = async (_, _) => { await release.Task; return new TaskEnd(TaskEndState.Succeeded); } };
        var scheduler = h.NewScheduler(runner, worker: "loop-a");

        await scheduler.TickAsync();
        await FakeStarted(runner, 1);
        var run = runner.Requests.Single().RunId!.Value;

        // the process stalls between the renewal and the release (nothing renews here: the renewal is off), the lease is over when the tick looks
        scheduler.SuspendHeartbeats = true;
        h.Clock.Advance(TimeSpan.FromSeconds(100));
        var report = await scheduler.TickAsync();

        Assert.Equal(0, report.ExpiredLeases);
        Assert.Equal(0, report.Claimed);
        Assert.Single(runner.Requests);                                   // no second attempt, no second run record
        var inFlight = h.Queue.ListInFlight(Owner).Single();
        Assert.Equal(WorkItemStatus.Running, inFlight.Item.Status);
        Assert.Equal(run, inFlight.Item.RunId);
        Assert.Single(h.Runs.Read(new RunQuery(Service, "pkg/prod/job")));

        scheduler.SuspendHeartbeats = false;
        release.SetResult();
        await scheduler.WhenIdleAsync();
        Assert.Equal(RunOutcome.Succeeded, Assert.Single(h.Runs.Read(new RunQuery(Service, "pkg/prod/job"))).Run.Outcome);
        Assert.Empty(h.Queue.ListInFlight(Owner));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_loop_that_stops_renewing_loses_its_item_to_another_loop_and_its_late_finish_is_refused(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromHours(1)), new TaskLimits(Attempts: 3))), "1");
        var releaseA = new TaskCompletionSource();
        var runnerA = new FakeRunner(h.Database)
        {
            // when it finally ends it reports a retryable failure: if the finish were accepted the item would go back to the queue
            Behaviour = async (_, _) =>
            {
                await releaseA.Task;
                return new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Temporary, "late"));
            },
        };
        var a = h.NewScheduler(runnerA, worker: "loop-a");
        a.SuspendHeartbeats = true;                                       // a stalled runner: the task goes on, the lease is not renewed

        await a.TickAsync();
        await FakeStarted(runnerA, 1);
        var firstRun = runnerA.Requests.Single().RunId!.Value;

        h.Clock.Advance(TimeSpan.FromSeconds(100));                       // past the 90 second lease
        var runnerB = new FakeRunner(h.Database);
        var b = h.NewScheduler(runnerB, worker: "loop-b", database: h.OpenDatabase());
        var report = await Tick(b);

        Assert.Equal(1, report.ExpiredLeases);
        var taken = Assert.Single(runnerB.Requests);
        Assert.Equal(2, taken.Attempt);                                   // a new attempt, a run record of its own, the same chain
        Assert.Equal(runnerA.Requests.Single().Chain, taken.Chain);
        Assert.NotEqual(firstRun, taken.RunId);
        Assert.Equal(RunOutcome.Interrupted, h.Runs.Get(firstRun)!.Run.Outcome);
        Assert.Equal(RunOutcome.Succeeded, h.Runs.Get(taken.RunId!.Value)!.Run.Outcome);
        Assert.Empty(h.Queue.ListInFlight(Owner));

        releaseA.SetResult();
        await a.WhenIdleAsync();

        Assert.Empty(h.Queue.ListInFlight(Owner));                        // the late finish changed nothing: no retry was queued behind B's back
        Assert.Contains(a.Status().Problems, p => p.Message.Contains("lease", StringComparison.OrdinalIgnoreCase));
        Assert.Single(h.Runs.Read(new RunQuery(Service, "pkg/prod/job", Outcome: RunOutcome.Succeeded)));
    }

    // ---- by hand

    [Theory, MemberData(nameof(Engines))]
    public async Task Run_now_queues_one_item_with_its_inputs_and_runs_it_once(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job")), "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        var job = new TaskRef(Package, Channel, "job");

        var id = scheduler.RunNow(job, new Dictionary<string, string> { ["region"] = "eu" }, "bob");
        Assert.NotNull(id);
        Assert.Null(scheduler.RunNow(job));                               // already queued
        Assert.Equal(1, scheduler.Status().Tasks.Single().Queued);
        await Tick(scheduler);
        await Tick(scheduler);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(id, request.RunId);
        Assert.Equal("eu", request.Inputs["region"]);
        Assert.Single(h.Runs.Read(new RunQuery(Service, "pkg/prod/job")));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_not_yet_started_follows_the_active_version(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var manifest = Manifest(Package, Def("job"));
        h.Activate(manifest, "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        scheduler.RunNow(new TaskRef(Package, Channel, "job"));

        h.Activate(manifest, "2");                                        // activated before the item was claimed
        await Tick(scheduler);

        Assert.Equal("2", Assert.Single(runner.Requests).PackageVersion);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_whose_version_is_missing_fails_the_run_with_a_record_and_does_not_retry(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var manifest = Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromHours(1)), new TaskLimits(Attempts: 3)));
        h.Projections.Replace(Package, Channel, ProjectionDocuments.RowsOf(manifest, "9"));    // the projection names a version the source does not have
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        await Tick(scheduler);

        Assert.Empty(runner.Requests);
        var run = h.Runs.LastRun("pkg/prod/job")!;
        Assert.Equal(RunOutcome.Failed, run.Run.Outcome);
        Assert.Contains("not available", run.Run.Failure!.Message);
        Assert.Empty(h.Queue.ListInFlight(Owner));
        Assert.Contains(scheduler.Status().Problems, p => p.Message.Contains("not available", StringComparison.Ordinal));
    }

    // ---- limits

    [Theory, MemberData(nameof(Engines))]
    public async Task A_package_limit_of_two_runs_two_at_once_and_holds_the_third(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var limits = new TaskLimits(Concurrency: 2);
        h.Activate(Manifest(Package, Def("a", limits: limits), Def("b", limits: limits), Def("c", limits: limits)), "1");
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(h.Database) { Behaviour = async (_, _) => { await release.Task; return new TaskEnd(TaskEndState.Succeeded); } };
        var scheduler = h.NewScheduler(runner, maxRuns: 10);
        foreach (var name in new[] { "a", "b", "c" }) scheduler.RunNow(new TaskRef(Package, Channel, name));

        var report = await scheduler.TickAsync();
        await FakeStarted(runner, 2);
        await scheduler.TickAsync();

        Assert.Equal(2, report.Claimed);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(1, h.Queue.ListInFlight(Owner).Count(w => w.Item.Status == WorkItemStatus.Pending));
        release.SetResult();
        await scheduler.WhenIdleAsync();
        await Tick(scheduler);
        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal(2, runner.MaxActive);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_loop_runs_no_more_tasks_at_once_than_its_setting(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("a"), Def("b"), Def("c")), "1");
        var release = new TaskCompletionSource();
        var runner = new FakeRunner(h.Database) { Behaviour = async (_, _) => { await release.Task; return new TaskEnd(TaskEndState.Succeeded); } };
        var scheduler = h.NewScheduler(runner, maxRuns: 1);
        foreach (var name in new[] { "a", "b", "c" }) scheduler.RunNow(new TaskRef(Package, Channel, name));

        await scheduler.TickAsync();
        await FakeStarted(runner, 1);
        await scheduler.TickAsync();

        Assert.Equal(1, scheduler.RunningCount);
        release.SetResult();
        for (var i = 0; i < 3; i++) await Tick(scheduler);
        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal(1, runner.MaxActive);
    }

    // ---- stopping

    [Theory, MemberData(nameof(Engines))]
    public async Task A_graceful_stop_cancels_a_running_task_and_gives_its_item_back(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromHours(1)))), "1");
        var runner = new FakeRunner(h.Database)
        {
            Behaviour = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new TaskEnd(TaskEndState.Succeeded);
            },
        };
        var scheduler = h.NewScheduler(runner, tick: TimeSpan.FromMilliseconds(30));
        using var stop = new CancellationTokenSource();
        var loop = scheduler.RunAsync(stop.Token);
        await FakeStarted(runner, 1);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, runner.Cancelled);
        Assert.Equal(0, scheduler.RunningCount);
        var item = Assert.Single(h.Queue.ListInFlight(Owner));
        Assert.Equal(WorkItemStatus.Pending, item.Item.Status);           // unfinished work is released, not cancelled
        Assert.Null(item.ClaimedBy);
        Assert.Equal(RunOutcome.Stopped, h.Runs.LastRun("pkg/prod/job")!.Run.Outcome);
    }

    /// <summary>Open work (28a, scheduler): a start that arrives while the previous run's lock is still held (between its item being finished and the lock being released) waits, and it must come back as the run the caller was given.</summary>
    [Theory, MemberData(nameof(Engines))]
    public async Task A_start_that_finds_the_run_lock_held_waits_and_then_runs_under_the_run_id_it_was_given(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromDays(30)))), "1");
        h.SeedHandled("job");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        var job = new TaskRef(Package, Channel, "job");
        var locks = new ModuleRunLockStore(h.Database, Service);

        // the previous run's item is finished, its lock not yet released
        Assert.True(locks.TryAcquire(Owner, RunnerScheduler.Kind, "job", Guid.NewGuid()));
        var given = scheduler.RunNow(job, requestedBy: "alice");
        Assert.NotNull(given);
        await Tick(scheduler);
        Assert.Empty(runner.Requests);

        locks.Release(Owner, RunnerScheduler.Kind, "job");
        h.Clock.Advance(TimeSpan.FromSeconds(6));                      // past the tick the item waits for
        await Tick(scheduler);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(given, request.RunId);                            // the run id RunNow returned is the run that happens: a caller that watches it finds it
        Assert.NotNull(h.Runs.Get(given!.Value));
    }
}
