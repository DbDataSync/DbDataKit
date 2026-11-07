namespace DbDataKit.State.Tracking;

/// <summary>
/// A named append-only table of one side (Build's <c>migration_log</c> is one): what columns it has, the <b>ordering key</b> that only grows (an id, or a time as UTC ticks) and the <b>row key</b> that is the row's own identity.
/// Rows are read as "added after a position"; the position is the pair (ordering value, row key), so rows that share an ordering value (the same timestamp) are neither skipped nor read twice.
/// </summary>
public sealed record TrackingStream
{
    public TrackingStream(string name, IReadOnlyList<string> columns, string orderingKey, string rowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        ArgumentNullException.ThrowIfNull(columns);
        if (!columns.Contains(orderingKey)) throw new ArgumentException($"The stream '{name}' names '{orderingKey}' as its ordering key, which is not one of its columns.", nameof(orderingKey));
        if (!columns.Contains(rowKey)) throw new ArgumentException($"The stream '{name}' names '{rowKey}' as its row key, which is not one of its columns.", nameof(rowKey));
        Name = name;
        Columns = [.. columns];
        OrderingKey = orderingKey;
        RowKey = rowKey;
    }

    public string Name { get; }

    public IReadOnlyList<string> Columns { get; }

    /// <summary>The column whose value only grows (an id or a time as ticks).</summary>
    public string OrderingKey { get; }

    /// <summary>The column that identifies a row (what an idempotent write is keyed by).</summary>
    public string RowKey { get; }
}

/// <summary>One row of a stream as it travels: its key, its ordering value and the whole row as a document (text, not JSON-typed: the follower never looks inside).</summary>
public sealed record TrackingRow(string Key, long Order, string Document);

/// <summary>Where in a stream: the ordering value and the row key. Equality is the way two sides are compared; only the ordering value says "behind" or "ahead".</summary>
public sealed record TrackingPosition(long Order, string Key);

/// <summary>What one run of the follower did. It is a value, never an exception: a deploy that asks for a copy gets this and goes on.</summary>
/// <param name="Copied">Rows written to the copy by this run.</param>
/// <param name="CaughtUp">The copy holds everything the primary had when the run last looked.</param>
/// <param name="Error">What went wrong (a side down, slow or refusing), or null.</param>
/// <param name="Diverged">The stream is marked diverged and is not being copied.</param>
public sealed record TrackingRunResult(int Copied, bool CaughtUp, string? Error, bool Diverged = false, TrackingDivergence? Divergence = null)
{
    public bool Ok => Error is null && !Diverged;
}

public enum TrackingResolution
{
    /// <summary>The copy keeps its rows after the common point under an archive marker (out of the live stream); copying restarts from the common point.</summary>
    Archive,

    /// <summary>The copy's rows after the common point are deleted; copying restarts from the common point.</summary>
    ResetCopy,
}

/// <summary>A connection and stream whose two sides part (plan 29 section 4a.3, item 4).</summary>
/// <param name="Common">The last position both sides agree on (null: they share nothing), the point where they part.</param>
public sealed record TrackingDivergence(
    long Id, string Identity, string Stream, string Status, string Reason, TrackingPosition? Common, TrackingPosition? PrimaryHigh, TrackingPosition? CopyHigh,
    DateTimeOffset DivergedAt, DateTimeOffset? ResolvedAt, string? ResolvedBy)
{
    public const string Diverged = "diverged";
    public const string Archived = "archived";
    public const string Reset = "reset";

    public bool IsOpen => Status == Diverged;
}

/// <summary>Reads rows of a stream for one connection identity: the primary side of a copy (and the copy side, when it is checked).</summary>
public interface ITrackingSource
{
    /// <summary>Up to <paramref name="limit"/> rows after <paramref name="after"/> (null: from the start), in ordering order, of this identity only.</summary>
    Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken cancellationToken);

    /// <summary>The position of the newest row, or null when there is none.</summary>
    Task<TrackingPosition?> HighWaterMarkAsync(string identity, TrackingStream stream, CancellationToken cancellationToken);

    /// <summary>The positions of the newest <paramref name="count"/> rows, oldest first: what two sides are compared by.</summary>
    Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken cancellationToken);
}

/// <summary>The copy side: takes rows idempotently, says how far it got, and can put its rows after a point aside or delete them.</summary>
public interface ITrackingSink : ITrackingSource
{
    /// <summary>Writes the rows keyed by (identity, stream, row key): a row that is already there is not added twice. Safe to repeat after a crash.</summary>
    Task WriteAsync(string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken);

    /// <summary>Takes the rows after <paramref name="common"/> (null: all) out of the live stream under <paramref name="marker"/>. Returns how many.</summary>
    Task<int> ArchiveAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, string marker, CancellationToken cancellationToken);

    /// <summary>Deletes the rows after <paramref name="common"/> (null: all). Returns how many.</summary>
    Task<int> DeleteAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, CancellationToken cancellationToken);
}

/// <summary>Keeps what the follower found: one open divergence per identity and stream, and the history of resolved ones.</summary>
public interface ITrackingDivergenceStore
{
    TrackingDivergence? Open(string identity, string stream);

    /// <summary>Marks the stream diverged (the open one, when there already is one, is returned unchanged).</summary>
    TrackingDivergence Mark(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh);

    /// <summary>Closes an open divergence as archived or reset. False when it was not open.</summary>
    bool Resolve(long id, TrackingResolution resolution, string actor);

    IReadOnlyList<TrackingDivergence> List(string? identity = null);
}
