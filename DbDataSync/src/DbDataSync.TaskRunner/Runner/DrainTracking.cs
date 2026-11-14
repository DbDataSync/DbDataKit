using DbDataKit.Runner.Kit;
using DbDataKit.State.Tracking;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;
using DbDataSync.Tracking;
using Microsoft.Extensions.Logging;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>What one follow of the entries of a runner-owned replication did.</summary>
/// <param name="Applies">False when the replication keeps its state in the runner's state only (nothing on the target to copy).</param>
public sealed record DrainTrackingResult(bool Applies, string? Identity, int Copied, bool CaughtUp, string? Error, bool Diverged, string? PositionProblem, string Said);

/// <summary>
/// The follower of a runner-owned replication (plan 28c E17, the runner side): the replication's drain copies the entries its passes recorded in <c>dbdatasync.position_log</c> on the target into the runner's state, through the runner
/// protocol (<see cref="RemoteSyncPositionLogFollower"/>), and checks that the positions its own checkpoint holds are ones the target recorded (a target restored from a backup, or a checkpoint that was reset, is a persisted divergence
/// in the runner's state, resolved with the runner's <c>POST /api/v1/tracking/divergences/{id}/resolve</c>). Only a replication whose file asks for <c>tracking: target</c> or <c>both</c> is followed; the drain does the
/// copy after its passes, at most once in <see cref="MinInterval"/> and once more when it ends, and it never fails the run: a target that is down, a copy the runner refused or a grant the run does not hold are lines in the log.
/// </summary>
public sealed class DrainTracking(string replication, ConfigRepository config, Func<string, CancellationToken, Task<System.Data.Common.DbConnection>> open, IRemoteTrackingCopy copy,
    Func<string, string, string?> positionOf, ILogger log, TimeProvider? time = null)
{
    /// <summary>The least time between two follows inside one drain (default taken: 30 seconds).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    /// <summary>The time one follow may take.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _one = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Follows now when <paramref name="force"/> or <see cref="MinInterval"/> has passed; skips when a follow is already running (the passes of a drain end at the same moment). Never throws.</summary>
    public async Task MaybeFollowAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _time.GetUtcNow() - _last < MinInterval) return;
        if (!await _one.WaitAsync(force ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            _last = _time.GetUtcNow();
            var result = await FollowAsync(cancellationToken).ConfigureAwait(false);
            if (result.Applies && result.Said.Length > 0 && (force || result.Copied > 0 || result.Error is not null || result.PositionProblem is not null || result.Diverged))
                log.Log(result.Error is not null || result.PositionProblem is not null || result.Diverged ? LogLevel.Warning : LogLevel.Information, "{Said}", result.Said);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogWarning("The entries of '{Replication}' could not be copied to the runner's state ({Type}: {Message}).", replication, ex.GetType().Name, ex.Message);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>One follow of every target that holds the replication's entries: the catch-up, then the position check when the catch-up went well. Public for the tests.</summary>
    public async Task<DrainTrackingResult> FollowAsync(CancellationToken cancellationToken)
    {
        var task = config.LoadReplicationTask(replication);
        var targets = SyncPositionLogTargets.Of(config, task);
        if (targets.Count == 0) return new(false, null, 0, false, null, false, null, "");
        var results = new List<DrainTrackingResult>();
        foreach (var target in targets) results.Add(await FollowOneAsync(target, cancellationToken).ConfigureAwait(false));
        return new(true, results[0].Identity, results.Sum(r => r.Copied), results.All(r => r.CaughtUp), results.Select(r => r.Error).FirstOrDefault(e => e is not null), results.Any(r => r.Diverged),
            results.Select(r => r.PositionProblem).FirstOrDefault(p => p is not null), string.Join(" ", results.Select(r => r.Said).Where(t => t.Length > 0)));
    }

    private async Task<DrainTrackingResult> FollowOneAsync(SyncPositionLogTarget target, CancellationToken cancellationToken)
    {
        var connectionName = target.Connection;
        var engine = target.Engine;
        var entries = new TargetPositionLog(engine);

        string identity;
        try
        {
            await using var probe = await open(connectionName, cancellationToken).ConfigureAwait(false);
            identity = SyncPositionLogStream.Identity(engine, probe.DataSource, probe.Database, connectionName, replication);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var said = $"The target '{connectionName}' did not answer, so the entries of '{replication}' were not copied to the runner's state: {ex.Message}";
            return new(true, null, 0, false, said, false, null, said);
        }

        var follower = new RemoteSyncPositionLogFollower(copy, entries, ct => open(connectionName, ct), identity, replication, time: _time);
        var run = await follower.CatchUpAsync(Budget, cancellationToken).ConfigureAwait(false);
        string? problem = null;
        if (run.Error is null && !run.Diverged)
        {
            try { problem = (await follower.CheckPositionsAsync(positionOf, cancellationToken).ConfigureAwait(false)).FirstOrDefault(f => f.Reason is not null)?.Reason; }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("The position check of '{Replication}' failed: {Message}", replication, ex.Message); }
        }

        var text = run.Error is not null
            ? $"The entries of '{replication}' could not be copied to the runner's state: {run.Error}"
            : run.Diverged
                ? $"The copy of the entries of '{replication}' in the runner's state is diverged from its target ({run.Divergence?.Reason}); an administrator resolves it (archive or reset)."
                : problem is not null
                    ? $"{problem} It is recorded as a divergence in the runner's state; an administrator resolves it (archive or reset)."
                    : $"{run.Copied} entry(s) of '{replication}' copied to the runner's state{(run.CaughtUp ? "" : ", not caught up yet")}.";
        return new(true, identity, run.Copied, run.CaughtUp, run.Error, run.Diverged, problem, text);
    }
}
