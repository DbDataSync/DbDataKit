using DbDataKit.Runner.Kit;
using DbDataSync.Core.Config;
using DbDataSync.Planning;
using DbDataSync.State;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// What the planner reads and writes when it runs in a drain on a runner (plan 28c E11): the queue of the drain's own owner on the runner (when each mapping was last queued, what is in flight, the items it queues), and the
/// position and hold of a mapping as the run's checkpoint. One <see cref="RefreshAsync"/> at the start of a cycle reads the queue's state once; what the planner asks in the cycle is answered from it, and what it queues
/// updates it, so a cycle costs one read and the writes, not a call per question.
/// <para>
/// What it does not have, and says so: the rows each mapping read since a cutoff (the input of an after-change reconcile strategy: <see cref="GetRowsReadSincePerMapping"/> throws naming itself, because a drain keeps
/// no history of passes beyond the queue's), and an audit table for the gate's checks (<see cref="RecordCheck"/> records nothing: <c>sync_state.change_check</c> is in the Sync back end's state, E13/E14).
/// </para>
/// </summary>
internal sealed class ProtocolPlannerState(IDrainQueue queue, IDrainCheckpoints checkpoints) : IPlannerState, IChangeGateState
{
    private static readonly string[] Kinds = [nameof(RunKind.Primary), nameof(RunKind.ReconcileDeletes)];

    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset> _lastPrimary = new(StringComparer.Ordinal);
    private Dictionary<string, DateTimeOffset> _lastReconcile = new(StringComparer.Ordinal);
    private HashSet<string> _reconcileInFlight = new(StringComparer.Ordinal);

    /// <summary>Reads the queue's state for the cycle that is about to plan. Returns whether anything of the owner is in flight.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        var snapshot = await queue.StateAsync(Kinds, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _lastPrimary = snapshot.Of(nameof(RunKind.Primary)).ToDictionary(s => s.Key, s => s.Value.LastEnqueued, StringComparer.Ordinal);
            _lastReconcile = snapshot.Of(nameof(RunKind.ReconcileDeletes)).ToDictionary(s => s.Key, s => s.Value.LastEnqueued, StringComparer.Ordinal);
            _reconcileInFlight = [.. snapshot.Of(nameof(RunKind.ReconcileDeletes)).Where(s => s.Value.InFlight).Select(s => s.Key)];
        }

        return snapshot.Outstanding;
    }

    public IReadOnlyDictionary<string, DateTimeOffset> GetLastPrimaryEnqueueByMapping(string replication)
    {
        lock (_gate) return new Dictionary<string, DateTimeOffset>(_lastPrimary, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, DateTimeOffset> GetLastReconcileEnqueueByMapping(string replication)
    {
        lock (_gate) return new Dictionary<string, DateTimeOffset>(_lastReconcile, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, long> GetRowsReadSincePerMapping(string replication, IReadOnlyDictionary<string, DateTimeOffset> sinceByMapping) =>
        throw new NotSupportedException(
            "IPlannerState.GetRowsReadSincePerMapping is not supported by a drain on the runner yet: an after-change reconcile strategy needs the rows each mapping's passes read since its last sweep, and the runner keeps no history of passes beyond " +
            "the queue's (the console's run history from the runner is E13). A reconcile with a cadence and no after-change strategy works.");

    public bool HasPendingReconcile(string replication, string mapping)
    {
        lock (_gate) return _reconcileInFlight.Contains(mapping);
    }

    public MappingReadState? GetReadState(string replication, string mapping, string watermarkKey) => ReadState(checkpoints, watermarkKey);

    public void EnqueuePrimary(string replication, string mapping)
    {
        var shape = WorkQueueStore.Describe(replication, RunKind.Primary, mapping);
        queue.EnqueueAsync(shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload, CancellationToken.None).GetAwaiter().GetResult();
        lock (_gate) _lastPrimary[mapping] = DateTimeOffset.UtcNow;
    }

    /// <summary>Queues one segment of a scheduled reconcile sweep; the sweep is then in flight for the mapping.</summary>
    public void EnqueueReconcile(string replication, string mapping, string segmentLabel, string segmentJson, WorkItemKinds kinds, string? deleteGuardJson)
    {
        var shape = WorkQueueStore.Describe(replication, RunKind.ReconcileDeletes, mapping, segmentLabel, segmentJson, kinds, deleteGuardJson);
        queue.EnqueueAsync(shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload, CancellationToken.None).GetAwaiter().GetResult();
        lock (_gate)
        {
            _lastReconcile[mapping] = DateTimeOffset.UtcNow;
            _reconcileInFlight.Add(mapping);
        }
    }

    // ---- the gate's state: the mapping's own position is the run's checkpoint

    public string? GetWatermark(string taskName, string mappingName, string watermarkKey) => checkpoints.Read(watermarkKey)?.Position;

    public void RecordCheck(string connection, string database, string readerKind, string? value, DateTimeOffset? sourceTimeUtc)
    {
    }

    /// <summary>The intent, hold and position of a mapping from its checkpoint; null when nothing was ever stored for it.</summary>
    internal static MappingReadState? ReadState(IDrainCheckpoints checkpoints, string key)
    {
        var position = checkpoints.Read(key);
        var (intent, hold) = checkpoints.ReadIntentAndHold(key);
        if (position is null && intent is null && hold is null)
            return null;
        return new MappingReadState(intent ?? ReadIntent.Changes, hold ?? ReadHold.None, position?.Position, position?.Time);
    }
}
