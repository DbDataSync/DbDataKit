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
/// </summary>
internal sealed class RunTracking(StateDatabase database, IReadOnlyDictionary<string, string> handles)
{
    private readonly BuildTrackingStore _store = new(database);
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

    private static BuildStream Stream(string name) =>
        BuildStreams.All.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"'{name}' is not a stream of Build's tracking tables ({string.Join(", ", BuildStreams.All.Select(s => s.Name))}).");

    private (string Identity, BuildStream Stream) Bind(string identity, string stream)
    {
        Check(identity);
        return (identity, Stream(stream));
    }

    private static string CheckpointName(BuildStream stream) => TrackingFollower.CheckpointNameOf(stream.Name);

    public TrackingCopyState State(string identity, string stream)
    {
        var (id, kind) = Bind(identity, stream);
        var checkpoint = _progress.GetCheckpoint(CheckpointName(kind), id);
        TrackingPosition? position = checkpoint is not null && checkpoint.Parts.TryGetValue(TrackingFollower.OrderPartName, out var order) && checkpoint.Parts.TryGetValue(TrackingFollower.KeyPartName, out var key)
            ? new TrackingPosition(order.AsInt, key.AsText) : null;
        return new TrackingCopyState(position, checkpoint?.Version ?? 0, _divergences.Open(id, kind.Name));
    }

    /// <summary>Writes the rows; with <paramref name="advance"/>, in the one transaction that moves the checkpoint to the last row. False when the checkpoint's version moved.</summary>
    public bool Write(string identity, string stream, IReadOnlyList<TrackingRow> rows, bool advance, long expectedVersion)
    {
        var (id, kind) = Bind(identity, stream);
        if (rows.Count == 0) return true;
        // a document is the task's: it must read as one, since the row's hash and whether it is unsettled come from it
        foreach (var row in rows)
        {
            try { using var _ = JsonDocument.Parse(row.Document); }
            catch (JsonException ex) { throw new ArgumentException($"The document of row '{row.Key}' of '{kind.Name}' is not JSON ({ex.Message}).", ex); }
        }

        if (!advance)
        {
            _store.WriteAsync(id, kind.Stream, rows, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        var last = rows[^1];
        var parts = new Dictionary<string, ProgressValue> { [TrackingFollower.OrderPartName] = ProgressValue.OfInt(last.Order), [TrackingFollower.KeyPartName] = ProgressValue.OfText(last.Key) };
        return _progress.Advance(CheckpointName(kind), parts, expectedVersion, itemKey: id, actor: "tracking-follower", alsoWrite: (connection, transaction) => _store.Write(connection, transaction, id, kind.Stream, rows));
    }

    public IReadOnlyList<TrackingPosition> Tail(string identity, string stream, int count)
    {
        var (id, kind) = Bind(identity, stream);
        return _store.TailAsync(id, kind.Stream, Math.Clamp(count, 1, 1000), CancellationToken.None).GetAwaiter().GetResult();
    }

    public IReadOnlyList<TrackingRow> Read(string identity, string stream, TrackingPosition? after, int limit)
    {
        var (id, kind) = Bind(identity, stream);
        return _store.ReadAfterAsync(id, kind.Stream, after, Math.Clamp(limit, 1, 5000), CancellationToken.None).GetAwaiter().GetResult();
    }

    public IReadOnlyList<(string Key, string Hash)> Unsettled(string identity, string stream, int limit)
    {
        var (id, kind) = Bind(identity, stream);
        return _store.Unsettled(id, kind.Name, Math.Clamp(limit, 1, 1000));
    }

    public TrackingDivergence Mark(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh)
    {
        var (id, kind) = Bind(identity, stream);
        return _divergences.Mark(id, kind.Name, reason, common, primaryHigh, copyHigh);
    }

    public int ReplaceSnapshot(string identity, string table, IReadOnlyList<TrackingRow> rows)
    {
        Check(identity);
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
