namespace DbDataSync.State;

/// <summary>
/// Prevents overlapping runs of the same (task, run kind, table mapping) — architecture/detailed-design.md
/// §3.1, §3.7, and architecture/implementation/done/phase-008-work-queue-schema.md for why this is scoped per
/// mapping rather than per replication: a Primary pass is now "this mapping's next incremental pass,"
/// not "this replication's next incremental pass over every mapping," so one slow mapping no longer
/// blocks every other mapping's schedule.
/// </summary>
public sealed class RunLockStore(StateDatabase database)
{
    // The lock itself is the shared one (plan 13, M03); Sync's names map onto it as the work queue's do: the replication owns it, the kind is the run kind, the mapping is the subject.
    private readonly DbDataKit.Scheduling.RunLockStore _locks = new(database, WorkQueueStore.Service);

    public bool TryAcquire(string taskName, RunKind runKind, string mappingName, Guid runId) =>
        _locks.TryAcquire(taskName, runKind.ToString(), mappingName, runId);

    public void Release(string taskName, RunKind runKind, string mappingName) =>
        _locks.Release(taskName, runKind.ToString(), mappingName);

    public bool IsLocked(string taskName, RunKind runKind, string mappingName) =>
        _locks.IsLocked(taskName, runKind.ToString(), mappingName);
}
