using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataKit.Dialect;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataProxy.Data;

/// <summary>
/// One statement parameter in a transport-neutral form: a type tag plus the value as invariant text. Text keeps it
/// lossless through JSON, which is how parameters reach the 32-bit worker process.
/// </summary>
/// <param name="Name">The Arrow column name the client used; only meaningful for statements with named markers.</param>
/// <param name="Type">One of null, bool, int64, uint64, double, decimal, string, date, timestamp, time, binary.</param>
/// <param name="Text">The value ("o" format for dates and times, base64 for binary); null for SQL NULL.</param>
public sealed record QueryParameter(string? Name, string Type, string? Text)
{
    /// <summary>The CLR value and ADO.NET type to bind.</summary>
    public (object Value, DbType DbType) ToClr()
    {
        if (Text is null || Type == "null") return (DBNull.Value, DbType.String);
        var inv = CultureInfo.InvariantCulture;
        return Type switch
        {
            "bool" => (bool.Parse(Text), DbType.Boolean),
            // Older ODBC drivers (the text driver, for one) reject SQL_BIGINT parameters, so values that fit bind as 32-bit.
            "int64" => long.Parse(Text, inv) is var n && n is >= int.MinValue and <= int.MaxValue ? ((int)n, DbType.Int32) : (n, DbType.Int64),
            "uint64" => (decimal.Parse(Text, inv), DbType.Decimal),
            "double" => (double.Parse(Text, inv), DbType.Double),
            "decimal" => (decimal.Parse(Text, inv), DbType.Decimal),
            "string" => (Text, DbType.String),
            "date" => (DateTime.ParseExact(Text, "o", inv, DateTimeStyles.RoundtripKind), DbType.Date),
            "timestamp" => (DateTime.ParseExact(Text, "o", inv, DateTimeStyles.RoundtripKind), DbType.DateTime2),
            "time" => (TimeSpan.ParseExact(Text, "c", inv), DbType.Time),
            "binary" => (Convert.FromBase64String(Text), DbType.Binary),
            _ => throw new UnsupportedValueException($"Unsupported parameter type '{Type}'."),
        };
    }
}

/// <summary>Converts between Arrow parameter batches (what Flight SQL clients send) and <see cref="QueryParameter"/> rows.</summary>
public static class ArrowParameters
{
    /// <summary>Reads one row of a parameter batch; each column is one parameter, in order.</summary>
    public static List<QueryParameter> ReadRow(RecordBatch batch, int row)
    {
        var parameters = new List<QueryParameter>(batch.ColumnCount);
        for (int c = 0; c < batch.ColumnCount; c++)
            parameters.Add(Read(batch.Schema.GetFieldByIndex(c).Name, batch.Column(c), row));
        return parameters;
    }

    private static QueryParameter Read(string name, IArrowArray array, int row)
    {
        var inv = CultureInfo.InvariantCulture;
        if (array.IsNull(row)) return new QueryParameter(name, "null", null);
        return array switch
        {
            BooleanArray a => new(name, "bool", a.GetValue(row)!.Value.ToString()),
            Int8Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            Int16Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            Int32Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            Int64Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt8Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt16Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt32Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt64Array a => new(name, "uint64", a.GetValue(row)!.Value.ToString(inv)),
            FloatArray a => new(name, "double", ((double)a.GetValue(row)!.Value).ToString("R", inv)),
            DoubleArray a => new(name, "double", a.GetValue(row)!.Value.ToString("R", inv)),
            Decimal128Array a => new(name, "decimal", a.GetValue(row)!.Value.ToString(inv)),
            StringArray a => new(name, "string", a.GetString(row)),
            LargeStringArray a => new(name, "string", a.GetString(row)),
            Date32Array a => new(name, "date", a.GetDateTime(row)!.Value.Date.ToString("o", inv)),
            Date64Array a => new(name, "date", a.GetDateTime(row)!.Value.Date.ToString("o", inv)),
            TimestampArray a => new(name, "timestamp", a.GetTimestamp(row)!.Value.UtcDateTime.ToString("o", inv)),
            Time32Array a => new(name, "time", TimeSpan.FromTicks(a.GetTime(row)!.Value.Ticks).ToString("c", inv)),
            Time64Array a => new(name, "time", TimeSpan.FromTicks(a.GetTime(row)!.Value.Ticks).ToString("c", inv)),
            BinaryArray a => new(name, "binary", Convert.ToBase64String(a.GetBytes(row))),
            _ => throw new UnsupportedValueException($"Parameter '{name}' has Arrow type {array.Data.DataType.Name}, which is not supported."),
        };
    }

    /// <summary>
    /// Builds a single-row parameter batch from CLR values (client side). Each value becomes one column named p0, p1, ...
    /// unless <paramref name="names"/> supplies names.
    /// </summary>
    public static RecordBatch FromValues(IReadOnlyList<object?> values, IReadOnlyList<string>? names = null)
    {
        var fields = new List<Field>();
        var arrays = new List<IArrowArray>();
        for (int i = 0; i < values.Count; i++)
        {
            string name = names is not null && i < names.Count ? names[i] : "p" + i;
            var (type, array) = Build(values[i]);
            fields.Add(new Field(name, type, true));
            arrays.Add(array);
        }
        return new RecordBatch(new Schema(fields, null), arrays, 1);
    }

    /// <summary>A list parameter (a semantic model's filter values): an Arrow list of one value type, taken from the first element (strings, whole numbers, numbers or booleans).</summary>
    private static (IArrowType, IArrowArray) BuildList(System.Collections.IEnumerable list)
    {
        var items = list.Cast<object?>().ToList();
        var (itemType, _) = items.Count > 0 ? Build(items.First(i => i is not null) ?? "") : (StringType.Default, null);
        var field = new Field("item", itemType, true);
        var type = new ListType(field);
        IArrowArray values = itemType switch
        {
            Int64Type => new Int64Array.Builder().AppendRange(items.Select(i => Convert.ToInt64(i, CultureInfo.InvariantCulture))).Build(),
            DoubleType => new DoubleArray.Builder().AppendRange(items.Select(i => Convert.ToDouble(i, CultureInfo.InvariantCulture))).Build(),
            BooleanType => new BooleanArray.Builder().AppendRange(items.Select(i => Convert.ToBoolean(i, CultureInfo.InvariantCulture))).Build(),
            StringType => new StringArray.Builder().AppendRange(items.Select(i => Convert.ToString(i, CultureInfo.InvariantCulture) ?? "")).Build(),
            _ => throw new UnsupportedValueException($"Cannot send a list of {itemType.Name} as a statement parameter."),
        };
        var offsets = new ArrowBuffer.Builder<int>().Append(0).Append(items.Count).Build();
        var data = new ArrayData(type, 1, 0, 0, [ArrowBuffer.Empty, offsets], [values.Data]);
        return (type, new ListArray(data));
    }

    private static (IArrowType, IArrowArray) Build(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return (StringType.Default, new StringArray.Builder().AppendNull().Build());
            case bool b:
                return (BooleanType.Default, new BooleanArray.Builder().Append(b).Build());
            case byte or sbyte or short or ushort or int or uint or long:
                return (Int64Type.Default, new Int64Array.Builder().Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)).Build());
            case ulong u when u <= long.MaxValue:
                return (Int64Type.Default, new Int64Array.Builder().Append((long)u).Build());
            case ulong u:
                return (new Decimal128Type(20, 0), new Decimal128Array.Builder(new Decimal128Type(20, 0)).Append((decimal)u).Build());
            case float or double:
                return (DoubleType.Default, new DoubleArray.Builder().Append(Convert.ToDouble(value, CultureInfo.InvariantCulture)).Build());
            case decimal d:
            {
                var type = new Decimal128Type(38, Math.Min(28, (int)((decimal.GetBits(d)[3] >> 16) & 0xFF)));
                return (type, new Decimal128Array.Builder(type).Append(d).Build());
            }
            case string s:
                return (StringType.Default, new StringArray.Builder().Append(s).Build());
            case DateOnly d:
                return (Date32Type.Default, new Date32Array.Builder().Append(d.ToDateTime(TimeOnly.MinValue)).Build());
            case DateTime dt:
            {
                var type = new TimestampType(TimeUnit.Microsecond, (string?)null);
                var utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                return (type, new TimestampArray.Builder(type).Append(new DateTimeOffset(utc)).Build());
            }
            case DateTimeOffset dto:
            {
                var type = new TimestampType(TimeUnit.Microsecond, "UTC");
                return (type, new TimestampArray.Builder(type).Append(dto).Build());
            }
            case TimeOnly t:
                return (new Time64Type(TimeUnit.Microsecond), new Time64Array.Builder(new Time64Type(TimeUnit.Microsecond)).Append(t.Ticks / 10).Build());
            case byte[] bytes:
                return (BinaryType.Default, new BinaryArray.Builder().Append((ReadOnlySpan<byte>)bytes).Build());
            case Guid g:
                return (StringType.Default, new StringArray.Builder().Append(g.ToString()).Build());
            case System.Collections.IEnumerable list:
                return BuildList(list);
            default:
                throw new UnsupportedValueException($"Cannot send a {value.GetType().Name} as a statement parameter.");
        }
    }
}

/// <summary>How a provider marks parameters in SQL text.</summary>
public enum ParameterStyle
{
    /// <summary>Positional <c>?</c> markers, bound in order (ODBC, DuckDB).</summary>
    Positional,

    /// <summary>Named <c>@name</c> markers (SQL Server, and Npgsql for PostgreSQL, which has no <c>?</c>); <c>?</c> markers are rewritten to <c>@p1</c>, <c>@p2</c>, ...</summary>
    NamedAtSign,
}

/// <summary>Applies <see cref="QueryParameter"/> values to a command.</summary>
internal static class ParameterBinder
{
    public static void Bind(DbCommand command, string sql, IReadOnlyList<QueryParameter>? parameters, ParameterStyle style, ISqlDialect? dialect = null)
    {
        dialect ??= style == ParameterStyle.NamedAtSign ? ProviderDialects.SqlServer : ProviderDialects.Odbc;
        command.CommandText = sql;
        if (parameters is null || parameters.Count == 0) return;

        int markers = ParameterMarkers.Count(sql, dialect);
        if (markers > 0)
        {
            if (markers != parameters.Count)
                throw new ParameterMismatchException($"The statement has {markers} '?' markers but {parameters.Count} parameters were supplied.");
            if (style == ParameterStyle.NamedAtSign) command.CommandText = ParameterMarkers.RewriteToNamed(sql, dialect);
        }

        for (int i = 0; i < parameters.Count; i++)
        {
            var (value, dbType) = parameters[i].ToClr();
            // DuckDB.NET maps DateTime to TIMESTAMP whatever DbType says; DateOnly is how it binds a DATE.
            if (dbType == DbType.Date && value is DateTime day && command.GetType().Name == "DuckDBCommand")
                value = DateOnly.FromDateTime(day);
            var parameter = command.CreateParameter();
            // A NULL carries no type, and Npgsql sends a typed NULL as that type: a text NULL does not go into a numeric or date column (42804).
            // Left untyped, it is sent as "unknown" and the server takes the type from the statement, as the other engines do on their own.
            if (!(value is DBNull && command.GetType().Name == "NpgsqlCommand")) parameter.DbType = dbType;
            parameter.Value = value;
            // Positional markers need no name for ODBC/DuckDB; the named style always does.
            if (style == ParameterStyle.NamedAtSign)
                parameter.ParameterName = markers > 0 ? "@p" + (i + 1) : Prefixed(parameters[i].Name, i);
            else if (markers == 0 && parameters[i].Name is { Length: > 0 } name)
                parameter.ParameterName = name.TrimStart('@', '$', ':');
            command.Parameters.Add(parameter);
        }
    }

    private static string Prefixed(string? name, int index) =>
        string.IsNullOrEmpty(name) ? "@p" + (index + 1) : name.StartsWith('@') ? name : "@" + name;
}
