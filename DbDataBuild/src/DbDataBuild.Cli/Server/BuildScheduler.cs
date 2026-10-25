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
                if (file.Settings.Schedule is not { Length: > 0 } cron) continue;
                var schedule = Schedule.OnCron(cron, file.Settings.TimeZone ?? "UTC");
                if (schedule.Validate() is { } why) { problem?.Invoke($"{connection}/{plan}: {why}"); continue; }
                jobs.Add(new ScheduledJob(connection, plan, schedule));
            }
        }
        return jobs;
    }
}

/// <summary>What the scheduler remembers about one job between ticks and restarts.</summary>
/// <param name="HandledUpTo">Scheduled times up to here are dealt with (see <see cref="ScheduleDecision.HandledUpTo"/>).</param>
/// <param name="LastStarted">When the last run began.</param>
/// <param name="LastExit">The last run's exit code, or null while it has not ended or never ran.</param>
internal sealed record JobState(DateTimeOffset? HandledUpTo, DateTimeOffset? LastStarted, int? LastExit);

/// <summary>The scheduler's own table in the server's state database; the queue's tables come from <see cref="WorkQueueSchema.Queue"/>.</summary>
internal static class BuildSchedulerSchema
{
    public const string Service = "dbdatabuild";

    public static StateSchema Schema => new([WorkQueueSchema.Queue,
        """
        -- What the scheduler remembers per job (project and connection/plan): up to when scheduled times are dealt with, when the last run began and how it ended.
        CREATE TABLE ScheduleJobs (
            Project {{key}} NOT NULL,
            Job {{key}} NOT NULL,
            HandledUpToUtc {{text}} NULL,
            LastStartedUtc {{text}} NULL,
            LastExit {{int}} NULL,
            PRIMARY KEY (Project, Job)
        );
        """]);

    public static StateDatabase Open(string serverDirectory) => new(Path.Combine(serverDirectory, "state.db"), Schema);
}

/// <summary>
/// Runs the refresh plans that declare a schedule (plan 13, M08), from the projects a server hosts. Every tick it asks the shared <see cref="ScheduleEvaluator"/> whether each scheduled plan is due and, if so, puts it on the shared work
/// queue; a worker takes it off and runs <c>connection refresh &lt;connection&gt; &lt;plan&gt;</c> in a child process, like any command. The queue holds one item of a plan at a time, so a plan whose run is still going is not started again, and a
/// manual run of the same plan meets the command's own lock and its <c>on_busy</c> rule, as it does without a server.
/// <para>
/// A job seen for the first time is not run: the scheduler records the moment and waits for its next scheduled time, so adding a schedule to a project never starts a run by itself. After a restart, scheduled times that passed while the server was
/// down run once (<see cref="MisfirePolicy.RunOnce"/>, as Sync does).
/// </para>
/// </summary>
internal sealed class BuildScheduler(ProjectsFile projects, IProjectCommandRunner runner, StateDatabase database, TimeProvider clock, Action<string> log, TimeSpan? lease = null)
{
    /// <summary>How long a claimed run stays claimed without a heartbeat; a run beats three times in it, so only a worker that died loses its run.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);

    private readonly WorkQueueStore _queue = new(database, BuildSchedulerSchema.Service, clock) { LeaseDuration = lease ?? DefaultLease };
    public const string Kind = "refresh";

    /// <summary>One tick: gives up leases that ran out and queues every plan that is due. Returns what it queued.</summary>
    public IReadOnlyList<(string Project, ScheduledJob Job)> Tick()
    {
        _queue.ReleaseExpired();
        var queued = new List<(string, ScheduledJob)>();
        var now = clock.GetUtcNow();
        foreach (var project in projects.All())
        {
            var root = projects.RootOf(project);
            if (!Directory.Exists(root)) continue;
            foreach (var job in RefreshJobs.Discover(root, p => log($"Project '{project.Id}': {p}")))
            {
                var state = GetState(project.Id, job);
                if (state?.HandledUpTo is null)
                {
                    SaveHandled(project.Id, job, now);
                    continue;
                }

                var decision = ScheduleEvaluator.Evaluate(job.Schedule, state.HandledUpTo, now);
                if (decision.HandledUpTo != state.HandledUpTo) SaveHandled(project.Id, job, decision.HandledUpTo ?? now);
                if (!decision.Due) continue;
                _queue.Enqueue(project.Id, Kind, job.Subject, lane: Kind);
                queued.Add((project.Id, job));
            }
        }
        return queued;
    }

    /// <summary>Takes the next queued run, or null.</summary>
    public (WorkItem Item, HostedProject Project)? Claim(string workerId)
    {
        foreach (var project in projects.All())
            if (_queue.TryClaimNext(project.Id, workerId) is { } item)
                return (item, project);
        return null;
    }

    /// <summary>Runs a claimed plan to its end, keeping its lease alive meanwhile, and records how it ended.</summary>
    public async Task RunAsync(WorkItem item, HostedProject project, string workerId, CancellationToken cancellationToken)
    {
        var parts = item.Subject.Split('/', 2);
        var started = clock.GetUtcNow();
        _queue.MarkRunning(item.Id);
        SaveStarted(project.Id, item.Subject, started);
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

        if (exit == 0) _queue.MarkDone(item.Id);
        else
        {
            _queue.MarkFailed(item.Id);
            log($"Project '{project.Id}', {item.Subject}: the scheduled run ended with exit code {exit}.");
        }
        SaveExit(project.Id, item.Subject, exit);
    }

    /// <summary>The scheduled plans of a project with the next time each is due and how its last run went.</summary>
    public IReadOnlyList<(ScheduledJob Job, DateTimeOffset? NextDue, JobState? State)> Jobs(HostedProject project)
    {
        var root = projects.RootOf(project);
        if (!Directory.Exists(root)) return [];
        var now = clock.GetUtcNow();
        return [.. RefreshJobs.Discover(root).Select(j => (j, (DateTimeOffset?)ScheduleEvaluator.NextAfter(j.Schedule, now), GetState(project.Id, j)))];
    }

    // ---- the per-job record ----

    private JobState? GetState(string project, ScheduledJob job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT HandledUpToUtc, LastStartedUtc, LastExit FROM ScheduleJobs WHERE Project = $project AND Job = $job;");
            cmd.Bind(database, "project", project);
            cmd.Bind(database, "job", job.Subject);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            static DateTimeOffset? When(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), System.Globalization.CultureInfo.InvariantCulture);
            return new JobState(When(reader, 0), When(reader, 1), reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)));
        });

    private void SaveHandled(string project, ScheduledJob job, DateTimeOffset handledUpTo) => Upsert(project, job.Subject, "HandledUpToUtc", handledUpTo.ToString("O"));

    private void SaveStarted(string project, string job, DateTimeOffset started) => Upsert(project, job, "LastStartedUtc", started.ToString("O"));

    private void SaveExit(string project, string job, int exit) => Upsert(project, job, "LastExit", exit);

    private void Upsert(string project, string job, string column, object value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var insert = database.Command(connection, database.Dialect.InsertOrIgnore("ScheduleJobs", "Project, Job", "$project, $job", "Project, Job")))
            {
                insert.Bind(database, "project", project);
                insert.Bind(database, "job", job);
                insert.ExecuteNonQuery();
            }
            using var update = database.Command(connection, $"UPDATE ScheduleJobs SET {column} = $value WHERE Project = $project AND Job = $job;");
            update.Bind(database, "value", value);
            update.Bind(database, "project", project);
            update.Bind(database, "job", job);
            update.ExecuteNonQuery();
        });
}

/// <summary>Drives a <see cref="BuildScheduler"/> for as long as the server runs: a tick every few seconds, and up to <c>maxRuns</c> plans running at once.</summary>
internal sealed class BuildSchedulerService(BuildScheduler scheduler, Action<string> log, TimeSpan? tick = null, int maxRuns = 4) : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(10);
    private readonly string _worker = $"scheduler-{Environment.ProcessId}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        using var timer = new PeriodicTimer(tick ?? DefaultTick);
        try
        {
            do
            {
                try
                {
                    scheduler.Tick();
                    running.RemoveAll(t => t.IsCompleted);
                    while (running.Count < maxRuns && scheduler.Claim(_worker) is var (item, project))
                        running.Add(Task.Run(() => scheduler.RunAsync(item, project, _worker, stoppingToken), CancellationToken.None));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // a tick that fails (a locked state file, a project folder that vanished) is tried again on the next one
                    log($"The scheduler's tick failed ({ex.GetType().Name}); it will try again.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* the server is stopping */ }
        await Task.WhenAll(running.Select(async t => { try { await t; } catch (OperationCanceledException) { } }));
    }
}
