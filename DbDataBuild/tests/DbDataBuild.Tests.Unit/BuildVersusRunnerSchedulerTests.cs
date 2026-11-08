using DbDataBuild.Cli.Runner;
using DbDataBuild.Cli.Server;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Limits;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 28c E09: Build's scheduler and the runner's real scheduler side by side over the same plans and one fake clock (the runner's half is the library's own <see cref="RunnerScheduler"/>, on SQLite, with an executor that only records). The runner's
/// side of the schedule is made the way <c>project publish</c> makes it: the plan's schedule comes from <see cref="RefreshJobs"/> (the one rule both use), the manifest's task name from <see cref="RunnerNaming"/>.
/// </summary>
public sealed class BuildVersusRunnerSchedulerTests : IDisposable
{
    private const string Package = "sales";
    private const string Channel = "prod";

    private sealed class CountingRunner(StateDatabase database) : ITaskRunner
    {
        public List<(string Task, DateTimeOffset At)> Started { get; } = [];
        public required TimeProvider Clock { get; init; }

        public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default)
        {
            var runs = new RunStore(database, RunnerNames.EngineService);
            var runId = request.RunId ?? database.NewId();
            runs.Start(request.Task.ToString(), runId, package: request.Task.Package, packageVersion: request.PackageVersion, channel: request.Task.Channel, attempt: request.Attempt);
            Started.Add((request.Task.Task, Clock.GetUtcNow()));
            var end = new TaskEnd(TaskEndState.Succeeded);
            var finish = end.ToRunFinish();
            runs.Finish(runId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
            return Task.FromResult(new TaskRunResult(runId, end, true, 0, false, TimeSpan.Zero, 0, 0, 0, 0, LimitEnforcement.None, LimitEnforcement.None, "none", 0, false, []));
        }
    }

    private sealed class NoFiles : IArtifactSource
    {
        public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ArtifactFile>>([]);

        public Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no files");
    }

    /// <summary>The runner's scheduler asked the way the Build server asks a runner, without the network: the same calls the management API makes of the same control.</summary>
    private sealed class InProcess(RunnerScheduler scheduler) : IRunnerSchedule
    {
        public Task<ClientTaskSchedule> ScheduleOfAsync(string package, string task, CancellationToken ct)
        {
            var reference = new TaskRef(package, Channel, task);
            var (owner, record) = scheduler.OwnerOfTask(reference);
            var s = scheduler.ScheduleStateOf(reference);
            return Task.FromResult(new ClientTaskSchedule(reference.ToString(), owner, record?.ChangedBy, record?.ChangedAt, record?.Reason, s.HandledUpTo, s.PausedBy is not null, s.PausedBy, s.PausedAt, s.Running, s.Queued, s.Version));
        }

        public Task SetOwnerAsync(string package, string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct)
        {
            scheduler.SetOwner(new TaskRef(package, Channel, task), owner, "build server", reason, seed is null ? null : new ScheduleSeed(seed.HandledUpTo, seed.PausedBy, seed.PausedAt));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClientDecision>> DecisionsAsync(string package, string task, DateTimeOffset? since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ClientDecision>>([.. scheduler.DecisionsOf(new TaskRef(package, Channel, task), since).Select(d => new ClientDecision(d.Source, d.DecidedAt, d.Missed, d.Paused))]);
    }

    private readonly string _directory = Directory.CreateTempSubdirectory("ddb-own-runner-").FullName;
    private readonly List<OwnershipFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private sealed record Both(OwnershipFixture Build, RunnerScheduler Runner, CountingRunner Executor, StateDatabase RunnerState, InProcess Remote);

    /// <summary>Build's fixture with the plans, and a runner whose projection holds the same plans as tasks of the package <c>sales</c> on <c>prod</c>, both on the fixture's clock.</summary>
    private Both Start(params (string Name, string Cron, string Zone)[] plans)
    {
        var build = new OwnershipFixture(plans);
        _fixtures.Add(build);
        var state = StateOpener.Open(new StateSettings(), Path.Combine(_directory, "runner.db"), [SchedulingModule.Module, RunnerModule.Module, RunsModule.Module, DefinitionsModule.Module], StateScope.Standalone, time: build.Clock);
        var executor = new CountingRunner(state) { Clock = build.Clock };
        var versions = new InMemoryVersionSource();
        var tasks = RefreshJobs.Discover(build.Project).Select(j => new PackageTask(RunnerNaming.TaskOf(j.Connection, j.Plan), new TaskEntry("T"), j.Schedule, [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], [], false)).ToArray();
        var manifest = new PackageManifest(Package, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "x.dll"), new BuiltAgainst(null, null, null), tasks);
        versions.Add(Package, "1", manifest);
        new TaskProjections(state).Replace(Package, Channel, ProjectionDocuments.RowsOf(manifest, "1"));
        var scheduler = new RunnerScheduler(state, executor, new ArtifactCache(Path.Combine(_directory, "cache"), new NoFiles()), versions,
            new RunnerSchedulerOptions { Channels = [Channel], WorkerId = "loop", Jitter = () => 0.5 });
        return new Both(build, scheduler, executor, state, new InProcess(scheduler));
    }

    private static string TaskOf(string plan) => RunnerNaming.TaskOf(OwnershipFixture.Connection, plan);

    [Fact]
    public async Task Observe_records_the_same_due_times_as_Build_for_cron_in_two_time_zones_across_a_daylight_change_a_missed_span_and_a_first_seen_plan()
    {
        var both = Start(("berlin", "0 9 * * *", "Europe/Berlin"), ("newyork", "0 9 * * *", "America/New_York"), ("hourly", "15 * * * *", "UTC"));
        var build = both.Build;
        var clock = build.Clock;
        clock.To(OwnershipFixture.At(24, 12));

        // 12:00: first sight on both sides, so neither queues anything for any plan
        Assert.Empty(await build.TickAndRun());
        var ownership = build.Ownership(both.Remote);
        foreach (var plan in new[] { "berlin", "newyork", "hourly" }) await ownership.ObserveAsync("sales", OwnershipFixture.Connection, plan, build.Link, null);
        Assert.Empty((await both.Runner.TickAsync()).Enqueued);
        Assert.All(new[] { "berlin", "newyork", "hourly" }, plan => Assert.Empty(both.Runner.DecisionsOf(new TaskRef(Package, Channel, TaskOf(plan)))));

        // hourly across the end of daylight saving in Berlin (25 October) and in New York (1 November), with the days between passing unticked (the server was down: the daily plans run once for them)
        var queued = new Dictionary<string, List<DateTimeOffset>> { ["berlin"] = [], ["newyork"] = [], ["hourly"] = [] };
        void Note(IReadOnlyList<(string Project, ScheduledJob Job)> jobs) { foreach (var (_, job) in jobs) queued[job.Plan].Add(clock.GetUtcNow()); }
        async Task Hourly(DateTimeOffset from, DateTimeOffset to)
        {
            for (clock.To(from); clock.GetUtcNow() <= to; clock.Advance(TimeSpan.FromHours(1)))
            {
                Note(await build.TickAndRun());
                var report = await both.Runner.TickAsync();
                await both.Runner.WhenIdleAsync();
                Assert.Empty(report.Enqueued);                                         // observed: nothing is ever queued on the runner
            }
        }

        await Hourly(OwnershipFixture.At(24, 18), OwnershipFixture.At(25, 12));
        await Hourly(OwnershipFixture.At(31, 8), new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero));
        var lastTick = clock.GetUtcNow() - TimeSpan.FromHours(1);

        // a span with nobody ticking (the server was down), then one tick on each side: one run, not eight
        clock.To(lastTick + TimeSpan.FromHours(8));
        Note(await build.TickAndRun());
        await both.Runner.TickAsync();

        Assert.Empty(both.Executor.Started);
        foreach (var plan in new[] { "berlin", "newyork", "hourly" })
        {
            var decisions = both.Runner.DecisionsOf(new TaskRef(Package, Channel, TaskOf(plan))).Where(d => d.Source == TaskDecisions.Runner).ToList();
            Assert.Equal(queued[plan], decisions.Select(d => d.DecidedAt));            // the runner would have queued exactly when Build did
            Assert.All(decisions, d => Assert.False(d.Paused));
        }

        Assert.Contains(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero), queued["berlin"]);        // 09:00 CET the day the clocks went back, 07:00 UTC the day before
        Assert.DoesNotContain(new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero), queued["berlin"]);
        Assert.Equal([new DateTimeOffset(2026, 10, 31, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero)],
            queued["newyork"].Where(t => t >= new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero) && t < new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)));      // 09:00 local before and after the clocks went back
        var missed = both.Runner.DecisionsOf(new TaskRef(Package, Channel, TaskOf("hourly"))).Last(d => d.Source == TaskDecisions.Runner);
        Assert.Equal(7, missed.Missed);                                                // eight quarter-pasts passed since the last tick, one decision stands for one of them

        // and what Build reported for the same times is paired with it by the comparison: no difference
        foreach (var r in build.Reported) both.Runner.RecordOldDecision(new TaskRef(Package, Channel, TaskOf(r.Plan)), r.At, r.Missed, r.Paused);
        foreach (var plan in new[] { "berlin", "newyork", "hourly" })
        {
            var comparison = await ownership.CompareAsync("sales", OwnershipFixture.Connection, plan, tolerance: TimeSpan.Zero);
            Assert.True(comparison.Agrees, string.Join("; ", comparison.Differences.Select(d => $"{d.At:u} {d.Kind}")));
            Assert.Equal(queued[plan].Count, comparison.Matched);
        }
    }

    [Fact]
    public async Task A_plan_is_scheduled_by_one_of_the_two_at_any_moment_from_publishing_to_cutover_and_back()
    {
        var both = Start();
        var (build, clock) = (both.Build, both.Build.Clock);
        var task = new TaskRef(Package, Channel, TaskOf("hourly"));
        build.Scheduler.Tick();                                                        // 12:00: noted
        both.Runner.SetOwner(task, TaskOwners.Old, "publish", "published: the Build server schedules it", null);   // what `project publish` records before the version can activate

        async Task<(int Build, int Runner)> BothTick()
        {
            var started = both.Executor.Started.Count;
            var queued = (await build.TickAndRun()).Count;
            await both.Runner.TickAsync();
            await both.Runner.WhenIdleAsync();
            return (queued, both.Executor.Started.Count - started);
        }

        clock.To(OwnershipFixture.At(25, 12, 16));
        Assert.Equal((1, 0), await BothTick());                                       // published, not cut over: Build's

        var ownership = build.Ownership(both.Remote);
        await ownership.CutOverAsync("sales", OwnershipFixture.Connection, "hourly", build.Link, "go");
        clock.To(OwnershipFixture.At(25, 13, 16));
        Assert.Equal((0, 1), await BothTick());                                       // cut over: the runner's
        clock.To(OwnershipFixture.At(25, 14, 16));
        Assert.Equal((0, 1), await BothTick());

        await ownership.RollBackAsync("sales", OwnershipFixture.Connection, "hourly", build.Link, "back");
        clock.To(OwnershipFixture.At(25, 15, 16));
        Assert.Equal((1, 0), await BothTick());                                       // rolled back: Build's again, once
        clock.To(OwnershipFixture.At(25, 16, 16));
        Assert.Equal((1, 0), await BothTick());
        Assert.Equal(["hourly", "hourly"], both.Executor.Started.Select(s => s.Task.Replace("sqlserver-", "")));
    }

    [Fact]
    public async Task A_cutover_during_a_run_in_flight_waits_and_the_runner_runs_the_time_that_came_meanwhile_once()
    {
        var both = Start();
        var (build, clock) = (both.Build, both.Build.Clock);
        build.Scheduler.Tick();
        both.Runner.SetOwner(new TaskRef(Package, Channel, TaskOf("hourly")), TaskOwners.Old, "publish", null, null);
        clock.To(OwnershipFixture.At(25, 12, 16));
        build.Scheduler.Tick();                                                       // the 12:15 run is queued
        var (item, hosted) = build.Scheduler.Claim("worker")!.Value;                  // and going
        var waits = 0;
        var ownership = build.Ownership(both.Remote, async (_, ct) =>
        {
            if (waits++ > 0) return;
            clock.To(OwnershipFixture.At(25, 13, 20));                                // 13:15 comes during the wait
            Assert.Empty(build.Scheduler.Tick());                                     // Build has let go
            Assert.Empty((await both.Runner.TickAsync()).Enqueued);          // the runner has not taken it yet
            await build.Scheduler.RunAsync(item, hosted, "worker", ct);
        });

        await ownership.CutOverAsync("sales", OwnershipFixture.Connection, "hourly", build.Link, "go");

        await both.Runner.TickAsync();
        await both.Runner.WhenIdleAsync();
        Assert.Equal([(TaskOf("hourly"), OwnershipFixture.At(25, 13, 20))], both.Executor.Started);   // the 13:15 time ran, once, on the runner
        await both.Runner.TickAsync();
        await both.Runner.WhenIdleAsync();
        Assert.Single(both.Executor.Started);
        Assert.Empty(build.Scheduler.Tick());
    }
}
