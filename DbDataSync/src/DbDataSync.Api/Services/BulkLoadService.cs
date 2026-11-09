using DbDataSync.Core.Sql;
using DbDataSync.Api.Models;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Turns a bulk load request into queued work: expand any <see cref="AutoSegment"/> against the real
/// source table, then enqueue one independently-scheduled unit of work per resulting segment.
/// <para>
/// Nothing spawns a process on this request's critical path. Enqueueing is a handful of SQLite writes,
/// which is what makes "queue hundreds of segments at once" a reasonable thing to do — the worker is
/// merely nudged into existence afterwards, and that's a no-op if one is already draining.
/// </para>
/// <para>
/// Also implements <see cref="IInitialLoadEnqueuer"/> — phase 134's runner-triggered path
/// (<see cref="EnqueueForInitialLoadAsync"/>) reuses this same segment-expansion/enqueue core, reached
/// by <c>LocalRunnerState</c> (which lives in <c>DbDataSync.State</c>, and so cannot reference this
/// class directly) through that interface. Registered in <c>DbDataSyncHost</c> as
/// <c>services.AddSingleton&lt;IInitialLoadEnqueuer&gt;(sp => sp.GetRequiredService&lt;BulkLoadService&gt;())</c>.
/// </para>
/// </summary>
public sealed class BulkLoadService(
    ConfigRepository configRepository,
    DriverConnectionFactory connections,
    WorkQueueStore workQueueStore,
    BulkLoadBatchStore batchStore,
    TaskRunStore taskRunStore,
    ProcessSupervisor supervisor,
    CustomSegmentExpansion customSegments,
    DbDataSync.Api.Runner.RunnerDispatch runnerDispatch) : IInitialLoadEnqueuer
{
    private readonly SegmentExpander expander = new(customSegments);

    public async Task<TriggerResult> EnqueueAsync(
        string replicationName, string mappingName, BulkLoadRequest request, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return TriggerResult.NotFound();
        }

        if (request.Segments.Count == 0)
            return TriggerResult.Invalid("At least one segment is required — use a Full segment to reload the whole table.");

        if (mapping.Sources.Count != 1)
            return TriggerResult.Invalid(
                $"Table mapping '{mappingName}' has {mapping.Sources.Count} sources; bulk load supports 1:1 mappings in v1.");

        IReadOnlyList<BatchReloadSegment> segments;
        try
        {
            segments = await ExpandAsync(task, mapping, request.Segments, request.ReaderKind, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException
                                       or MetadataNotCachedException)
        {
            // A bad segment column, an undividable column type, an unreachable source, or (phase 167V:
            // ExpandAutoSegmentsAsync now resolves a segment column's type from the mapping's cache
            // rather than a live catalog call) a segment column the cache doesn't have are all the
            // request being wrong or unrunnable — not a server fault, and worth saying plainly rather
            // than enqueueing work that is guaranteed to fail once a worker claims it.
            return TriggerResult.Invalid(DriverFailures.Describe(ex));
        }

        var kinds = new WorkItemKinds(request.ReaderKind, request.CacheKind, request.WriterKind);

        if (runnerDispatch.RunnerOwns(replicationName))
        {
            // the runner owns the drain: the reload is a request of the run of its drain (plan 28c E12), with its batch as the drain's own record
            var order = SegmentOrderResolution.Resolve(request.SegmentOrder, TargetConnection(task, mapping), task, mapping);
            var (rows, caveat) = await SegmentExpander.EstimateRowsAsync(task, mapping, connections.OpenAsync, cancellationToken);
            var batch = Guid.NewGuid().ToString("N");
            var requests = segments.Select(s => new DrainRequest(
                nameof(RunKind.BulkLoad), mappingName, s.Describe(), SegmentSerializer.Serialize(s), request.ReaderKind, request.CacheKind, request.WriterKind,
                OneAtATime: order == SegmentOrder.OneAtATime, BatchId: batch, BatchSegments: segments.Count, EstimatedRows: rows, EstimateCaveat: caveat)).ToList();
            return await runnerDispatch.SendAsync(replicationName, requests, runAll: false, cancellationToken);
        }

        // One id for the whole reload, minted here and carried onto every segment's run, so the
        // Monitoring screen can add the segments back up.
        var batchId = Guid.NewGuid().ToString("N");
        var runIds = await CreateBatchAndEnqueueAsync(
            task, mapping, replicationName, mappingName, batchId, segments, kinds,
            throwOnCollision: false, request.SegmentOrder, cancellationToken);

        var ensureResult = supervisor.EnsureWorkerRunning(replicationName);
        return ensureResult.Outcome == TriggerOutcome.FailedToStart ? ensureResult : TriggerResult.Started(runIds);
    }

    /// <summary>
    /// Phase 134's runner-triggered path: <c>RunExecutor</c> resolved a <c>Primary</c> pass's intent to
    /// <see cref="ReadIntent.InitialLoad"/> against a reader that can capture its position without
    /// reading a row, and <c>IRunnerState.RequestInitialLoad</c> has already made that captured
    /// position durable (<c>ChangeWatermarks.PendingWatermark</c>) and set the mapping's
    /// <see cref="ReadHold.Loading"/> hold before this is ever called — this only ever creates the work
    /// a Primary pass itself cannot run directly.
    /// <para>
    /// **No operator-provided segment list here** — there is no operator in this path to ask for one.
    /// Segmented exactly like an ordinary scheduled reload of this mapping: its own
    /// <see cref="TableMappingConfig.DefaultSegmenting"/>, empty meaning Full, the same convention
    /// <see cref="ReconcileService.EnqueueScheduledAsync"/> already follows for the same reason.
    /// </para>
    /// <para>
    /// <paramref name="batchId"/> is minted by the caller, not here — the caller (<c>LocalRunnerState</c>)
    /// has to write the pending watermark row under this exact id *before* calling this, so a crash
    /// between the two leaves "held, nothing queued yet" rather than "queued, nothing blocking a
    /// concurrent Primary pass".
    /// </para>
    /// </summary>
    public async Task EnqueueForInitialLoadAsync(
        string replicationName, string mappingName, string batchId, CancellationToken cancellationToken)
    {
        var task = configRepository.LoadReplicationTask(replicationName);
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);

        // Empty means Full — the same convention DefaultSegmenting already has for a standalone
        // reload's own configured segments.
        IReadOnlyList<BatchReloadSegment> requested = mapping.DefaultSegmenting.Count == 0
            ? [new FullSegment()]
            : mapping.DefaultSegmenting;

        var segments = await ExpandAsync(task, mapping, requested, readerKindOverride: null, cancellationToken);

        // Phase 143: unlike the operator-facing path above, this request is not the same thing as
        // whatever else might already be enqueuing this mapping's segments — a losing collision here
        // must not be silently attached to it (see WorkQueueCollisionException's own doc). The caller,
        // LocalRunnerState.RequestInitialLoad, lets this propagate rather than catching it.
        await CreateBatchAndEnqueueAsync(
            task, mapping, replicationName, mappingName, batchId, segments, WorkItemKinds.FromConfig,
            throwOnCollision: true, requestedOrder: null, cancellationToken);

        supervisor.EnsureWorkerRunning(replicationName);
    }

    /// <summary>
    /// Phase 203M: a Primary pass that already resolved its segments (a Custom strategy has run, an Auto
    /// range has been divided) hands them over to run as a batch. Not expanded again here — that would
    /// run the strategy a second time and could select differently. The segments are enqueued with
    /// <c>throwOnCollision: false</c>: the caller has already checked that nothing else of this mapping
    /// is in flight, so a collision here is a race and collapsing into the other request is right.
    /// </summary>
    public async Task<string> EnqueueSegmentsAsync(
        string replicationName, string mappingName, IReadOnlyList<BatchReloadSegment> segments,
        CancellationToken cancellationToken)
    {
        var task = configRepository.LoadReplicationTask(replicationName);
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);

        var batchId = Guid.NewGuid().ToString("N");
        await CreateBatchAndEnqueueAsync(
            task, mapping, replicationName, mappingName, batchId, segments, WorkItemKinds.FromConfig,
            throwOnCollision: false, requestedOrder: null, cancellationToken);

        supervisor.EnsureWorkerRunning(replicationName);
        return batchId;
    }

    /// <summary>
    /// The reusable core behind both enqueue paths above: one catalog-statistics row estimate, one
    /// <c>BulkLoadBatches</c> row recording the planned segment count (phase 107 — deliberately not
    /// <c>COUNT(RunId)</c>, which undercounts when an equivalent segment was already in flight), and one
    /// <c>WorkQueue</c> row per segment.
    /// </summary>
    /// <param name="throwOnCollision">True for the auto-triggered-initial-load path, where a collision
    /// with other in-flight work for the same mapping+segment is a different caller's request, not this
    /// one's own — see <see cref="WorkQueueStore.EnqueueOrThrow"/>. False for the operator-facing path,
    /// where two requests for the same segment really are the same thing and should collapse.</param>
    /// <param name="requestedOrder">Whether the segments take turns or run together, when the request said; otherwise the default from
    /// the mapping, the replication or the target connection (plan 13, M03).</param>
    private async Task<IReadOnlyList<Guid>> CreateBatchAndEnqueueAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, string replicationName, string mappingName,
        string batchId, IReadOnlyList<BatchReloadSegment> segments, WorkItemKinds kinds,
        bool throwOnCollision, SegmentOrder? requestedOrder, CancellationToken cancellationToken)
    {
        var order = SegmentOrderResolution.Resolve(requestedOrder, TargetConnection(task, mapping), task, mapping);
        var (estimatedRows, estimateCaveat) = await SegmentExpander.EstimateRowsAsync(task, mapping, connections.OpenAsync, cancellationToken);
        batchStore.CreateBatch(batchId, replicationName, mappingName, segments.Count, estimatedRows, estimateCaveat);

        // Enqueued one at a time, not via a single LINQ projection, so a later segment's collision can
        // be rolled back against the earlier ones this same call already committed — see the follow-up
        // doc this closes: a multi-segment DefaultSegmenting mapping racing an operator's own reload of
        // the identical scheme used to leave a mix of real and orphaned segments under one batch that
        // could never reach BulkLoadState.Completed, since nothing would ever enqueue the segment that
        // lost. Only relevant when throwOnCollision is true — the operator-facing path's own collisions
        // collapse instead of throwing, so there is nothing here to roll back for it.
        var runIds = new List<Guid>();
        try
        {
            foreach (var segment in segments)
            {
                var runId = throwOnCollision
                    ? workQueueStore.EnqueueOrThrow(
                        replicationName, RunKind.BulkLoad, mappingName, segment.Describe(),
                        SegmentSerializer.Serialize(segment), kinds, batchId, segments: order)
                    : workQueueStore.Enqueue(
                        replicationName, RunKind.BulkLoad, mappingName, segment.Describe(),
                        SegmentSerializer.Serialize(segment), kinds, batchId, segments: order);
                runIds.Add(runId);
            }
        }
        catch (WorkQueueCollisionException)
        {
            // Whatever this call already enqueued is real, durable work that nobody else will ever
            // finish on this batch's behalf — cancelled the same way an operator's own CancelRun does
            // (TryCancelPending, then CompleteRun records the terminal status), not merely left Pending,
            // so nothing here reports Queued forever either. The batch row itself is removed rather than
            // left recording a SegmentCount none of its segments can still reach — matching the single-
            // segment case, which leaves no trace of a losing attempt at all.
            foreach (var runId in runIds)
                if (workQueueStore.TryCancelPending(runId))
                    taskRunStore.CompleteRun(runId, RunStatus.Cancelled, 0, 0,
                        "Cancelled: a later segment in the same initial load lost its WorkQueue race.");
            batchStore.DeleteBatch(batchId);
            throw;
        }

        return runIds;
    }

    /// <summary>The connection the mapping writes to, for the segment-order default it may carry; null when the mapping has no target or the connection cannot be read, in
    /// which case the levels above it decide.</summary>
    private ConnectionConfig? TargetConnection(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        try
        {
            return mapping.Targets.Count == 0
                ? null
                : configRepository.LoadConnection(EndpointResolution.ResolveTarget(task, mapping.Targets[0]).ConnectionName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves <see cref="AutoSegment"/>s and <see cref="CustomSegment"/>s into concrete segments (<see cref="SegmentExpander"/>, shared with a drain on a runner). Done here, once, rather than in the
    /// worker: the bounds come from the source's real MIN/MAX, and each resulting range has to become its own queue row so segments can be scheduled, retried and observed independently.
    /// </summary>
    private Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested,
        string? readerKindOverride, CancellationToken cancellationToken) =>
        expander.ExpandAsync(task, mapping, requested, readerKindOverride, connections.OpenAsync, cancellationToken);
}
