using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;

namespace DbDataKit.Connections.FlightSql;

/// <summary>An Arrow type with no value a caller can carry (a list, a struct, a map, an interval). The message names the Arrow type, never a value.</summary>
public sealed class ArrowTypeNotCarriedException(string arrowType) : Exception($"the Arrow type {arrowType} has no value a copy can carry")
{
    public string ArrowType { get; } = arrowType;
}

/// <summary>
/// The strict reading of an Arrow value: the plain .NET object a driver would have given (a number of its own width, a string, a decimal, a <see cref="DateOnly"/>, a <see cref="DateTime"/>, a
/// <see cref="DateTimeOffset"/>, a <see cref="TimeOnly"/>, bytes), and a refusal (<see cref="ArrowTypeNotCarriedException"/>) for a type that has no scalar value. It is what a reader that converts each value
/// by a declared logical type wants (Build's copy, the metadata reader's text columns); <see cref="FlightSqlDataReader"/> uses its own, lenient reading, whose shapes differ (a date is a <see cref="DateTime"/>,
/// a nested value is text) because ADO.NET's readers have always handed those out.
/// </summary>
public static class ArrowValueReader
{
    /// <summary>The value at <paramref name="row"/> of <paramref name="array"/>; null for SQL NULL. Throws <see cref="ArrowTypeNotCarriedException"/> for a type that has no scalar value.</summary>
    public static object? Value(IArrowArray array, int row)
    {
        if (array.IsNull(row)) return null;
        return array switch
        {
            Int64Array a => a.GetValue(row),
            Int32Array a => a.GetValue(row),
            Int16Array a => a.GetValue(row),
            Int8Array a => a.GetValue(row),
            UInt64Array a => a.GetValue(row),
            UInt32Array a => a.GetValue(row),
            UInt16Array a => a.GetValue(row),
            UInt8Array a => a.GetValue(row),
            DoubleArray a => a.GetValue(row),
            FloatArray a => a.GetValue(row),
            HalfFloatArray a => (double?)a.GetValue(row),
            BooleanArray a => a.GetValue(row),
            StringArray a => a.GetString(row),
            LargeStringArray a => a.GetString(row),
            StringViewArray a => a.GetString(row),
            BinaryArray a => a.GetBytes(row).ToArray(),
            LargeBinaryArray a => a.GetBytes(row).ToArray(),
            BinaryViewArray a => a.GetBytes(row).ToArray(),
            Decimal128Array a => a.GetValue(row),
            Decimal256Array a => a.GetValue(row),
            Date32Array a => a.GetDateOnly(row),
            Date64Array a => a.GetDateOnly(row),
            TimestampArray a => Timestamp(a, row),
            Time32Array a => a.GetTime(row),
            Time64Array a => a.GetTime(row),
            DurationArray a => a.GetTimeSpan(row),
            FixedSizeBinaryArray a => a.GetBytes(row).ToArray(),
            DictionaryArray a => Value(a.Dictionary, (int)(Index(a.Indices, row) ?? throw new ArrowTypeNotCarriedException("dictionary"))),
            _ => throw new ArrowTypeNotCarriedException(TypeName(array.Data.DataType)),
        };
    }

    private static long? Index(IArrowArray indices, int row) => indices switch
    {
        Int8Array a => a.GetValue(row), Int16Array a => a.GetValue(row), Int32Array a => a.GetValue(row), Int64Array a => a.GetValue(row),
        UInt8Array a => a.GetValue(row), UInt16Array a => a.GetValue(row), UInt32Array a => a.GetValue(row), UInt64Array a => (long?)a.GetValue(row),
        _ => null,
    };

    // a timestamp with a zone is an instant (a DateTimeOffset at UTC); one without is a clock reading, kept as it is. A nanosecond timestamp keeps the 100 ns of a .NET tick.
    private static object? Timestamp(TimestampArray array, int row)
    {
        var instant = array.GetTimestamp(row);
        if (instant == null) return null;
        if (string.IsNullOrEmpty(((TimestampType)array.Data.DataType).Timezone)) return DateTime.SpecifyKind(instant.Value.UtcDateTime, DateTimeKind.Unspecified);
        return instant.Value;                                                                          // (a conditional expression of the two would convert the DateTime to a DateTimeOffset)
    }

    /// <summary>The short name of an Arrow type for a sentence (`list`, `struct`, `interval_month_day_nano`).</summary>
    public static string TypeName(IArrowType type) => type.Name;

    /// <summary>
    /// The type of a field of an Arrow schema, named as `information_schema` would name it (`bigint`, `character varying`, `decimal` with its precision and scale, `timestamp with time zone`...). A type with no
    /// standard name (a list, a struct) is named by its Arrow name (`list`). A dictionary is named by its values.
    /// </summary>
    public static ArrowColumnType ColumnType(Field field)
    {
        var type = field.DataType is DictionaryType dictionary ? dictionary.ValueType : field.DataType;
        int? precision = null, scale = null;
        string name;
        switch (type)
        {
            case Int64Type or UInt32Type: name = "bigint"; break;
            case Int32Type or UInt16Type: name = "integer"; break;
            case Int16Type or Int8Type or UInt8Type: name = "smallint"; break;
            case BooleanType: name = "boolean"; break;
            case FloatType or HalfFloatType: name = "real"; break;
            case DoubleType: name = "double precision"; break;
            case Decimal128Type d: name = "decimal"; precision = d.Precision; scale = d.Scale; break;
            case Decimal256Type d: name = "decimal"; precision = d.Precision; scale = d.Scale; break;
            case Date32Type or Date64Type: name = "date"; break;
            case TimestampType t: name = string.IsNullOrEmpty(t.Timezone) ? "timestamp" : "timestamp with time zone"; break;
            case Time32Type or Time64Type: name = "time"; break;
            case StringType or LargeStringType or StringViewType: name = "character varying"; break;
            case BinaryType or LargeBinaryType or BinaryViewType or FixedSizeBinaryType: name = "blob"; break;
            default: name = type.Name; break;
        }
        return new ArrowColumnType(name, precision, scale);
    }
}

/// <summary>A column type as `information_schema` names it, with the precision and scale of a decimal.</summary>
public sealed record ArrowColumnType(string Name, int? Precision, int? Scale);
