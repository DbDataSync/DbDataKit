using System.Collections;
using System.Data.Common;
using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>
/// One forward-only <see cref="DbDataReader"/> over a whole stream of batches, so a bulk route (SqlBulkCopy) can be fed from
/// <see cref="DataBatch.AsDataReader"/> without copying rows into a DataTable. Only one batch is held at a time. Values are adapted to what the
/// provider accepts (<see cref="ParameterValues"/>). <see cref="ReadAsync(CancellationToken)"/> advances batches asynchronously; the synchronous
/// <see cref="Read"/> blocks on it.
/// </summary>
public sealed class ChainedBatchReader : DbDataReader
{
    private readonly IAsyncEnumerator<DataBatch> _batches;
    private readonly TransferSchema _schema;
    private readonly EngineId _engine;
    private DbDataReader? _current;
    private bool _done;
    private bool _hasRow;

    public ChainedBatchReader(IAsyncEnumerable<DataBatch> batches, TransferSchema schema, EngineId engine, CancellationToken ct = default)
    {
        _batches = batches.GetAsyncEnumerator(ct);
        _schema = schema; _engine = engine;
    }

    /// <summary>Rows returned so far.</summary>
    public long RowsRead { get; private set; }

    public override int Depth => 0;
    public override int FieldCount => _schema.Count;
    public override bool HasRows => !_done;
    public override bool IsClosed => _done && _current is null;
    public override int RecordsAffected => -1;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        while (!_done)
        {
            if (_current is not null && _current.Read()) { _hasRow = true; RowsRead++; return true; }
            _current?.Dispose();
            _current = null;
            if (!await _batches.MoveNextAsync()) { _done = true; break; }
            var batch = _batches.Current;
            if (!batch.Schema.SameShapeAs(_schema)) throw new TransferException("a batch does not have the schema the reader was created with");
            _current = batch.AsDataReader();
        }
        _hasRow = false;
        return false;
    }

    public override bool NextResult() => false;
    public override void Close() { _current?.Dispose(); _current = null; _done = true; }
    protected override void Dispose(bool disposing) { if (disposing) { Close(); _batches.DisposeAsync().AsTask().GetAwaiter().GetResult(); } base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { Close(); await _batches.DisposeAsync(); await base.DisposeAsync(); }

    private DbDataReader Row() => _hasRow && _current is not null ? _current : throw new InvalidOperationException("No current row.");

    public override string GetName(int ordinal) => _schema[ordinal].Name;
    public override int GetOrdinal(string name) => _schema.IndexOf(name) is var i and >= 0 ? i : throw new IndexOutOfRangeException($"no column `{name}`");
    public override string GetDataTypeName(int ordinal) => _schema[ordinal].Type.ToString();
    public override Type GetFieldType(int ordinal) => ParameterValues.ClrTypeFor(_engine, _schema[ordinal].Type);
    public override bool IsDBNull(int ordinal) => Row().IsDBNull(ordinal);
    public override object GetValue(int ordinal) => Row().IsDBNull(ordinal) ? DBNull.Value : ParameterValues.Adapt(_engine, Row().GetValue(ordinal))!;
    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);
        if (buffer is null) return bytes.Length;
        var n = (int)Math.Min(length, bytes.Length - dataOffset);
        if (n <= 0) return 0;
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = (string)GetValue(ordinal);
        if (buffer is null) return s.Length;
        var n = (int)Math.Min(length, s.Length - dataOffset);
        if (n <= 0) return 0;
        s.CopyTo((int)dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
}
