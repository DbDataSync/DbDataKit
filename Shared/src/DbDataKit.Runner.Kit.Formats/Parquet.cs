using System.Runtime.CompilerServices;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Parquet.Serialization;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>A column of a Parquet file a writer makes: its name, the CLR type of its values (<c>int</c>, <c>long</c>, <c>string</c>, <c>double</c>, <c>decimal</c>, <c>bool</c>, <c>DateTime</c>, <c>byte[]</c>, <c>Guid</c>) and whether it may hold nulls.</summary>
public sealed record ParquetColumn(string Name, Type Type, bool Nullable = true);

/// <summary>
/// Reads a flat Parquet file one row group at a time (memory is one row group), with the values typed as the file holds them. A file with a nested or repeated column is refused with a message that names the column: the kit reads tables, not documents.
/// A stream that cannot seek is copied to a temporary file first (Parquet reads its footer first).
/// </summary>
public static class ParquetRows
{
    private static object? Plain(object? v) => v switch
    {
        null or DBNull => null,
        ReadOnlyMemory<char> m => m.ToString(),
        ReadOnlyMemory<byte> b => b.ToArray(),
        _ => v,
    };

    public static async IAsyncEnumerable<Row> ReadAsync(Stream stream, ReadOptions? read = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        read ??= new ReadOptions();
        var source = stream;
        FileStream? spool = null;
        if (!stream.CanSeek)
        {
            spool = new FileStream(Path.Combine(Path.GetTempPath(), "dbdatakit-parquet-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await stream.CopyToAsync(spool, ct).ConfigureAwait(false);
            spool.Position = 0;
            source = spool;
        }

        try
        {
            var options = new ParquetOptions();
            ParquetReader reader;
            try { reader = await ParquetReader.CreateAsync(source, cancellationToken: ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                throw new FileFormatException($"{(read.File is null ? "The data" : $"'{read.File}'")} is not a Parquet file, or is damaged.", read.File, ex);
            }

            DataField[] fields;
            int groups;
            await using (reader)
            {
                fields = reader.Schema.GetDataFields();
                groups = reader.RowGroupCount;
            }

            foreach (var f in fields)
                if (f.MaxRepetitionLevel > 0 || f.Path.Length > 1)
                    throw new FileFormatException($"{(read.File is null ? "The file" : $"'{read.File}'")} has the nested or repeated column '{f.Path}'; the kit reads flat Parquet files only.", read.File);
            var names = fields.Select(f => f.Name).ToArray();
            long number = 0;
            for (var g = 0; g < groups; g++)
            {
                ct.ThrowIfCancellationRequested();
                source.Position = 0;
                var group = await ParquetSerializer.DeserializeUntypedAsync(source, options, g, ct).ConfigureAwait(false);
                foreach (var record in group.Data)
                {
                    var values = new object?[names.Length];
                    for (var c = 0; c < values.Length; c++) values[c] = record.TryGetValue(names[c], out var v) ? Plain(v) : null;
                    yield return new Row(++number, 0, names, values);
                }
            }
        }
        finally
        {
            if (spool is not null) await spool.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Writes a Parquet file in row groups of <c>rowGroupSize</c> rows (memory is one row group). The values must be of the CLR type of their column (a mismatch is refused naming the column, never converted silently). each row group is a complete file section with its footer, so a file is valid after <see cref="FinishAsync"/> writes the last one;
/// a writer disposed without it leaves a valid file with the groups written so far, and a failed run discards the file (a step's payload that did not complete is never committed).
/// </summary>
public sealed class ParquetRowWriter : IRowWriter
{
    private readonly Stream _target;
    private readonly Stream _work;
    private readonly bool _spooled;
    private readonly ParquetSchema _schema;
    private readonly ParquetColumn[] _columns;
    private readonly List<IDictionary<string, object?>> _pending = [];
    private readonly int _rowGroupSize;
    private bool _first = true;
    private bool _finished;

    private ParquetRowWriter(Stream target, Stream work, bool spooled, ParquetSchema schema, ParquetColumn[] columns, int rowGroupSize)
    {
        _target = target;
        _work = work;
        _spooled = spooled;
        _schema = schema;
        _columns = columns;
        _rowGroupSize = rowGroupSize;
        Columns = [.. columns.Select(c => c.Name)];
    }

    public IReadOnlyList<string> Columns { get; }

    public long Written { get; private set; }

    /// <param name="output">Where the file goes; it is left open. Parquet appends row groups by reading the footer back, so a stream that cannot seek and read is served through a temporary file that is copied to it by <see cref="FinishAsync"/>.</param>
    public static Task<ParquetRowWriter> CreateAsync(Stream output, IReadOnlyList<ParquetColumn> columns, int rowGroupSize = 50_000, CancellationToken ct = default)
    {
        if (columns.Count == 0) throw new ArgumentException("A Parquet file needs at least one column.", nameof(columns));
        var schema = new ParquetSchema(columns.Select(c => (Field)new DataField(c.Name, c.Type, c.Nullable)).ToArray());
        var direct = output.CanSeek && output.CanRead && output.CanWrite && output.Length == 0;
        Stream work = direct ? output : new FileStream(Path.Combine(Path.GetTempPath(), "dbdatakit-parquet-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return Task.FromResult(new ParquetRowWriter(output, work, !direct, schema, [.. columns], Math.Max(1, rowGroupSize)));
    }

    public async ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default)
    {
        if (values.Count != _columns.Length) throw new ArgumentException($"A record has {_columns.Length} values, not {values.Count}.", nameof(values));
        var row = new Dictionary<string, object?>(_columns.Length);
        for (var i = 0; i < _columns.Length; i++)
        {
            var v = values[i] is DBNull ? null : values[i];
            if (v is null && !_columns[i].Nullable) throw new FileFormatException($"The column {_columns[i].Name} does not allow nulls and a value is null.");
            if (v is not null && v.GetType() != _columns[i].Type) throw new FileFormatException($"A value of column {_columns[i].Name} is a {v.GetType().Name} and the column holds {_columns[i].Type.Name}.");
            row[_columns[i].Name] = v is byte[] bytes ? new ReadOnlyMemory<byte>(bytes) : v;
        }

        _pending.Add(row);
        Written++;
        if (_pending.Count >= _rowGroupSize) await FlushGroupAsync(ct).ConfigureAwait(false);
    }

    private async Task FlushGroupAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;
        _work.Position = _work.Length;
        await ParquetSerializer.SerializeUntypedAsync(_pending, _schema, _work, new ParquetOptions { Append = !_first }, null, ct).ConfigureAwait(false);
        _first = false;
        _pending.Clear();
    }

    public async ValueTask FinishAsync(CancellationToken ct = default)
    {
        if (_finished) return;
        _finished = true;
        await FlushGroupAsync(ct).ConfigureAwait(false);
        if (_first)
        {
            // a file with no rows still needs its schema and footer
            await ParquetSerializer.SerializeUntypedAsync(Array.Empty<IDictionary<string, object?>>(), _schema, _work, new ParquetOptions(), null, ct).ConfigureAwait(false);
            _first = false;
        }

        if (_spooled)
        {
            _work.Position = 0;
            await _work.CopyToAsync(_target, ct).ConfigureAwait(false);
        }

        await _target.FlushAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _spooled ? _work.DisposeAsync() : ValueTask.CompletedTask;
}
