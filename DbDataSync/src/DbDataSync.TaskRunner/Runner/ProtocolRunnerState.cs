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
}

/// <summary>How one pass ended, as the engine reported it through <see cref="IRunnerState.CompleteRun"/>.</summary>
internal sealed record PassOutcome(Guid RunId, RunStatus Status, long RowsRead, long RowsWritten, string? Summary, string? FailureKind, string? Detail);

/// <summary>
/// <see cref="IRunnerState"/> over the runner's protocol (plan 28c E03), for the subset a replication drained by a runner uses: the prerequisites a pass needs that the runner already answers (the run lock is the
/// runner's, a mapping's lock is held here, in memory), the position as a checkpoint of the run's progress, the pass outcome as what the drain reports, and the run's log as the run's log. There is no work queue (the
/// drain plans its passes itself), no pause and no cancel poll (a cancel is the run's token), and nothing here carries rows (ED5): what crosses the socket is claims, positions, counts, outcomes and log lines.
/// <para>
/// Everything else the engine can ask of state throws <see cref="NotSupportedException"/> naming the member, so a replication that needs it (an initial load that must be requested, a verification result, a hold) fails
/// at that call with a message that says what is missing, and not by doing something a little different.
/// </para>
/// </summary>
internal sealed class ProtocolRunnerState(IDrainCheckpoints checkpoints, ILogger log) : IRunnerState
{
    private readonly ConcurrentDictionary<(RunKind Kind, string Mapping), Guid> _locks = new();
    private readonly ConcurrentDictionary<Guid, PassOutcome> _outcomes = new();
    private readonly ConcurrentDictionary<long, WorkItemStatus> _items = new();

    /// <summary>The outcome the pass with this run id reported, once; null when it reported none (it did not run to its end).</summary>
    public PassOutcome? TakeOutcome(Guid runId) => _outcomes.TryRemove(runId, out var outcome) ? outcome : null;

    private static NotSupportedException Unsupported(string member, string why) =>
        new($"IRunnerState.{member} is not supported by a drain on the runner yet: {why}");

    // ---- Prerequisites ----

    /// <summary>A replication is a definition on the runner; whether it runs is its schedule and pause, not a row.</summary>
    public void UpsertTask(string taskName, bool enabled)
    {
    }

    public bool HasOutstandingWork(string taskName, RunLane lane) => throw Unsupported(nameof(HasOutstandingWork), "the drain plans its passes itself and has no queue to ask.");

    /// <summary>A cancel reaches the pass as the run's token, so there is nothing to poll.</summary>
    public bool IsCancelRequested(Guid runId) => false;

    public WorkItem? TryClaimNext(string taskName, string workerId, RunLane lane) => throw Unsupported(nameof(TryClaimNext), "the drain plans its passes itself and has no queue to claim from.");

    /// <summary>The runner's run lock keeps two drains of one replication apart; this keeps two passes of one mapping apart within the drain.</summary>
    public bool TryAcquireLock(string taskName, RunKind runKind, string mappingName, Guid runId) => _locks.TryAdd((runKind, mappingName), runId);

    public string? GetWatermark(string taskName, string mappingName, string sourceTable) => checkpoints.Read(sourceTable)?.Position;

    public MappingReadState? GetReadState(string taskName, string mappingName, string sourceTable) =>
        checkpoints.Read(sourceTable) is { } stored
            ? new MappingReadState(ReadIntent.Changes, ReadHold.None, stored.Position, stored.Time)
            : null;

    public void BeginRun(Guid runId, int? pid)
    {
    }

    public void SetRunSegment(Guid runId, string label) => throw Unsupported(nameof(SetRunSegment), "a pass that reads one segment inline is not part of the first proof.");

    public void RequestInitialLoad(string taskName, string mappingName, string sourceTable, string capturedPosition, DateTimeOffset? capturedPositionTimeUtc) =>
        throw Unsupported(nameof(RequestInitialLoad), $"'{mappingName}' has no position yet and would start with an initial load, which is a bulk load on a queue the drain does not have. Give it a position first (an initial load on the old path, or a checkpoint set by an administrator).");

    public string? RequestSegmentedLoad(string taskName, string mappingName, string segmentsJson) =>
        throw Unsupported(nameof(RequestSegmentedLoad), $"'{mappingName}' resolved to segments, which run as a batch on a queue the drain does not have.");

    // ---- Outcomes ----

    public void MarkRunning(long workItemId) => _items[workItemId] = WorkItemStatus.Running;

    public void MarkDone(long workItemId) => _items[workItemId] = WorkItemStatus.Done;

    public void MarkFailed(long workItemId) => _items[workItemId] = WorkItemStatus.Failed;

    public void ReleaseClaim(long workItemId) => _items[workItemId] = WorkItemStatus.Pending;

    public void ReleaseLock(string taskName, RunKind runKind, string mappingName) => _locks.TryRemove((runKind, mappingName), out _);

    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null, RunTiming? timing = null, string? previousWatermark = null, string? newWatermark = null, string? errorDetail = null)
    {
        _outcomes[runId] = new PassOutcome(runId, status, rowsRead, rowsWritten, errorSummary, failureKind, errorDetail);
        if (newWatermark is not null) log.LogInformation("Position moved from {Previous} to {New}.", previousWatermark ?? "<none>", newWatermark);
    }

    /// <summary>Only ever called after the target write has committed, so the checkpoint moves only then.</summary>
    public void SetWatermark(string taskName, string mappingName, string sourceTable, string watermark, DateTimeOffset? watermarkTimeUtc = null) =>
        checkpoints.Write(sourceTable, watermark, watermarkTimeUtc);

    /// <summary>A pass writes <see cref="ReadIntent.Changes"/> each time it advances; that is what a checkpoint with a position and no intent already means. Any other intent has nowhere to go yet (the checkpoint is reduced to position and time).</summary>
    public void SetReadIntent(string taskName, string mappingName, string sourceTable, ReadIntent intent)
    {
        if (intent != ReadIntent.Changes)
            throw Unsupported(nameof(SetReadIntent), $"the checkpoint holds a position and its time and no intent, so '{intent}' cannot be kept (E15 declares it).");
    }

    public void SetReadHold(string taskName, string mappingName, string sourceTable, ReadHold hold) =>
        throw Unsupported(nameof(SetReadHold), $"the checkpoint holds no hold, so '{hold}' cannot be kept (E15 declares it). The pass that asked for it failed first; its own failure is in the run's log.");

    public void RecordVerificationResult(VerificationResultRecord result) => throw Unsupported(nameof(RecordVerificationResult), "a verification result is a payload of a verify task (E06, E14).");

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
