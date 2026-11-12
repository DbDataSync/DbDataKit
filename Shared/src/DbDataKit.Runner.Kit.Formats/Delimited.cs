using System.Runtime.CompilerServices;
using System.Text;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>How a CSV or TSV file is laid out. <see cref="Csv"/> and <see cref="Tsv"/> are the two usual settings.</summary>
public sealed record DelimitedOptions
{
    public char Delimiter { get; init; } = ',';

    /// <summary>The character that quotes a field (a delimiter, a line break or the quote itself inside it is written doubled); null for none, which is what a TSV file usually is.</summary>
    public char? Quote { get; init; } = '"';

    /// <summary>The first record names the columns. When false, <see cref="Columns"/> does, or the columns are named column1, column2 and so on.</summary>
    public bool HasHeader { get; init; } = true;

    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>An empty field that was not quoted is null (an empty quoted field is the empty string). Off: it is the empty string.</summary>
    public bool EmptyAsNull { get; init; }

    /// <summary>A line with nothing on it is not a record.</summary>
    public bool SkipBlankLines { get; init; } = true;

    /// <summary>The encoding when the file has no byte order mark; a mark wins. UTF-8 when null.</summary>
    public Encoding? Encoding { get; init; }

    /// <summary>The line break the writer ends a record with.</summary>
    public string NewLine { get; init; } = "\n";

    /// <summary>The writer puts a byte order mark first (UTF-8 only; Excel wants it for non-ASCII text).</summary>
    public bool WriteByteOrderMark { get; init; }

    /// <summary>The writer quotes an empty string, so a reader with <see cref="EmptyAsNull"/> can tell it from a missing value.</summary>
    public bool QuoteEmptyStrings { get; init; } = true;

    public static DelimitedOptions Csv { get; } = new();

    public static DelimitedOptions Tsv { get; } = new() { Delimiter = '\t', Quote = null };
}

/// <summary>Reads CSV and TSV as a stream of rows, one record at a time: memory is the buffer and one record, whatever the file's size.</summary>
public static class DelimitedReader
{
    public static IAsyncEnumerable<Row> ReadAsync(Stream stream, DelimitedOptions? options = null, ReadOptions? read = null, CancellationToken ct = default)
    {
        options ??= DelimitedOptions.Csv;
        var reader = new StreamReader(stream, options.Encoding ?? new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
        return ReadAsync(reader, options, read, ct);
    }

    public static async IAsyncEnumerable<Row> ReadAsync(TextReader text, DelimitedOptions options, ReadOptions? read = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        read ??= new ReadOptions();
        var gate = new BadRowGate("CSV/TSV", read);
        var scanner = new Scanner(text, options, read.OnBadRow == BadRowDecision.Collect);
        IReadOnlyList<string>? columns = options.Columns is { Count: > 0 } given ? [.. given] : null;
        if (options.HasHeader)
        {
            var header = await scanner.NextAsync(ct).ConfigureAwait(false);
            while (header is { Blank: true } && options.SkipBlankLines) header = await scanner.NextAsync(ct).ConfigureAwait(false);
            if (header is null) yield break;
            if (header.Problem is { } p) throw new FileFormatException($"{Where(read)} The header line cannot be read: {p} (line {header.Line}).", read.File);
            columns = [.. header.Fields.Select((f, i) => string.IsNullOrWhiteSpace(f) ? "column" + (i + 1) : f!.Trim())];
        }

        long number = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var record = await scanner.NextAsync(ct).ConfigureAwait(false);
            if (record is null) yield break;
            if (record.Blank && options.SkipBlankLines) continue;
            number++;
            columns ??= [.. Enumerable.Range(1, record.Fields.Count).Select(i => "column" + i)];
            if (record.Problem is { } problem)
            {
                gate.Report(number, record.Line, record.ProblemColumn, problem, () => record.Raw);
                continue;
            }

            if (record.Fields.Count != columns.Count)
            {
                gate.Report(number, record.Line, 0, $"the record has {record.Fields.Count} fields, the header has {columns.Count}", () => record.Raw);
                continue;
            }

            yield return new Row(number, record.Line, columns, record.Fields.ToArray());
        }

        static string Where(ReadOptions o) => o.File is null ? "The CSV/TSV data:" : $"'{o.File}':";
    }

    private sealed class Record
    {
        public List<string?> Fields = [];
        public long Line;
        public bool Blank;
        public string? Problem;
        public int ProblemColumn;
        public string? Raw;
    }

    /// <summary>The scanner reads characters from a buffer and builds one record at a time; the record object is new per call, the buffers are reused.</summary>
    private sealed class Scanner(TextReader text, DelimitedOptions o, bool keepRaw)
    {
        private readonly char[] _buffer = new char[32768];
        private int _pos, _len;
        private long _line = 1;
        private readonly StringBuilder _field = new();
        private readonly StringBuilder _raw = new();
        private readonly char _delimiter = o.Delimiter;
        private readonly char _quote = o.Quote ?? '\0';
        private readonly bool _quoting = o.Quote is not null;

        private async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            _len = await text.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            _pos = 0;
            return _len > 0;
        }

        public async ValueTask<Record?> NextAsync(CancellationToken ct)
        {
            var record = new Record { Line = _line };
            _field.Clear();
            _raw.Clear();
            var state = State.FieldStart;
            var quotedField = false;
            var any = false;
            var column = 0;
            var content = 0;
            var recordStartLine = _line;

            while (true)
            {
                if (_pos >= _len && !await FillAsync(ct).ConfigureAwait(false))
                {
                    if (state == State.Quoted)
                    {
                        record.Problem ??= "a quoted field is not closed before the end of the file";
                        if (record.ProblemColumn == 0) record.ProblemColumn = column;
                    }

                    if (!any) return null;
                    EndField(record, quotedField);
                    return Finish(record, recordStartLine, content);
                }

                var c = _buffer[_pos++];
                any = true;
                column++;
                if (state == State.Quoted || c is not ('\n' or '\r'))
                {
                    content++;
                    if (keepRaw) _raw.Append(c);
                }

                if (state == State.Quoted)
                {
                    if (c == _quote) state = State.AfterQuote;
                    else
                    {
                        _field.Append(c);
                        if (c == '\n') { _line++; column = 0; }
                    }

                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    if (c == '\r')
                    {
                        if (_pos >= _len) await FillAsync(ct).ConfigureAwait(false);
                        if (_pos < _len && _buffer[_pos] == '\n') _pos++;
                    }

                    _line++;
                    EndField(record, quotedField);
                    return Finish(record, recordStartLine, content);
                }

                if (c == _delimiter)
                {
                    EndField(record, quotedField);
                    quotedField = false;
                    state = State.FieldStart;
                    continue;
                }

                switch (state)
                {
                    case State.FieldStart when _quoting && c == _quote:
                        state = State.Quoted;
                        quotedField = true;
                        break;
                    case State.AfterQuote when c == _quote:
                        _field.Append(_quote);
                        state = State.Quoted;
                        break;
                    case State.AfterQuote:
                        if (record.Problem is null) { record.Problem = "text follows the closing quote of a field"; record.ProblemColumn = column; }
                        _field.Append(c);
                        break;
                    case State.Unquoted when _quoting && c == _quote:
                        if (record.Problem is null) { record.Problem = "a quote inside a field that does not start with one"; record.ProblemColumn = column; }
                        _field.Append(c);
                        break;
                    default:
                        _field.Append(c);
                        state = State.Unquoted;
                        break;
                }
            }
        }

        private void EndField(Record record, bool quoted)
        {
            var value = _field.ToString();
            record.Fields.Add(value.Length == 0 && !quoted && o.EmptyAsNull ? null : value);
            _field.Clear();
        }

        private Record Finish(Record record, long startLine, int content)
        {
            record.Line = startLine;
            record.Blank = content == 0;
            if (keepRaw) record.Raw = _raw.ToString();
            return record;
        }

        private enum State { FieldStart, Unquoted, Quoted, AfterQuote }
    }
}

/// <summary>Writes CSV and TSV: a header (unless the options say none), then one record per call, quoting only what needs it.</summary>
public sealed class DelimitedWriter : IRowWriter
{
    private readonly StreamWriter _writer;
    private readonly DelimitedOptions _o;
    private readonly char _quote;

    private DelimitedWriter(StreamWriter writer, IReadOnlyList<string> columns, DelimitedOptions options)
    {
        _writer = writer;
        Columns = columns;
        _o = options;
        _quote = options.Quote ?? '\0';
    }

    public IReadOnlyList<string> Columns { get; }

    public long Written { get; private set; }

    public static async Task<DelimitedWriter> CreateAsync(Stream stream, IReadOnlyList<string> columns, DelimitedOptions? options = null, bool leaveOpen = true, CancellationToken ct = default)
    {
        options ??= DelimitedOptions.Csv;
        var encoding = options.Encoding ?? new UTF8Encoding(options.WriteByteOrderMark);
        var writer = new DelimitedWriter(new StreamWriter(stream, encoding, 65536, leaveOpen), [.. columns], options);
        if (options.HasHeader) await writer.WriteRecordAsync(columns.Select(c => (string?)c).ToArray(), isHeader: true, ct).ConfigureAwait(false);
        return writer;
    }

    public async ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default)
    {
        if (values.Count != Columns.Count) throw new ArgumentException($"A record has {Columns.Count} values, not {values.Count}.", nameof(values));
        var text = new string?[values.Count];
        for (var i = 0; i < text.Length; i++) text[i] = ValueText.Format(values[i]);
        await WriteRecordAsync(text, isHeader: false, ct).ConfigureAwait(false);
        Written++;
    }

    public async ValueTask FinishAsync(CancellationToken ct = default) => await _writer.FlushAsync(ct).ConfigureAwait(false);

    public ValueTask DisposeAsync() => new(_writer.DisposeAsync().AsTask());

    private async ValueTask WriteRecordAsync(string?[] fields, bool isHeader, CancellationToken ct)
    {
        var line = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) line.Append(_o.Delimiter);
            var f = fields[i];
            if (f is null) continue;
            if (f.Length == 0)
            {
                if (_o.Quote is not null && _o.QuoteEmptyStrings && !isHeader) line.Append(_quote).Append(_quote);
                continue;
            }

            var needs = f.AsSpan().IndexOfAny(_o.Delimiter, '\n', '\r') >= 0 || (_o.Quote is not null && f.Contains(_quote));
            if (!needs) { line.Append(f); continue; }
            if (_o.Quote is null)
                throw new FileFormatException($"A value of column {(isHeader ? "header" : Columns[i])} holds the delimiter or a line break and this format has no quoting; write the file as CSV, or remove the character.");
            line.Append(_quote).Append(f.Replace(_quote.ToString(), new string(_quote, 2))).Append(_quote);
        }

        line.Append(_o.NewLine);
        await _writer.WriteAsync(line.ToString().AsMemory(), ct).ConfigureAwait(false);
    }
}
