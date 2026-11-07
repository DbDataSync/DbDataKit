using DbDataKit.Monitoring;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State.Tests;
using DbDataKit.State;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>Plan 28a R08: the retry policy and planner (pure), and retries as runs of one work item through the scheduler loop on each engine.</summary>
public sealed class RetryTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static readonly TimeProvider Clock = new TestClock(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    private static TaskEnd Failed(TaskFailureKind kind, string? code = null) => new(TaskEndState.Failed, Failure: new TaskFailure(kind, "boom", Code: code));

    // ---- the policy

    [Fact]
    public void The_policy_comes_from_the_limits_with_defaults_for_what_they_leave_out()
    {
        var policy = RetryPolicy.From(new TaskLimits(Attempts: 4, Backoff: TimeSpan.FromSeconds(10)));
        Assert.Equal((4, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10)), (policy.Attempts, policy.Backoff, policy.MaxBackoff));
        Assert.Equal(1, RetryPolicy.From(TaskLimits.None).Attempts);                                  // a manifest that says nothing gets one run
        Assert.Equal(TimeSpan.FromMinutes(5), RetryPolicy.From(new TaskLimits(Backoff: TimeSpan.FromMinutes(5), MaxBackoff: TimeSpan.FromMinutes(1))).MaxBackoff);   // the ceiling is never below the base
    }

    [Fact]
    public void The_back_off_doubles_up_to_the_ceiling_and_the_jitter_moves_it_but_never_past_it()
    {
        var policy = new RetryPolicy(10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), jitter: 0.2);
        Assert.Equal([30, 60, 120, 240, 300, 300], new[] { 1, 2, 3, 4, 5, 6 }.Select(n => (int)policy.DelayAfter(n).TotalSeconds));
        Assert.Equal(24, policy.DelayAfter(1, 0).TotalSeconds, 6);          // 30 s minus a fifth
        Assert.Equal(36, policy.DelayAfter(1, 1).TotalSeconds, 6);          // 30 s plus a fifth
        Assert.Equal(300, policy.DelayAfter(5, 1).TotalSeconds, 6);         // jitter above the ceiling is cut at it
        Assert.Equal(300, policy.DelayAfter(500).TotalSeconds, 6);          // no overflow for a huge attempt number
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(2, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)));
    }

    // ---- the planner

    [Theory]
    [InlineData(TaskFailureKind.Temporary, true)]
    [InlineData(TaskFailureKind.TimedOut, true)]
    [InlineData(TaskFailureKind.ProcessExited, true)]
    [InlineData(TaskFailureKind.Permanent, false)]
    [InlineData(TaskFailureKind.ProtocolViolation, false)]
    [InlineData(TaskFailureKind.LimitExceeded, false)]
    public void Only_retryable_kinds_are_planned_again(TaskFailureKind kind, bool retried)
    {
        var plan = RetryPlanner.Plan(Failed(kind), 1, new RetryPolicy(3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)), Clock, () => 0.5);

        Assert.Equal(retried, plan is not null);
        if (plan is not null) Assert.Equal((Clock.GetUtcNow().AddSeconds(30), 2), (plan.At, plan.NextAttempt));
    }

    [Fact]
    public void A_cancel_a_success_and_completed_with_failures_are_never_retried_and_attempts_run_out()
    {
        var policy = new RetryPolicy(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.Null(RetryPlanner.Plan(TaskEndMapping.Cancelled(), 1, policy, Clock));
        Assert.Null(RetryPlanner.Plan(new TaskEnd(TaskEndState.Succeeded), 1, policy, Clock));
        Assert.Null(RetryPlanner.Plan(new TaskEnd(TaskEndState.CompletedWithFailures, FailedItems: 2), 1, policy, Clock));
        Assert.NotNull(RetryPlanner.Plan(Failed(TaskFailureKind.Temporary), 1, policy, Clock));
        Assert.Null(RetryPlanner.Plan(Failed(TaskFailureKind.Temporary), 2, policy, Clock));          // attempts count the first run
        Assert.Null(RetryPlanner.Plan(Failed(TaskFailureKind.Temporary), 1, RetryPolicy.None, Clock));
    }

    [Fact]
    public void A_connections_classification_in_the_failure_decides_when_it_contradicts_the_kind()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        Assert.NotNull(RetryPlanner.Plan(Failed(TaskFailureKind.Temporary, "Network"), 1, policy, Clock));
        Assert.NotNull(RetryPlanner.Plan(Failed(TaskFailureKind.Temporary, "Timeout"), 1, policy, Clock));
        Assert.Null(RetryPlanner.Plan(Failed(TaskFailureKind.Temporary, "Login"), 1, policy, Clock));  // the connection called it permanent
    }

    // ---- through the loop

    private static PackageManifest Flaky(int attempts) =>
        Manifest(Package, Def("flaky", limits: new TaskLimits(Attempts: attempts, Backoff: TimeSpan.FromSeconds(30), MaxBackoff: TimeSpan.FromMinutes(10))));

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_failing_twice_with_a_temporary_failure_then_succeeding_is_three_runs_of_one_item(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Flaky(3), "1");
        var runner = new FakeRunner(h.Database)
        {
            Behaviour = (request, _) => Task.FromResult(request.Attempt < 3 ? Failed(TaskFailureKind.Temporary, "Network") : new TaskEnd(TaskEndState.Succeeded)),
        };
        var scheduler = h.NewScheduler(runner);
        var started = h.Clock.GetUtcNow();
        scheduler.RunNow(new TaskRef(Package, Channel, "flaky"));

        await Tick(scheduler);                                              // attempt 1 fails: the item waits 30 s
        var waiting = Assert.Single(h.Queue.ListInFlight("pkg/prod"));
        Assert.Equal((WorkItemStatus.Pending, started + TimeSpan.FromSeconds(30)), (waiting.Item.Status, waiting.AvailableAt));
        Assert.Equal(started + TimeSpan.FromSeconds(30), scheduler.Status().Tasks.Single().NextAttemptAt);

        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await Tick(scheduler);
        Assert.Single(runner.Requests);                                     // not before its time

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Tick(scheduler);                                              // attempt 2 fails: the wait doubles
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(60), Assert.Single(h.Queue.ListInFlight("pkg/prod")).AvailableAt);

        h.Clock.Advance(TimeSpan.FromSeconds(59));
        await Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Tick(scheduler);                                              // attempt 3 succeeds

        var requests = runner.Requests.ToList();
        Assert.Equal([1, 2, 3], requests.Select(r => r.Attempt));
        Assert.Single(requests.Select(r => r.Chain).Distinct());            // one chain: steps and units are reused
        Assert.Equal(3, requests.Select(r => r.RunId).Distinct().Count());  // a run record of its own each
        var runs = h.Runs.Read(new RunQuery(Service, "pkg/prod/flaky")).OrderBy(r => r.Attempt).ToList();
        Assert.Equal([1, 2, 3], runs.Select(r => r.Attempt));
        Assert.Equal([RunOutcome.Failed, RunOutcome.Failed, RunOutcome.Succeeded], runs.Select(r => r.Run.Outcome));
        Assert.Empty(h.Queue.ListInFlight("pkg/prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_permanent_failure_is_one_run(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Flaky(3), "1");
        var runner = new FakeRunner(h.Database) { Behaviour = (_, _) => Task.FromResult(Failed(TaskFailureKind.Permanent)) };
        var scheduler = h.NewScheduler(runner);
        scheduler.RunNow(new TaskRef(Package, Channel, "flaky"));

        await Tick(scheduler);
        h.Clock.Advance(TimeSpan.FromHours(1));
        await Tick(scheduler);

        Assert.Single(runner.Requests);
        Assert.Empty(h.Queue.ListInFlight("pkg/prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cancelled_run_is_not_retried(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Flaky(3), "1");
        var runner = new FakeRunner(h.Database) { Behaviour = (_, _) => Task.FromResult(TaskEndMapping.Cancelled("an operator stopped it")) };
        var scheduler = h.NewScheduler(runner);
        scheduler.RunNow(new TaskRef(Package, Channel, "flaky"));

        await Tick(scheduler);
        h.Clock.Advance(TimeSpan.FromHours(1));
        await Tick(scheduler);

        Assert.Single(runner.Requests);
        Assert.Equal(RunOutcome.Stopped, h.Runs.LastRun("pkg/prod/flaky")!.Run.Outcome);
        Assert.Empty(h.Queue.ListInFlight("pkg/prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_retry_keeps_the_first_attempts_version_after_a_new_one_was_activated(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Flaky(3), "1");
        var runner = new FakeRunner(h.Database)
        {
            Behaviour = (request, _) => Task.FromResult(request.Attempt < 3 ? Failed(TaskFailureKind.Temporary) : new TaskEnd(TaskEndState.Succeeded)),
        };
        var scheduler = h.NewScheduler(runner);
        scheduler.RunNow(new TaskRef(Package, Channel, "flaky"));

        await Tick(scheduler);                                              // attempt 1 on version 1 fails
        h.Activate(Flaky(3), "2");                                          // version 2 becomes active meanwhile
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await Tick(scheduler);
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        await Tick(scheduler);

        Assert.Equal(["1", "1", "1"], runner.Requests.Select(r => r.PackageVersion));
        // the next run of the task is a new item and uses the new version
        scheduler.RunNow(new TaskRef(Package, Channel, "flaky"));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Tick(scheduler);
        Assert.Equal("2", runner.Requests.Last().PackageVersion);
        Assert.Equal(1, runner.Requests.Last().Attempt);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_retry_waits_for_its_back_off_also_after_the_loop_restarts(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Flaky(2), "1");
        var runner = new FakeRunner(h.Database) { Behaviour = (request, _) => Task.FromResult(request.Attempt < 2 ? Failed(TaskFailureKind.ProcessExited) : new TaskEnd(TaskEndState.Succeeded)) };
        h.NewScheduler(runner, worker: "first").RunNow(new TaskRef(Package, Channel, "flaky"));
        await Tick(h.NewScheduler(runner, worker: "first"));

        var other = h.NewScheduler(runner, worker: "second", database: h.OpenDatabase());
        await Tick(other);
        Assert.Single(runner.Requests);                                     // the back-off is in the database, not in a loop's memory
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await Tick(other);
        Assert.Equal([1, 2], runner.Requests.Select(r => r.Attempt));
    }
}
