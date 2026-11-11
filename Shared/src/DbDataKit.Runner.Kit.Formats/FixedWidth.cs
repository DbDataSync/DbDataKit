using System.Runtime.CompilerServices;
using System.Text;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>A column of a fixed-width file: <paramref name="Start"/> is the zero-based character position, <paramref name="Length"/> the width.</summary>
/// <param name="Trim">The reader trims the padding, the writer pads (left-aligned unless <paramref name="RightAlign"/>).</param>
public sealed record FixedColumn(string Name, int Start, int Length, bool Trim = true, bool RightAlign = false);

public sealed record FixedWidthOptions
{
    public required IReadOnlyList<FixedColumn> Columns { get; init; }

    /// <summary>The first line is a header to skip (the reader) or to write with the column names (the writer).</summary>
    public bool HasHeaderLine { get; init; }

    /// <summary>A field that is only padding is null. Off: the empty string.</summary>
    public bool EmptyAsNull { get; init; } = true;

    /// <summary>A line that ends before its last column is missing the rest (the missing fields are null) instead of being a bad record.</summary>
    public bool AllowShortLines { get; init; }

    public bool SkipBlankLines { get; init; } = true;

    public Encoding? Encoding { get; init; }

    public string NewLine { get; init; } = "\n";

    public char Pad { get; init; } = ' ';

    /// <summary>The writer cuts a value longer than its column. Off: such a value is refused (a file that silently loses text is worse than one that is not written).</summary>
    public bool TruncateOnWrite { get; init; }

    internal int LineWidth => Columns.Count == 0 ? 0 : Columns.Max(c => c.Start + c.Length);
}

/// <summary>Reads a fixed-width file line by line; memory is one line.</summary>
public static class FixedWidthReader
{
    public static async IAsyncEnumerable<Row> ReadAsync(Stream stream, FixedWidthOptions options, ReadOptions? read = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        read ??= new ReadOptions();
        if (options.Columns.Count == 0) throw new ArgumentException("A fixed-width file needs its columns.", nameof(options));
        var gate = new BadRowGate("fixed-width", read);
        using var text = new StreamReader(stream, options.Encoding ?? new UTF8Encoding(false), true, 65536, leaveOpen: true);
        var names = options.Columns.Select(c => c.Name).ToArray();
        var width = options.LineWidth;
        long line = 0, number = 0;
        if (options.HasHeaderLine)
        {
            if (await text.ReadLineAsync(ct).ConfigureAwait(false) is null) yield break;
            line++;
        }

        while (await text.ReadLineAsync(ct).ConfigureAwait(false) is { } current)
        {
            line++;
            if (current.Length == 0 && options.SkipBlankLines) continue;
            number++;
            if (current.Length < width && !options.AllowShortLines)
            {
                gate.Report(number, line, current.Length + 1, $"the line is {current.Length} characters, the columns need {width}", () => current);
                continue;
            }

            var values = new object?[names.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var c = options.Columns[i];
                if (c.Start >= current.Length) { values[i] = null; continue; }
                var piece = current.Substring(c.Start, Math.Min(c.Length, current.Length - c.Start));
                if (c.Trim) piece = piece.Trim(options.Pad == ' ' ? [' ', '\t'] : [options.Pad, ' ', '\t']);
                values[i] = piece.Length == 0 && options.EmptyAsNull ? null : piece;
            }

            yield return new Row(number, line, names, values);
        }
    }
}

public sealed class FixedWidthWriter : IRowWriter
{
    private readonly StreamWriter _writer;
    private readonly FixedWidthOptions _o;

    private FixedWidthWriter(StreamWriter writer, FixedWidthOptions options)
    {
        _writer = writer;
        _o = options;
        Columns = [.. options.Columns.Select(c => c.Name)];
    }

    public IReadOnlyList<string> Columns { get; }

    public long Written { get; private set; }

    public static async Task<FixedWidthWriter> CreateAsync(Stream stream, FixedWidthOptions options, bool leaveOpen = true, CancellationToken ct = default)
    {
        var w = new FixedWidthWriter(new StreamWriter(stream, options.Encoding ?? new UTF8Encoding(false), 65536, leaveOpen), options);
        if (options.HasHeaderLine) await w.WriteLineAsync(options.Columns.Select(c => (string?)c.Name).ToArray(), true, ct).ConfigureAwait(false);
        return w;
    }

    public async ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default)
    {
        if (values.Count != Columns.Count) throw new ArgumentException($"A record has {Columns.Count} values, not {values.Count}.", nameof(values));
        await WriteLineAsync(values.Select(ValueText.Format).ToArray(), false, ct).ConfigureAwait(false);
        Written++;
    }

    public async ValueTask FinishAsync(CancellationToken ct = default) => await _writer.FlushAsync(ct).ConfigureAwait(false);

    public ValueTask DisposeAsync() => new(_writer.DisposeAsync().AsTask());

    private async ValueTask WriteLineAsync(string?[] fields, bool header, CancellationToken ct)
    {
        var line = new StringBuilder(_o.LineWidth + _o.NewLine.Length);
        for (var i = 0; i < _o.Columns.Count; i++)
        {
            var c = _o.Columns[i];
            var text = fields[i] ?? string.Empty;
            if (text.Length > c.Length)
            {
                if (!_o.TruncateOnWrite && !header) throw new FileFormatException($"A value of column {c.Name} is {text.Length} characters and the column is {c.Length} wide; widen the column or set TruncateOnWrite.");
                text = text[..c.Length];
            }

            if (line.Length < c.Start) line.Append(_o.Pad, c.Start - line.Length);
            line.Append(c.RightAlign ? text.PadLeft(c.Length, _o.Pad) : text.PadRight(c.Length, _o.Pad));
        }

        line.Append(_o.NewLine);
        await _writer.WriteAsync(line.ToString().AsMemory(), ct).ConfigureAwait(false);
    }
}
