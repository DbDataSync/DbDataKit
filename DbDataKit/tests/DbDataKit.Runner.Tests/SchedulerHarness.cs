using System.Collections.Concurrent;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Limits;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Runner.Tests;

/// <summary>A clock the tests move.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset at) => _now = at;
}

/// <summary>The executor, faked: records the request, writes the run record as the real one does, and ends as <see cref="Behaviour"/> says.</summary>
internal sealed class FakeRunner(StateDatabase database, string service = "runner") : ITaskRunner
{
    public ConcurrentQueue<TaskRunRequest> Requests { get; } = new();
    public Func<TaskRunRequest, CancellationToken, Task<TaskEnd>> Behaviour { get; set; } = (_, _) => Task.FromResult(new TaskEnd(TaskEndState.Succeeded));
    public int Active;
    public int MaxActive;
    public int Cancelled;

    public IReadOnlyList<TaskRunRequest> Of(string task) => [.. Requests.Where(r => r.Task.Task == task)];

    public async Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default)
    {
        var runs = new RunStore(database, service);
        var runId = request.RunId ?? database.NewId();
        runs.Start(request.Task.ToString(), runId, package: request.Task.Package, packageVersion: request.PackageVersion, channel: request.Task.Channel, attempt: request.Attempt);
        Requests.Enqueue(request);
        var now = Interlocked.Increment(ref Active);
        int seen;
        while (now > (seen = Volatile.Read(ref MaxActive)) && Interlocked.CompareExchange(ref MaxActive, now, seen) != seen) { }
        TaskEnd end;
        try
        {
            end = await Behaviour(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref Cancelled);
            end = TaskEndMapping.Cancelled("cancelled");
        }
        finally
        {
            Interlocked.Decrement(ref Active);
        }

        var finish = end.ToRunFinish();
        runs.Finish(runId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
        return new TaskRunResult(runId, end, true, 0, false, TimeSpan.Zero, 0, 0, 0, 0, LimitEnforcement.None, LimitEnforcement.None, "none", 0, false, []);
    }
}

internal sealed class EmptySource : IArtifactSource
{
    public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ArtifactFile>>([]);

    public Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no files");
}

/// <summary>One state database on an engine with a clock the test moves, a version source, an artifact cache and a way to make schedulers over them.</summary>
internal sealed class SchedulerHarness : IDisposable
{
    public const string Package = "pkg";
    public const string Channel = "prod";
    public const string Service = "runner";

    private readonly StateEngines _engines = new();
    private readonly StateEngines.Location _location;
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-sched-").FullName;

    public SchedulerHarness(string engine)
    {
        _location = _engines.Provision(engine);
        Database = OpenDatabase();
        Cache = new ArtifactCache(Path.Combine(_directory, "cache"), new EmptySource());
    }

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public StateDatabase Database { get; }

    public InMemoryVersionSource Versions { get; } = new();

    public ArtifactCache Cache { get; }

    public TaskProjections Projections => new(Database);

    public ModuleWorkQueueStore Queue => new(Database, Service);

    public RunStore Runs => new(Database, Service);

    public ModuleJobStateStore Jobs => new(Database, Service);

    /// <summary>A second connection to the same database, on the same clock (a second loop instance).</summary>
    public StateDatabase OpenDatabase() => _location.Open([SchedulingModule.Module, RunnerModule.Module, RunsModule.Module, DefinitionsModule.Module], StateScope.Standalone, time: Clock);

    public static PackageTask Def(string name, Schedule? schedule = null, TaskLimits? limits = null, bool allowOverlap = false, string[]? requires = null) =>
        new(name, new TaskEntry("T"), schedule ?? new Schedule(), [], [], [], ProgressDeclaration.Empty, limits ?? TaskLimits.None, requires ?? [], [], allowOverlap);

    public static PackageManifest Manifest(string package, params PackageTask[] tasks) =>
        new(package, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "x.dll"), new BuiltAgainst(null, null, null), tasks);

    /// <summary>Makes a version of a package available and active on the channel: the version source knows it and the projection is rebuilt from its manifest.</summary>
    public void Activate(PackageManifest manifest, string version, string channel = Channel, StateDatabase? database = null)
    {
        Versions.Add(manifest.Name, version, manifest);
        new TaskProjections(database ?? Database).Replace(manifest.Name, channel, ProjectionDocuments.RowsOf(manifest, version));
    }

    /// <summary>Marks a task's scheduled times as handled up to now, so its first tick is not the "never ran" one.</summary>
    public void SeedHandled(string task, string package = Package, string channel = Channel) => Jobs.SaveHandled($"{package}/{channel}", task, Clock.GetUtcNow());

    public RunnerScheduler NewScheduler(
        ITaskRunner runner, string? worker = null, StateDatabase? database = null, TimeSpan? heartbeat = null, int maxRuns = 4, double jitter = 0.5, TimeSpan? tick = null, string[]? channels = null, Func<Guid?>? instance = null) =>
        new(database ?? Database, runner, Cache, Versions, new RunnerSchedulerOptions
        {
            Channels = channels ?? [Channel],
            WorkerId = worker ?? "loop-" + Guid.NewGuid().ToString("N"),
            Heartbeat = heartbeat ?? TimeSpan.FromSeconds(30),
            Tick = tick ?? TimeSpan.FromSeconds(5),
            MaxConcurrentRuns = maxRuns,
            Jitter = () => jitter,
            Instance = instance,
        });

    public static async Task<TickReport> Tick(RunnerScheduler scheduler)
    {
        var report = await scheduler.TickAsync();
        await scheduler.WhenIdleAsync();
        return report;
    }

    public static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Gave up waiting for {what}.");
            await System.Threading.Tasks.Task.Delay(20);
        }
    }

    public void Dispose()
    {
        _engines.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }
}
