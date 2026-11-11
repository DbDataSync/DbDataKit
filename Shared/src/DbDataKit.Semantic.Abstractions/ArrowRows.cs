using System.Data.Common;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataKit.Semantic.Abstractions;

/// <summary>Reads a <see cref="DbDataReader"/> into Arrow arrays and back to the engine's row form (plan 19 J24).</summary>
public static class ArrowRows
{
    private enum Kind { Long, Double, Decimal, Bool, Text, DateText }

    private sealed class Column(string name, Kind kind, int precision, int scale)
    {
        public string Name { get; } = name;
        public Kind Kind { get; } = kind;
        public int Precision { get; } = precision;
        public int Scale { get; } = scale;
        public Int64Array.Builder? Long;
        public DoubleArray.Builder? Double;
        public Decimal128Array.Builder? Decimal;
        /// <summary>A decimal the provider gave no precision for is held as read, and given the largest scale seen when the batch is built (exact, as every value fits it).</summary>
        public List<decimal?>? Inferred;
        public BooleanArray.Builder? Bool;
        public StringArray.Builder? Text;
    }

    /// <summary>The Arrow form of each column, or null for the whole read when one has none (instants, binary, GUIDs, and decimals the provider gives no precision for).</summary>
    private static List<Column>? Plan(DbDataReader reader)
    {
        var schema = reader.CanGetColumnSchema() ? reader.GetColumnSchema() : null;
        var cols = new List<Column>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var t = reader.GetFieldType(i);
            var name = reader.GetName(i);
            if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte)) cols.Add(new(name, Kind.Long, 0, 0));
            else if (t == typeof(double) || t == typeof(float)) cols.Add(new(name, Kind.Double, 0, 0));
            else if (t == typeof(bool)) cols.Add(new(name, Kind.Bool, 0, 0));
            else if (t == typeof(string)) cols.Add(new(name, Kind.Text, 0, 0));
            else if (t == typeof(DateOnly)
                || (t == typeof(DateTime) && reader.GetDataTypeName(i).Equals("date", StringComparison.OrdinalIgnoreCase))) cols.Add(new(name, Kind.DateText, 0, 0));
            else if (t == typeof(decimal) && schema?[i] is { NumericPrecision: > 0 and <= 38, NumericScale: { } s } c) cols.Add(new(name, Kind.Decimal, c.NumericPrecision!.Value, s));
            else if (t == typeof(decimal)) cols.Add(new(name, Kind.Decimal, 0, 0));
            else return null;
        }
        return cols;
    }

    public static ColumnarRows? TryRead(DbDataReader reader, int maxRows)
    {
        if (Plan(reader) is not { } cols) return null;
        foreach (var c in cols)
        {
            switch (c.Kind)
            {
                case Kind.Long: c.Long = new(); break;
                case Kind.Double: c.Double = new(); break;
                case Kind.Decimal: if (c.Precision > 0) c.Decimal = new(new Decimal128Type(c.Precision, c.Scale)); else c.Inferred = []; break;
                case Kind.Bool: c.Bool = new(); break;
                default: c.Text = new(); break;
            }
        }
        var types = cols.Select((_, i) => reader.GetFieldType(i)).ToArray();
        int n = 0;
        while (n < maxRows && reader.Read())
        {
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                if (reader.IsDBNull(i)) { AppendNull(c); continue; }
                switch (c.Kind)
                {
                    case Kind.Long: c.Long!.Append(Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture)); break;
                    case Kind.Double: c.Double!.Append(types[i] == typeof(double) ? reader.GetDouble(i) : reader.GetFloat(i)); break;
                    case Kind.Decimal: if (c.Inferred is not null) c.Inferred.Add(reader.GetDecimal(i)); else c.Decimal!.Append(reader.GetDecimal(i)); break;
                    case Kind.Bool: c.Bool!.Append(reader.GetBoolean(i)); break;
                    case Kind.Text: c.Text!.Append(reader.GetString(i)); break;
                    default: c.Text!.Append(types[i] == typeof(DateOnly) ? reader.GetFieldValue<DateOnly>(i).ToString("yyyy-MM-dd") : reader.GetDateTime(i).ToString("yyyy-MM-dd")); break;
                }
            }
            n++;
        }
        var arrays = cols.Select(Build).ToList();
        var fields = cols.Select((c, i) => new Field(c.Name, arrays[i].Data.DataType, true)).ToList();
        return new ColumnarRows([.. cols.Select(c => c.Name)], new RecordBatch(new Schema(fields, null), arrays, n));
    }

    private static void AppendNull(Column c)
    {
        switch (c.Kind)
        {
            case Kind.Long: c.Long!.AppendNull(); break;
            case Kind.Double: c.Double!.AppendNull(); break;
            case Kind.Decimal: if (c.Inferred is not null) c.Inferred.Add(null); else c.Decimal!.AppendNull(); break;
            case Kind.Bool: c.Bool!.AppendNull(); break;
            default: c.Text!.AppendNull(); break;
        }
    }

    private static IArrowArray Build(Column c) => c.Kind switch
    {
        Kind.Long => c.Long!.Build(), Kind.Double => c.Double!.Build(), Kind.Decimal => BuildDecimal(c),
        Kind.Bool => c.Bool!.Build(), _ => c.Text!.Build(),
    };

    private static IArrowArray BuildDecimal(Column c)
    {
        if (c.Inferred is null) return c.Decimal!.Build();
        int scale = 0;
        foreach (var v in c.Inferred) if (v is { } d) scale = Math.Max(scale, (decimal.GetBits(d)[3] >> 16) & 0xFF);
        var builder = new Decimal128Array.Builder(new Decimal128Type(38, scale));
        foreach (var v in c.Inferred) if (v is { } d) builder.Append(d); else builder.AppendNull();
        return builder.Build();
    }

    /// <summary>The value at a row, boxed the way <see cref="QueryRowReader"/> boxes it.</summary>
    public static object? Value(IArrowArray a, int i) => a switch
    {
        Int64Array x => x.GetValue(i),
        DoubleArray x => x.GetValue(i),
        Decimal128Array x => x.GetValue(i),
        BooleanArray x => x.GetValue(i),
        StringArray x => x.IsNull(i) ? null : x.GetString(i),
        _ => throw new NotSupportedException($"Arrow array {a.Data.DataType.Name} is not a column of a source read."),
    };

    public static QueryRows ToRows(ColumnarRows c)
    {
        var arrays = Enumerable.Range(0, c.Batch.ColumnCount).Select(c.Batch.Column).ToArray();
        var rows = new List<Dictionary<string, object?>>(c.Count);
        for (int r = 0; r < c.Count; r++)
        {
            var row = new Dictionary<string, object?>(arrays.Length);
            for (int i = 0; i < arrays.Length; i++) row[c.Columns[i]] = Value(arrays[i], r);
            rows.Add(row);
        }
        return new QueryRows(c.Columns, rows);
    }
}
