using System.Text.Json;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>An identity or a stream a run named is not one its token covers (the identity does not end with <c>@&lt;a connection the run holds a handle for&gt;</c>). The runner refuses it; nothing was changed.</summary>
internal sealed class TrackingNotGrantedException(string message) : UnauthorizedAccessException(message);

/// <summary>
/// The runner's copy of Build's tracking for one run (plan 28c E16): the <c>build_tracking</c> module and the follower's checkpoints in the runner's state, written for a task that read a target's tracking tables with the
/// login of a connection it was granted. The rows and the checkpoint move in one transaction here (the task never holds a connection to this state). Every call names an identity, and the identity must end with
/// <c>@&lt;connection&gt;</c> where the connection is one the run holds a handle for: the name of a slot of the run, or the connection the channel binds that slot to; a run cannot write the copy of a connection it was not given.
/// <para>
/// The same calls carry Sync's copy of the units a runner-owned replication recorded on its target (plan 28c E17): the stream <see cref="SyncUnitCopy.Name"/> for an identity that starts with
/// <see cref="SyncUnitCopy.IdentityPrefix"/>, kept in the shared <c>tracking</c> module (<see cref="StateTrackingStore"/>) under the same follower checkpoints. A Build stream under a Sync identity, or the unit stream under a Build
/// identity, is refused.
/// </para>
/// </summary>
internal sealed class RunTracking(StateDatabase database, IReadOnlyDictionary<string, string> handles)
{
    private readonly BuildTrackingStore _store = new(database);
    private readonly StateTrackingStore _units = new(database);
    private readonly ProgressStore _progress = new(database, BuildTrackingState.ProgressKey);
    private readonly StateTrackingDivergences _divergences = new(database);

    /// <summary>The connections the run was given: slot names and the connections they are bound to.</summary>
    private HashSet<string> Granted { get; } = [.. handles.Keys.Concat(handles.Values).Where(c => !string.IsNullOrWhiteSpace(c))];

    private void Check(string identity)
    {
        var at = identity?.LastIndexOf('@') ?? -1;
        if (at < 0 || !Granted.Contains(identity![(at + 1)..]))
            throw new TrackingNotGrantedException($"The identity '{identity}' does not end with '@' and a connection this run holds a handle for ({(Granted.Count == 0 ? "it holds none" : string.Join(", ", Granted.Order(StringComparer.Ordinal)))}).");
    }

    /// <summary>The stream a call names, for the identity it names: one of Build's tables, or Sync's units.</summary>
    private readonly record struct Bound(string Identity, string Name, TrackingStream Stream, bool Unit);

    private Bound Bind(string identity, string stream)
    {
        Check(identity);
        if (SyncUnitCopy.IsIdentity(identity))
        {
            if (stream != SyncUnitCopy.Name) throw new ArgumentException($"'{stream}' is not a stream of Sync's units; the identity '{identity}' has one, '{SyncUnitCopy.Name}'.");
            return new(identity, stream, SyncUnitCopy.Stream, true);
        }

        if (stream == SyncUnitCopy.Name) throw new ArgumentException($"The stream '{SyncUnitCopy.Name}' is Sync's units, and the identity '{identity}' is not a copy of them ('{SyncUnitCopy.IdentityPrefix}...').");
        var build = BuildStreams.All.FirstOrDefault(s => s.Name == stream) ?? throw new ArgumentException($"'{stream}' is not a stream of Build's tracking tables ({string.Join(", ", BuildStreams.All.Select(s => s.Name))}).");
        return new(identity, stream, build.Stream, false);
    }

    private static string CheckpointName(Bound bound) => TrackingFollower.CheckpointNameOf(bound.Name);

    public TrackingCopyState State(string identity, string stream)
    {
        var bound = Bind(identity, stream);
        var (id, kind) = (bound.Identity, bound);
        var checkpoint = _progress.GetCheckpoint(CheckpointName(bound), id);
        TrackingPosition? position = checkpoint is not null && checkpoint.Parts.TryGetValue(TrackingFollower.OrderPartName, out var order) && checkpoint.Parts.TryGetValue(TrackingFollower.KeyPartName, out var key)
            ? new TrackingPosition(order.AsInt, key.AsText) : null;
        return new TrackingCopyState(position, checkpoint?.Version ?? 0, _divergences.Open(id, kind.Name));
    }

    /// <summary>Writes the rows; with <paramref name="advance"/>, in the one transaction that moves the checkpoint to the last row. False when the checkpoint's version moved.</summary>
    public bool Write(string identity, string stream, IReadOnlyList<TrackingRow> rows, bool advance, long expectedVersion)
    {
        var bound = Bind(identity, stream);
        var (id, kind) = (bound.Identity, bound);
        if (rows.Count == 0) return true;
        // a document is the task's: it must read as one, since the row's hash and whether it is unsettled come from it
        foreach (var row in rows)
        {
            try { using var _ = JsonDocument.Parse(row.Document); }
            catch (JsonException ex) { throw new ArgumentException($"The document of row '{row.Key}' of '{kind.Name}' is not JSON ({ex.Message}).", ex); }
        }

        if (!advance)
        {
            if (bound.Unit) _units.WriteAsync(id, kind.Stream, rows, CancellationToken.None).GetAwaiter().GetResult();
            else _store.WriteAsync(id, kind.Stream, rows, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        var last = rows[^1];
        var parts = new Dictionary<string, ProgressValue> { [TrackingFollower.OrderPartName] = ProgressValue.OfInt(last.Order), [TrackingFollower.KeyPartName] = ProgressValue.OfText(last.Key) };
        var advanced = _progress.Advance(CheckpointName(kind), parts, expectedVersion, itemKey: id, actor: "tracking-follower", alsoWrite: (connection, transaction) => { if (bound.Unit) _units.Write(connection, transaction, id, kind.Stream, rows); else _store.Write(connection, transaction, id, kind.Stream, rows); });
        // the run's record in the runs module (plan 29a Q21), after the rows and the checkpoint are in
        if (advanced && kind.Name == "run_log") BuildRunsProjection.Project(database, id, rows);
        return advanced;
    }

    public IReadOnlyList<TrackingPosition> Tail(string identity, string stream, int count)
    {
        var kind = Bind(identity, stream);
        var count1 = Math.Clamp(count, 1, 1000);
        return kind.Unit
            ? _units.TailAsync(kind.Identity, kind.Stream, count1, CancellationToken.None).GetAwaiter().GetResult()
            : _store.TailAsync(kind.Identity, kind.Stream, count1, CancellationToken.None).GetAwaiter().GetResult();
    }

    public IReadOnlyList<TrackingRow> Read(string identity, string stream, TrackingPosition? after, int limit)
    {
        var kind = Bind(identity, stream);
        return kind.Unit
            ? _units.ReadAfterAsync(kind.Identity, kind.Stream, after, Math.Clamp(limit, 1, 5000), CancellationToken.None).GetAwaiter().GetResult()
            : _store.ReadAfterAsync(kind.Identity, kind.Stream, after, Math.Clamp(limit, 1, 5000), CancellationToken.None).GetAwaiter().GetResult();
    }

    public IReadOnlyList<(string Key, string Hash)> Unsettled(string identity, string stream, int limit)
    {
        var kind = Bind(identity, stream);
        return kind.Unit ? [] : _store.Unsettled(kind.Identity, kind.Name, Math.Clamp(limit, 1, 1000));
    }

    public TrackingDivergence Mark(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh)
    {
        var kind = Bind(identity, stream);
        return _divergences.Mark(kind.Identity, kind.Name, reason, common, primaryHigh, copyHigh);
    }

    public int ReplaceSnapshot(string identity, string table, IReadOnlyList<TrackingRow> rows)
    {
        Check(identity);
        if (SyncUnitCopy.IsIdentity(identity)) throw new ArgumentException("Sync's units have no snapshot tables.");
        if (!BuildStreams.Snapshots.Contains(table)) throw new ArgumentException($"'{table}' is not a snapshot of Build's tracking tables ({string.Join(", ", BuildStreams.Snapshots)}).");
        foreach (var row in rows)
        {
            try { using var _ = JsonDocument.Parse(row.Document); }
            catch (JsonException ex) { throw new ArgumentException($"The document of row '{row.Key}' of '{table}' is not JSON ({ex.Message}).", ex); }
        }

        _store.ReplaceSnapshot(identity, table, [.. rows.Select(r => (r.Key, r.Order, r.Document))]);
        return rows.Count;
    }
}
