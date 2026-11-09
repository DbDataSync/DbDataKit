using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.Planning;

/// <summary>
/// What the planner reads and writes: when each mapping was last queued, what is in flight, a mapping's stored read state (its hold), and the queue it puts due work into. The planner decides; this is where its
/// decisions and its inputs live. Two implementations: Sync's own state database (<see cref="LocalPlannerState"/>, the API's scheduler tick) and the runner's queue of the drain's own owner (the engine's
/// <c>ProtocolPlannerState</c>, plan 28c E11).
/// </summary>
public interface IPlannerState
{
    /// <summary>When each mapping of the replication was last queued for a scheduled <see cref="RunKind.Primary"/> pass.</summary>
    IReadOnlyDictionary<string, DateTimeOffset> GetLastPrimaryEnqueueByMapping(string replication);

    /// <summary>When each mapping was last queued for a scheduled <see cref="RunKind.ReconcileDeletes"/> sweep (phase 125's cadence).</summary>
    IReadOnlyDictionary<string, DateTimeOffset> GetLastReconcileEnqueueByMapping(string replication);

    /// <summary>The rows each mapping's successful Primary passes read since its own cutoff: the input of an after-change reconcile strategy. A state that cannot say throws <see cref="NotSupportedException"/> naming this member.</summary>
    IReadOnlyDictionary<string, long> GetRowsReadSincePerMapping(string replication, IReadOnlyDictionary<string, DateTimeOffset> sinceByMapping);

    /// <summary>Whether the mapping already has a reconcile sweep in flight (no second one is queued behind it).</summary>
    bool HasPendingReconcile(string replication, string mapping);

    /// <summary>The intent, hold and position stored for one mapping's source, or null when it has none.</summary>
    MappingReadState? GetReadState(string replication, string mapping, string watermarkKey);

    /// <summary>Queues one scheduled Primary pass for the mapping (an equal item already in flight makes this a no-op).</summary>
    void EnqueuePrimary(string replication, string mapping);
}

/// <summary>The planner over Sync's own state database: the stores the API's scheduler has always used.</summary>
public sealed class LocalPlannerState(TaskRunStore taskRuns, WorkQueueStore workQueue, ChangeWatermarkStore watermarks) : IPlannerState
{
    public IReadOnlyDictionary<string, DateTimeOffset> GetLastPrimaryEnqueueByMapping(string replication) => taskRuns.GetLastPrimaryEnqueueByMapping(replication);

    public IReadOnlyDictionary<string, DateTimeOffset> GetLastReconcileEnqueueByMapping(string replication) => taskRuns.GetLastReconcileEnqueueByMapping(replication);

    public IReadOnlyDictionary<string, long> GetRowsReadSincePerMapping(string replication, IReadOnlyDictionary<string, DateTimeOffset> sinceByMapping) =>
        taskRuns.GetRowsReadSincePerMapping(replication, sinceByMapping);

    public bool HasPendingReconcile(string replication, string mapping) => workQueue.HasPendingReconcile(replication, mapping);

    public MappingReadState? GetReadState(string replication, string mapping, string watermarkKey) => watermarks.GetReadState(replication, mapping, watermarkKey);

    public void EnqueuePrimary(string replication, string mapping) => workQueue.Enqueue(replication, RunKind.Primary, mapping);
}
