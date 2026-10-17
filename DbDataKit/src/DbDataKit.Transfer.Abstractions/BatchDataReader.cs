using System.Collections;
using System.Data.Common;

namespace DbDataKit.Transfer;

/// <summary>A forward-only <see cref="DbDataReader"/> over rows addressed by (row, column). Used by both batch kinds.</summary>
internal sealed class BatchDataReader(TransferSchema schema, int rows, Func<int, int, object?> cell) : DbDataReader
{
    private int _row = -1;
    private bool _closed;

    public override int Depth => 0;
    public override int FieldCount => schema.Count;
    public override bool HasRows => rows > 0;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    private object? Cell(int ordinal)
    {
        if (_row < 0 || _row >= rows) throw new InvalidOperationException("No current row.");
        return cell(_row, ordinal);
    }

    public override bool Read() => !_closed && ++_row < rows;
    public override bool NextResult() => false;
    public override void Close() => _closed = true;

    public override string GetName(int ordinal) => schema[ordinal].Name;
    public override int GetOrdinal(string name) => schema.IndexOf(name) is var i and >= 0 ? i : throw new IndexOutOfRangeException($"no column `{name}`");
    public override string GetDataTypeName(int ordinal) => schema[ordinal].Type.ToString();
    public override Type GetFieldType(int ordinal) => ClrContract.TypeOf(schema[ordinal].Type);
    public override bool IsDBNull(int ordinal) => Cell(ordinal) is null or DBNull;

    public override object GetValue(int ordinal) => Cell(ordinal) ?? DBNull.Value;
    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override bool GetBoolean(int ordinal) => (bool)Cell(ordinal)!;
    public override byte GetByte(int ordinal) => Convert.ToByte(Cell(ordinal));
    public override char GetChar(int ordinal) => Convert.ToChar(Cell(ordinal));
    public override DateTime GetDateTime(int ordinal) => Cell(ordinal) switch
    {
        DateTime d => d,
        DateTimeOffset o => o.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        var v => Convert.ToDateTime(v),
    };
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(Cell(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(Cell(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(Cell(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)Cell(ordinal)!;
    public override short GetInt16(int ordinal) => Convert.ToInt16(Cell(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(Cell(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(Cell(ordinal));
    public override string GetString(int ordinal) => (string)Cell(ordinal)!;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])Cell(ordinal)!;
        if (buffer is null) return bytes.Length;
        var n = (int)Math.Min(length, bytes.Length - dataOffset);
        if (n <= 0) return 0;
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = (string)Cell(ordinal)!;
        if (buffer is null) return s.Length;
        var n = (int)Math.Min(length, s.Length - dataOffset);
        if (n <= 0) return 0;
        s.CopyTo((int)dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
}
