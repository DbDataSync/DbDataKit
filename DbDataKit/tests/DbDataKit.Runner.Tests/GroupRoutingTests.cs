using DbDataKit.Monitoring;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>Plan 28a R11 and R12: instances with a lease, a runner that lost its item is told, labels and routing, and the activation check.</summary>
public sealed class GroupRoutingTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static string Owner => $"{Package}/{Channel}";

    private static Task Started(FakeRunner runner, int count) => Until(() => runner.Requests.Count >= count, $"{count} runs to have started");

    // ---- R11: instances and their lease

    [Theory, MemberData(nameof(Engines))]
    public void An_instance_is_live_while_its_heartbeat_is_inside_the_lease_and_a_lapsed_row_is_removed_with_its_labels_handed_on(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var instances = new RunnerInstances(h.Database);
        var lease = TimeSpan.FromSeconds(90);
        var old = instances.Register("dbdatarunner", "host-1", "1.0");
        instances.SetLabel(old, "gpu", InstanceLabel.Admin);
        instances.ReplaceReportedLabels(old, ["network: outside"]);
        var other = instances.Register("dbdatarunner", "host-2", "1.0");

        h.Clock.Advance(TimeSpan.FromSeconds(60));
        instances.Heartbeat(other);
        Assert.Equal(2, instances.ListLive(lease).Count);

        h.Clock.Advance(TimeSpan.FromSeconds(40));                       // 100 s since `old` beat, 40 s since `other` did
        instances.Heartbeat(other);
        Assert.Equal([other], instances.ListLive(lease).Select(i => i.Id));

        var fresh = instances.Register("dbdatarunner", "host-1", "1.0");
        var inherited = instances.RemoveLapsed("dbdatarunner", "host-1", fresh, lease);

        Assert.Equal(["gpu"], inherited);                                  // the administrator's label, not the reported one
        Assert.Null(instances.Get(old));
        Assert.Empty(instances.Labels(old));
        Assert.NotNull(instances.Get(other));                              // another host's row is not touched
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_runner_that_comes_back_after_its_item_was_taken_is_told_cancels_its_task_and_records_nothing(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("job", Schedule.Interval(TimeSpan.FromHours(1)))), "1");
        var runnerA = new FakeRunner(h.Database) { Behaviour = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new TaskEnd(TaskEndState.Succeeded); } };
        var a = h.NewScheduler(runnerA, worker: "loop-a", heartbeat: TimeSpan.FromMilliseconds(50));
        a.SuspendHeartbeats = true;

        await a.TickAsync();
        await Started(runnerA, 1);

        h.Clock.Advance(TimeSpan.FromSeconds(100));
        var runnerB = new FakeRunner(h.Database);
        var b = h.NewScheduler(runnerB, worker: "loop-b", database: h.OpenDatabase());
        await Tick(b);
        Assert.Single(runnerB.Requests);

        a.SuspendHeartbeats = false;                                       // the stalled runner wakes up: its next renewal is refused
        await a.WhenIdleAsync();

        Assert.Equal(1, runnerA.Cancelled);                                // the local task was cancelled
        var told = Assert.Single(a.Status().Problems, p => p.Message.Contains("lost the lease", StringComparison.Ordinal));
        Assert.Contains("another runner took the item", told.Message);
        Assert.Empty(h.Queue.ListInFlight(Owner));
        Assert.Single(h.Runs.Read(new RunQuery(Service, "pkg/prod/job", Outcome: RunOutcome.Succeeded)));   // only B's run succeeded
        Assert.Equal(RunOutcome.Interrupted, h.Runs.Get(runnerA.Requests.Single().RunId!.Value)!.Run.Outcome);
    }

    // ---- R12: labels and routing

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_requiring_a_label_no_runner_has_stays_pending_with_the_reason_and_starts_when_a_runner_gets_the_label(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("render", Schedule.Interval(TimeSpan.FromHours(1)), requires: ["network:outside", "gpu"])), "1");
        var instances = new RunnerInstances(h.Database);
        var me = instances.Register("dbdatarunner", "host-1", "1.0");
        instances.ReplaceReportedLabels(me, RunnerLabels.FromCapabilities(new Dictionary<string, string> { ["network"] = "outside", ["os"] = "linux", ["memory_limit"] = "watched", ["limit_mechanism"] = "watchdog" }));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, instance: () => me);

        await Tick(scheduler);
        await Tick(scheduler);

        Assert.Empty(runner.Requests);                                      // queued, not claimed
        var waiting = Assert.Single(h.Queue.ListInFlight(Owner));
        Assert.Equal(WorkItemStatus.Pending, waiting.Item.Status);
        var status = Assert.Single(scheduler.Status().Tasks);
        Assert.Equal(1, status.Queued);
        Assert.Contains("'gpu'", status.WaitingReason);
        Assert.DoesNotContain("network:outside", status.WaitingReason);   // that one the runner reports
        Assert.Equal(["network:outside", "gpu"], status.RequiredLabels);
        Assert.Equal(["memory_limit:watched", "network:outside", "os:linux"], instances.Labels(me).Select(l => l.Label));

        instances.SetLabel(me, "gpu", InstanceLabel.Admin);
        await Tick(scheduler);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("render", request.Task.Task);
        Assert.Empty(h.Queue.ListInFlight(Owner));
        Assert.Null(Assert.Single(scheduler.Status().Tasks).WaitingReason);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Of_two_runners_only_the_one_with_every_label_claims_and_a_task_without_requirements_goes_to_either(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Activate(Manifest(Package, Def("render", Schedule.Interval(TimeSpan.FromHours(1)), requires: ["gpu"]), Def("plain", Schedule.Interval(TimeSpan.FromHours(1)))), "1");
        var instances = new RunnerInstances(h.Database);
        var plain = instances.Register("dbdatarunner", "host-1", "1.0");
        var gpu = instances.Register("dbdatarunner", "host-2", "1.0");
        instances.SetLabel(gpu, "gpu", InstanceLabel.Admin);
        var runnerA = new FakeRunner(h.Database);
        var runnerB = new FakeRunner(h.Database);
        var a = h.NewScheduler(runnerA, worker: "a", instance: () => plain);
        var b = h.NewScheduler(runnerB, worker: "b", database: h.OpenDatabase(), instance: () => gpu);

        await a.TickAsync();                                                // queues both, claims "plain" only (the item "render" is not its)
        await a.WhenIdleAsync();
        Assert.Equal(["plain"], runnerA.Requests.Select(r => r.Task.Task));
        Assert.Contains("render", h.Queue.ListInFlight(Owner).Select(w => w.Item.Subject));
        Assert.Null(Assert.Single(a.Status().Tasks, t => t.Task.Task == "render").WaitingReason);   // a live runner has the label

        await Tick(b);
        Assert.Equal(["render"], runnerB.Requests.Select(r => r.Task.Task));
        Assert.DoesNotContain(runnerA.Requests, r => r.Task.Task == "render");
    }

    // ---- the activation guard

    private static LiveInstance Live(params string[] labels) =>
        new(new RunnerInstance(Guid.NewGuid(), "dbdatarunner", "h", "1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, RunnerInstance.Running), labels.ToHashSet(), new Dictionary<string, string>());

    [Fact]
    public void CanActivate_names_the_missing_label_and_nothing_when_some_runner_has_them_all()
    {
        var live = new[] { Live("os:linux", "network:outside"), Live("os:windows") };
        Assert.Empty(RoutingCheck.CanActivate([], live));
        Assert.Empty(RoutingCheck.CanActivate(["network: outside"], live));                      // a space after the colon is the same label
        Assert.Equal(["gpu"], RoutingCheck.CanActivate(["network:outside", "gpu"], live));
        Assert.Equal(["gpu"], RoutingCheck.CanActivate(["gpu"], live));
        Assert.Equal(["gpu", "tpu"], RoutingCheck.CanActivate(["gpu", "tpu"], live));
        Assert.Equal(["gpu"], RoutingCheck.CanActivate(["gpu"], []));                            // nothing live: everything is missing
        Assert.Single(RoutingCheck.CanActivate(["network:outside", "gpu"], [Live("network:outside"), Live("gpu")]));   // no single runner has both: what the closest one lacks
    }

    [Fact]
    public void The_refusal_and_the_reason_name_the_label_and_labels_are_normalized()
    {
        var message = RoutingCheck.ActivationRefusal("orders", "7", ["gpu"]);
        Assert.Contains("'gpu'", message);
        Assert.Contains("orders", message);
        Assert.Contains("'gpu'", RoutingCheck.Reason(["gpu"], 2));
        Assert.Equal("os:linux", RunnerLabels.Normalize(" os : linux "));
        Assert.Equal(["network:outside", "os:linux", "memory_limit:enforced"],
            RunnerLabels.FromCapabilities(new Dictionary<string, string> { ["os"] = "linux", ["memory_limit"] = "enforced", ["network"] = "outside", ["limit_mechanism"] = "cgroup" }));
    }
}
