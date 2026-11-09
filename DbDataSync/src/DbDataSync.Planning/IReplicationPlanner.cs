using DbDataSync.Core.Config;

namespace DbDataSync.Planning;

/// <summary>How a scheduled reconcile sweep was (or was not) queued.</summary>
public enum ScheduledReconcileOutcome
{
    /// <summary>Work was queued.</summary>
    Queued,

    /// <summary>The mapping or its replication is gone.</summary>
    NotFound,

    /// <summary>The sweep cannot be queued as configured (not a 1:1 mapping, segments that cannot be divided); the reason says why.</summary>
    Invalid,

    /// <summary>Work was queued, but starting the thing that runs it failed (the API's worker); the reason says why. The queue holds the work.</summary>
    QueuedNotStarted,
}

/// <summary>The answer of <see cref="IScheduledReconcileEnqueuer"/>.</summary>
public sealed record ScheduledReconcileResult(ScheduledReconcileOutcome Outcome, string? Reason = null)
{
    public static ScheduledReconcileResult Queued { get; } = new(ScheduledReconcileOutcome.Queued);
}

/// <summary>
/// Queues phase 125's scheduled reconcile sweep for one mapping, segmented as the mapping's own default segmenting says (segments that need the source or the target to be divided are expanded by whoever has a connection).
/// The API's <c>ReconcileService</c> is one implementation; the engine of a drain on a runner has its own.
/// </summary>
public interface IScheduledReconcileEnqueuer
{
    Task<ScheduledReconcileResult> EnqueueScheduledAsync(string replication, string mapping, CancellationToken cancellationToken);
}

/// <summary>
/// What a tick of a replication decides: which of its mappings are due for a scheduled pass, which are due for a delete-reconcile sweep, and queues them (plan 28c E11; <c>SchedulerService</c>'s ticks, extracted). Called
/// by the API's scheduler (every five seconds, for every replication, then it starts the worker) and by the engine of a drain on a runner (at the start of each cycle, for the replication it drains), so both
/// decide the same way.
/// </summary>
public interface IReplicationPlanner
{
    /// <summary>
    /// A continuous replication's due-ness is per mapping: each mapping's own last queue time against the schedule's interval, then the held mappings and the quiet sources are left out (see <see cref="ChangePollingGate"/>).
    /// True when anything was queued.
    /// </summary>
    Task<bool> TickContinuousAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken);

    /// <summary>A periodic replication's due-ness is the replication's: one cron occurrence queues every mapping, measured from the newest of the mappings' last queue times. True when anything was queued.</summary>
    Task<bool> TickPeriodicAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken);

    /// <summary>Every mapping's delete-reconciliation: its own cadence or after-change trigger, independent of the replication's schedule mode. True when anything was queued.</summary>
    Task<bool> TickReconcileAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken);

    /// <summary>The tick of the replication's own mode, then the reconcile tick: what one cycle is. True when anything was queued by either.</summary>
    Task<bool> PlanAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken);
}
