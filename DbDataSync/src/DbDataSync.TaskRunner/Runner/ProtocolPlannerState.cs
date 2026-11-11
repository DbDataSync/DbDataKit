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
/// With a <see cref="IDrainLedger"/> it also has the rows each mapping's primary passes read since its last reconcile sweep was queued (the input of an after-change reconcile strategy; without one
/// <see cref="GetRowsReadSincePerMapping"/> throws naming itself), and it keeps the checks the polling gate made for <see cref="TakeChecks"/>: the gate's <c>change_check</c> history is written by the drain as an output of the
/// run, which the Sync back end turns into rows of <c>sync_state.change_check</c> when it reads the run (E13). A check is kept when its value changed or a minute has passed since the last one of its group was kept, so a
/// continuous drain does not write a row every five seconds per source (the API's own store keeps every tick).
/// </para>
/// </summary>
internal sealed class ProtocolPlannerState(IDrainQueue queue, IDrainCheckpoints checkpoints, IDrainLedger? ledger = null) : IPlannerState, IChangeGateState
{
    /// <summary>How long a check whose value did not change is not kept again.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private static readonly string[] Kinds = [nameof(RunKind.Primary), nameof(RunKind.ReconcileDeletes)];

    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset> _lastPrimary = new(StringComparer.Ordinal);
    private Dictionary<string, DateTimeOffset> _lastReconcile = new(StringComparer.Ordinal);
    private HashSet<string> _reconcileInFlight = new(StringComparer.Ordinal);
    private readonly List<ChangeCheckEntry> _checks = [];
    private readonly Dictionary<(string, string, string), (string? Value, DateTimeOffset At)> _lastChecks = [];

    /// <summary>Whether somebody paused the drain's task on the runner, as of the last <see cref="RefreshAsync"/>: a pause stops the next start of a drain, and a drain that is up stops planning.</summary>
    public bool Paused { get; private set; }

    /// <summary>Reads the queue's state for the cycle that is about to plan. Returns whether anything of the owner is in flight.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        var snapshot = await queue.StateAsync(Kinds, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _lastPrimary = snapshot.Of(nameof(RunKind.Primary)).ToDictionary(s => s.Key, s => s.Value.LastEnqueued, StringComparer.Ordinal);
            _lastReconcile = snapshot.Of(nameof(RunKind.ReconcileDeletes)).ToDictionary(s => s.Key, s => s.Value.LastEnqueued, StringComparer.Ordinal);
            _reconcileInFlight = [.. snapshot.Of(nameof(RunKind.ReconcileDeletes)).Where(s => s.Value.InFlight).Select(s => s.Key)];
            Paused = snapshot.Paused;
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

    /// <summary>The rows each mapping's primary passes read since its last sweep was queued (all of them for a mapping never swept): the cutoff the planner passes is that sweep's time, which is what the ledger's count restarts at.</summary>
    public IReadOnlyDictionary<string, long> GetRowsReadSincePerMapping(string replication, IReadOnlyDictionary<string, DateTimeOffset> sinceByMapping) =>
        ledger is null
            ? throw new NotSupportedException("IPlannerState.GetRowsReadSincePerMapping is not supported by a drain without a ledger of the rows each mapping read: an after-change reconcile strategy needs it.")
            : sinceByMapping.Keys.ToDictionary(mapping => mapping, ledger.RowsReadSinceSweep, StringComparer.Ordinal);

    /// <summary>The successful primary passes each mapping made since its last sweep was queued (what <c>reconcile.afterEveryPasses</c> counts).</summary>
    public IReadOnlyDictionary<string, long> GetPassesSincePerMapping(string replication, IReadOnlyDictionary<string, DateTimeOffset> sinceByMapping) =>
        ledger is null
            ? throw new NotSupportedException("IPlannerState.GetPassesSincePerMapping is not supported by a drain without a ledger of the passes each mapping made: reconcile.afterEveryPasses needs it.")
            : sinceByMapping.Keys.ToDictionary(mapping => mapping, ledger.PassesSinceSweep, StringComparer.Ordinal);

    /// <summary>A successful primary pass of the mapping ended, whatever it read: what <c>afterEveryPasses</c> counts.</summary>
    public void NotePassSucceeded(string mapping) => ledger?.AddPass(mapping);

    /// <summary>A successful primary pass of the mapping read rows: what an after-change strategy reacts to.</summary>
    public void NoteRowsRead(string mapping, long rows)
    {
        if (rows > 0) ledger?.AddRowsRead(mapping, rows);
    }

    /// <summary>The checks the gate made since the last call, to be recorded as an output of the run; empty when none was worth keeping.</summary>
    public IReadOnlyList<ChangeCheckEntry> TakeChecks()
    {
        lock (_gate)
        {
            var taken = _checks.ToArray();
            _checks.Clear();
            return taken;
        }
    }

    public bool HasPendingReconcile(string replication, string mapping)
    {
        lock (_gate) return _reconcileInFlight.Contains(mapping);
    }

    public MappingReadState? GetReadState(string replication, string mapping, string watermarkKey) => ReadState(checkpoints, WatermarkCheckpoint.ItemKey(mapping, watermarkKey));

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

        ledger?.MarkSweep(mapping);
    }

    // ---- the gate's state: the mapping's own position is the run's checkpoint

    public string? GetWatermark(string taskName, string mappingName, string watermarkKey) => checkpoints.Read(WatermarkCheckpoint.ItemKey(mappingName, watermarkKey))?.Position;

    public void RecordCheck(string connection, string database, string readerKind, string? value, DateTimeOffset? sourceTimeUtc)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var group = (connection, database, readerKind);
            if (_lastChecks.TryGetValue(group, out var last) && last.Value == value && now - last.At < CheckEvery)
                return;
            _lastChecks[group] = (value, now);
            _checks.Add(new ChangeCheckEntry(connection, database, readerKind, value, sourceTimeUtc, now));
        }
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
