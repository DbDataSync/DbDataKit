using System.Globalization;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>
/// One record of a file: its number (1 is the first record after a header), the line it starts on where the format has lines (0 where it has none), the column names and the values. The text formats give strings (null for a missing value), JSON gives
/// strings, numbers (<c>long</c> when it is a whole number that fits, else <c>double</c>), booleans and null, Excel and Parquet give what the file holds. The kit never guesses a type from text: converting is the landing's (or the job's) job.
/// </summary>
public sealed class Row
{
    private Dictionary<string, int>? _index;

    public Row(long number, long line, IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        if (columns.Count != values.Count) throw new ArgumentException("A row has one value per column.", nameof(values));
        Number = number;
        Line = line;
        Columns = columns;
        Values = values;
    }

    /// <summary>The record's number, from 1, not counting a header.</summary>
    public long Number { get; }

    /// <summary>The line the record starts on (1 is the first line of the file), or 0 when the format has no lines.</summary>
    public long Line { get; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<object?> Values { get; }

    /// <summary>The value of a column, or null when there is no such column (use <see cref="Has"/> to tell the two apart).</summary>
    public object? this[string column] => Index().TryGetValue(column, out var i) ? Values[i] : null;

    public object? this[int ordinal] => Values[ordinal];

    public bool Has(string column) => Index().ContainsKey(column);

    /// <summary>The value as text, the way the file wrote it (numbers and dates in the invariant culture).</summary>
    public string? Text(string column) => this[column] switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    private Dictionary<string, int> Index()
    {
        if (_index is not null) return _index;
        var index = new Dictionary<string, int>(Columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < Columns.Count; i++) index.TryAdd(Columns[i], i);
        return _index = index;
    }

    public override string ToString() => $"row {Number}";
}

/// <summary>What the kit does with a record it cannot read (plan 27a IX08).</summary>
public enum BadRowDecision
{
    /// <summary>Stop: a <see cref="BadRowException"/> that names the file's format, the record, the line and the reason. The default: nothing is lost without the author saying so.</summary>
    Refuse,

    /// <summary>Leave it out and go on; the count of bad rows is kept (<see cref="BadRows.Count"/>).</summary>
    Skip,

    /// <summary>Leave it out, go on, and keep the record's position, reason and text (<see cref="BadRows.Rows"/>, up to <see cref="BadRows.Limit"/>) so the job can reject them (<c>RejectAsync</c>) or report them.</summary>
    Collect,
}

/// <summary>Where a bad record is and why it is bad.</summary>
/// <param name="Row">The record's number, from 1, counting the bad ones too.</param>
/// <param name="Line">The line it starts on, 0 where the format has none.</param>
/// <param name="Column">The column (character position in the line) the problem was found at, 0 when not known.</param>
/// <param name="Reason">A fixed sentence that holds no value of the record.</param>
/// <param name="Text">The record as it was written, cut to <see cref="BadRows.MaxText"/> characters; null when it was not kept (the decision is not Collect) or the format cannot give it.</param>
public sealed record BadRow(long Row, long Line, int Column, string Reason, string? Text = null)
{
    public override string ToString() => Line > 0 ? $"row {Row} (line {Line}{(Column > 0 ? ", column " + Column : "")}): {Reason}" : $"row {Row}: {Reason}";
}

/// <summary>The bad rows of one read: how many, and (when the decision is <see cref="BadRowDecision.Collect"/>) which. Pass one in <see cref="ReadOptions"/> to read it afterwards.</summary>
public sealed class BadRows
{
    public const int MaxText = 2000;

    private readonly List<BadRow> _rows = [];

    /// <param name="limit">How many bad rows are kept; the rest are counted.</param>
    public BadRows(int limit = 1000) => Limit = limit;

    public int Limit { get; }

    /// <summary>How many records were bad, kept or not.</summary>
    public long Count { get; private set; }

    /// <summary>The ones kept (Collect only).</summary>
    public IReadOnlyList<BadRow> Rows => _rows;

    internal void Add(BadRow row, bool keep)
    {
        Count++;
        if (keep && _rows.Count < Limit) _rows.Add(row.Text is { Length: > MaxText } t ? row with { Text = t[..MaxText] } : row);
    }
}

/// <summary>How a read treats a bad record.</summary>
public sealed record ReadOptions
{
    public BadRowDecision OnBadRow { get; init; } = BadRowDecision.Refuse;

    /// <summary>Where the bad rows go. Give one to read it afterwards; when the caller gives none a private one is used.</summary>
    public BadRows Bad { get; init; } = new();

    /// <summary>The name of the file, for messages (a failure names the file, never its content).</summary>
    public string? File { get; init; }
}

/// <summary>A file that cannot be read the way it is declared. Not a temporary failure: reading it again gives the same result, so the runner does not retry it (it treats an exception that says nothing else as permanent).</summary>
public class FileFormatException(string message, string? file = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The file's name when the caller gave one.</summary>
    public string? File { get; } = file;

    public bool IsTemporary => false;
}

/// <summary>A record could not be read and the decision was <see cref="BadRowDecision.Refuse"/>.</summary>
public sealed class BadRowException(string format, BadRow row, string? file) : FileFormatException($"{(file is null ? "The" : $"'{file}':")} {format} data has a bad record: {row}.", file)
{
    public string Format { get; } = format;

    public BadRow Row { get; } = row;
}

/// <summary>What a reader does with one bad record; shared so every format treats the decision the same way.</summary>
internal sealed class BadRowGate(string format, ReadOptions options)
{
    /// <summary>Handles a bad record: throws for Refuse, otherwise notes it. Returns when the reader may go on.</summary>
    public void Report(long row, long line, int column, string reason, Func<string?>? text = null)
    {
        var bad = new BadRow(row, line, column, reason);
        if (options.OnBadRow == BadRowDecision.Refuse) throw new BadRowException(format, bad, options.File);
        var keep = options.OnBadRow == BadRowDecision.Collect;
        options.Bad.Add(keep && text is not null ? bad with { Text = text() } : bad, keep);
    }
}

/// <summary>Writes rows to a file. The columns are fixed when the writer is opened; every call gives one value per column.</summary>
public interface IRowWriter : IAsyncDisposable
{
    IReadOnlyList<string> Columns { get; }

    /// <summary>How many rows were written.</summary>
    long Written { get; }

    ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default);

    /// <summary>Ends the file (a closing bracket, a footer, the last row group). Disposing without it leaves a file that may be incomplete, which is right for a failed run.</summary>
    ValueTask FinishAsync(CancellationToken ct = default);
}
