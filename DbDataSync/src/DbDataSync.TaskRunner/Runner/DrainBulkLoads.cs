using DbDataKit.Runner.Kit;
using DbDataSync.Core.Config;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>A bulk load batch a drain keeps for an initial load (plan 28c E11b): what <c>BulkLoadBatchStore</c> keeps for the API's own path, as a record of the run's progress, so the position is promoted once the last segment is done.</summary>
internal sealed record BatchRecord(string Mapping, string SourceKey, int Segments, int Succeeded, int Failed, long RowsRead, long RowsWritten, long? EstimatedRows, string? EstimateCaveat, DateTimeOffset CreatedAt)
{
    public bool Finished => Succeeded + Failed >= Segments;
}

/// <summary>
/// What a drain keeps beside the position, as the run's progress (plan 28c E11b): a position captured for an initial load that is waiting for its batch (the checkpoint's <c>pending_*</c> parts), the batches of
/// initial loads, and the rows each mapping's primary passes have read (what an after-change reconcile strategy reacts to). Every write is durable when the call returns.
/// </summary>
internal interface IDrainLedger
{
    /// <summary>Stores the position captured ahead of an initial load, beside the position the mapping had, and holds the mapping while it loads (<see cref="ReadHold.Loading"/>).</summary>
    void SetPendingLoad(string key, string position, DateTimeOffset? time, string batchId);

    /// <summary>Moves the pending position to the position and clears the pending parts, the intent is <see cref="ReadIntent.Changes"/> and the hold <see cref="ReadHold.None"/>; false when the mapping is not held for loading or has no pending position.</summary>
    bool PromotePendingLoad(string key);

    BatchRecord? ReadBatch(string batchId);

    void WriteBatch(string batchId, BatchRecord batch);

    /// <summary>Adds to the rows the mapping's successful primary passes read.</summary>
    void AddRowsRead(string mapping, long rows);

    /// <summary>The rows read since the mapping's last reconcile sweep was queued (all of them for a mapping never swept).</summary>
    long RowsReadSinceSweep(string mapping);

    /// <summary>A sweep was queued for the mapping: what was read so far is accounted for.</summary>
    void MarkSweep(string mapping);

    /// <summary>Records that the request of the API with this id was applied; false when it was recorded already (a retried run reads its input again).</summary>
    bool TryMarkApplied(string requestId);

    bool WasApplied(string requestId);
}

/// <summary>Where a drain's results that belong to the run go, as the run's recorded output rather than as state: a verification result, the checks the polling gate made.</summary>
internal interface IDrainOutputs
{
    void VerificationResult(VerificationResultRecord result);

    /// <summary>The checks the polling gate made in a cycle (the gate's history, written by the drain; see <see cref="ProtocolPlannerState.TakeChecks"/>).</summary>
    void ChangeChecks(string replication, IReadOnlyList<ChangeCheckEntry> checks);
}

/// <summary>What a drain needs to divide segments and to estimate a table: a connection to the source or the target, which only the engine has (<see cref="DbDataSync.Scripting.SegmentExpander"/> over the engine's way of opening one).</summary>
internal interface IDrainSegments
{
    /// <param name="readerKind">The reader whose buckets divide an automatic segment, or null for the mapping's bulk load reader (a reconcile sweep names <c>KeyReconcile</c>).</param>
    Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested, string? readerKind, CancellationToken cancellationToken);

    Task<(long? Rows, string? Caveat)> EstimateAsync(ReplicationTaskConfig task, TableMappingConfig mapping, CancellationToken cancellationToken);
}

/// <summary>
/// Bulk loads of a drain's own replication on the runner (plan 28c E11b): <c>RequestInitialLoad</c> and <c>RequestSegmentedLoad</c> of <see cref="IRunnerState"/> as the API's <c>BulkLoadService</c> does them, with the
/// runner's queue of the owner in place of Sync's work queue. The segmenting is the same code (<see cref="DbDataSync.Scripting.SegmentExpander"/>); the items are the same rows (<see cref="WorkQueueStore.Describe"/>)
/// with the batch they belong to in the payload; the batch is a record of the run's progress, and the position captured for an initial load is promoted when the last segment of its batch succeeded, as
/// <c>LocalRunnerState.CompleteRun</c> does.
/// </summary>
internal sealed class DrainBulkLoads(ConfigRepository config, IDrainQueue queue, IDrainLedger ledger, IDrainSegments segments, ILogger log)
{
    private static readonly string Kind = nameof(RunKind.BulkLoad);

    private bool InFlight(string mapping) =>
        queue.StateAsync([Kind], CancellationToken.None).GetAwaiter().GetResult().Of(Kind).TryGetValue(mapping, out var known) && known.InFlight;

    /// <summary>
    /// A mapping with no position: enqueue its segments first, and only then make the pending position and the loading hold durable, so a request that loses (the mapping already has a bulk load) leaves the hold as it was.
    /// Segmented as the mapping's own default segmenting says (empty means Full). Throws <see cref="WorkQueueCollisionException"/> when a bulk load of the mapping is in flight.
    /// </summary>
    public void RequestInitialLoad(string taskName, string mappingName, string sourceKey, string capturedPosition, DateTimeOffset? capturedPositionTime)
    {
        if (InFlight(mappingName))
            throw new WorkQueueCollisionException(taskName, RunKind.BulkLoad, mappingName, WorkQueueStore.NoSegment);

        var task = config.LoadReplicationTask(taskName);
        var mapping = config.LoadTableMapping(taskName, mappingName);
        IReadOnlyList<BatchReloadSegment> requested = mapping.DefaultSegmenting.Count == 0 ? [new FullSegment()] : mapping.DefaultSegmenting;
        var expanded = segments.ExpandAsync(task, mapping, requested, null, CancellationToken.None).GetAwaiter().GetResult();
        var batchId = Guid.NewGuid().ToString("N");
        var (rows, caveat) = segments.EstimateAsync(task, mapping, CancellationToken.None).GetAwaiter().GetResult();
        ledger.WriteBatch(batchId, new BatchRecord(mappingName, sourceKey, expanded.Count, 0, 0, 0, 0, rows, caveat, DateTimeOffset.UtcNow));
        Enqueue(task, mapping, batchId, expanded);
        ledger.SetPendingLoad(sourceKey, capturedPosition, capturedPositionTime, batchId);
        log.LogInformation("'{Mapping}' has no position: its initial load is queued as batch {Batch} ({Count} segment(s)); the captured position is promoted when every segment succeeded.", mappingName, batchId, expanded.Count);
    }

    /// <summary>The segments a primary pass resolved (a custom strategy has run, an automatic range has been divided) as a batch of their own; null when a bulk load of the mapping is in flight.</summary>
    public string? RequestSegmentedLoad(string taskName, string mappingName, string segmentsJson)
    {
        if (InFlight(mappingName))
            return null;
        var task = config.LoadReplicationTask(taskName);
        var mapping = config.LoadTableMapping(taskName, mappingName);
        var resolved = SegmentSerializer.DeserializeMany(segmentsJson);
        var batchId = Guid.NewGuid().ToString("N");
        Enqueue(task, mapping, batchId, resolved);
        return batchId;
    }

    private void Enqueue(ReplicationTaskConfig task, TableMappingConfig mapping, string batchId, IReadOnlyList<BatchReloadSegment> items)
    {
        var order = SegmentOrderResolution.Resolve(null, TargetConnection(task, mapping), task, mapping);
        foreach (var segment in items)
        {
            var shape = WorkQueueStore.Describe(task.Name, RunKind.BulkLoad, mapping.Name, segment.Describe(), SegmentSerializer.Serialize(segment), WorkItemKinds.FromConfig, null, order, batchId, items.Count);
            queue.EnqueueAsync(shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private ConnectionConfig? TargetConnection(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        try
        {
            return mapping.Targets.Count == 0 ? null : config.LoadConnection(EndpointResolution.ResolveTarget(task, mapping.Targets[0]).ConnectionName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (bool Succeeded, long RowsRead, long RowsWritten)> _counted = [];

    /// <summary>
    /// A segment's pass ended. The batch counts it (once per run, so a retry of the same item changes its earlier count and does not add one), and when every segment is over and none failed, the position the initial
    /// load captured is promoted. A batch with a failed segment leaves the mapping loading, as the API's path does: an operator forces a fresh attempt through the read-state recovery.
    /// </summary>
    public void SegmentFinished(Guid runId, string batchId, bool succeeded, long rowsRead, long rowsWritten)
    {
        lock (_gate)
        {
            if (ledger.ReadBatch(batchId) is not { } batch)
                return; // a segmented load of a pass has no record: nothing waits on it
            var (succeededBefore, readBefore, writtenBefore) = _counted.TryGetValue(runId, out var before) ? before : (false, 0L, 0L);
            var known = _counted.ContainsKey(runId);
            var updated = batch with
            {
                Succeeded = batch.Succeeded + (succeeded ? 1 : 0) - (known && succeededBefore ? 1 : 0),
                Failed = batch.Failed + (succeeded ? 0 : 1) - (known && !succeededBefore ? 1 : 0),
                RowsRead = batch.RowsRead + rowsRead - readBefore,
                RowsWritten = batch.RowsWritten + rowsWritten - writtenBefore,
            };
            _counted[runId] = (succeeded, rowsRead, rowsWritten);
            ledger.WriteBatch(batchId, updated);
            if (updated.Finished && updated.Failed == 0 && ledger.PromotePendingLoad(updated.SourceKey))
                log.LogInformation("Batch {Batch} of '{Mapping}' is complete: its captured position is the mapping's position now.", batchId, updated.Mapping);
            else if (updated.Finished && updated.Failed > 0)
                log.LogWarning("Batch {Batch} of '{Mapping}' ended with {Failed} failed segment(s): the mapping stays loading until an operator forces a fresh attempt.", batchId, updated.Mapping, updated.Failed);
        }
    }
}
