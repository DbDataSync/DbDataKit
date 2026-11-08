using DbDataKit.Monitoring;
using DbDataKit.Quality;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Quality.Runner.Tests;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>
/// Plan 23a FQ08: the system package <c>quality-system</c> on the runner: its task evaluates a dataset's checks on request, on a schedule and after another task's run, writes the results into the <c>quality</c> module, and two runners of a group do not both
/// evaluate one request. State on every engine; the checks read a DuckDB file through its read-only connection.
/// </summary>
public sealed class QualityTaskTests : IDisposable
{
    private static readonly TaskRef QualityTask = new(QualitySystemTasks.Package, "prod", QualitySystemTasks.Task);
    private static readonly TaskRef Extract = new("pkg", "prod", "extract");

    private readonly QualityData _data = new();

    public static TheoryData<string> Engines => StateEngines.Engines;

    public void Dispose() => _data.Dispose();

    private static PackageManifest Package(QualityTaskSettings? settings = null) => PackageManifestReader.Load(QualitySystemTasks.Manifest(settings)).ThrowIfInvalid();

    private static PackageManifest Producer() => SchedulerHarness.Manifest("pkg", TriggerHarness.Def("extract"));

    private (RunnerScheduler Scheduler, QualityStore Store, FakeRunner Others) Loop(TriggerHarness h, IQualitySource source, StateDatabase? database = null, string? worker = null)
    {
        var db = database ?? h.Database;
        var store = new QualityStore(db);
        var others = new FakeRunner(db);
        var runner = new QualityTaskRunner(new QualitySystemTasks(source, db, store), others);
        return (h.NewScheduler(runner, db), store, others);
    }

    private static IReadOnlyList<StoredRun> QualityRuns(TriggerHarness h) => h.Runs.Read(new RunQuery("runner", QualityTask.ToString(), Limit: 100));

    [Fact]
    public void The_manifest_is_a_valid_package_with_one_task_its_input_and_its_triggers()
    {
        var manifest = Package(new QualityTaskSettings(Every: TimeSpan.FromHours(1), AfterRuns: ["pkg/extract", "other/load"], Dataset: "orders"));

        Assert.Equal(QualitySystemTasks.Package, manifest.Name);
        var task = Assert.Single(manifest.Tasks);
        Assert.Equal(QualitySystemTasks.Task, task.Name);
        Assert.Equal(TimeSpan.FromHours(1), task.Schedule.Every);
        Assert.Equal([("extract", "pkg"), ("load", "other")], task.Triggers.Select(t => (t.Task!, t.Package)));
        Assert.Throws<ArgumentException>(() => QualitySystemTasks.Manifest(new QualityTaskSettings(AfterRuns: ["load"])));
        Assert.All(task.Triggers, t => Assert.Equal(TriggerKind.AfterRun, t.Kind));
        var input = Assert.Single(task.Inputs);
        Assert.Equal(("dataset", false, "orders"), (input.Name, input.Required, input.Default));
        Assert.False(Package().Tasks[0].Schedule.HasSchedule);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_task_runs_after_another_tasks_run_and_its_results_are_in_the_table(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Producer());
        h.Activate(Package(new QualityTaskSettings(AfterRuns: ["pkg/extract"])));
        var (scheduler, store, others) = Loop(h, _data.Source([QualityData.NotNullCustomer(), QualityData.Unique()]));

        await h.Tick(scheduler);                       // the trigger starts following from here
        scheduler.RunNow(Extract);
        await h.Tick(scheduler);                       // the producer runs
        Assert.Empty(QualityRuns(h));
        await h.Tick(scheduler);                       // its end is seen: one evaluation is queued and runs

        var run = Assert.Single(QualityRuns(h));
        Assert.Equal(RunOutcome.CompletedWithFailures, run.Run.Outcome);          // a check failed: a finding, not a failure of the task
        Assert.Single(others.Requests);                                    // only the producer went to the executor
        var statuses = store.StatusesOf(QualityData.Orders).ToDictionary(s => s.CheckId);
        Assert.Equal((CheckOutcome.Fail, true), (statuses["customer_present"].Outcome, statuses["customer_present"].Blocking));
        Assert.Equal(CheckOutcome.Pass, statuses["id_unique"].Outcome);
        var result = Assert.Single(store.History(QualityData.Orders, "customer_present"));
        Assert.Equal(run.Id.ToString("D"), result.RunId);                          // the result is the run's
        Assert.Equal(["id=2", "id=4"], result.SampleKeys);
        await h.Tick(scheduler);
        Assert.Single(QualityRuns(h));                                             // each finished run triggers once
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_task_runs_on_a_schedule_and_by_hand(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Package(new QualityTaskSettings(Every: TimeSpan.FromHours(1))));
        var (scheduler, store, _) = Loop(h, _data.Source([QualityData.Unique()]));

        await h.Tick(scheduler);
        Assert.Single(QualityRuns(h));
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        await h.Tick(scheduler);
        Assert.Single(QualityRuns(h));                 // not due yet
        h.Clock.Advance(TimeSpan.FromMinutes(40));
        await h.Tick(scheduler);
        Assert.Equal(2, QualityRuns(h).Count);

        Assert.NotNull(scheduler.RunNow(QualityTask, requestedBy: "alice"));                // on request
        await h.Tick(scheduler);
        Assert.Equal(3, QualityRuns(h).Count);
        Assert.All(QualityRuns(h), r => Assert.Equal(RunOutcome.Succeeded, r.Run.Outcome));
        Assert.Equal(3, store.History(QualityData.Orders, "id_unique").Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_input_names_the_dataset_and_an_unknown_one_fails_the_run_without_a_result(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Package());
        var (scheduler, store, _) = Loop(h, _data.Source([QualityData.Unique()]));

        scheduler.RunNow(QualityTask, new Dictionary<string, string> { ["dataset"] = "nowhere" });
        await h.Tick(scheduler);
        var failed = Assert.Single(QualityRuns(h));
        Assert.Equal(RunOutcome.Failed, failed.Run.Outcome);
        Assert.Empty(store.StatusesOf(QualityData.Orders));

        scheduler.RunNow(QualityTask, new Dictionary<string, string> { ["dataset"] = "orders" });
        await h.Tick(scheduler);
        Assert.Equal(1, QualityRuns(h).Count(r => r.Run.Outcome == RunOutcome.Succeeded));
        Assert.Single(store.StatusesOf(QualityData.Orders));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_runners_of_a_group_do_not_both_evaluate_one_request(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Package());
        var source = _data.Source([QualityData.NotNullCustomer(), QualityData.Unique()]);
        var (a, storeA, _) = Loop(h, source);
        var other = h.OpenDatabase();
        var (b, storeB, _) = Loop(h, source, other);

        Assert.NotNull(a.RunNow(QualityTask));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(a.TickAsync(), b.TickAsync());
        await Task.WhenAll(a.WhenIdleAsync(), b.WhenIdleAsync());

        Assert.Single(QualityRuns(h));                 // one work item, claimed by one of them under its lease
        Assert.Equal(1, _data.OpenCount);              // and the data was read once
        Assert.Single(storeA.History(QualityData.Orders, "customer_present"));
        Assert.Single(storeB.History(QualityData.Orders, "id_unique"));
        Assert.Single(storeA.Blocks());
    }
}
