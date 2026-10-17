using System.Data;
using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataProxy.Data;

/// <summary>
/// Appends one column of a <see cref="DbDataReader"/> into an Arrow array builder.
/// The Arrow type is chosen from the provider-reported CLR type (and schema metadata for decimals/dates).
/// </summary>
internal sealed class ArrowColumnWriter
{
    private readonly Action<DbDataReader, int> _append;
    private readonly Action _appendNull;
    private readonly Func<IArrowArray> _build;

    private ArrowColumnWriter(Field field, Action<DbDataReader, int> append, Action appendNull, Func<IArrowArray> build)
    {
        Field = field;
        _append = append;
        _appendNull = appendNull;
        _build = build;
    }

    public Field Field { get; }

    public void Append(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) _appendNull();
        else _append(reader, ordinal);
    }

    /// <summary>Builds the accumulated values and resets the builder for the next batch.</summary>
    public IArrowArray Build() => _build();

    private const long UnixEpochTicks = 621355968000000000L;
    private static readonly int UnixEpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    // Used when the provider reports no precision/scale for a decimal column.
    private const int FallbackPrecision = 38;
    private const int FallbackScale = 9;

    public static ArrowColumnWriter Create(DbDataReader reader, int ordinal, ColumnMeta meta)
    {
        string name = reader.GetName(ordinal);
        string typeName = SafeTypeName(reader, ordinal);
        Type clr;
        try
        {
            clr = reader.GetFieldType(ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Driver-specific types (e.g. ODBC SQL_SS_TIMESTAMPOFFSET) that System.Data.Odbc cannot read at all.
            throw new UnsupportedValueException(
                $"Column '{name}' has a data type the ODBC provider cannot read ({ex.Message.TrimEnd('.')}). " +
                $"Convert it in the query, for example CAST({name} AS nvarchar(40)).", ex);
        }

        if (clr == typeof(bool))
        {
            var b = new BooleanArray.Builder();
            return Make(name, BooleanType.Default, b, (r, i) => b.Append(r.GetBoolean(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(sbyte))
        {
            var b = new Int8Array.Builder();
            return Make(name, Int8Type.Default, b, (r, i) => b.Append(r.GetFieldValue<sbyte>(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(byte))
        {
            var b = new UInt8Array.Builder();
            return Make(name, UInt8Type.Default, b, (r, i) => b.Append(r.GetByte(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(short))
        {
            var b = new Int16Array.Builder();
            return Make(name, Int16Type.Default, b, (r, i) => b.Append(r.GetInt16(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(ushort))
        {
            var b = new UInt16Array.Builder();
            return Make(name, UInt16Type.Default, b, (r, i) => b.Append(r.GetFieldValue<ushort>(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(int))
        {
            var b = new Int32Array.Builder();
            return Make(name, Int32Type.Default, b, (r, i) => b.Append(r.GetInt32(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(uint))
        {
            var b = new UInt32Array.Builder();
            return Make(name, UInt32Type.Default, b, (r, i) => b.Append(r.GetFieldValue<uint>(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(long))
        {
            var b = new Int64Array.Builder();
            return Make(name, Int64Type.Default, b, (r, i) => b.Append(r.GetInt64(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(ulong))
        {
            var b = new UInt64Array.Builder();
            return Make(name, UInt64Type.Default, b, (r, i) => b.Append(r.GetFieldValue<ulong>(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(float))
        {
            var b = new FloatArray.Builder();
            return Make(name, FloatType.Default, b, (r, i) => b.Append(r.GetFloat(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(double))
        {
            var b = new DoubleArray.Builder();
            return Make(name, DoubleType.Default, b, (r, i) => b.Append(r.GetDouble(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(decimal))
        {
            int precision = meta.Precision is > 0 and <= 38 ? meta.Precision.Value : FallbackPrecision;
            // DuckDB.NET reports 0/0 for a result with no rows yet; a scale means nothing without a precision.
            int scale = meta.Precision is > 0 && meta.Scale is >= 0 and <= 38 && meta.Scale.Value <= precision
                ? meta.Scale.Value
                : Math.Min(FallbackScale, precision);
            var type = new Decimal128Type(precision, scale);
            var b = new Decimal128Array.Builder(type);
            return Make(name, type, b, (r, i) => b.Append(r.GetDecimal(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(DateTime))
        {
            if (typeName.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                var b = new Date32Array.Builder();
                return Make(name, Date32Type.Default, b,
                    (r, i) => b.Append(DateOnly.FromDateTime(r.GetDateTime(i))), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
            }
            var type = new TimestampType(TimeUnit.Microsecond, (string?)null);
            var tb = new TimestampArray.Builder(type);
            return Make(name, type, tb, (r, i) => tb.Append(new DateTimeOffset(r.GetDateTime(i).Ticks, TimeSpan.Zero)), () => tb.AppendNull(), () => { var a = tb.Build(); tb.Clear(); return a; });
        }
        if (clr == typeof(DateTimeOffset))
        {
            var type = new TimestampType(TimeUnit.Microsecond, "UTC");
            var b = new TimestampArray.Builder(type);
            return Make(name, type, b,
                (r, i) => b.Append(r.GetFieldValue<DateTimeOffset>(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(DateOnly))
        {
            var b = new Date32Array.Builder();
            return Make(name, Date32Type.Default, b, (r, i) => b.Append(r.GetFieldValue<DateOnly>(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(TimeSpan))
        {
            var type = new Time64Type(TimeUnit.Microsecond);
            var b = new Time64Array.Builder(type);
            return Make(name, type, b, (r, i) => b.Append(r.GetFieldValue<TimeSpan>(i).Ticks / 10), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(TimeOnly))
        {
            var type = new Time64Type(TimeUnit.Microsecond);
            var b = new Time64Array.Builder(type);
            return Make(name, type, b, (r, i) => b.Append(r.GetFieldValue<TimeOnly>(i).Ticks / 10), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }
        if (clr == typeof(byte[]))
        {
            var b = new BinaryArray.Builder();
            return Make(name, BinaryType.Default, b, (r, i) => b.Append((ReadOnlySpan<byte>)r.GetFieldValue<byte[]>(i)), () => b.AppendNull(), () => { var a = b.Build(); b.Clear(); return a; });
        }

        // Strings, GUIDs and anything exotic travel as their text representation.
        var sb = new StringArray.Builder();
        if (clr == typeof(string))
            return Make(name, StringType.Default, sb, (r, i) => sb.Append(r.GetString(i)), () => sb.AppendNull(), () => { var a = sb.Build(); sb.Clear(); return a; });
        return Make(name, StringType.Default, sb,
            (r, i) => sb.Append(Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
            () => sb.AppendNull(), () => { var a = sb.Build(); sb.Clear(); return a; });
    }

    private static string SafeTypeName(DbDataReader reader, int ordinal)
    {
        try { return reader.GetDataTypeName(ordinal) ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private static ArrowColumnWriter Make<TBuilder>(
        string name, IArrowType type, TBuilder builder,
        Action<DbDataReader, int> append, Action appendNull, Func<IArrowArray> build)
        where TBuilder : class
    {
        _ = builder;
        return new ArrowColumnWriter(new Field(name, type, nullable: true), append, appendNull, build);
    }
}

/// <summary>Numeric precision/scale reported by the provider's schema table, when available.</summary>
internal readonly record struct ColumnMeta(int? Precision, int? Scale)
{
    public static ColumnMeta[] Read(DbDataReader reader)
    {
        var result = new ColumnMeta[reader.FieldCount];
        try
        {
            var table = reader.GetSchemaTable();
            if (table is null) return result;
            for (int i = 0; i < table.Rows.Count && i < result.Length; i++)
            {
                var row = table.Rows[i];
                result[i] = new ColumnMeta(ToInt(row, "NumericPrecision"), ToInt(row, "NumericScale"));
            }
        }
        catch (Exception)
        {
            // Providers that cannot describe their columns fall back to default decimal precision.
        }
        return result;
    }

    private static int? ToInt(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull) return null;
        int value = Convert.ToInt32(row[column], System.Globalization.CultureInfo.InvariantCulture);
        // Providers use 255/-1/0 to say "unknown".
        return value is >= 0 and < 255 ? value : null;
    }
}
