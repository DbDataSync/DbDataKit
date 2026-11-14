using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;

namespace DbDataSync.TaskRunner;

/// <summary>
/// A replication that keeps its state on the target (plan 28c E17): the table (<see cref="TargetPositionLog"/>), which side decides where a pass resumes, and how a pass
/// starts and ends. Split from <see cref="RunExecutor"/> so each rule is testable on its own.
/// </summary>
/// <param name="TargetPrimary">The target's position decides where the next pass reads from (mode <c>target</c>, or <c>both</c> with the target primary).</param>
public sealed record PositionLogTracking(TargetPositionLog State, bool TargetPrimary)
{
    /// <summary>
    /// What applies to this pass, or null when the state is the runner's only (the mode, an engine without a writer, a writer that cannot carry the state). A file that asked
    /// for the target and cannot have it is refused naming why; the default quietly takes the runner's state and says so in the run's log.
    /// </summary>
    public static PositionLogTracking? For(ReplicationTaskConfig task, IDriver targetDriver, IChangeWriter writer, Action<LogSeverity, string> log)
    {
        var resolved = TrackingResolution.Resolve(task.Tracking, targetDriver.DriverType);
        if (resolved.EffectiveMode == "state")
        {
            if (resolved.RequestedMode != "state")
                throw new InvalidOperationException($"Replication '{task.Name}': {resolved.Reason}");
            return null;
        }

        if (!writer.SupportsPositionLog)
        {
            var message = $"The writer '{writer.Kind}' does not commit the entry's state row with its data, so the state of '{task.Name}' cannot be kept on the target; choose another writer or `tracking: {{ mode: state }}`.";
            throw new InvalidOperationException(message);
        }

        var engine = TargetPositionLog.EngineOf(targetDriver.DriverType)
            ?? throw new InvalidOperationException($"Replication '{task.Name}': the target driver '{targetDriver.DriverType}' has no state table writer.");
        return new PositionLogTracking(new TargetPositionLog(engine), resolved.EffectiveMode == "target" || resolved.EffectivePrimary == "target");
    }

    /// <summary>
    /// Creates the table when it is missing and says where the pass resumes: the target's position when the target is the primary, the runner's otherwise. Returns the position
    /// to read from and what to say when the two sides differ, (the table is created on first use; a login that cannot create it fails the pass naming the right it needs).
    /// </summary>
    public async Task<(PositionLogTracking Tracking, string? Resume, string? Message)> StartAsync(
        DbConnection targetConnection, string replication, string mapping, string watermarkKey, string? statePosition, CancellationToken cancellationToken)
    {
        await State.EnsureAsync(targetConnection, cancellationToken);

        var latest = await State.LatestAsync(targetConnection, replication, mapping, watermarkKey, cancellationToken);
        var (resume, message) = Reconcile(statePosition, latest, TargetPrimary);
        return (this, resume, message);
    }

    /// <summary>
    /// Where the pass resumes when the target and the runner's state each say where the last one ended. Same position: nothing to say. The target holds none: it is adopted from the
    /// runner's state (the first pass in this mode, or a mapping new to it) and the pass records it. They differ: the primary decides and the pass says so, which is how a
    /// target restored from a backup (behind the runner's state) is found: the replication resumes from what the target really holds, not from a position its data never reached.
    /// </summary>
    public static (string? Resume, string? Message) Reconcile(string? statePosition, PositionLogEntry? latest, bool targetPrimary)
    {
        if (latest is null)
            return (statePosition, statePosition is null ? null : $"The target holds no state for this mapping; the pass continues from the runner's position ('{statePosition}') and the target records it from here. If this target was restored from a backup older than its state table, the table is gone with it and this cannot be told apart: data between the backup and that position is not re-read (reload the mapping to be sure).");
        if (statePosition is not null && string.Equals(statePosition, latest.WatermarkAfter, StringComparison.Ordinal))
            return (statePosition, null);

        var stateText = statePosition is null ? "none" : $"'{statePosition}'";
        if (targetPrimary)
            return (latest.WatermarkAfter,
                $"The target's position ('{latest.WatermarkAfter}', entry {latest.Sequence}) differs from the runner state's ({stateText}): the target was restored or the state was reset. The replication resumes from the target's position." + (statePosition is null ? " The runner state holds no position (it was lost or reset), so a reader that captures its own position starts an initial load instead." : ""));
        return (statePosition,
            $"The target's position ('{latest.WatermarkAfter}', entry {latest.Sequence}) differs from the runner state's ({stateText}); the state is the primary, so the pass continues from the state's position.");
    }

    /// <summary>
    /// Applies <c>tracking.retention</c> (default the last 1000 entries and anything newer than 7 days) to the mapping's entries, in a transaction of its own on the target. The newest entry of each
    /// source stays whatever the setting says. A failure is logged as a warning and does not fail the pass: the data and the position it committed are not affected by a prune.
    /// </summary>
    public async Task<int> PruneAsync(DbConnection targetConnection, ReplicationTaskConfig task, string mapping, DateTimeOffset now, Action<LogSeverity, string> log, CancellationToken cancellationToken)
    {
        var retention = TrackingResolution.RetentionOf(task.Tracking);
        try
        {
            await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
            var removed = await State.PruneAsync(targetConnection, transaction, task.Name, mapping, retention.EffectiveEntries(), retention.EffectiveDays(), now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (removed > 0) log(LogSeverity.Info, $"'{mapping}': {removed} old entry(s) removed from {TargetPositionLog.Schema}.{TargetPositionLog.Table} (kept: the newest {retention.EffectiveEntries()} and anything newer than {retention.EffectiveDays()} day(s)).");
            return removed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log(LogSeverity.Warning, $"'{mapping}': the old entries of {TargetPositionLog.Schema}.{TargetPositionLog.Table} could not be pruned: {exception.Message}");
            return 0;
        }
    }

    /// <summary>The entry of one segment, as the hook writes it.</summary>
    public static PositionLogDraft Draft(Guid runId, string replication, string mapping, string watermarkKey, string? before, ReadResult read, long rowsStaged) =>
        new(Guid.NewGuid(), replication, mapping, watermarkKey, before, read.WatermarkAfterRead!, read.WatermarkTimeAfterRead, rowsStaged, runId);

    /// <summary>
    /// After the writer returned: a write that carried rows and did not write the state row broke the rule (the entry is on the target without its position), which is a failure naming the
    /// writer. An entry without rows has nothing to be atomic with, so its row is written on its own.
    /// </summary>
    public async Task FinishAsync(DbConnection targetConnection, PositionLogScope.Scope scope, PositionLogDraft draft, string writerKind, CancellationToken cancellationToken)
    {
        if (scope.Fired > 0) return;
        if (draft.RowsStaged > 0)
            throw new InvalidOperationException($"The writer '{writerKind}' wrote the entry without its state row, so the target holds data whose position it does not record. It must commit through a transaction that calls PositionLogScope.BeforeCommitAsync.");
        await State.WriteAsync(targetConnection, null, draft, cancellationToken);
    }
}
