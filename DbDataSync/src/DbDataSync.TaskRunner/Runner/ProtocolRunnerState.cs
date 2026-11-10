using System.Collections.Concurrent;
using DbDataSync.Core.Config;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>Where a drain keeps the one position a mapping has (the checkpoint <c>watermark</c> of plan 28c section 5.1, reduced to <c>position</c> and <c>position_time</c>); the drain job is the implementation, over the run's progress.</summary>
internal interface IDrainCheckpoints
{
    /// <summary>The position stored for the key (the mapping's watermark key), or null when none was ever stored.</summary>
    (string Position, DateTimeOffset? Time)? Read(string key);

    /// <summary>Makes the position durable now (the checkpoint moves in the protocol call that saves it).</summary>
    void Write(string key, string position, DateTimeOffset? time);

    /// <summary>The intent and the hold stored beside the position (the checkpoint's parts <c>intent</c> and <c>hold</c>); null for a part that was never set, which means its default (<see cref="ReadIntent.Changes"/>, <see cref="ReadHold.None"/>).</summary>
    (ReadIntent? Intent, ReadHold? Hold) ReadIntentAndHold(string key);

    /// <summary>Stores the intent beside the position and makes it durable now.</summary>
    void WriteIntent(string key, ReadIntent intent);

    /// <summary>Stores the hold beside the position and makes it durable now.</summary>
    void WriteHold(string key, ReadHold hold);
}

/// <summary>How one pass ended, as the engine reported it through <see cref="IRunnerState.CompleteRun"/>.</summary>
internal sealed record PassOutcome(Guid RunId, RunStatus Status, long RowsRead, long RowsWritten, string? Summary, string? FailureKind, string? Detail);

/// <summary>
/// <see cref="IRunnerState"/> over the runner's protocol (plan 28c E03 and E11): the queue of the drain's own owner for claims, marks, releases and the cancel poll (the protocol's <c>ClaimWork</c>, <c>StartWork</c>,
/// <c>FinishWork</c>, <c>ReleaseWork</c> and <c>IsCancelRequested</c>, bound to the run), the position, the intent and the hold as the run's checkpoint, the pass outcome as what the drain reports, and the run's log as the run's
/// log. The run lock is the runner's (it keeps two drains of one replication apart); a mapping's lock is held here, in memory, and keeps two passes of one mapping apart within the drain. Nothing here carries rows (ED5):
/// what crosses the socket is claims, positions, counts, outcomes and log lines.
/// <para>
/// Bulk loads (<see cref="RequestInitialLoad"/>, <see cref="RequestSegmentedLoad"/>) are <see cref="DrainBulkLoads"/>'s, over the same queue, and a verification result is a recorded output of the run
/// (<see cref="IDrainOutputs"/>). A state made without them (a test of something else) throws <see cref="NotSupportedException"/> naming the member and what is missing, and does not do something a little different.
/// </para>
/// </summary>
internal sealed class ProtocolRunnerState(IDrainCheckpoints checkpoints, ILogger log, IDrainQueue? queue = null, DrainBulkLoads? bulkLoads = null, IDrainOutputs? outputs = null) : IRunnerState
{
    private readonly ConcurrentDictionary<Guid, string> _batchOfRun = new();
    private readonly ConcurrentDictionary<(RunKind Kind, string Mapping), Guid> _locks = new();
    private readonly ConcurrentDictionary<Guid, PassOutcome> _outcomes = new();
    private readonly ConcurrentDictionary<long, WorkItemStatus> _items = new();
    private readonly ConcurrentDictionary<long, string> _mappingsOfItems = new();

    /// <summary>The outcome the pass with this run id reported, once; null when it reported none (it did not run to its end).</summary>
    public PassOutcome? TakeOutcome(Guid runId) => _outcomes.TryRemove(runId, out var outcome) ? outcome : null;

    /// <summary>Whether the item was given back to the queue unrun (it lost its mapping's lock): that is not a failure of the pass.</summary>
    public bool WasReleased(long workItemId) => _items.TryGetValue(workItemId, out var status) && status == WorkItemStatus.Pending;

    private IDrainQueue Queue => queue ?? throw new NotSupportedException(
        "IRunnerState's queue calls (TryClaimNext, HasOutstandingWork, IsCancelRequested) are not supported by a state made without the runner's queue; the drain gives its state one.");

    private static NotSupportedException Unsupported(string member, string why) =>
        new($"IRunnerState.{member} is not supported by a drain on the runner yet: {why}");

    // ---- Prerequisites

    /// <summary>A replication is a definition on the runner; whether it runs is its schedule and pause, not a row.</summary>
    public void UpsertTask(string taskName, bool enabled)
    {
    }

    /// <summary>Whether anything of the owner is pending, claimed or running. The queue does not split by lane here: a drain runs every kind through one pool.</summary>
    public bool HasOutstandingWork(string taskName, RunLane lane) =>
        Queue.StateAsync([], CancellationToken.None).GetAwaiter().GetResult().Outstanding;

    /// <summary>An operator asked for this item to stop (the queue's cancel mark on its row), or the runner asked the whole run to (then no pass is claimed: the run's token has fired).</summary>
    public bool IsCancelRequested(Guid runId) => Queue.IsCancelRequestedAsync(runId, CancellationToken.None).GetAwaiter().GetResult();

    public WorkItem? TryClaimNext(string taskName, string workerId, RunLane lane)
    {
        var item = Queue.ClaimAsync(lane.ToString(), CancellationToken.None).GetAwaiter().GetResult();
        return item is null ? null : Claimed(taskName, item);
    }

    /// <summary>A claimed item as Sync's own: the replication is the drain's, the kinds and the segment come from the payload the planner wrote.</summary>
    public WorkItem Claimed(string taskName, DbDataKit.Runner.Kit.RunnerWorkItem item)
    {
        var claimed = WorkQueueStore.FromQueue(item.Id, taskName, item.Kind, item.Subject, item.Part, item.RunId, nameof(WorkItemStatus.Claimed), item.Payload);
        _items[item.Id] = WorkItemStatus.Claimed;
        _mappingsOfItems[item.Id] = item.Subject;
        if (claimed.BulkLoadBatchId is { } batch)
            _batchOfRun[item.RunId] = batch;
        return claimed;
    }

    /// <summary>The runner's run lock keeps two drains of one replication apart; this keeps two passes of one mapping apart within the drain.</summary>
    public bool TryAcquireLock(string taskName, RunKind runKind, string mappingName, Guid runId) => _locks.TryAdd((runKind, mappingName), runId);

    public string? GetWatermark(string taskName, string mappingName, string sourceTable) => checkpoints.Read(WatermarkCheckpoint.ItemKey(mappingName, sourceTable))?.Position;

    public MappingReadState? GetReadState(string taskName, string mappingName, string sourceTable) => ProtocolPlannerState.ReadState(checkpoints, WatermarkCheckpoint.ItemKey(mappingName, sourceTable));

    public void BeginRun(Guid runId, int? pid)
    {
    }

    /// <summary>Names the one segment a pass reads inline: cosmetic (a line of the run's log says it, with the pass's run id).</summary>
    public void SetRunSegment(Guid runId, string label) => log.LogInformation("Run {RunId} reads segment {Segment}.", runId, label);

    /// <summary>A mapping with no position: its initial load is a batch of this replication's own queue and the captured position waits for it (<see cref="DrainBulkLoads.RequestInitialLoad"/>).</summary>
    public void RequestInitialLoad(string taskName, string mappingName, string sourceTable, string capturedPosition, DateTimeOffset? capturedPositionTimeUtc) =>
        (bulkLoads ?? throw Unsupported(nameof(RequestInitialLoad), $"'{mappingName}' has no position yet and would start with an initial load, which needs the drain's bulk loads (a state made without them)."))
            .RequestInitialLoad(taskName, mappingName, sourceTable, capturedPosition, capturedPositionTimeUtc);

    public string? RequestSegmentedLoad(string taskName, string mappingName, string segmentsJson) =>
        (bulkLoads ?? throw Unsupported(nameof(RequestSegmentedLoad), $"'{mappingName}' resolved to segments, which run as a bulk load batch of the drain's queue (a state made without bulk loads)."))
            .RequestSegmentedLoad(taskName, mappingName, segmentsJson);

    // ---- Outcomes

    /// <summary>The pass begins. A claim the run no longer holds (its lease ran out) is not run: <see cref="WorkClaimLostException"/>.</summary>
    public void MarkRunning(long workItemId)
    {
        if (queue is not null && !queue.StartAsync(workItemId, CancellationToken.None).GetAwaiter().GetResult())
            throw new WorkClaimLostException(workItemId, _mappingsOfItems.GetValueOrDefault(workItemId, "?"));
        _items[workItemId] = WorkItemStatus.Running;
    }

    public void MarkDone(long workItemId) => Finish(workItemId, DbDataKit.Runner.Kit.WorkFinish.Done, WorkItemStatus.Done);

    public void MarkFailed(long workItemId) => Finish(workItemId, DbDataKit.Runner.Kit.WorkFinish.Failed, WorkItemStatus.Failed);

    private void Finish(long id, DbDataKit.Runner.Kit.WorkFinish outcome, WorkItemStatus status)
    {
        _items[id] = status;
        queue?.FinishAsync(id, outcome, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void ReleaseClaim(long workItemId)
    {
        _items[workItemId] = WorkItemStatus.Pending;
        queue?.ReleaseAsync(workItemId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void ReleaseLock(string taskName, RunKind runKind, string mappingName) => _locks.TryRemove((runKind, mappingName), out _);

    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null, RunTiming? timing = null, string? previousWatermark = null, string? newWatermark = null, string? errorDetail = null)
    {
        _outcomes[runId] = new PassOutcome(runId, status, rowsRead, rowsWritten, errorSummary, failureKind, errorDetail);
        if (_batchOfRun.TryGetValue(runId, out var batchId))
            bulkLoads?.SegmentFinished(runId, batchId, status == RunStatus.Succeeded, rowsRead, rowsWritten);
        if (newWatermark is not null) log.LogInformation("Position moved from {Previous} to {New}.", previousWatermark ?? "<none>", newWatermark);
    }

    /// <summary>Only ever called after the target write has committed, so the checkpoint moves only then.</summary>
    public void SetWatermark(string taskName, string mappingName, string sourceTable, string watermark, DateTimeOffset? watermarkTimeUtc = null) =>
        checkpoints.Write(WatermarkCheckpoint.ItemKey(mappingName, sourceTable), watermark, watermarkTimeUtc);

    /// <summary>What the next pass is meant to do, beside the position (the checkpoint's <c>intent</c>); <see cref="ReadIntent.Changes"/> is what a checkpoint with a position and no intent already means.</summary>
    public void SetReadIntent(string taskName, string mappingName, string sourceTable, ReadIntent intent) => checkpoints.WriteIntent(WatermarkCheckpoint.ItemKey(mappingName, sourceTable), intent);

    /// <summary>Why the next pass is not to run, beside the position (the checkpoint's <c>hold</c>): the planner leaves a held mapping out of what it queues, as the API's scheduler does.</summary>
    public void SetReadHold(string taskName, string mappingName, string sourceTable, ReadHold hold) => checkpoints.WriteHold(WatermarkCheckpoint.ItemKey(mappingName, sourceTable), hold);

    /// <summary>A verification result is an output of the run (a step's value, stored with the run): the Sync back end reads it from the run when it lists results for a runner-owned replication.</summary>
    public void RecordVerificationResult(VerificationResultRecord result) =>
        (outputs ?? throw Unsupported(nameof(RecordVerificationResult), "a verification result is a recorded output of the run, and this state has nowhere to record it."))
            .VerificationResult(result);

    public void Log(Guid runId, LogSeverity level, string message) =>
        log.Log(level switch
        {
            LogSeverity.Trace => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Error => LogLevel.Error,
            _ => LogLevel.Information,
        }, "{Message}", message);

    /// <summary>The run's log is flushed with the run's own calls; nothing is held here.</summary>
    public void Flush()
    {
    }
}
