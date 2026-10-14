using System.Globalization;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// Arrow arrays to the CLR values an ADO.NET reader hands out, and Arrow types to the ANSI type names a
/// column reports. Everything the rest of the pipeline sees is a plain CLR value — it never learns the
/// rows crossed the wire as Arrow.
/// </summary>
internal static class ArrowValues
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The value at <paramref name="row"/>, or null for an Arrow null. Never throws on a type
    /// it does not recognise: a column nobody mapped must not fail a read, so it surfaces as text.</summary>
    public static object? ValueAt(IArrowArray array, int row)
    {
        if (array.IsNull(row))
            return null;

        switch (array)
        {
            case BooleanArray a: return a.GetValue(row);
            case Int8Array a: return a.GetValue(row);
            case Int16Array a: return a.GetValue(row);
            case Int32Array a: return a.GetValue(row);
            case Int64Array a: return a.GetValue(row);
            case UInt8Array a: return a.GetValue(row);
            case UInt16Array a: return a.GetValue(row);
            case UInt32Array a: return a.GetValue(row);
            case UInt64Array a: return a.GetValue(row);
            case FloatArray a: return a.GetValue(row);
            case DoubleArray a: return a.GetValue(row);
            case HalfFloatArray a: return (float)a.GetValue(row)!.Value;

            case Decimal128Array a: return DecimalOrText(a.GetString(row)!);
            case Decimal256Array a: return DecimalOrText(a.GetString(row)!);

            case StringArray a: return a.GetString(row);
            case LargeStringArray a: return a.GetString(row);
            case StringViewArray a: return a.GetString(row);
            case BinaryArray a: return a.GetBytes(row).ToArray();
            case LargeBinaryArray a: return a.GetBytes(row).ToArray();
            case BinaryViewArray a: return a.GetBytes(row).ToArray();

            case Date32Array a: return a.GetDateTime(row);
            case Date64Array a: return a.GetDateTime(row);

            case TimestampArray a:
            {
                var stamp = a.GetTimestamp(row)!.Value;
                var zone = ((TimestampType)a.Data.DataType).Timezone;
                // A zoned timestamp is an instant, so it keeps its offset; an unzoned one is a wall-clock
                // reading and must not be dressed up as UTC. (Two returns, not one conditional: a
                // `?:` over a DateTime and a DateTimeOffset has the natural type DateTimeOffset, which
                // quietly turns the wall-clock reading into an instant in the machine's own zone.)
                if (string.IsNullOrEmpty(zone))
                    return DateTime.SpecifyKind(stamp.UtcDateTime, DateTimeKind.Unspecified);

                return stamp;
            }

            case Time32Array a:
            {
                var unit = ((Time32Type)a.Data.DataType).Unit;
                return TimeSpan.FromTicks(a.GetValue(row)!.Value * (unit == TimeUnit.Second ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerMillisecond));
            }
            case Time64Array a:
            {
                var unit = ((Time64Type)a.Data.DataType).Unit;
                var value = a.GetValue(row)!.Value;
                return TimeSpan.FromTicks(unit == TimeUnit.Microsecond ? value * 10 : value / 100);
            }

            case DictionaryArray a:
                return ValueAt(a.Dictionary, IndexOf(a, row));

            // Run-end encoded: a column stored as the end position of each run plus one value per run
            // (`A A A B B` as ends [3, 5], values [A, B]). The row's value is its run's.
            case RunEndEncodedArray a:
                return ValueAt(a.Values, a.FindPhysicalIndex(row));

            case NullArray:
                return null;

            case ListArray a:
                return Describe(a.GetSlicedValues(row));
            case LargeListArray a:
                return Describe(a.GetSlicedValues(row));
            case StructArray a:
            {
                var type = (StructType)a.Data.DataType;
                var parts = type.Fields.Select((f, i) => $"{f.Name}: {Render(ValueAt(a.Fields[i], row))}");
                return "{" + string.Join(", ", parts) + "}";
            }

            default:
                return $"<{array.Data.DataType.Name}>";
        }
    }

    private static int IndexOf(DictionaryArray array, int row) => array.Indices switch
    {
        Int8Array a => a.GetValue(row)!.Value,
        Int16Array a => a.GetValue(row)!.Value,
        Int32Array a => a.GetValue(row)!.Value,
        Int64Array a => (int)a.GetValue(row)!.Value,
        UInt8Array a => a.GetValue(row)!.Value,
        UInt16Array a => a.GetValue(row)!.Value,
        UInt32Array a => (int)a.GetValue(row)!.Value,
        _ => throw new NotSupportedException($"Dictionary index type {array.Indices.Data.DataType.Name} is not supported."),
    };

    private static object DecimalOrText(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : text;

    private static string Describe(IArrowArray values)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Render(ValueAt(values, i)));
        }
        return sb.Append(']').ToString();
    }

    private static string Render(object? value) => value switch
    {
        null => "null",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    /// <summary>
    /// The CLR type <see cref="ValueAt"/> produces for a column of this Arrow type — what
    /// <c>DbDataReader.GetFieldType</c> reports. An approximation for the types that fall back to text.
    /// </summary>
    public static Type ClrType(IArrowType type) => type switch
    {
        BooleanType => typeof(bool),
        Int8Type => typeof(sbyte),
        Int16Type => typeof(short),
        Int32Type => typeof(int),
        Int64Type => typeof(long),
        UInt8Type => typeof(byte),
        UInt16Type => typeof(ushort),
        UInt32Type => typeof(uint),
        UInt64Type => typeof(ulong),
        HalfFloatType or FloatType => typeof(float),
        DoubleType => typeof(double),
        Decimal128Type or Decimal256Type => typeof(object),
        BinaryType or LargeBinaryType or BinaryViewType => typeof(byte[]),
        Date32Type or Date64Type => typeof(DateTime),
        TimestampType t => string.IsNullOrEmpty(t.Timezone) ? typeof(DateTime) : typeof(DateTimeOffset),
        Time32Type or Time64Type => typeof(TimeSpan),
        DictionaryType d => ClrType(d.ValueType),
        RunEndEncodedType r => ClrType(r.ValuesDataType),
        _ => typeof(string),
    };

    /// <summary>
    /// The ANSI name for an Arrow type, in the spelling <c>AnsiDialect.ToCanonicalType</c> reads back.
    /// Unsigned types go one size up (nothing in ANSI SQL is unsigned) and anything with no scalar
    /// equivalent is reported as <c>varchar</c>, matching what <see cref="ValueAt"/> actually returns.
    /// </summary>
    public static string NativeTypeName(IArrowType type) => type switch
    {
        BooleanType => "boolean",
        Int8Type => "tinyint",
        Int16Type or UInt8Type => "smallint",
        Int32Type or UInt16Type => "integer",
        Int64Type or UInt32Type => "bigint",
        UInt64Type => "decimal(20,0)",
        HalfFloatType or FloatType => "real",
        DoubleType => "double precision",
        Decimal128Type d => $"decimal({d.Precision},{d.Scale})",
        Decimal256Type d => $"decimal({d.Precision},{d.Scale})",
        BinaryType or LargeBinaryType or BinaryViewType => "varbinary",
        Date32Type or Date64Type => "date",
        TimestampType t => string.IsNullOrEmpty(t.Timezone) ? "timestamp" : "timestamp with time zone",
        Time32Type or Time64Type => "time",
        DictionaryType d => NativeTypeName(d.ValueType),
        RunEndEncodedType r => NativeTypeName(r.ValuesDataType),
        _ => "varchar",
    };
}
