using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>The logical column types of the conventions (plan 29 section 3.3), as one engine-neutral list: <c>{{key}}</c> and <c>{{text}}</c> are both <see cref="Text"/>.</summary>
internal enum LogicalType
{
    Text,
    Int,
    Uuid,
    Time,
    Json,
    Bool,
    Decimal,
    Blob,
}

/// <summary>One column of a module's table, in declaration order, with the logical type its template gave it.</summary>
internal sealed record LogicalColumn(string Name, LogicalType Type);

/// <summary>A module's table as its migrations declare it: the columns in order and the primary key's columns in key order.</summary>
internal sealed record LogicalTable(string Name, IReadOnlyList<LogicalColumn> Columns, IReadOnlyList<LogicalColumn> Key);

/// <summary>
/// What a table's columns mean, the same on every engine, and how a value is written in its neutral form. Used by <c>StateVerifier</c> and meant to be adopted by the snapshot exporter
/// and importer, so a digest computed from a database and one computed from a snapshot agree.
/// <para>
/// <b>Approach.</b> The column types are not read back from the engine's catalog (SQL Server's <c>NVARCHAR</c> and PostgreSQL's <c>TEXT</c> would not say whether a column is a key or a text, and SQLite's
/// <c>TEXT</c> does not say whether it holds a time or a uuid). They are read from the module's migration templates, which are the one place the logical type is written: every
/// <c>CREATE TABLE {table:x}</c> and <c>ALTER TABLE {table:x} {{addcolumn}}</c> is parsed for its <c>{{...}}</c> token, and <c>{{identity:col}}</c> is an int key. A token that is not a convention type
/// is an error naming the module, table and column.
/// </para>
/// <para>
/// <b>Neutral values</b> (<see cref="Normalise"/>): uuid as lower-case hyphenated text; time as UTC ISO text of fixed width with seven digits (<c>2026-11-07T10:00:00.1234567Z</c>; PostgreSQL
/// keeps microseconds, so a time with a tenth of a microsecond differs between it and the other engines, as it would in a move); bool as <c>true</c> or <c>false</c>; int as invariant text; decimal as invariant text
/// without trailing zeros; text as it is; json as canonical JSON (keys sorted, no white space: PostgreSQL's <c>JSONB</c> rewrites a document, SQLite and SQL Server keep it as written);
/// blob as base64 in a row line.
/// </para>
/// <para>
/// <b>Row hash</b> (<see cref="HashRow"/>): SHA-256 of the row's snapshot line (a JSON array of neutral values). <b>Table digest</b> (<see cref="RowsDigest"/>): the sum of the row hashes modulo 2^256, the
/// same definition as the snapshot manifest's <c>rows_digest</c>, so it does not depend on row order. Rows are still read in a fixed key order (<see cref="OrderExpression"/>: uuid as lower-case text,
/// text in the engine's binary collation) so two tables can be walked together to name the rows that differ. The value rules deliberately mirror <c>Snapshots/SnapshotValues</c> (X01); once both are merged
/// the two should share one implementation.
/// </para>
/// </summary>
internal static partial class LogicalColumns
{
    private static readonly ConditionalWeakTable<StateModule, IReadOnlyDictionary<string, LogicalTable>> Cache = [];

    [GeneratedRegex(@"CREATE\s+TABLE\s+\{table:(?<table>[a-z0-9_]+)\}\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable { get; }

    [GeneratedRegex(@"ALTER\s+TABLE\s+\{table:(?<table>[a-z0-9_]+)\}\s+\{\{addcolumn\}\}\s+(?<name>\w+)\s+(?<type>\{\{\w+(?::\w+)?\}\})", RegexOptions.IgnoreCase)]
    private static partial Regex AddColumn { get; }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment { get; }

    /// <summary>The tables of <paramref name="module"/> by name, as its migrations declare them.</summary>
    public static IReadOnlyDictionary<string, LogicalTable> For(StateModule module) => Cache.GetValue(module, Parse);

    private static Dictionary<string, LogicalTable> Parse(StateModule module)
    {
        var columns = new Dictionary<string, List<LogicalColumn>>(StringComparer.Ordinal);
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var migration in module.Migrations)
        {
            var text = LineComment.Replace(migration, "");
            foreach (Match create in CreateTable.Matches(text))
            {
                var table = create.Groups["table"].Value;
                if (!columns.TryGetValue(table, out var list))
                {
                    columns[table] = list = [];
                    keys[table] = [];
                    order.Add(table);
                }

                foreach (var item in TopLevelItems(text, create.Index + create.Length))
                {
                    var words = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 0) continue;
                    if (words[0].Equals("PRIMARY", StringComparison.OrdinalIgnoreCase))
                    {
                        keys[table].AddRange(ColumnList(item));
                        continue;
                    }

                    if (words[0] is "FOREIGN" or "UNIQUE" or "CONSTRAINT" or "CHECK") continue;
                    if (words[0].StartsWith("{{identity:", StringComparison.Ordinal))
                    {
                        var name = words[0]["{{identity:".Length..^2];
                        list.Add(new LogicalColumn(name, LogicalType.Int));
                        keys[table].Add(name);
                        continue;
                    }

                    list.Add(new LogicalColumn(words[0], TypeOf(module, table, words[0], words.Length > 1 ? words[1] : "")));
                    if (item.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)) keys[table].Add(words[0]);
                }
            }

            foreach (Match add in AddColumn.Matches(text))
            {
                var table = add.Groups["table"].Value;
                if (!columns.TryGetValue(table, out var list))
                    throw new InvalidOperationException($"The module '{module.Name}' adds a column to the table '{table}', which no earlier migration creates.");
                list.Add(new LogicalColumn(add.Groups["name"].Value, TypeOf(module, table, add.Groups["name"].Value, add.Groups["type"].Value)));
            }
        }

        var result = new Dictionary<string, LogicalTable>(StringComparer.Ordinal);
        foreach (var table in order)
        {
            var key = keys[table].Select(k => columns[table].FirstOrDefault(c => c.Name == k)
                ?? throw new InvalidOperationException($"The primary key of '{module.Name}.{table}' names the column '{k}', which the table does not declare.")).ToList();
            if (key.Count == 0)
                throw new InvalidOperationException($"The table '{module.Name}.{table}' has no primary key, so its rows have no order a verification (or a snapshot) can rely on.");
            result[table] = new LogicalTable(table, columns[table], key);
        }

        return result;
    }

    private static LogicalType TypeOf(StateModule module, string table, string column, string token) => token switch
    {
        "{{key}}" or "{{text}}" => LogicalType.Text,
        "{{int}}" => LogicalType.Int,
        "{{uuid}}" => LogicalType.Uuid,
        "{{time}}" => LogicalType.Time,
        "{{json}}" => LogicalType.Json,
        "{{bool}}" => LogicalType.Bool,
        "{{decimal}}" => LogicalType.Decimal,
        "{{blob}}" => LogicalType.Blob,
        _ => throw new InvalidOperationException($"The column {module.Name}.{table}.{column} is declared as '{token}', which is not one of the convention types ({{{{key}}}}, {{{{text}}}}, {{{{int}}}}, {{{{uuid}}}}, {{{{time}}}}, {{{{json}}}}, {{{{bool}}}}, {{{{decimal}}}}, {{{{blob}}}})."),
    };

    // ---- values

    /// <summary>The neutral form of what a reader returned for a column of <paramref name="type"/>: null, a <see cref="string"/> or, for a blob, a <see cref="byte"/> array.</summary>
    public static object? Normalise(LogicalType type, object? raw)
    {
        if (raw is null or DBNull) return null;
        return type switch
        {
            LogicalType.Text => raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture),
            LogicalType.Int => Convert.ToInt64(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            LogicalType.Uuid => StateValues.ToGuid(raw).ToString("D", CultureInfo.InvariantCulture),
            LogicalType.Time => StateValues.FormatTime(StateValues.ToTime(raw)),
            LogicalType.Bool => Convert.ToBoolean(raw, CultureInfo.InvariantCulture) ? "true" : "false",
            LogicalType.Decimal => DecimalText(StateValues.ToDecimal(raw)),
            LogicalType.Json => CanonicalJson(raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture)!),
            LogicalType.Blob => raw as byte[] ?? throw new InvalidCastException($"A blob column held a {raw.GetType().Name}."),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private static string DecimalText(decimal value) => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A JSON document with its object members sorted by name (ordinal) and no white space; numbers and strings are kept as written. The same rule as the snapshot exporter's, so a database and a
    /// manifest agree. Text that is not valid JSON is returned as it is.
    /// </summary>
    public static string CanonicalJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                WriteCanonical(writer, document.RootElement);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static readonly JsonWriterOptions RowOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    /// <summary>
    /// The hash of one row: SHA-256 of its line as the snapshot writes it, a JSON array (no white space, no line break) of the columns' neutral values in declaration order: uuid as lower-case text, time as
    /// fixed-width UTC text, bool as a JSON boolean, int as a JSON number, decimal as text without trailing zeros, text as a string, json as canonical-JSON text and a blob as a base64 string, NULL as null.
    /// </summary>
    public static byte[] HashRow(IReadOnlyList<LogicalType> types, object?[] raw)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer, RowOptions))
        {
            json.WriteStartArray();
            for (var i = 0; i < types.Count; i++) WriteValue(json, types[i], raw[i]);
            json.WriteEndArray();
        }

        return SHA256.HashData(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter json, LogicalType type, object? raw)
    {
        if (raw is null or DBNull)
        {
            json.WriteNullValue();
            return;
        }

        switch (type)
        {
            case LogicalType.Int: json.WriteNumberValue(Convert.ToInt64(raw, CultureInfo.InvariantCulture)); break;
            case LogicalType.Bool: json.WriteBooleanValue(Convert.ToBoolean(raw, CultureInfo.InvariantCulture)); break;
            case LogicalType.Blob: json.WriteBase64StringValue((byte[])raw); break;
            default: json.WriteStringValue((string)Normalise(type, raw)!); break;
        }
    }

    /// <summary>Adds row hashes modulo 2^256, each read as a big-endian number: the table digest, which does not depend on the order rows were read in (engines order uuid and text keys differently).</summary>
    public sealed class RowsDigest
    {
        private readonly byte[] _sum = new byte[32];

        public void Add(ReadOnlySpan<byte> hash)
        {
            var carry = 0;
            for (var i = 31; i >= 0; i--)
            {
                var value = _sum[i] + hash[i] + carry;
                _sum[i] = (byte)value;
                carry = value >> 8;
            }
        }

        public string Hex() => Convert.ToHexStringLower(_sum);
    }

    // ---- key order

    /// <summary>The key's neutral values (a <see cref="long"/> for an int, a <see cref="string"/> for everything else), compared by <see cref="Compare"/>.</summary>
    public static IReadOnlyList<object> KeyOf(IReadOnlyList<LogicalColumn> key, object?[] rawKey) =>
        [.. key.Select((c, i) => c.Type == LogicalType.Int
            ? Convert.ToInt64(rawKey[i], CultureInfo.InvariantCulture)
            : (object)(Normalise(c.Type, rawKey[i]) as string ?? throw new InvalidOperationException($"A key column ({c.Name}) held a value that cannot be ordered.")))];

    public static int Compare(IReadOnlyList<object> a, IReadOnlyList<object> b)
    {
        for (var i = 0; i < a.Count; i++)
        {
            var c = a[i] is long x ? x.CompareTo((long)b[i]) : string.CompareOrdinal((string)a[i], (string)b[i]);
            if (c != 0) return c;
        }

        return 0;
    }

    public static string KeyText(IReadOnlyList<object> key) => string.Join("/", key.Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)));

    /// <summary>The SQL expression a key column is ordered and paged by, the same on every engine: a uuid as lower-case text, text in the engine's binary collation, the rest as it is.</summary>
    public static string OrderExpression(StateDialect dialect, LogicalColumn column) => column.Type switch
    {
        LogicalType.Uuid => Collate(dialect, dialect.UuidAsText(column.Name)),
        LogicalType.Text => Collate(dialect, column.Name),
        _ => column.Name,
    };

    private static string Collate(StateDialect dialect, string expression) => dialect.Engine switch
    {
        StateEngineIds.Postgres => expression + " COLLATE \"C\"",
        StateEngineIds.MsSql => expression + " COLLATE Latin1_General_100_BIN2",
        _ => expression,
    };

    /// <summary>The SQL expression for the byte length of a blob column, or null for an engine that is not built in.</summary>
    public static string? BlobLength(StateDialect dialect, string column) => dialect.Engine switch
    {
        StateEngineIds.Sqlite => $"length({column})",
        StateEngineIds.Postgres => $"octet_length({column})",
        StateEngineIds.MsSql => $"DATALENGTH({column})",
        _ => null,
    };

    // ---- parsing helpers (the same shape as StateConventions')

    private static IEnumerable<string> TopLevelItems(string text, int start)
    {
        var depth = 1;
        var inString = false;
        var item = new StringBuilder();
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\'') inString = false;
            }
            else if (c == '\'') inString = true;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0)
            {
                yield return item.ToString().Trim();
                yield break;
            }
            else if (c == ',' && depth == 1)
            {
                yield return item.ToString().Trim();
                item.Clear();
                continue;
            }

            item.Append(c);
        }

        yield return item.ToString().Trim();
    }

    private static IEnumerable<string> ColumnList(string item)
    {
        var open = item.IndexOf('(', StringComparison.Ordinal);
        if (open < 0) yield break;
        foreach (var part in TopLevelItems(item, open + 1))
        {
            var name = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(name)) yield return name;
        }
    }
}
