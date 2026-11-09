using System.Data.Common;
using System.Text.Json;
using DbDataSync.Core.Config;

namespace DbDataSync.State;

public enum WorkItemStatus
{
    Pending,
    Claimed,
    Running,
    Done,
    Failed,
    Cancelled,
}

public sealed record WorkItem(
    long Id,
    string TaskName,
    RunKind RunKind,
    string MappingName,
    string SegmentLabel,
    string? SegmentJson,
    Guid RunId,
    WorkItemStatus Status,
    WorkItemKinds Kinds,
    /// <summary>Phase 124: a serialized <c>DeleteGuard</c> override for this
    /// <see cref="RunKind.ReconcileDeletes"/> item — null for every other kind, and null for a
    /// reconcile item that didn't override the writer's configured/default guard.</summary>
    string? DeleteGuardJson = null,
    /// <summary>Plan 28c E11b: the bulk-load batch a <see cref="RunKind.BulkLoad"/> item belongs to and how many segments the batch has, for a queue that has no run record to carry them (the runner's): null for any
    /// other item, and for an item of Sync's own queue, whose run row names its batch.</summary>
    string? BulkLoadBatchId = null,
    int? BatchSegments = null);

/// <summary>
/// Which reader/cache/writer this unit of work should use, when that isn't simply the replication's
/// configured pipeline. Every component is null for a Primary pass — an incremental replication runs
/// the pipeline it was configured with. A BulkLoad sets them, because reloading a segment needs a
/// reload reader and (usually) a reconciling writer regardless of what the replication does for its
/// ongoing sync.
/// </summary>
public sealed record WorkItemKinds(string? ReaderKind = null, string? CacheKind = null, string? WriterKind = null)
{
    /// <summary>Use the replication's own configured pipeline.</summary>
    public static WorkItemKinds FromConfig { get; } = new();
}

/// <summary>
/// Sync's work queue (architecture/implementation/done/phase-008-work-queue-schema.md): an adapter over the shared queue of
/// <c>DbDataKit.Scheduling</c>, its module (plan 13, M03; plan 28c, E14), that keeps the names Sync has always used. A replication is the owner, the run kind is the kind, a mapping is the subject
/// and a segment is the part; what only Sync's worker needs (the segment's definition, the reader, cache and writer kinds, the delete guard) travels in the payload. The API
/// process (handling bulk load triggers and scheduled due-ness) and the TaskRunner worker process(es) it spawns still communicate only through this table.
/// <para>
/// Sync's run record (<c>runs.run</c> and <c>sync_state.run_detail</c>) is written in the same transaction as the enqueue, and its claim time in the same transaction as the claim, so a
/// queued backlog is visible in run history at once and a run is never claimed in one table and not the other.
/// </para>
/// </summary>
public sealed class WorkQueueStore(StateDatabase database)
{
    /// <summary>The service name Sync's work carries in the shared queue.</summary>
    public const string Service = "dbdatasync";

    /// <summary>Sentinel for WorkQueue.SegmentLabel when a unit of work has no segment (every Primary
    /// item, and a BulkLoad's single Full-mode segment) — NOT NULL because this column participates in
    /// a uniqueness constraint, where SQL's every-NULL-is-distinct rule would silently defeat it.</summary>
    public const string NoSegment = DbDataKit.Scheduling.ModuleWorkQueueStore.NoPart;

    private readonly DbDataKit.Scheduling.ModuleWorkQueueStore _queue = new(database, Service);

    /// <summary>One unit of Sync work as the shared queue holds it: the keys, the resource that makes segments take turns, the lane, and the payload.</summary>
    public sealed record QueueShape(string Owner, string Kind, string Subject, string Part, string Resource, string Lane, string Payload);

    /// <summary>
    /// How a unit of work is written into the shared queue (the owner is the replication, the kind the run kind, the subject the mapping, the part the segment; what only the worker needs travels in the payload).
    /// Public so that a queue that is not Sync's own state database (the runner's, plan 28c E11) holds exactly the rows this one does: the same keys, the same lane, the same payload.
    /// </summary>
    public static QueueShape Describe(
        string taskName, RunKind runKind, string mappingName, string segmentLabel = NoSegment, string? segmentJson = null, WorkItemKinds? kinds = null,
        string? deleteGuardJson = null, SegmentOrder segments = SegmentOrder.OneAtATime, string? bulkLoadBatchId = null, int? batchSegments = null)
    {
        var payload = JsonSerializer.Serialize(new Payload(segmentJson, kinds?.ReaderKind, kinds?.CacheKind, kinds?.WriterKind, deleteGuardJson, bulkLoadBatchId, batchSegments));
        // Segments of one kind on one mapping take turns by sharing a resource: the queue lets a different part run beside another, so the turn-taking has to be asked for.
        var resource = segments == SegmentOrder.OneAtATime && segmentLabel != NoSegment ? $"{taskName}/{mappingName}/{runKind}" : "";
        return new QueueShape(taskName, runKind.ToString(), mappingName, segmentLabel, resource, RunLanes.LaneFor(runKind).ToString(), payload);
    }

    /// <summary>What the shared queue holds for Sync besides the keys: everything the worker needs that the queue does not look at.</summary>
    private sealed record Payload(string? SegmentJson, string? ReaderKind, string? CacheKind, string? WriterKind, string? DeleteGuardJson, string? BatchId = null, int? BatchSegments = null);

    /// <summary>
    /// Enqueues one unit of work and its corresponding Queued TaskRuns row in one transaction, so a
    /// queued backlog is visible in run history immediately, not only once a worker claims it. A
    /// no-op (no new row, existing RunId returned) if an equivalent item is already
    /// Pending/Claimed/Running for this (task, kind, mapping, segment).
    /// <para>
    /// This silent-collapse behaviour is correct when two callers' requests really are the same thing —
    /// two identical reload clicks should collapse into one real reload, not run it twice. See
    /// <see cref="EnqueueOrThrow"/> for the callers where that isn't true.
    /// </para>
    /// <para>
    /// <paramref name="segments"/> says whether this mapping's segments of the same kind take turns or may run together (plan 13, M03); it
    /// matters only for work that has a segment.
    /// </para>
    /// </summary>
    public Guid Enqueue(
        string taskName,
        RunKind runKind,
        string mappingName,
        string segmentLabel = NoSegment,
        string? segmentJson = null,
        WorkItemKinds? kinds = null,
        string? bulkLoadBatchId = null,
        // Phase 124: a serialized DeleteGuard override — see WorkItem.DeleteGuardJson.
        string? deleteGuardJson = null,
        SegmentOrder segments = SegmentOrder.OneAtATime) =>
        EnqueueCore(
            taskName, runKind, mappingName, segmentLabel, segmentJson, kinds, bulkLoadBatchId,
            deleteGuardJson, segments, throwOnCollision: false);

    /// <summary>
    /// Like <see cref="Enqueue"/>, except a collision with other in-flight work for this exact (task,
    /// kind, mapping, segment) throws <see cref="WorkQueueCollisionException"/> instead of silently
    /// returning the existing item's RunId.
    /// <para>
    /// For a caller whose request is genuinely independent of whoever else might be enqueuing the same
    /// segment — currently only <c>LocalRunnerState.RequestInitialLoad</c>'s auto-triggered initial
    /// load, which must not be silently attached to an unrelated, differently-scoped, differently-timed
    /// reload that happens to be racing it (phase 143). Every other caller keeps using
    /// <see cref="Enqueue"/>.
    /// </para>
    /// </summary>
    public Guid EnqueueOrThrow(
        string taskName,
        RunKind runKind,
        string mappingName,
        string segmentLabel = NoSegment,
        string? segmentJson = null,
        WorkItemKinds? kinds = null,
        string? bulkLoadBatchId = null,
        string? deleteGuardJson = null,
        SegmentOrder segments = SegmentOrder.OneAtATime) =>
        EnqueueCore(
            taskName, runKind, mappingName, segmentLabel, segmentJson, kinds, bulkLoadBatchId,
            deleteGuardJson, segments, throwOnCollision: true);

    private Guid EnqueueCore(
        string taskName,
        RunKind runKind,
        string mappingName,
        string segmentLabel,
        string? segmentJson,
        WorkItemKinds? kinds,
        string? bulkLoadBatchId,
        string? deleteGuardJson,
        SegmentOrder segments,
        bool throwOnCollision)
    {
        var shape = Describe(taskName, runKind, mappingName, segmentLabel, segmentJson, kinds, deleteGuardJson, segments);
        var payload = shape.Payload;
        var resource = shape.Resource;
        var lane = shape.Lane;

        void WriteRun(DbConnection connection, DbTransaction transaction, Guid runId) =>
            // EnqueuedAt, and nothing else: a queued run has not been claimed and has not started, so claimed_at and began_at stay null until the moments they name actually happen (phase 73).
            SyncRunRows.Insert(database, connection, transaction, runId, taskName, runKind, mappingName, segmentLabel == NoSegment ? null : segmentLabel, bulkLoadBatchId, database.Time.GetUtcNow());

        try
        {
            return throwOnCollision
                ? _queue.EnqueueOrThrow(taskName, runKind.ToString(), mappingName, segmentLabel, resource, lane, payload, alsoWrite: WriteRun)
                : _queue.Enqueue(taskName, runKind.ToString(), mappingName, segmentLabel, resource, lane, payload, alsoWrite: WriteRun);
        }
        catch (DbDataKit.Scheduling.WorkQueueCollisionException)
        {
            throw new WorkQueueCollisionException(taskName, runKind, mappingName, segmentLabel);
        }
    }

    /// <summary>Claims the next available Pending item for a task, if any. The queue skips an item
    /// that conflicts with one already Claimed or Running (the same kind on the same mapping, whatever
    /// the segment, unless the segments were enqueued to run together), so a claim never has to be given back
    /// for losing a RunLocks race in the common case (see RunLockStore — this is a fast pre-filter, not
    /// a substitute for it).
    /// <para>
    /// The claim itself writes two rows in one transaction: the queue item, and <c>ClaimedAtUtc</c> on
    /// the run it belongs to (phase 73). This is the genuine claim moment — <c>TaskRunStore.BeginRun</c>
    /// happens later, once the worker is actually starting the work, and writes <c>StartedAtUtc</c>.
    /// </para></summary>
    /// <param name="lane">Restrict the claim to one lane's <see cref="RunKind"/>s. Null claims from
    /// any lane — never what the worker wants (it drives each lane's channel from its own claim loop),
    /// but what a reconciliation check or a test that is not about the split means.</param>
    /// <param name="runId">Test-only: claim a specific run rather than whatever is next for the task.
    /// Production never passes this — a worker wants the queue's own priority order, not one run it
    /// already knows about. It exists so a test can state "the row I just enqueued" as an intent instead
    /// of inferring it from being the only thing in the queue, which silently stops being true the moment
    /// anything else (a scheduler tick, another test) enqueues a competing row for the same task. See
    /// architecture/planning/todo/follow-up-runwatermarktimetests-claims-the-wrong-row-again-via-the-real-scheduler.md.</param>
    public WorkItem? TryClaimNext(string taskName, string workerId, RunLane? lane = null, Guid? runId = null)
    {
        var claimed = _queue.TryClaimNext(
            taskName, workerId, lane?.ToString(), runId: runId,
            alsoWrite: (connection, transaction, item, at) =>
            {
                // In the same transaction as the claim, and that is the point of the transaction (phase 73): a run must never be Claimed in the queue with no claim time in its run detail, which is exactly
                // what two independent statements would allow on a crash between them. One claim moment, written to both from one variable.
                using var cmd = database.Command(SyncStateModule.Name, connection, transaction, "UPDATE {table:run_detail} SET claimed_at = $now WHERE run_id = $run;");
                cmd.Bind(database, "now", at).Bind(database, "run", item.RunId);
                cmd.ExecuteNonQuery();
            });
        return claimed is null ? null : ToSyncItem(claimed);
    }

    public void MarkRunning(long id) => _queue.MarkRunning(id);

    public void MarkDone(long id) => _queue.MarkDone(id);

    /// <summary>Gives up a claim without executing it — used when a claimed item then loses the real
    /// RunLocks race (rare: the queue's own pre-filter already screens out the common case).
    /// Back to Pending, not Failed — this wasn't an execution failure, just a lost race to retry.</summary>
    public void ReleaseClaim(long id) => _queue.ReleaseClaim(id);

    public void MarkFailed(long id) => _queue.MarkFailed(id);

    /// <summary>Replications holding Claimed or Running items. What reconciliation needs to ask, because
    /// an item claimed by a worker that died before starting it has no run to be found by.</summary>
    public IReadOnlyList<string> GetTasksWithInFlightWork() => _queue.OwnersWithHeldWork();

    /// <summary>
    /// Returns every in-flight item of a replication to the queue. For when the worker holding them
    /// died without saying anything — a kill, a crash, an API restart that found no live process.
    /// <para>
    /// Pending rather than Failed, for the same reason <see cref="ReleaseClaim"/> is: nobody observed
    /// an execution failure, only that the process holding the work is gone. And it has to happen at
    /// all, because an item left Claimed or Running makes its mapping permanently un-enqueueable, so
    /// the replication silently stops rather than failing.
    /// </para>
    /// <para>
    /// Per replication rather than per run, because a worker claims ahead of its consumers and can die
    /// holding items it never started, which have no started run to be found by. Only correct once the
    /// caller knows the worker is gone — a live one legitimately holds unstarted claims.
    /// </para>
    /// </summary>
    public int ReleaseClaimsForTask(string taskName) => _queue.ReleaseClaimsForOwner(taskName);

    public int ReleaseClaimsForRun(Guid runId) => _queue.ReleaseClaimsForRun(runId);

    /// <summary>Cancels a not-yet-claimed item directly — no process interaction needed. An
    /// already-Claimed/Running item can't be cancelled this way in v1 (see ProcessSupervisor.CancelRun's
    /// fallback to killing the whole worker process — an accepted, documented v1 limitation).</summary>
    public bool TryCancelPending(Guid runId) => _queue.TryCancelPending(runId);

    /// <summary>Asks the worker holding a Claimed/Running item to stop it: marks the row, and the worker
    /// (which polls <see cref="IsCancelRequested"/>) cancels that one item. False when there is no such
    /// in-flight item — it finished, was never claimed (use <see cref="TryCancelPending"/>), or doesn't
    /// exist.</summary>
    public bool RequestCancel(Guid runId) => _queue.RequestCancel(runId);

    /// <summary>True once an operator has asked for this run's item to stop.</summary>
    public bool IsCancelRequested(Guid runId) => _queue.IsCancelRequested(runId);

    /// <summary>True if any Pending/Claimed/Running item remains for this task. With
    /// <paramref name="lane"/> set, only that lane's <see cref="RunKind"/>s count — a worker's
    /// per-lane drain loop exits once its lane is empty, and never waits on the other lane's work.
    /// Null means "anything at all", which is what reconciliation asks.</summary>
    public bool HasOutstandingWork(string taskName, RunLane? lane = null) => _queue.HasOutstandingWork(taskName, lane?.ToString());

    /// <summary>Whether this mapping already has a <see cref="RunKind.ReconcileDeletes"/> item
    /// in flight — phase 125's scheduler dedup, mapping-scoped rather than lane-scoped like
    /// <see cref="HasOutstandingWork"/> (which cannot distinguish "some other mapping's bulk load is
    /// running" from "this mapping's own sweep already is").</summary>
    public bool HasPendingReconcile(string taskName, string mappingName) =>
        _queue.HasInFlight(taskName, RunKind.ReconcileDeletes.ToString(), mappingName);

    /// <summary>Whether this mapping already has a <see cref="RunKind.BulkLoad"/> item in flight — what
    /// a segmented Primary pass checks before dispatching its own batch, so a slow batch is not stacked
    /// on by the next scheduled tick.</summary>
    public bool HasInFlightBulkLoad(string taskName, string mappingName) =>
        _queue.HasInFlight(taskName, RunKind.BulkLoad.ToString(), mappingName);

    private static WorkItem ToSyncItem(DbDataKit.Scheduling.WorkItem item) => FromQueue(item.Id, item.Owner, item.Kind, item.Subject, item.Part, item.RunId, item.Status.ToString(), item.PayloadJson);

    /// <summary>Sync's item from the fields of a row of a shared queue (the runner's claim hands them out as strings, plan 28c E11).</summary>
    public static WorkItem FromQueue(long id, string owner, string kind, string subject, string part, Guid runId, string status, string? payloadJson)
    {
        var payload = payloadJson is null ? null : JsonSerializer.Deserialize<Payload>(payloadJson);
        return new WorkItem(
            id,
            owner,
            Enum.Parse<RunKind>(kind),
            subject,
            part,
            payload?.SegmentJson,
            runId,
            Enum.Parse<WorkItemStatus>(status),
            new WorkItemKinds(payload?.ReaderKind, payload?.CacheKind, payload?.WriterKind),
            payload?.DeleteGuardJson,
            payload?.BatchId,
            payload?.BatchSegments);
    }
}
