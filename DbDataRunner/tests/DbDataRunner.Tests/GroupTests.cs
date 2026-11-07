using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Limits;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>Plan 28a R11 and R12 with real servers and real task processes: runners of a group on one database, a runner that goes silent and comes back, labels and routing, and a second server on SQLite.</summary>
public sealed class GroupTests
{
    public static TheoryData<string> ServerEngines => [StateEngineIds.MsSql, StateEngineIds.Postgres];

    public static TheoryData<string> Engines => StateEngines.Engines;

    private static string Text(JsonNode? node, string name) => (string)node![name]!;

    private static JsonNode? Task(JsonNode? status, string name) => status!["tasks"]!.AsArray().FirstOrDefault(t => (string)t!["task"]! == name);

    private static async Task<JsonNode> StatusWhenAsync(RunnerHarness h, Func<JsonNode, bool> condition, string what, int seconds = 60)
    {
        try { return await h.WaitAsync(async () => (await h.GetAsync("/api/v1/status")).Body is { } body && condition(body) ? body : null, what, seconds); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + "\nThe status said: " + (await h.GetAsync("/api/v1/status")).Body?.ToJsonString());
        }
    }

    [Theory, MemberData(nameof(ServerEngines))]
    public async Task Two_runners_on_one_database_are_a_group_a_silent_one_loses_its_item_and_does_not_finish_it_when_it_returns(string engine)
    {
        await using var one = new RunnerHarness(engine);
        await using var two = new RunnerHarness(engine, sharedState: one.Settings.State);
        one.Install(SamplePackage.Task("waits"));
        await one.StartAsync();

        // the first runner takes the item and runs it
        var (accepted, started) = await one.PostAsync("/api/v1/tasks/sample/dev/waits/run");
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        var firstRun = Guid.Parse(Text(started, "run_id"));
        await StatusWhenAsync(one, s => Task(s, "waits")!["running"]!.GetValue<int>() == 1, "the first runner to run the task");
        var firstWorker = one.Engine.WorkerId;

        await two.StartAsync();
        var group = (await two.GetAsync("/api/v1/instances")).Body!;
        Assert.Equal("main", Text(group, "group"));
        var live = group["instances"]!.AsArray();
        Assert.Equal(2, live.Count);
        var mine = live.Single(i => Text(i, "id") == one.Engine.InstanceId!.Value.ToString())!;
        Assert.Equal(1, mine["running"]!.GetValue<int>());
        Assert.Equal(0, live.Single(i => Text(i, "id") == two.Engine.InstanceId!.Value.ToString())!["running"]!.GetValue<int>());
        Assert.NotEmpty(Text(mine, "host"));
        Assert.NotEmpty(Text(mine, "version"));
        Assert.Contains(mine["labels"]!.AsArray(), l => Text(l, "label") == "os:" + Text(mine["capabilities"], "os") && Text(l, "source") == "reported");

        // the first runner goes silent (a stopped process, a cut network): its task goes on, the lease is not renewed
        one.Engine.SuspendHeartbeats = true;
        var runs = new RunStore(two.State, RunnerState.EngineService);
        await two.WaitAsync(() => System.Threading.Tasks.Task.FromResult(runs.Read(new RunQuery(RunnerState.EngineService, "sample/dev/waits", Package: "sample", Channel: "dev")).Any(r => r.Attempt == 2 && r.Run.Outcome == RunOutcome.Running) ? "taken" : null),
            "the second runner to take the item once the lease ran out", 60);
        var taken = runs.Read(new RunQuery(RunnerState.EngineService, "sample/dev/waits", Package: "sample", Channel: "dev")).Single(r => r.Attempt == 2);
        Assert.Equal(RunOutcome.Interrupted, runs.Get(firstRun)!.Run.Outcome);       // the dead run is marked, the same chain goes on
        await two.WaitAsync(async () => (await two.GetAsync("/api/v1/instances")).Body!["instances"]!.AsArray().Count == 1 ? "alone" : null, "the silent runner to lapse from the group view", 20);   // it is no longer live

        // all=true still lists it, marked as not live: what the console shows as a runner that is gone
        var everyone = (await two.GetAsync("/api/v1/instances?all=true")).Body!["instances"]!.AsArray();
        Assert.Equal(2, everyone.Count);
        Assert.False(everyone.Single(i => Text(i, "id") == one.Engine.InstanceId!.Value.ToString())!["live"]!.GetValue<bool>());
        Assert.True(everyone.Single(i => Text(i, "id") == two.Engine.InstanceId!.Value.ToString())!["live"]!.GetValue<bool>());

        // the first runner comes back: it is told, cancels its local task, and records nothing
        one.Engine.SuspendHeartbeats = false;
        await StatusWhenAsync(one, s => s["problems"]!.AsArray().Any(p => Text(p, "message").Contains("lost the lease", StringComparison.Ordinal)), "the revived runner to find out");
        await StatusWhenAsync(one, s => s["running_here"]!.GetValue<int>() == 0, "the revived runner's task to be cancelled");
        var problem = (await one.GetAsync("/api/v1/status")).Body!["problems"]!.AsArray().Single(p => Text(p, "message").Contains("lost the lease", StringComparison.Ordinal));
        Assert.Contains("another runner took the item", Text(problem, "message"));
        Assert.Equal(RunOutcome.Interrupted, runs.Get(firstRun)!.Run.Outcome);       // its late finish did not overwrite the record
        Assert.Equal(RunOutcome.Running, runs.Get(taken.Id)!.Run.Outcome);           // the second runner still has the item
        Assert.Equal(1, new ModuleWorkQueueStore(two.State, RunnerState.EngineService).HeldCounts().Values.Sum());
        Assert.DoesNotContain(firstWorker, new ModuleWorkQueueStore(two.State, RunnerState.EngineService).HeldCounts().Keys);

        // and it is live again, one row per process
        await two.WaitAsync(async () => (await two.GetAsync("/api/v1/instances")).Body!["instances"]!.AsArray().Count == 2 ? "both" : null, "both runners to be live");
        Assert.Equal(2, new RunnerInstances(two.State).ListLive(two.Settings.InstanceLease).Count);
    }

    [Theory, MemberData(nameof(ServerEngines))]
    public async Task A_task_that_requires_a_label_no_runner_has_stays_pending_with_the_reason_and_starts_when_a_runner_gets_the_label(string engine)
    {
        await using var one = new RunnerHarness(engine);
        await using var two = new RunnerHarness(engine, sharedState: one.Settings.State);
        one.Install(SamplePackage.Task("waits") with { Requires = ["gpu", $"os:{LimitCapabilities.Detect()[LimitCapabilities.Os]}"] });
        await one.StartAsync();
        await two.StartAsync();

        // the capabilities are labels, read-only
        var status = (await one.GetAsync("/api/v1/status")).Body!;
        var labels = status["instances"]!.AsArray().SelectMany(i => i!["labels"]!.AsArray()).Where(l => Text(l, "source") == "reported").Select(l => Text(l, "label")).Distinct().ToList();
        Assert.Contains(labels, l => l.StartsWith("os:", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.StartsWith("memory_limit:", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.StartsWith("network:", StringComparison.Ordinal));

        var (accepted, _) = await one.PostAsync("/api/v1/tasks/sample/dev/waits/run");
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        var waiting = await StatusWhenAsync(one, s => Task(s, "waits")!["waiting_reason"] is not null, "the reason");
        Assert.Contains("'gpu'", Text(Task(waiting, "waits"), "waiting_reason"));
        await System.Threading.Tasks.Task.Delay(1500);
        var still = Task((await one.GetAsync("/api/v1/status")).Body, "waits")!;
        Assert.Equal((0, 1), (still["running"]!.GetValue<int>(), still["queued"]!.GetValue<int>()));       // pending: neither runner took it

        // an administrator gives the second runner the label: it starts there
        var (exit, output, error) = await Label(two, "add", "gpu", two.Engine.InstanceId!.Value);
        Assert.True(exit == 0, error);
        Assert.Contains("Added 'gpu'", output);
        var running = await StatusWhenAsync(two, s => Task(s, "waits")!["running"]!.GetValue<int>() == 1, "the task to start once a runner has the label");
        Assert.Null(Task(running, "waits")!["waiting_reason"]);
        Assert.Equal(1, new ModuleWorkQueueStore(two.State, RunnerState.EngineService).HeldCounts()[two.Engine.WorkerId]);
        Assert.Contains(new AuditLog(two.State, RunnerState.ServiceId).Read(new AuditQuery(VerbPrefix: "runner.label.")), e => e.Entry.Verb == RunnerLabelVerbs.AddVerb && e.Entry.TargetId == two.Engine.InstanceId!.Value.ToString());
    }

    private static async Task<(int Exit, string Out, string Error)> Label(RunnerHarness h, string verb, string label, Guid instance)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var settings = h.Settings.State;
        Func<string, string?> environment = name => name switch
        {
            RunnerSettings.StateEngineVariable => settings.Engine,
            RunnerSettings.StateConnectionVariable => settings.ConnectionString,
            _ => null,
        };
        var exit = await RunnerCli.Router(environment).RunAsync(["label", verb, label, "--instance", instance.ToString(), "--repo", h.Directory], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task SQLite_refuses_a_second_server_naming_the_first_and_a_stopped_one_leaves_nothing_behind()
    {
        await using var first = new RunnerHarness(StateEngineIds.Sqlite);
        await first.StartAsync();
        var firstId = first.Engine.InstanceId!.Value;

        await using var second = new RunnerHarness(StateEngineIds.Sqlite, sharedState: first.Settings.State, directory: first.Directory);
        var refused = await Assert.ThrowsAsync<SecondInstanceException>(second.StartAsync);
        Assert.Contains(firstId.ToString(), refused.Message);
        Assert.Contains("SQLite", refused.Message);
        Assert.Contains(Environment.MachineName, refused.Message);
        Assert.Single(new RunnerInstances(first.State).ListLive(first.Settings.InstanceLease));             // the refused one wrote no row

        await first.StopAsync();
        await using var third = new RunnerHarness(StateEngineIds.Sqlite, sharedState: first.Settings.State, directory: first.Directory);
        await third.StartAsync();
        var instances = new RunnerInstances(third.State);
        Assert.Equal([third.Engine.InstanceId!.Value], instances.ListLive(third.Settings.InstanceLease).Select(i => i.Id));
        Assert.Null(instances.Get(firstId));                                                                 // the earlier process's row was cleared at start
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_restarted_runner_keeps_its_administrator_labels_and_a_crashed_instances_row_is_cleared(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        var instances = new RunnerInstances(h.State);
        var crashed = h.Engine.InstanceId!.Value;
        instances.SetLabel(crashed, "gpu", InstanceLabel.Admin);
        var (exit, _, error) = await Label(h, "remove", "os:linux", crashed);
        Assert.NotEqual(0, exit);                                                                            // a reported label is read-only
        Assert.Contains("reported by the runner", error);
        await h.StopAsync();                                                                                 // marks it gone

        await h.StartAsync();
        var fresh = h.Engine.InstanceId!.Value;
        Assert.NotEqual(crashed, fresh);
        Assert.Null(instances.Get(crashed));
        Assert.Contains(instances.Labels(fresh), l => l is { Label: "gpu", Source: InstanceLabel.Admin });
    }
}
