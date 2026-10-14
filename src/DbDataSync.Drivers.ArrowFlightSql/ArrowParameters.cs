using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>Builds the one-row record batch a prepared statement's parameters travel as: one column per
/// positional marker, typed from the CLR value.</summary>
internal static class ArrowParameters
{
    public static RecordBatch Build(IReadOnlyList<object?> values)
    {
        var fields = new List<Field>(values.Count);
        var arrays = new List<IArrowArray>(values.Count);

        for (var i = 0; i < values.Count; i++)
        {
            var array = ArrayFor(values[i]);
            fields.Add(new Field($"p{i}", array.Data.DataType, nullable: true));
            arrays.Add(array);
        }

        return new RecordBatch(new Schema(fields, null), arrays, 1);
    }

    private static IArrowArray ArrayFor(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return new NullArray(1);
            case bool v:
                return new BooleanArray.Builder().Append(v).Build();
            case sbyte v:
                return new Int8Array.Builder().Append(v).Build();
            case byte v:
                return new UInt8Array.Builder().Append(v).Build();
            case short v:
                return new Int16Array.Builder().Append(v).Build();
            case ushort v:
                return new UInt16Array.Builder().Append(v).Build();
            case int v:
                return new Int32Array.Builder().Append(v).Build();
            case uint v:
                return new UInt32Array.Builder().Append(v).Build();
            case long v:
                return new Int64Array.Builder().Append(v).Build();
            case ulong v:
                return new UInt64Array.Builder().Append(v).Build();
            case float v:
                return new FloatArray.Builder().Append(v).Build();
            case double v:
                return new DoubleArray.Builder().Append(v).Build();
            case decimal v:
            {
                var scale = (decimal.GetBits(v)[3] >> 16) & 0xFF;
                return new Decimal128Array.Builder(new Decimal128Type(38, scale)).Append(v).Build();
            }
            case string v:
                return new StringArray.Builder().Append(v).Build();
            case char v:
                return new StringArray.Builder().Append(v.ToString()).Build();
            case Guid v:
                return new StringArray.Builder().Append(v.ToString()).Build();
            case byte[] v:
                return new BinaryArray.Builder().Append(v.AsSpan()).Build();
            case DateTime v:
                // Treated as a wall-clock reading with no zone, which is what a column of
                // `timestamp` holds; the instant of a `DateTimeOffset` below keeps its zone.
                return new TimestampArray.Builder(TimestampType.Default).Append(new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc))).Build();
            case DateTimeOffset v:
                return new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC")).Append(v).Build();
            case DateOnly v:
                return new Date32Array.Builder().Append(v.ToDateTime(TimeOnly.MinValue)).Build();
            case TimeOnly v:
                return new Time64Array.Builder(new Time64Type(TimeUnit.Microsecond)).Append(v.Ticks / 10).Build();
            case TimeSpan v:
                return new Time64Array.Builder(new Time64Type(TimeUnit.Microsecond)).Append(v.Ticks / 10).Build();
            case Enum v:
                return new Int64Array.Builder().Append(Convert.ToInt64(v)).Build();
            default:
                throw new NotSupportedException(
                    $"A parameter of type {value.GetType().Name} cannot be sent to a Flight SQL server. " +
                    $"Set {FlightSqlSettings.ParameterModeKey}=Inline to render values into the statement instead.");
        }
    }
}
