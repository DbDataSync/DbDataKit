using System.Text;

namespace DbDataKit.State.Verification;

/// <summary>How a verification reads and what it reports.</summary>
public sealed record VerifyOptions
{
    /// <summary>Rows read at a time from each table (a page of the keyset). Memory use is about two pages (one from each side) plus one row's hash per row of a page.</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>Rows at a time of a table that holds blobs, whose rows are large (a payload chunk is 1 MiB by default).</summary>
    public int BlobBatchSize { get; init; } = 4;

    /// <summary>How many keys a difference names (a count of all of them is always given).</summary>
    public int KeysPerDifference { get; init; } = 10;

    /// <summary>
    /// Read and hash the bytes of every payload chunk. Off by default: chunks are then compared by payload, sequence and size, and the payload's stored SHA-256 (a column of the payload row) is
    /// compared with the rest of that row, which finds a payload that was written differently but not a byte flipped in place. On, a changed byte is found and the chunk named.
    /// </summary>
    public bool RehashPayloads { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(BlobBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(KeysPerDifference, 0);
    }
}

public enum VerificationDifferenceKind
{
    /// <summary>A module the left source has and the right does not.</summary>
    MissingModule,

    /// <summary>A module the right source has and the left does not.</summary>
    ExtraModule,

    /// <summary>The two sources have the module at a different version (or with different migrations); its tables are not compared.</summary>
    ModuleVersionMismatch,

    /// <summary>A table the left source has and the right does not.</summary>
    MissingTable,

    /// <summary>A table the right source has and the left does not.</summary>
    ExtraTable,

    /// <summary>The two tables have different numbers of rows.</summary>
    CountMismatch,

    /// <summary>Rows the left table has, by key, and the right does not.</summary>
    MissingRow,

    /// <summary>Rows the right table has, by key, and the left does not.</summary>
    ExtraRow,

    /// <summary>Rows with the same key and different values.</summary>
    ChangedRow,

    /// <summary>The digests differ and one source cannot say which rows (a manifest holds digests only).</summary>
    DigestMismatch,
}

/// <summary>One thing that differs.</summary>
/// <param name="Module">The module's key, always.</param>
/// <param name="Table">The table, or null for a difference of the whole module.</param>
/// <param name="Count">How many rows (for the row kinds), or the two counts' difference is in <paramref name="Message"/>.</param>
/// <param name="Keys">The first keys of the rows concerned, as text; at most <see cref="VerifyOptions.KeysPerDifference"/>.</param>
public sealed record VerificationDifference(VerificationDifferenceKind Kind, string Module, string? Table, string Message, long Count = 1, IReadOnlyList<string>? Keys = null)
{
    public override string ToString() => Message;
}

/// <summary>What was compared for one table, equal or not.</summary>
public sealed record TableComparison(string Module, string Table, long LeftRows, long RightRows, string LeftSha256, string RightSha256)
{
    public bool Equal => LeftRows == RightRows && LeftSha256 == RightSha256;
}

/// <summary>Where a verification is: which table, which side or step, how many rows of it have been read, and how many tables are done of how many there are to do.</summary>
public sealed record VerificationProgress(string Module, string Table, string Step, long Rows, int TablesDone, int TablesTotal);

/// <summary>The outcome: <see cref="Equal"/>, and when it is not, every difference with its table and the first keys.</summary>
public sealed class VerificationReport
{
    internal VerificationReport(string left, string right, IReadOnlyList<TableComparison> tables, IReadOnlyList<VerificationDifference> differences)
    {
        Left = left;
        Right = right;
        Tables = tables;
        Differences = differences;
    }

    /// <summary>The source taken as what is expected.</summary>
    public string Left { get; }

    /// <summary>The source compared with it.</summary>
    public string Right { get; }

    /// <summary>Every table that was compared, in order.</summary>
    public IReadOnlyList<TableComparison> Tables { get; }

    public IReadOnlyList<VerificationDifference> Differences { get; }

    public bool Equal => Differences.Count == 0;

    /// <summary>The report as lines of text: one line per table compared, then each difference.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var table in Tables)
            text.AppendLine($"{(table.Equal ? "equal    " : "DIFFERENT")} {table.Module}.{table.Table}: {table.LeftRows} rows, {table.RightRows} rows");
        foreach (var difference in Differences)
            text.AppendLine($"{difference.Kind}: {difference.Message}");
        text.AppendLine(Equal ? $"{Left} and {Right} are equal." : $"{Left} and {Right} differ ({Differences.Count} differences).");
        return text.ToString();
    }

    public override string ToString() => Describe();
}

/// <summary>
/// Compares two state databases (plan 29b slice X03, decision M5): the modules each has and at which version, and for every table of a module they share, the row count and a SHA-256 digest of
/// the rows' logical values in primary-key order, so a PostgreSQL database and the SQLite file it was moved from compare equal. A table whose digests differ is walked again by key, in pages, and
/// the rows that are missing, extra or changed are counted and the first keys named. Nothing is loaded whole: each side reads one page at a time. The sources are <see cref="ITableDigestSource"/>s, so a
/// snapshot's manifest can stand in for either side where it can give a digest (it names the table that differs, not the rows).
/// </summary>
public sealed class StateVerifier(VerifyOptions? options = null)
{
    private readonly VerifyOptions _options = options ?? new VerifyOptions();

    /// <summary>Compares <paramref name="left"/> (what is expected) with <paramref name="right"/>.</summary>
    public VerificationReport Verify(StateDatabase left, StateDatabase right, CancellationToken cancellationToken = default, IProgress<VerificationProgress>? progress = null) =>
        Verify(new DatabaseDigestSource(left, $"the {left.Dialect.Engine} state database (left)"), new DatabaseDigestSource(right, $"the {right.Dialect.Engine} state database (right)"), cancellationToken, progress);

    public VerificationReport Verify(ITableDigestSource left, ITableDigestSource right, CancellationToken cancellationToken = default, IProgress<VerificationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        _options.Validate();

        var differences = new List<VerificationDifference>();
        var tables = new List<TableComparison>();
        var leftModules = left.Modules();
        var rightModules = right.Modules().ToDictionary(m => m.Key, StringComparer.Ordinal);

        var pairs = new List<(DigestModule Left, DigestModule Right)>();
        foreach (var module in leftModules)
        {
            if (!rightModules.TryGetValue(module.Key, out var other))
            {
                differences.Add(new(VerificationDifferenceKind.MissingModule, module.Key, null, $"the module '{module.Key}' (version {module.Version}) is in {left.Description} and not in {right.Description}"));
            }
            else if (module.Version != other.Version || (module.Checksum is not null && other.Checksum is not null && module.Checksum != other.Checksum))
            {
                var why = module.Version != other.Version
                    ? $"version {module.Version} in {left.Description} and version {other.Version} in {right.Description}"
                    : $"the same version ({module.Version}) with different migrations (checksums {module.Checksum} and {other.Checksum})";
                differences.Add(new(VerificationDifferenceKind.ModuleVersionMismatch, module.Key, null, $"the module '{module.Key}' is at {why}; its tables were not compared"));
            }
            else
            {
                pairs.Add((module, other));
            }
        }

        var leftKeys = leftModules.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var module in rightModules.Values.Where(m => !leftKeys.Contains(m.Key)))
            differences.Add(new(VerificationDifferenceKind.ExtraModule, module.Key, null, $"the module '{module.Key}' (version {module.Version}) is in {right.Description} and not in {left.Description}"));

        var total = pairs.Sum(p => p.Left.Tables.Union(p.Right.Tables, StringComparer.Ordinal).Count());
        var done = 0;
        foreach (var (leftModule, rightModule) in pairs)
        {
            foreach (var table in leftModule.Tables.Union(rightModule.Tables, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inLeft = leftModule.Tables.Contains(table);
                var inRight = rightModule.Tables.Contains(table);
                if (!inLeft || !inRight)
                {
                    differences.Add(inLeft
                        ? new(VerificationDifferenceKind.MissingTable, leftModule.Key, table, $"the table '{leftModule.Key}.{table}' is in {left.Description} and not in {right.Description}")
                        : new(VerificationDifferenceKind.ExtraTable, leftModule.Key, table, $"the table '{leftModule.Key}.{table}' is in {right.Description} and not in {left.Description}"));
                }
                else
                {
                    Compare(left, right, leftModule.Key, table, tables, differences, cancellationToken, progress, done, total);
                }

                progress?.Report(new VerificationProgress(leftModule.Key, table, "done", 0, ++done, total));
            }
        }

        return new VerificationReport(left.Description, right.Description, tables, differences);
    }

    private void Compare(ITableDigestSource left, ITableDigestSource right, string module, string table, List<TableComparison> tables, List<VerificationDifference> differences,
        CancellationToken ct, IProgress<VerificationProgress>? progress, int done, int total)
    {
        var l = left.Digest(module, table, _options, ct, rows => progress?.Report(new VerificationProgress(module, table, "left", rows, done, total)));
        var r = right.Digest(module, table, _options, ct, rows => progress?.Report(new VerificationProgress(module, table, "right", rows, done, total)));
        var comparison = new TableComparison(module, table, l.Rows, r.Rows, l.Sha256, r.Sha256);
        tables.Add(comparison);
        if (comparison.Equal) return;

        var name = $"{module}.{table}";
        if (l.Rows != r.Rows)
            differences.Add(new(VerificationDifferenceKind.CountMismatch, module, table, $"{name} has {l.Rows} rows in {left.Description} and {r.Rows} in {right.Description}", Math.Abs(l.Rows - r.Rows)));

        if (left is IRowDigestSource rowsLeft && right is IRowDigestSource rowsRight)
        {
            var found = Walk(rowsLeft, rowsRight, module, table, ct, progress, done, total);
            if (found.Missing.Count > 0)
                differences.Add(new(VerificationDifferenceKind.MissingRow, module, table, $"{name}: {found.Missing.Count} rows are in {left.Description} and not in {right.Description}, first keys {Keys(found.Missing)}", found.Missing.Count, found.Missing.Keys));
            if (found.Extra.Count > 0)
                differences.Add(new(VerificationDifferenceKind.ExtraRow, module, table, $"{name}: {found.Extra.Count} rows are in {right.Description} and not in {left.Description}, first keys {Keys(found.Extra)}", found.Extra.Count, found.Extra.Keys));
            if (found.Changed.Count > 0)
                differences.Add(new(VerificationDifferenceKind.ChangedRow, module, table, $"{name}: {found.Changed.Count} rows have the same key and different values, first keys {Keys(found.Changed)}", found.Changed.Count, found.Changed.Keys));
        }
        else if (l.Rows == r.Rows)
        {
            differences.Add(new(VerificationDifferenceKind.DigestMismatch, module, table, $"{name} has the same number of rows ({l.Rows}) in both and different contents (digest {l.Sha256} in {left.Description}, {r.Sha256} in {right.Description})"));
        }
    }

    private static string Keys(Found found) => string.Join(", ", found.Keys) + (found.Count > found.Keys.Count ? ", ..." : "");

    private sealed class Found(int keep)
    {
        public long Count { get; private set; }

        public List<string> Keys { get; } = [];

        public void Add(string key)
        {
            Count++;
            if (Keys.Count < keep) Keys.Add(key);
        }
    }

    /// <summary>A merge of the two tables' rows in key order, a page of each in memory at a time.</summary>
    private (Found Missing, Found Extra, Found Changed) Walk(IRowDigestSource left, IRowDigestSource right, string module, string table, CancellationToken ct, IProgress<VerificationProgress>? progress, int done, int total)
    {
        var missing = new Found(_options.KeysPerDifference);
        var extra = new Found(_options.KeysPerDifference);
        var changed = new Found(_options.KeysPerDifference);
        var a = new Cursor(left, module, table, _options, ct);
        var b = new Cursor(right, module, table, _options, ct);
        long seen = 0;
        while (true)
        {
            var x = a.Peek();
            var y = b.Peek();
            if (x is null && y is null) break;
            var order = x is null ? 1 : y is null ? -1 : LogicalColumns.Compare(x.Key, y.Key);
            if (order < 0)
            {
                missing.Add(x!.KeyText);
                a.Take();
            }
            else if (order > 0)
            {
                extra.Add(y!.KeyText);
                b.Take();
            }
            else
            {
                if (!x!.Hash.AsSpan().SequenceEqual(y!.Hash)) changed.Add(x.KeyText);
                a.Take();
                b.Take();
            }

            if (++seen % 1000 == 0) progress?.Report(new VerificationProgress(module, table, "compare", seen, done, total));
        }

        return (missing, extra, changed);
    }

    private sealed class Cursor(IRowDigestSource source, string module, string table, VerifyOptions options, CancellationToken ct)
    {
        private readonly Queue<RowDigest> _page = new();
        private IReadOnlyList<object>? _after;
        private bool _finished;

        public RowDigest? Peek()
        {
            if (_page.Count == 0 && !_finished)
            {
                var rows = source.Rows(module, table, _after, options.BatchSize, options, ct);
                foreach (var row in rows) _page.Enqueue(row);
                if (rows.Count > 0) _after = rows[^1].Key;
                _finished = rows.Count == 0;
            }

            return _page.Count > 0 ? _page.Peek() : null;
        }

        public void Take() => _page.Dequeue();
    }
}
