using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbData.Dialect;
using DbData.Dialect.Arrow;

namespace DbData.Transfer;

/// <summary>Row-major batch of CLR values that follow <see cref="ClrContract"/>. Null (or DBNull) is SQL NULL.</summary>
public sealed class RowDataBatch : DataBatch
{
    private readonly IReadOnlyList<object?[]> _rows;

    public RowDataBatch(TransferSchema schema, IReadOnlyList<object?[]> rows)
    {
        Schema = schema;
        _rows = rows;
        foreach (var r in rows)
            if (r.Length != schema.Count) throw new ArgumentException($"a row has {r.Length} values; the schema has {schema.Count} columns", nameof(rows));
    }

    public override int Rows => _rows.Count;
    public override TransferSchema Schema { get; }
    public IReadOnlyList<object?[]> RowValues => _rows;

    public override DbDataReader AsDataReader() => new BatchDataReader(Schema, Rows, (r, c) => _rows[r][c] is DBNull ? null : _rows[r][c]);

    public override RecordBatch AsArrow()
    {
        var fields = new List<Field>();
        var arrays = new List<IArrowArray>();
        for (var c = 0; c < Schema.Count; c++)
        {
            var col = Schema[c];
            var mapped = ArrowTypeMap.ToArrow(col.Type);
            if (!mapped.IsMapped) throw new TransferException($"column `{col.Name}`: type {col.Type} has no Arrow mapping");
            var type = mapped.Result!;
            fields.Add(new Field(col.Name, type, col.Nullable));
            arrays.Add(Build(type, col, c));
        }
        return new RecordBatch(new Apache.Arrow.Schema(fields, null), arrays, Rows);
    }

    private IArrowArray Build(IArrowType type, TransferColumn col, int c)
    {
        object? V(int r) => _rows[r][c] is DBNull ? null : _rows[r][c];
        switch (type)
        {
            case BooleanType: { var b = new BooleanArray.Builder(); for (var r = 0; r < Rows; r++) { if (V(r) is bool v) b.Append(v); else b.AppendNull(); } return b.Build(); }
            case Int8Type: return Numeric<sbyte, Int8Array.Builder, Int8Array>(new Int8Array.Builder(), c);
            case UInt8Type: return Numeric<byte, UInt8Array.Builder, UInt8Array>(new UInt8Array.Builder(), c);
            case Int16Type: return Numeric<short, Int16Array.Builder, Int16Array>(new Int16Array.Builder(), c);
            case UInt16Type: return Numeric<ushort, UInt16Array.Builder, UInt16Array>(new UInt16Array.Builder(), c);
            case Int32Type: return Numeric<int, Int32Array.Builder, Int32Array>(new Int32Array.Builder(), c);
            case UInt32Type: return Numeric<uint, UInt32Array.Builder, UInt32Array>(new UInt32Array.Builder(), c);
            case Int64Type: return Numeric<long, Int64Array.Builder, Int64Array>(new Int64Array.Builder(), c);
            case UInt64Type: return Numeric<ulong, UInt64Array.Builder, UInt64Array>(new UInt64Array.Builder(), c);
            case FloatType: return Numeric<float, FloatArray.Builder, FloatArray>(new FloatArray.Builder(), c);
            case DoubleType: return Numeric<double, DoubleArray.Builder, DoubleArray>(new DoubleArray.Builder(), c);
            case Decimal128Type d: { var b = new Decimal128Array.Builder(d); for (var r = 0; r < Rows; r++) { if (V(r) is decimal v) b.Append(v); else b.AppendNull(); } return b.Build(); }
            case StringType:
            {
                var b = new StringArray.Builder();
                for (var r = 0; r < Rows; r++) { var v = V(r); if (v is null) b.AppendNull(); else b.Append(v is Guid g ? g.ToString() : (string)v); }
                return b.Build();
            }
            case BinaryType:
            {
                var b = new BinaryArray.Builder();
                for (var r = 0; r < Rows; r++) { if (V(r) is byte[] v) b.Append(v.AsSpan()); else b.AppendNull(); }
                return b.Build();
            }
            case Date32Type:
            {
                var b = new Date32Array.Builder();
                for (var r = 0; r < Rows; r++) { if (V(r) is DateOnly v) b.Append(v); else b.AppendNull(); }
                return b.Build();
            }
            case Time64Type t:
            {
                var b = new Time64Array.Builder(t);
                for (var r = 0; r < Rows; r++) { if (V(r) is TimeOnly v) b.Append(v.Ticks / 10); else b.AppendNull(); }
                return b.Build();
            }
            case TimestampType ts:
            {
                var b = new TimestampArray.Builder(ts);
                for (var r = 0; r < Rows; r++)
                {
                    switch (V(r))
                    {
                        case DateTimeOffset o: b.Append(o); break;
                        case DateTime d: b.Append(new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc))); break;
                        default: b.AppendNull(); break;
                    }
                }
                return b.Build();
            }
            case DurationType du:
            {
                var b = new DurationArray.Builder(du);
                for (var r = 0; r < Rows; r++) { if (V(r) is TimeSpan v) b.Append(v.Ticks / 10); else b.AppendNull(); }
                return b.Build();
            }
            default: throw new TransferException($"column `{col.Name}`: Arrow type {type.Name} is not supported by the row adapter");
        }
    }

    private IArrowArray Numeric<T, TB, TA>(TB builder, int c) where T : struct where TA : IArrowArray where TB : IArrowArrayBuilder<T, TA, TB>
    {
        for (var r = 0; r < Rows; r++)
        {
            var v = _rows[r][c];
            if (v is null or DBNull) builder.AppendNull();
            else builder.Append((T)v);
        }
        return builder.Build(default);
    }
}
