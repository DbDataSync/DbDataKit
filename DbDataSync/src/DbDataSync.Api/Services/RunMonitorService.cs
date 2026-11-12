using DbDataSync.Api.Hubs;
using DbDataSync.State;
using Microsoft.AspNetCore.SignalR;

namespace DbDataSync.Api.Services;

/// <summary>
/// Polls the central SQLite Logs/TaskRuns tables and republishes over SignalR (architecture/detailed-design.md
/// §3.1) — the API never receives a direct callback from a TaskRunner child process, since state flows
/// through DbDataSync.State, not an IPC channel.
/// <para>
/// Completion is detected two ways: via TaskRuns.Status leaving Running (a run caught as active on
/// some earlier tick), and via a "recently ended" query covering runs that completed their entire
/// lifecycle between two polling ticks and so were never once observed as active — a real
/// possibility now that a claimed unit of work can complete in well under this service's 1-second
/// tick (no per-run process spawn overhead standing in the way). Process.HasExited is not used at
/// all: one worker process now backs many concurrently-active RunIds. See
/// architecture/implementation/done/phase-008-work-queue-schema.md.
/// </para>
/// </summary>
public sealed class RunMonitorService(
    ProcessSupervisor supervisor,
    TaskRunStore taskRunStore,
    LogWriter logWriter,
    IHubContext<RunHub> hub,
    DbDataSync.Api.Runner.RunnerRunStreamer runnerStream,
    ILogger<RunMonitorService> logger,
    DbDataSync.Api.Management.SyncDataChanges? dataChanges = null) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    // Wider than TickInterval so a "recently ended" run is never missed due to ordinary tick jitter —
    // GetRecentlyEndedRuns is filtered by EndedAtUtc, and _notified below de-dupes regardless of how
    // many ticks a given run's completion happens to be visible across.
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(5);

    private readonly Dictionary<Guid, long> _lastLogId = new();
    private readonly HashSet<Guid> _watching = new();
    // Unbounded for the lifetime of the API process — acceptable for now (matches other
    // not-yet-productionized simplifications in this phase); a long-lived API instance processing a
    // very large number of runs would eventually want this pruned or replaced with a persisted
    // "notified" flag on TaskRuns itself.
    private readonly HashSet<Guid> _notified = new();

    /// <summary>
    /// How often the runs of the replications a runner owns are looked at (plan 28c E13): faster than this server's own tick, because the lines are asked of another process over HTTP and the live view is meant to show a
    /// line within a second of its being written. One request lists the runs; a request per run going reads its new lines.
    /// </summary>
    private static readonly TimeSpan RunnerTickInterval = TimeSpan.FromMilliseconds(400);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(StreamOwnRunsAsync(stoppingToken), StreamRunnerRunsAsync(stoppingToken));

    private async Task StreamRunnerRunsAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RunnerTickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await runnerStream.Tick(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the runner's side of the live view is never allowed to end this service: the lines of this server's own runs go on
                logger.LogWarning(ex, "The live view of the runner's runs failed; it is tried again.");
            }
        }
    }

    private async Task StreamOwnRunsAsync(CancellationToken stoppingToken)
    {
        supervisor.ReconcileOrphanedRuns();

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var active = taskRunStore.GetActiveRuns();
            var activeIds = active.Select(r => r.RunId).ToHashSet();

            foreach (var run in active)
            {
                if (_watching.Add(run.RunId)) DbDataKit.Telemetry.HostTelemetry.RunStarted(DbDataSyncHost.ProductName, run.RunKind.ToString());   // plan 22a GP28: counted once, when first seen
                await StreamLogsAsync(run.RunId, stoppingToken);
            }

            // Runs previously caught as active that have since left that set...
            var justCompleted = _watching.Except(activeIds).ToList();
            // ...unioned with runs that completed their whole lifecycle between ticks and so were
            // never in _watching at all (see class doc).
            var recentlyEnded = taskRunStore.GetRecentlyEndedRuns(DateTimeOffset.UtcNow - RecentWindow);
            foreach (var run in recentlyEnded)
            {
                if (_notified.Contains(run.RunId) || justCompleted.Contains(run.RunId))
                    continue;
                justCompleted.Add(run.RunId);
            }

            foreach (var runId in justCompleted)
            {
                if (!_notified.Add(runId))
                    continue;

                await StreamLogsAsync(runId, stoppingToken); // catch any trailing log lines first
                var run = taskRunStore.GetRun(runId);
                if (run is not null)
                    DbDataKit.Telemetry.HostTelemetry.RunEnded(DbDataSyncHost.ProductName, run.RunKind.ToString(), run.Status.ToString().ToLowerInvariant(),
                        run.StartedAtUtc is { } began && run.EndedAtUtc is { } ended ? ended - began : null);   // counts and durations by outcome, never the error text (plan 22a GP28)
                // plan 24a UC13: a run that wrote rows is a dataset that changed; the serving groups this server is paired with are told in the background
                if (run is not null) dataChanges?.RunCompleted(run);
                await hub.Clients.Group(runId.ToString()).SendAsync(
                    "runCompleted",
                    new { runId, status = run?.Status.ToString(), run?.RowsRead, run?.RowsWritten, run?.ErrorSummary },
                    stoppingToken);

                _watching.Remove(runId);
                _lastLogId.Remove(runId);
            }
        }
    }

    private async Task StreamLogsAsync(Guid runId, CancellationToken cancellationToken)
    {
        var sinceId = _lastLogId.GetValueOrDefault(runId);
        var newLogs = logWriter.GetLogs(runId, sinceId == 0 ? null : sinceId);
        var group = hub.Clients.Group(runId.ToString());

        foreach (var entry in newLogs)
        {
            await group.SendAsync(
                "logLine",
                new { entry.Id, entry.TimestampUtc, Level = entry.Level.ToString(), entry.Message },
                cancellationToken);
            _lastLogId[runId] = entry.Id;
        }
    }
}
