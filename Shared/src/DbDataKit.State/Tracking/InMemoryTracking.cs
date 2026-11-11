namespace DbDataKit.State.Tracking;

/// <summary>
/// A stream held in memory, as a primary (a test adds rows to it, or replaces them to look like a restored backup) and as a copy. Rows are kept per identity and stream, ordered by (ordering value, key) with
/// the key compared as ordinal text. Archived rows are kept apart under their marker.
/// </summary>
public sealed class InMemoryTrackingStore : ITrackingSink
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Identity, string Stream), SortedDictionary<(long Order, string Key), TrackingRow>> _live = [];
    private readonly Dictionary<(string Identity, string Stream, string Marker), List<TrackingRow>> _archived = [];

    /// <summary>Adds rows (a primary's own append). A row with a key that is there already replaces it.</summary>
    public void Add(string identity, TrackingStream stream, params TrackingRow[] rows)
    {
        lock (_lock)
        {
            var set = Set(identity, stream);
            foreach (var row in rows)
            {
                foreach (var old in set.Values.Where(r => r.Key == row.Key).ToList()) set.Remove((old.Order, old.Key));
                set[(row.Order, row.Key)] = row;
            }
        }
    }

    /// <summary>Replaces the whole stream (what a restored backup does to a primary).</summary>
    public void Replace(string identity, TrackingStream stream, params TrackingRow[] rows)
    {
        lock (_lock) Set(identity, stream).Clear();
        Add(identity, stream, rows);
    }

    public IReadOnlyList<TrackingRow> Rows(string identity, TrackingStream stream)
    {
        lock (_lock) return [.. Set(identity, stream).Values];
    }

    public IReadOnlyList<TrackingRow> Archived(string identity, TrackingStream stream, string marker)
    {
        lock (_lock) return _archived.TryGetValue((identity, stream.Name, marker), out var rows) ? [.. rows] : [];
    }

    public IReadOnlyList<string> Identities()
    {
        lock (_lock) return [.. _live.Where(e => e.Value.Count > 0).Select(e => e.Key.Identity).Distinct().Order(StringComparer.Ordinal)];
    }

    private SortedDictionary<(long Order, string Key), TrackingRow> Set(string identity, TrackingStream stream)
    {
        if (!_live.TryGetValue((identity, stream.Name), out var set))
            _live[(identity, stream.Name)] = set = new(Comparer<(long Order, string Key)>.Create((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Key, b.Key)));
        return set;
    }

    private static bool After(TrackingRow row, TrackingPosition? common) =>
        common is null || row.Order > common.Order || (row.Order == common.Order && string.CompareOrdinal(row.Key, common.Key) > 0);

    public Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock) return Task.FromResult<IReadOnlyList<TrackingRow>>([.. Set(identity, stream).Values.Where(r => After(r, after)).Take(limit)]);
    }

    public Task<TrackingPosition?> HighWaterMarkAsync(string identity, TrackingStream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock) return Task.FromResult(Set(identity, stream).Values.LastOrDefault() is { } last ? new TrackingPosition(last.Order, last.Key) : null);
    }

    public Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var values = Set(identity, stream).Values;
            return Task.FromResult<IReadOnlyList<TrackingPosition>>([.. values.Skip(Math.Max(0, values.Count - count)).Select(r => new TrackingPosition(r.Order, r.Key))]);
        }
    }

    public Task WriteAsync(string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Add(identity, stream, [.. rows]);
        return Task.CompletedTask;
    }

    public Task<int> ArchiveAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, string marker, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var set = Set(identity, stream);
            var moved = set.Values.Where(r => After(r, common)).ToList();
            foreach (var row in moved) set.Remove((row.Order, row.Key));
            if (!_archived.TryGetValue((identity, stream.Name, marker), out var kept)) _archived[(identity, stream.Name, marker)] = kept = [];
            kept.AddRange(moved);
            return Task.FromResult(moved.Count);
        }
    }

    public Task<int> DeleteAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var set = Set(identity, stream);
            var gone = set.Values.Where(r => After(r, common)).ToList();
            foreach (var row in gone) set.Remove((row.Order, row.Key));
            return Task.FromResult(gone.Count);
        }
    }
}

/// <summary>Divergences kept in memory, for tests and for a follower that needs no database of its own.</summary>
public sealed class InMemoryTrackingDivergences(TimeProvider? time = null) : ITrackingDivergenceStore
{
    private readonly object _lock = new();
    private readonly List<TrackingDivergence> _all = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TrackingDivergence? Open(string identity, string stream)
    {
        lock (_lock) return _all.FirstOrDefault(d => d.Identity == identity && d.Stream == stream && d.IsOpen);
    }

    public TrackingDivergence Mark(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh)
    {
        lock (_lock)
        {
            if (Open(identity, stream) is { } open) return open;
            var made = new TrackingDivergence(_all.Count + 1, identity, stream, TrackingDivergence.Diverged, reason, common, primaryHigh, copyHigh, _time.GetUtcNow(), null, null);
            _all.Add(made);
            return made;
        }
    }

    public bool Resolve(long id, TrackingResolution resolution, string actor)
    {
        lock (_lock)
        {
            var index = _all.FindIndex(d => d.Id == id && d.IsOpen);
            if (index < 0) return false;
            _all[index] = _all[index] with { Status = resolution == TrackingResolution.Archive ? TrackingDivergence.Archived : TrackingDivergence.Reset, ResolvedAt = _time.GetUtcNow(), ResolvedBy = actor };
            return true;
        }
    }

    public IReadOnlyList<TrackingDivergence> List(string? identity = null)
    {
        lock (_lock) return [.. _all.Where(d => identity is null || d.Identity == identity)];
    }
}
