using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;

namespace DbDataSync.TaskRunner;

/// <summary>
/// A replication that keeps its state on the target (plan 28c E17): the table (<see cref="TargetUnitState"/>), which side decides where a pass resumes, and how a pass
/// starts and ends. Split from <see cref="RunExecutor"/> so each rule is testable on its own.
/// </summary>
/// <param name="TargetPrimary">The target's position decides where the next pass reads from (mode <c>target</c>, or <c>both</c> with the target primary).</param>
/// <param name="Explicit">The file asked for it. A default that cannot be honoured falls back to the runner's state with a warning; an explicit ask is a failure.</param>
public sealed record UnitStateTracking(TargetUnitState State, bool TargetPrimary, bool Explicit)
{
    /// <summary>
    /// What applies to this pass, or null when the state is the runner's only (the mode, an engine without a writer, a writer that cannot carry the state). A file that asked
    /// for the target and cannot have it is refused naming why; the default quietly takes the runner's state and says so in the run's log.
    /// </summary>
    public static UnitStateTracking? For(ReplicationTaskConfig task, IDriver targetDriver, IChangeWriter writer, Action<LogSeverity, string> log)
    {
        var resolved = TrackingResolution.Resolve(task.Tracking, targetDriver.DriverType);
        if (resolved.EffectiveMode == "state")
        {
            if (!resolved.Defaulted && resolved.RequestedMode != "state")
                throw new InvalidOperationException($"Replication '{task.Name}': {resolved.Reason}");
            return null;
        }

        if (!writer.SupportsUnitState)
        {
            var message = $"The writer '{writer.Kind}' does not commit the unit's state row with its data, so the state of '{task.Name}' cannot be kept on the target; choose another writer or `tracking: {{ mode: state }}`.";
            if (!resolved.Defaulted) throw new InvalidOperationException(message);
            log(LogSeverity.Info, "Tracking state is kept in the runner's state: " + message);
            return null;
        }

        var engine = targetDriver.DriverType.Equals(DriverIds.MsSql, StringComparison.OrdinalIgnoreCase) ? UnitStateEngine.SqlServer : UnitStateEngine.Postgres;
        return new UnitStateTracking(new TargetUnitState(engine), resolved.EffectiveMode == "target" || resolved.EffectivePrimary == "target", !resolved.Defaulted);
    }

    /// <summary>
    /// Creates the table when it is missing and says where the pass resumes: the target's position when the target is the primary, the runner's otherwise. Returns the position
    /// to read from and what to say when the two sides differ, or <c>null</c> state when the default cannot create the table (the pass then runs on the runner's state alone).
    /// </summary>
    public async Task<(UnitStateTracking? Tracking, string? Resume, string? Message)> StartAsync(
        DbConnection targetConnection, string replication, string mapping, string watermarkKey, string? statePosition, CancellationToken cancellationToken)
    {
        try
        {
            await State.EnsureAsync(targetConnection, cancellationToken);
        }
        catch (InvalidOperationException exception) when (!Explicit)
        {
            return (null, statePosition, "Tracking state is kept in the runner's state: " + exception.Message);
        }

        var latest = await State.LatestAsync(targetConnection, replication, mapping, watermarkKey, cancellationToken);
        var (resume, message) = Reconcile(statePosition, latest, TargetPrimary);
        return (this, resume, message);
    }

    /// <summary>
    /// Where the pass resumes when the target and the runner's state each say where the last one ended. Same position: nothing to say. The target holds none: it is adopted from the
    /// runner's state (the first pass in this mode, or a mapping new to it) and the pass records it. They differ: the primary decides and the pass says so, which is how a
    /// target restored from a backup (behind the runner's state) is found: the replication resumes from what the target really holds, not from a position its data never reached.
    /// </summary>
    public static (string? Resume, string? Message) Reconcile(string? statePosition, UnitRecord? latest, bool targetPrimary)
    {
        if (latest is null)
            return (statePosition, statePosition is null ? null : $"The target holds no state for this mapping; the pass continues from the runner's position ('{statePosition}') and the target records it from here.");
        if (statePosition is not null && string.Equals(statePosition, latest.WatermarkAfter, StringComparison.Ordinal))
            return (statePosition, null);

        var stateText = statePosition is null ? "none" : $"'{statePosition}'";
        if (targetPrimary)
            return (latest.WatermarkAfter,
                $"The target's position ('{latest.WatermarkAfter}', unit {latest.Sequence}) differs from the runner state's ({stateText}): the target was restored or the state was reset. The replication resumes from the target's position.");
        return (statePosition,
            $"The target's position ('{latest.WatermarkAfter}', unit {latest.Sequence}) differs from the runner state's ({stateText}); the state is the primary, so the pass continues from the state's position.");
    }

    /// <summary>The unit of one segment, as the hook writes it.</summary>
    public static UnitDraft Draft(Guid runId, string replication, string mapping, string watermarkKey, string? before, ReadResult read, long rowsStaged) =>
        new(Guid.NewGuid(), replication, mapping, watermarkKey, before, read.WatermarkAfterRead!, read.WatermarkTimeAfterRead, rowsStaged, runId);

    /// <summary>
    /// After the writer returned: a write that carried rows and did not write the state row broke the rule (the unit is on the target without its position), which is a failure naming the
    /// writer. A unit without rows has nothing to be atomic with, so its row is written on its own.
    /// </summary>
    public async Task FinishAsync(DbConnection targetConnection, UnitStateScope.Scope scope, UnitDraft draft, string writerKind, CancellationToken cancellationToken)
    {
        if (scope.Fired > 0) return;
        if (draft.RowsStaged > 0)
            throw new InvalidOperationException($"The writer '{writerKind}' wrote the unit without its state row, so the target holds data whose position it does not record. It must commit through a transaction that calls UnitStateScope.BeforeCommitAsync.");
        await State.WriteAsync(targetConnection, null, draft, cancellationToken);
    }
}
