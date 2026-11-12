using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>
/// Reads JSON as rows: <see cref="ReadArrayAsync"/> for a file that is one array of objects, <see cref="ReadLinesAsync"/> for JSON Lines (one object per line). Each object is a row whose columns are its property names; a value that is an object or an array arrives as its JSON text.
/// A JSON Lines file resumes after a bad line. A JSON array cannot: after a syntax error there is no telling where the next element starts, so the read reports the error (as a bad row, with the line and column) and ends, whatever the decision.
/// </summary>
public static class JsonRows
{
    private const int MaxElementBytes = 64 * 1024 * 1024;

    public static async IAsyncEnumerable<Row> ReadArrayAsync(Stream stream, ReadOptions? read = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        read ??= new ReadOptions();
        var gate = new BadRowGate("JSON", read);
        var columns = new ColumnCache();
        var scanner = new ArrayScanner();
        var found = new List<JsonDocument>();
        long number = 0;
        var buffer = new byte[64 * 1024];
        int start = 0, end = 0;
        var final = false;
        while (!scanner.Done)
        {
            ct.ThrowIfCancellationRequested();
            if (!final)
            {
                if (end == buffer.Length)
                {
                    if (start > 0) { Buffer.BlockCopy(buffer, start, buffer, 0, end - start); end -= start; start = 0; }
                    else if (buffer.Length >= MaxElementBytes) throw new FileFormatException($"{(read.File is null ? "The JSON data" : $"'{read.File}'")} has an element larger than {MaxElementBytes / 1024 / 1024} MiB, which the reader will not hold.", read.File);
                    else Array.Resize(ref buffer, buffer.Length * 2);
                }

                var n = await stream.ReadAsync(buffer.AsMemory(end), ct).ConfigureAwait(false);
                if (n == 0) final = true; else end += n;
            }

            found.Clear();
            int consumed;
            JsonException? error = null;
            try
            {
                consumed = scanner.Scan(buffer.AsSpan(start, end - start), final, found);
            }
            catch (JsonException ex)
            {
                consumed = 0;
                error = ex;
            }

            foreach (var document in found)
            {
                using (document)
                {
                    number++;
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        var element = document.RootElement;
                        gate.Report(number, 0, 0, $"element {number} is a JSON {element.ValueKind.ToString().ToLowerInvariant()}, not an object", () => Cut(element.GetRawText()));
                        continue;
                    }

                    yield return ToRow(document.RootElement, number, 0, columns);
                }
            }

            if (error is not null)
            {
                gate.Report(number + 1, (error.LineNumber ?? 0) + 1, (int)(error.BytePositionInLine ?? 0) + 1,
                    "the JSON is not well formed here; a JSON array cannot be read past a syntax error");
                yield break;
            }

            if (scanner.NotAnArray)
            {
                gate.Report(1, 1, 1, "the top level is not an array of objects");
                yield break;
            }

            start += consumed;
            if (final && consumed == 0 && !scanner.Done) { gate.Report(number + 1, 0, 0, "the JSON ends before the array is closed"); yield break; }
        }
    }

    /// <summary>Scans a buffer for the elements of a top-level array, keeping the reader's state between buffers; an element that is not complete in the buffer waits for more bytes.</summary>
    private sealed class ArrayScanner
    {
        private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 64 });
        private bool _started;

        public bool Done { get; private set; }

        public bool NotAnArray { get; private set; }

        public int Scan(ReadOnlySpan<byte> data, bool final, List<JsonDocument> output)
        {
            var reader = new Utf8JsonReader(data, final, _state);
            long consumed = 0;
            while (true)
            {
                var checkpoint = reader.BytesConsumed;
                var checkpointState = reader.CurrentState;
                if (!reader.Read())
                {
                    consumed = reader.BytesConsumed;
                    _state = reader.CurrentState;
                    if (final && data.Length > 0 && !Done) consumed = checkpoint;
                    return (int)consumed;
                }

                if (!_started)
                {
                    if (reader.TokenType != JsonTokenType.StartArray) { NotAnArray = true; return 0; }
                    _started = true;
                    continue;
                }

                if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 0)
                {
                    Done = true;
                    _state = reader.CurrentState;
                    return (int)reader.BytesConsumed;
                }

                if (JsonDocument.TryParseValue(ref reader, out var document))
                {
                    output.Add(document!);
                    continue;
                }

                // the element is not all here yet: start again at its beginning when more bytes have come
                _state = checkpointState;
                return (int)checkpoint;
            }
        }
    }

    public static async IAsyncEnumerable<Row> ReadLinesAsync(Stream stream, ReadOptions? read = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        read ??= new ReadOptions();
        var gate = new BadRowGate("JSON Lines", read);
        var columns = new ColumnCache();
        using var text = new StreamReader(stream, new UTF8Encoding(false), true, 65536, leaveOpen: true);
        long line = 0, number = 0;
        while (await text.ReadLineAsync(ct).ConfigureAwait(false) is { } current)
        {
            line++;
            if (string.IsNullOrWhiteSpace(current)) continue;
            number++;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(current, new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException ex)
            {
                gate.Report(number, line, (int)(ex.BytePositionInLine ?? 0) + 1, "the line is not well formed JSON", () => current);
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    gate.Report(number, line, 1, $"the line is a JSON {document.RootElement.ValueKind.ToString().ToLowerInvariant()}, not an object", () => current);
                    continue;
                }

                yield return ToRow(document.RootElement, number, line, columns);
            }
        }
    }

    private static string Cut(string text) => text.Length > BadRows.MaxText ? text[..BadRows.MaxText] : text;

    private static Row ToRow(JsonElement element, long number, long line, ColumnCache cache)
    {
        var names = new List<string>();
        var values = new List<object?>();
        foreach (var property in element.EnumerateObject())
        {
            if (names.Contains(property.Name)) continue;
            names.Add(property.Name);
            values.Add(Value(property.Value));
        }

        return new Row(number, line, cache.For(names), values);
    }

    internal static object? Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.TryGetDecimal(out var d) ? d : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => v.GetRawText(),
    };

    /// <summary>Rows of one file usually have the same properties in the same order; they then share one column list.</summary>
    private sealed class ColumnCache
    {
        private string[] _last = [];

        public IReadOnlyList<string> For(List<string> names)
        {
            if (_last.AsSpan().SequenceEqual(names.ToArray())) return _last;
            return _last = [.. names];
        }
    }

    internal static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null or DBNull: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case short sh: w.WriteNumberValue(sh); break;
            case byte by: w.WriteNumberValue(by); break;
            case decimal d: w.WriteNumberValue(d); break;
            case double db when double.IsFinite(db): w.WriteNumberValue(db); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            case double or float: w.WriteNullValue(); break;
            case JsonElement e: e.WriteTo(w); break;
            case JsonNode n: n.WriteTo(w); break;
            default: w.WriteStringValue(ValueText.Format(value)); break;
        }
    }
}

/// <summary>Writes one JSON array of objects, element by element.</summary>
public sealed class JsonArrayWriter : IRowWriter
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Utf8JsonWriter _w;
    private bool _finished;

    private JsonArrayWriter(Stream stream, IReadOnlyList<string> columns, bool indented, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Columns = columns;
        _w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _w.WriteStartArray();
    }

    public IReadOnlyList<string> Columns { get; }

    public long Written { get; private set; }

    public static JsonArrayWriter Create(Stream stream, IReadOnlyList<string> columns, bool indented = false, bool leaveOpen = true) => new(stream, [.. columns], indented, leaveOpen);

    public async ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default)
    {
        if (values.Count != Columns.Count) throw new ArgumentException($"A record has {Columns.Count} values, not {values.Count}.", nameof(values));
        _w.WriteStartObject();
        for (var i = 0; i < values.Count; i++)
        {
            _w.WritePropertyName(Columns[i]);
            JsonRows.WriteValue(_w, values[i]);
        }

        _w.WriteEndObject();
        if (++Written % 500 == 0) await _w.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask FinishAsync(CancellationToken ct = default)
    {
        if (_finished) return;
        _finished = true;
        _w.WriteEndArray();
        await _w.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _w.DisposeAsync().ConfigureAwait(false);
        if (!_leaveOpen) await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Writes JSON Lines: one object per line, each line complete when written (a file cut short by a crash still holds whole records).</summary>
public sealed class JsonLinesWriter : IRowWriter
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly MemoryStream _line = new();
    private readonly Utf8JsonWriter _w;

    private JsonLinesWriter(Stream stream, IReadOnlyList<string> columns, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Columns = columns;
        _w = new Utf8JsonWriter(_line, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public IReadOnlyList<string> Columns { get; }

    public long Written { get; private set; }

    public static JsonLinesWriter Create(Stream stream, IReadOnlyList<string> columns, bool leaveOpen = true) => new(stream, [.. columns], leaveOpen);

    public async ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default)
    {
        if (values.Count != Columns.Count) throw new ArgumentException($"A record has {Columns.Count} values, not {values.Count}.", nameof(values));
        _line.SetLength(0);
        _w.Reset(_line);
        _w.WriteStartObject();
        for (var i = 0; i < values.Count; i++)
        {
            _w.WritePropertyName(Columns[i]);
            JsonRows.WriteValue(_w, values[i]);
        }

        _w.WriteEndObject();
        await _w.FlushAsync(ct).ConfigureAwait(false);
        _line.WriteByte((byte)'\n');
        await _stream.WriteAsync(_line.GetBuffer().AsMemory(0, (int)_line.Length), ct).ConfigureAwait(false);
        Written++;
    }

    public async ValueTask FinishAsync(CancellationToken ct = default) => await _stream.FlushAsync(ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await _w.DisposeAsync().ConfigureAwait(false);
        if (!_leaveOpen) await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
