using DbDataKit.Monitoring;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner.Execution.Tests;

/// <summary>Plan 28a R08 and R09 with the real executor: the scheduler loop materializes the sample package through the artifact cache and runs its tasks in real processes, on each engine.</summary>
public sealed class SchedulerExecutionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatakit-sched-exec-").FullName;

    public static TheoryData<string> Engines => ExecutionEngines.Engines;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }

    private static PackageTask Def(string name, Schedule? schedule = null, TaskLimits? limits = null) =>
        new(name, new TaskEntry("T"), schedule ?? new Schedule(), [], [], [], ProgressDeclaration.Empty, limits ?? TaskLimits.None, [], []);

    private async Task<RunnerScheduler> SchedulerFor(ExecutorHarness h, params PackageTask[] tasks)
    {
        var manifest = new PackageManifest(ExecutorHarness.Package, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "DbDataKit.Runner.TestPackage.dll"), new BuiltAgainst(null, null, null), tasks);
        var (source, files) = await PackageFixture.SampleFiles();
        var versions = new InMemoryVersionSource().Add(ExecutorHarness.Package, "1.0.0", manifest, files);
        new TaskProjections(h.Database).Replace(ExecutorHarness.Package, ExecutorHarness.Channel, ProjectionDocuments.RowsOf(manifest, "1.0.0"));
        return new RunnerScheduler(h.Database, h.Executor, new ArtifactCache(Path.Combine(_root, "cache"), source), versions, new RunnerSchedulerOptions
        {
            Channels = [ExecutorHarness.Channel],
            Tick = TimeSpan.FromMilliseconds(50),
            CancellationGrace = TimeSpan.FromSeconds(10),
        });
    }

    private static async Task Settle(RunnerScheduler scheduler)
    {
        await scheduler.TickAsync();
        await scheduler.WhenIdleAsync();
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_scheduled_task_runs_in_a_real_process_from_the_materialized_version(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var scheduler = await SchedulerFor(h, Def("ok", Schedule.Interval(TimeSpan.FromHours(1))));

        await Settle(scheduler);
        await Settle(scheduler);

        var run = Assert.Single(h.Runs.Read(new RunQuery("runner", "sample/prod/ok")));
        Assert.Equal(RunOutcome.Succeeded, run.Run.Outcome);
        Assert.Equal(("sample", "1.0.0", "prod", 1), (run.Package, run.PackageVersion, run.Channel, run.Attempt));
        Assert.Contains(h.Log(Guid.Parse(run.Run.RunId)), l => l.Message == "hello from ok");
        Assert.Equal(RunOutcome.Succeeded, scheduler.Status().Tasks.Single().LastRun!.Run.Outcome);
        Assert.Equal(0, new ModuleJobStateStore(h.Database, "runner").Get("sample/prod", "ok")!.LastExit);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_permanent_failure_is_one_real_run_even_when_the_manifest_allows_attempts(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var scheduler = await SchedulerFor(h, Def("fails", limits: new TaskLimits(Attempts: 3, Backoff: TimeSpan.FromMilliseconds(1), MaxBackoff: TimeSpan.FromMilliseconds(1))));
        scheduler.RunNow(new TaskRef("sample", "prod", "fails"));

        await Settle(scheduler);
        await Task.Delay(100);
        await Settle(scheduler);

        var run = Assert.Single(h.Runs.Read(new RunQuery("runner", "sample/prod/fails")));
        Assert.Equal(RunOutcome.Failed, run.Run.Outcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_process_that_dies_is_retried_as_runs_of_one_item_on_one_chain_and_one_version(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var scheduler = await SchedulerFor(h, Def("exits-three", limits: new TaskLimits(Attempts: 2, Backoff: TimeSpan.FromMilliseconds(1), MaxBackoff: TimeSpan.FromMilliseconds(1))));
        scheduler.RunNow(new TaskRef("sample", "prod", "exits-three"));

        await Settle(scheduler);
        await Task.Delay(200);                                              // past the back-off (real time here)
        await Settle(scheduler);
        await Task.Delay(200);
        await Settle(scheduler);

        var runs = h.Runs.Read(new RunQuery("runner", "sample/prod/exits-three")).OrderBy(r => r.Attempt).ToList();
        Assert.Equal([1, 2], runs.Select(r => r.Attempt));                  // attempts count the first run: two in all
        Assert.All(runs, r => Assert.Equal(RunOutcome.Failed, r.Run.Outcome));
        Assert.All(runs, r => Assert.Equal("1.0.0", r.PackageVersion));
        Assert.Equal(2, runs.Select(r => r.Id).Distinct().Count());
        Assert.Empty(new ModuleWorkQueueStore(h.Database, "runner").ListInFlight("sample/prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_graceful_stop_cancels_a_real_task_and_gives_its_item_back(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var scheduler = await SchedulerFor(h, Def("waits", Schedule.Interval(TimeSpan.FromHours(1))));
        using var stop = new CancellationTokenSource();
        var loop = scheduler.RunAsync(stop.Token);
        var runId = await WaitForRun(h, "sample/prod/waits");
        await h.WaitForLog(runId, "ready");

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(RunOutcome.Stopped, h.Runs.Get(runId)!.Run.Outcome);
        var item = Assert.Single(new ModuleWorkQueueStore(h.Database, "runner").ListInFlight("sample/prod"));
        Assert.Equal(WorkItemStatus.Pending, item.Item.Status);
    }

    private static async Task<Guid> WaitForRun(ExecutorHarness h, string job)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            if (h.Runs.Read(new RunQuery("runner", job)).FirstOrDefault() is { } run) return run.Id;
            await Task.Delay(50);
        }

        throw new TimeoutException($"No run of {job} began.");
    }
}
