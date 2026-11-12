using System.Runtime.CompilerServices;
using DbDataKit.Runner.Kit.Formats;

namespace DbDataKit.Runner.Kit.Changes;

/// <summary>
/// What a run knew about its source when it ended: the hash of every row by key (<see cref="RowHash"/>). It is a plain JSON-serialisable class so a job keeps it as a <c>[Checkpoint]</c> member of its progress class
/// (<c>[Checkpoint] public RowSnapshot? Seen { get; set; }</c>): the runner saves it with the run's outcome, so a failed run leaves the old snapshot, and rewinding the checkpoint rewinds the comparison. The keys are kept as they are (the progress store
/// is the runner's own state, readable by operators): a source whose keys must not be kept there is not a source for this class.
/// </summary>
public sealed class RowSnapshot
{
    /// <summary>The algorithm of the hashes (<see cref="RowHash.Algorithm"/>).</summary>
    public string Algorithm { get; set; } = RowHash.Algorithm;

    /// <summary>The hash of each row, by its key.</summary>
    public Dictionary<string, string> Rows { get; set; } = new(StringComparer.Ordinal);

    public int Count => Rows.Count;
}

public enum ChangeKind
{
    /// <summary>The key was not in the last snapshot.</summary>
    Added,

    /// <summary>The key was there with another hash.</summary>
    Changed,

    /// <summary>The key was there with this hash.</summary>
    Unchanged,
}

/// <summary>A row that is new or changed since the last snapshot.</summary>
public sealed record RowChange(ChangeKind Kind, string Key, Row Row);

/// <summary>The result of a comparison.</summary>
/// <param name="Added">Rows whose key was not in the last snapshot.</param>
/// <param name="Changed">Rows whose key was there with another hash.</param>
/// <param name="Unchanged">Rows that are as they were.</param>
/// <param name="DeletedKeys">Keys that were in the last snapshot and were not seen in this run, in the order they had (sorted).</param>
/// <param name="Next">The snapshot to keep: what this run saw. Assign it to the progress member as the last thing the run does.</param>
public sealed record ChangeSet(long Added, long Changed, long Unchanged, IReadOnlyList<string> DeletedKeys, RowSnapshot Next)
{
    public long Deleted => DeletedKeys.Count;

    public long Seen => Added + Changed + Unchanged;

    public bool Any => Added + Changed + Deleted > 0;
}

public sealed record ChangeOptions
{
    /// <summary>The columns that make the key of a row (for <see cref="ChangeDetector.Observe(Row)"/>); several make a composite key.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Columns left out of the hash (a load time or a sequence the source sets on every export). The key columns are always left out.</summary>
    public IReadOnlyList<string> IgnoreColumns { get; init; } = [];

    /// <summary>A source that gave no rows when the last snapshot held some is refused (<see cref="ChangeException"/>): it is far more often a broken export than a source emptied. Set to accept it, and every key is then deleted.</summary>
    public bool AllowEmptySource { get; init; }

    /// <summary>Refuses a comparison that would delete more than this share (0 to 1) of the last snapshot's rows; null: no limit.</summary>
    public double? MaxDeletedFraction { get; init; }
}

/// <summary>A comparison that cannot be trusted: a snapshot of another algorithm, a row without its key, a key twice in one run, a source that went empty.</summary>
public sealed class ChangeException(string message) : Exception(message)
{
    /// <summary>Retrying changes nothing: the data or the declaration is what is wrong.</summary>
    public bool IsTemporary => false;
}

/// <summary>
/// Finds what changed in a source with no change feed (plan 27a IX13): the run reads the whole source, offers every row to <see cref="Observe(string, string)"/> (or lets <see cref="ChangesAsync"/> do it for rows of the file readers), is told for each whether it is added,
/// changed or unchanged, and at the end <see cref="Complete"/> names the keys that vanished and hands over the snapshot to keep. The detector never changes the snapshot it was given.
/// <para>
/// <b>Complete only after the whole source was read.</b> A key not seen counts as deleted; a read that ended early (an exception, a page missing) must never reach <see cref="Complete"/>. A run that fails before it keeps nothing, because the progress is saved
/// with the run's outcome and the member is only assigned from the result of <see cref="Complete"/>.
/// </para>
/// </summary>
public sealed class ChangeDetector
{
    private readonly RowSnapshot _previous;
    private readonly ChangeOptions _options;
    private readonly RowSnapshot _next = new();
    private readonly HashSet<string> _hashColumnsIgnored;
    private long _added, _changed, _unchanged;
    private bool _complete;

    public ChangeDetector(RowSnapshot? previous, ChangeOptions? options = null)
    {
        _previous = previous ?? new RowSnapshot();
        if (_previous.Algorithm != RowHash.Algorithm) throw new ChangeException($"The snapshot was made with the hash '{_previous.Algorithm}' and this version of the kit compares with '{RowHash.Algorithm}'; the checkpoint has to be rewound or cleared, a comparison across hashes would call every row changed.");
        _options = options ?? new ChangeOptions();
        _hashColumnsIgnored = [.. _options.KeyColumns, .. _options.IgnoreColumns];
    }

    /// <summary>Whether a snapshot was given with rows in it (a first run has none: everything is added).</summary>
    public bool HadSnapshot => _previous.Count > 0;

    /// <summary>The rows offered so far.</summary>
    public long Seen => _added + _changed + _unchanged;

    /// <summary>Offers one row by its key and the hash of its values (<see cref="RowHash"/>).</summary>
    public ChangeKind Observe(string key, string hash)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(hash);
        if (_complete) throw new InvalidOperationException("The comparison is complete; make a new detector for another run.");
        if (!_next.Rows.TryAdd(key, hash)) throw new ChangeException($"A key appeared twice in the source (row {Seen + 1}); a key has to identify one row (the key is not shown).");
        if (!_previous.Rows.TryGetValue(key, out var before))
        {
            _added++;
            return ChangeKind.Added;
        }

        if (before == hash)
        {
            _unchanged++;
            return ChangeKind.Unchanged;
        }

        _changed++;
        return ChangeKind.Changed;
    }

    /// <summary>Offers one row of a file reader: the key is the text of <see cref="ChangeOptions.KeyColumns"/> and the hash covers the other columns (less <see cref="ChangeOptions.IgnoreColumns"/>).</summary>
    public ChangeKind Observe(Row row)
    {
        if (_options.KeyColumns.Count == 0) throw new InvalidOperationException("ChangeOptions.KeyColumns names the columns that make a row's key.");
        var parts = new string[_options.KeyColumns.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var column = _options.KeyColumns[i];
            if (!row.Has(column)) throw new ChangeException($"The source has no key column '{column}' (row {row.Number}).");
            parts[i] = row.Text(column) is { Length: > 0 } text ? text : throw new ChangeException($"Row {row.Number} has no value in the key column '{column}'.");
        }

        var key = parts.Length == 1 ? parts[0] : string.Join('\u001f', parts);
        var columns = row.Columns.Where(c => !_hashColumnsIgnored.Contains(c)).ToList();
        return Observe(key, RowHash.Of(row, columns));
    }

    /// <summary>The added and changed rows of a reader, one at a time (an unchanged row is passed over). Enumerate it to the end, then call <see cref="Complete"/>.</summary>
    public async IAsyncEnumerable<RowChange> ChangesAsync(IAsyncEnumerable<Row> rows, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
        {
            var kind = Observe(row);
            if (kind != ChangeKind.Unchanged) yield return new RowChange(kind, KeyOf(row), row);
        }
    }

    private string KeyOf(Row row) => _options.KeyColumns.Count == 1 ? row.Text(_options.KeyColumns[0])! : string.Join('\u001f', _options.KeyColumns.Select(c => row.Text(c)));

    /// <summary>The end of the source: the keys that were in the last snapshot and were not seen are deleted, and <see cref="ChangeSet.Next"/> is what to keep.</summary>
    public ChangeSet Complete()
    {
        if (_complete) throw new InvalidOperationException("The comparison is already complete.");
        _complete = true;
        var deleted = _previous.Rows.Keys.Where(k => !_next.Rows.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        if (Seen == 0 && _previous.Count > 0 && !_options.AllowEmptySource)
            throw new ChangeException($"The source gave no rows and the last snapshot held {_previous.Count}; this is refused as a broken read (ChangeOptions.AllowEmptySource accepts it).");
        if (_options.MaxDeletedFraction is { } limit && _previous.Count > 0 && (double)deleted.Count / _previous.Count > limit)
            throw new ChangeException($"{deleted.Count} of the {_previous.Count} rows of the last snapshot are gone from the source, more than the allowed share of {limit:0.##}; refused (ChangeOptions.MaxDeletedFraction).");
        return new ChangeSet(_added, _changed, _unchanged, deleted, _next);
    }
}
