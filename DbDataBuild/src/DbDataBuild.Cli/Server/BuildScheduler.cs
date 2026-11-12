using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataBuild.Cli.Server;

/// <summary>A refresh plan that declares a <c>schedule:</c>, found in a project's compiled files.</summary>
internal sealed record ScheduledJob(string Connection, string Plan, Schedule Schedule)
{
    /// <summary>What the queue calls the job within its project.</summary>
    public string Subject => $"{Connection}/{Plan}";
}

internal static class RefreshJobs
{
    /// <summary>
    /// The scheduled plans of a project, from <c>rendered/&lt;connection&gt;/refresh/*.yml</c> (plan 13, D13: schedules are data in the project, and a change of one is a commit). A plan with an invalid schedule is left out and named in
    /// <paramref name="problem"/>; project compile refuses such a schedule already, so this is a file edited by hand.
    /// </summary>
    /// <summary>
    /// What a compiled plan's settings say about when it runs: the schedule (null when it declares none) or why the one it declares is not valid. The one reading the server's scheduler and `project publish` (plan 28c E07, which turns it
    /// into the manifest's schedule) share, so the two cannot disagree about a plan.
    /// </summary>
    public static (Schedule? Schedule, string? Problem) ScheduleOf(RefreshPlanSettingsText settings)
    {
        if (settings.Schedule is not { Length: > 0 } cron) return (null, null);
        var schedule = Schedule.OnCron(cron, settings.TimeZone ?? "UTC");
        return schedule.Validate() is { } why ? (null, why) : (schedule, null);
    }

    public static IReadOnlyList<ScheduledJob> Discover(string projectRoot, Action<string>? problem = null)
    {
        var rendered = Path.Combine(projectRoot, RenderCommand.RenderedDir);
        if (!Directory.Exists(rendered)) return [];
        var jobs = new List<ScheduledJob>();
        foreach (var connection in Directory.EnumerateDirectories(rendered).Select(Path.GetFileName).Where(n => n is not (null or "lowered")).Select(n => n!).Order(StringComparer.Ordinal))
        {
            foreach (var plan in RefreshViews.PlanNames(projectRoot, connection))
            {
                var path = Path.Combine(rendered, RefreshPlanDocument.PathOf(connection, plan));
                var diagnostics = new List<Diagnostic>();
                if (RefreshPlanDocument.Parse(File.ReadAllText(path), path, diagnostics) is not { } file) { problem?.Invoke($"{path}: not a readable refresh plan"); continue; }
                var (schedule, why) = ScheduleOf(file.Settings);
                if (why is not null) { problem?.Invoke($"{connection}/{plan}: {why}"); continue; }
                if (schedule is null) continue;
                jobs.Add(new ScheduledJob(connection, plan, schedule));
            }
        }
        return jobs;
    }
}

/// <summary>The server's state database (plan 29a Q17): the shared <c>scheduling</c> module, which holds the work queue, the run locks and the per-job record and pause. A database made by the earlier structure, one schema version for the whole database, is refused with a message naming the file.</summary>
internal static class BuildSchedulerSchema
{
    public const string Service = "dbdatabuild";

    /// <summary>The scheduling module, the <c>quality</c> module (the results of the models' checks, plan 23a FQ09: install scope), and the modules of the copy of the tracking tables (plan 28c E16): the progress store the follower's checkpoints are in, the divergence records and <c>build_tracking</c> itself.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } = [SchedulingModule.Module, RunsModule.Module, DbDataBuild.State.BuildRunsModule.Module, .. DbDataKit.Tracking.Build.BuildTrackingState.Modules, QualityModule.Module];

    /// <summary>Install-scoped modules ignore it; the copy's modules are kept for the group <c>main</c>, the one a server has until it has groups of its own.</summary>
    public static StateScope Scope => DbDataKit.Tracking.Build.BuildTrackingState.Scope;

    /// <param name="time">The clock the queue's leases and availability are measured on: the system's, or a test's.</param>
    /// <param name="settings">Where the state is; null: what flags-free <see cref="BuildState.Resolve"/> finds (the environment, the <c>state:</c> section of <c>server.yaml</c>, else SQLite in <c>state.db</c>).</param>
    public static StateDatabase Open(string serverDirectory, TimeProvider? time = null, StateSettings? settings = null) => BuildState.Open(serverDirectory, time, settings);
}

/// <summary>Where a scheduler reports what it queued for a plan whose owner is <c>observe</c> (plan 28c E09, ED7): the runner records it beside what it would have queued, so the two can be compared.</summary>
internal interface IObservedDecisions
{
    /// <summary>The scheduler, at <paramref name="at"/>, queued the plan (<paramref name="paused"/> false) or passed its time over because the plan is paused (true). <paramref name="missed"/> is how many times it stood for beyond one.</summary>
    void Report(string project, ScheduledJob job, DateTimeOffset at, int missed, bool paused);
}

/// <summary>
/// Runs the refresh plans that declare a schedule (plan 13, M08), from the projects a server hosts. Every tick it asks the shared <see cref="ScheduleEvaluator"/> whether each scheduled plan is due and, if so, puts it on the shared work
/// queue; a worker takes it off and runs <c>connection refresh &lt;connection&gt; &lt;plan&gt;</c> in a child process, like any command. The queue holds one item of a plan at a time, so a plan whose run is still going is not started again, and a
/// manual run of the same plan meets the command's own lock and its <c>on_busy</c> rule, as it does without a server.
/// <para>
/// A job seen for the first time is not run: the scheduler records the moment and waits for its next scheduled time, so adding a schedule to a project never starts a run by itself. After a restart, scheduled times that passed while the server was
/// down run once (<see cref="MisfirePolicy.RunOnce"/>, as Sync does).
/// </para>
/// <para>
/// A plan has one scheduling owner at a time (plan 28c E09, ED6), read from <paramref name="schedule"/> at every tick: <c>old</c> (this scheduler, the default), <c>observe</c> (this scheduler, and what it queues is also reported to
/// <paramref name="observed"/> for the comparison with the runner) or <c>runner</c>, for which this scheduler does nothing at all: it neither queues the plan nor moves its position, so the position a cutover copied is the one a rollback finds.
/// The owner is read again just before a run is queued, so a cutover that was written between the two reads is honoured.
/// </para>
/// </summary>
internal sealed class BuildScheduler(ProjectsFile projects, IProjectCommandRunner runner, StateDatabase database, TimeProvider clock, Action<string> log, TimeSpan? lease = null, Func<bool>? paused = null, BuildTrackingHost? tracking = null,
    Func<BuildScheduleSettings>? schedule = null, IObservedDecisions? observed = null)
{
    /// <summary>How long a claimed run stays claimed without a heartbeat; a run beats three times in it, so only a worker that died loses its run.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);

    private readonly ModuleJobStateStore _jobs = new(database, BuildSchedulerSchema.Service);
    private readonly DbDataBuild.State.PlanRunStore _runs = new(database, BuildSchedulerSchema.Service);
    private readonly ModuleJobPauseStore _pauses = new(database, BuildSchedulerSchema.Service);
    private readonly ModuleWorkQueueStore _queue = new(database, BuildSchedulerSchema.Service) { LeaseDuration = lease ?? DefaultLease };
    public const string Kind = "refresh";

    /// <summary>One tick: gives up leases that ran out and queues every plan that is due. Returns what it queued.</summary>
    public IReadOnlyList<(string Project, ScheduledJob Job)> Tick()
    {
        _queue.ReleaseExpired();
        var queued = new List<(string, ScheduledJob)>();
        var now = clock.GetUtcNow();
        var owners = schedule?.Invoke() ?? BuildScheduleSettings.Default;
        foreach (var project in projects.All())
        {
            var root = projects.RootOf(project);
            if (!Directory.Exists(root)) continue;
            foreach (var job in RefreshJobs.Discover(root, p => log($"Project '{project.Id}': {p}")))
            {
                var owner = owners.OwnerOf(project.Id, job.Subject);
                if (owner == TaskOwners.Runner) continue;      // the runner schedules it (E09): this scheduler does not even look
                var state = _jobs.Get(project.Id, job.Subject);
                if (state?.HandledUpTo is null)
                {
                    _jobs.SaveHandled(project.Id, job.Subject, now);
                    continue;
                }

                var decision = ScheduleEvaluator.Evaluate(job.Schedule, state.HandledUpTo, now);
                if (decision.HandledUpTo != state.HandledUpTo) _jobs.SaveHandled(project.Id, job.Subject, decision.HandledUpTo ?? now);
                if (!decision.Due) continue;
                if (_pauses.IsPaused(project.Id, job.Subject))     // the time is handled (above) and not run: resuming does not start the plan for the times it was paused through
                {
                    if (owner == TaskOwners.Observe) observed?.Report(project.Id, job, now, decision.Missed, paused: true);
                    continue;
                }

                // a cutover may have been written since the owners were read; the last look is the one that counts
                if (schedule is not null && schedule().OwnerOf(project.Id, job.Subject) == TaskOwners.Runner)
                {
                    _jobs.SaveHandled(project.Id, job.Subject, state.HandledUpTo.Value);    // the time was not run here, so it is not handled here: the runner finds the position as it was
                    continue;
                }

                _queue.Enqueue(project.Id, Kind, job.Subject, lane: Kind);
                queued.Add((project.Id, job));
                if (owner == TaskOwners.Observe) observed?.Report(project.Id, job, now, decision.Missed, paused: false);
            }
        }
        return queued;
    }

    private int _running;

    /// <summary>Plans this scheduler is running now (claimed and not yet ended), for the maintenance mode's idle check.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Takes the next queued run, or null; always null while the server is in maintenance (<c>paused</c>): ticks still queue what is due, nothing is started.</summary>
    public (WorkItem Item, HostedProject Project)? Claim(string workerId)
    {
        if (paused?.Invoke() == true) return null;
        foreach (var project in projects.All())
            if (_queue.TryClaimNext(project.Id, workerId) is { } item)
            {
                Interlocked.Increment(ref _running);
                return (item, project);
            }
        return null;
    }

    /// <summary>Runs a claimed plan to its end, keeping its lease alive meanwhile, and records how it ended.</summary>
    public async Task RunAsync(WorkItem item, HostedProject project, string workerId, CancellationToken cancellationToken)
    {
        try { await RunClaimedAsync(item, project, workerId, cancellationToken); }
        finally { Interlocked.Decrement(ref _running); }
    }

    private async Task RunClaimedAsync(WorkItem item, HostedProject project, string workerId, CancellationToken cancellationToken)
    {
        var parts = item.Subject.Split('/', 2);
        var started = clock.GetUtcNow();
        using var telemetry = DbDataKit.Telemetry.HostTelemetry.StartRun("dbdatabuild", Kind, item.Subject);   // plan 22a GP28: counted, timed, and an outcome; never the command line
        _queue.MarkRunning(item.Id);
        _jobs.SaveStarted(project.Id, item.Subject, started);
        using var beating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(_queue.LeaseDuration / 3, clock);
                while (await timer.WaitForNextTickAsync(beating.Token)) _queue.Heartbeat(item.Id, workerId);
            }
            catch (OperationCanceledException) { /* the run ended */ }
        }, CancellationToken.None);

        int exit;
        try
        {
            var result = await runner.RunAsync(["connection", "refresh", parts[0], parts[1], "--project", projects.RootOf(project)], projects.RootOf(project), cancellationToken);
            exit = result.Exit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Project '{project.Id}', {item.Subject}: the scheduled run could not be started ({ex.GetType().Name}).");
            exit = -1;
        }
        finally
        {
            await beating.CancelAsync();
            await heartbeat;
        }

        if (exit == 0) telemetry.Succeeded();
        else if (cancellationToken.IsCancellationRequested) telemetry.Cancelled();
        else telemetry.Failed();
        if (exit == 0) _queue.MarkDone(item.Id);
        else
        {
            _queue.MarkFailed(item.Id);
            log($"Project '{project.Id}', {item.Subject}: the scheduled run ended with exit code {exit}.");
        }
        _jobs.SaveExit(project.Id, item.Subject, exit);
        _runs.Record(project.Id, item.Subject, item.RunId, started, clock.GetUtcNow(), exit);   // UI09: the run is listed by GET /api/v1/plan-runs
        // the copy of the tracking tables follows a run, within a budget and never failing it (a down copy or target is a line in the log)
        if (tracking is { Enabled: true }) await tracking.CopyAsync(project, parts[0], TrackingBudget, CancellationToken.None);
    }

    /// <summary>The longest the copy after a run, or in a catch-up, takes.</summary>
    public static readonly TimeSpan TrackingBudget = TimeSpan.FromSeconds(20);

    /// <summary>One catch-up of every hosted project's copy, for the tick: a copy that was down catches up without a refresh.</summary>
    public Task CatchUpTrackingAsync(CancellationToken cancellationToken) => tracking is { Enabled: true } ? tracking.CopyAsync(null, null, TrackingBudget, cancellationToken) : Task.CompletedTask;

    /// <summary>The scheduled plans of a project with the next time each is due and how its last run went.</summary>
    public IReadOnlyList<(ScheduledJob Job, DateTimeOffset? NextDue, JobState? State)> Jobs(HostedProject project)
    {
        var root = projects.RootOf(project);
        if (!Directory.Exists(root)) return [];
        var now = clock.GetUtcNow();
        return [.. RefreshJobs.Discover(root).Select(j => (j, (DateTimeOffset?)ScheduleEvaluator.NextAfter(j.Schedule, now), _jobs.Get(project.Id, j.Subject)))];
    }

    /// <summary>The newest finished runs of the scheduled plans, newest first (UI09).</summary>
    public IReadOnlyList<DbDataBuild.State.PlanRun> RecentRuns(int limit) => _runs.Recent(limit);

    /// <summary>Who paused the plan and when, or null when it is not paused.</summary>
    public (string By, DateTimeOffset At)? PausedBy(HostedProject project, ScheduledJob job) => _pauses.Get(project.Id, job.Subject);

    /// <summary>The scheduled plan of a project by connection and name, or null.</summary>
    public ScheduledJob? Find(HostedProject project, string connection, string plan) =>
        Directory.Exists(projects.RootOf(project)) ? RefreshJobs.Discover(projects.RootOf(project)).FirstOrDefault(j => j.Connection == connection && j.Plan == plan) : null;

    /// <summary>Stops the plan from being queued at its scheduled times until it is resumed. A run already going is not touched.</summary>
    public void Pause(HostedProject project, ScheduledJob job, string by) => _pauses.Set(project.Id, job.Subject, by, clock.GetUtcNow());

    public void Resume(HostedProject project, ScheduledJob job) => _pauses.Clear(project.Id, job.Subject);

    /// <summary>Queues the plan now, apart from its schedule (a paused plan too: the person asked). False when a run of it is already queued or going.</summary>
    public bool RunNow(HostedProject project, ScheduledJob job)
    {
        try { _queue.EnqueueOrThrow(project.Id, Kind, job.Subject, lane: Kind); return true; }
        catch (WorkQueueCollisionException) { return false; }
    }
}

/// <summary>Drives a <see cref="BuildScheduler"/> for as long as the server runs: a tick every few seconds, and up to <c>maxRuns</c> plans running at once.</summary>
internal sealed class BuildSchedulerService(BuildScheduler scheduler, Action<string> log, TimeSpan? tick = null, int maxRuns = 4) : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TrackingCatchUpEvery = TimeSpan.FromMinutes(5);
    private readonly string _worker = $"scheduler-{Environment.ProcessId}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        var lastCatchUp = DateTime.UtcNow;
        using var timer = new PeriodicTimer(tick ?? DefaultTick);
        try
        {
            do
            {
                try
                {
                    scheduler.Tick();
                    if (DateTime.UtcNow - lastCatchUp > TrackingCatchUpEvery) { lastCatchUp = DateTime.UtcNow; _ = scheduler.CatchUpTrackingAsync(stoppingToken); }
                    running.RemoveAll(t => t.IsCompleted);
                    while (running.Count < maxRuns && scheduler.Claim(_worker) is var (item, project))
                        running.Add(Task.Run(() => scheduler.RunAsync(item, project, _worker, stoppingToken), CancellationToken.None));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // a tick that fails (a locked state file, a project folder that vanished) is tried again on the next one
                    log($"The scheduler's tick failed ({ex.GetType().Name}{(ex is InvalidOperationException ? ": " + ex.Message : "")}); it will try again.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* the server is stopping */ }
        await Task.WhenAll(running.Select(async t => { try { await t; } catch (OperationCanceledException) { } }));
    }
}
