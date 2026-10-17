using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Dialect;
using DbDataKit.Dialect.Arrow;

namespace DbDataKit.Transfer;

/// <summary>A batch backed by an Arrow record batch; <see cref="AsDataReader"/> reads values straight from the arrays.</summary>
public sealed class ArrowDataBatch : DataBatch
{
    private readonly RecordBatch _batch;
    private readonly Func<int, object?>[] _readers;

    public ArrowDataBatch(RecordBatch batch)
    {
        _batch = batch;
        var cols = new List<TransferColumn>();
        for (var i = 0; i < batch.ColumnCount; i++)
        {
            var f = batch.Schema.GetFieldByIndex(i);
            var mapped = ArrowTypeMap.FromArrow(f.DataType);
            if (!mapped.IsMapped) throw new TransferException($"column `{f.Name}`: Arrow type {f.DataType.Name} has no canonical mapping");
            cols.Add(new TransferColumn(f.Name, mapped.Result!, f.IsNullable));
        }
        Schema = new TransferSchema(cols);
        _readers = [.. Enumerable.Range(0, batch.ColumnCount).Select(i => Reader(batch.Column(i)))];
    }

    public override int Rows => _batch.Length;
    public override TransferSchema Schema { get; }

    public override DbDataReader AsDataReader() => new BatchDataReader(Schema, Rows, (r, c) => _readers[c](r));
    public override RecordBatch AsArrow() => _batch;

    private static Func<int, object?> Reader(IArrowArray a) => a switch
    {
        BooleanArray x => i => x.GetValue(i),
        Int8Array x => i => x.GetValue(i),
        UInt8Array x => i => x.GetValue(i),
        Int16Array x => i => x.GetValue(i),
        UInt16Array x => i => x.GetValue(i),
        Int32Array x => i => x.GetValue(i),
        UInt32Array x => i => x.GetValue(i),
        Int64Array x => i => x.GetValue(i),
        UInt64Array x => i => x.GetValue(i),
        FloatArray x => i => x.GetValue(i),
        DoubleArray x => i => x.GetValue(i),
        Decimal128Array x => i => x.GetValue(i),
        StringArray x => i => x.IsNull(i) ? null : x.GetString(i),
        BinaryArray x => i => x.IsNull(i) ? null : x.GetBytes(i).ToArray(),
        Date32Array x => i => x.GetDateOnly(i),
        Time64Array x => i => x.GetValue(i) is { } us ? new TimeOnly(us * 10) : null,
        TimestampArray x => i => TimestampValue(x, i),
        DurationArray x => i => x.GetValue(i) is { } us ? TimeSpan.FromTicks(us * 10) : null,
        _ => throw new TransferException($"Arrow array {a.Data.DataType.Name} is not supported by the reader adapter"),
    };

    private static object? TimestampValue(TimestampArray x, int i)
    {
        var v = x.GetTimestamp(i);
        if (v is null) return null;
        // object: a bare ternary would convert the DateTime to DateTimeOffset
        return string.IsNullOrEmpty(((TimestampType)x.Data.DataType).Timezone) ? (object)DateTime.SpecifyKind(v.Value.UtcDateTime, DateTimeKind.Unspecified) : v.Value;
    }
}
