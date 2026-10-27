using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>
/// Validates and runs agent-written DuckDB SQL over result result sets only.
///
/// This uses DuckDB's own parser through json_serialize_sql, which only serializes SELECT statements.
/// The JSON shape walked below
/// (BASE_TABLE, TABLE_FUNCTION, cte_map) should be checked against the DuckDB version you pin.
/// Defense in depth: execution also runs with external access disabled and configuration locked.
/// </summary>
public static class Sandbox
{
    private static readonly Dictionary<string, string> TypeSql = new()
    {
        ["string"] = "VARCHAR", ["number"] = "DOUBLE", ["integer"] = "BIGINT", ["boolean"] = "BOOLEAN",
    };

    private static QisError Invalid(string message, List<string>? suggestions = null) =>
        new("mashup", "escape_hatch_invalid", "$.sql", message, suggestions);

    public static List<QisError> Validate(string sql, IReadOnlySet<string> allowed)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        // json_serialize_sql only accepts a constant, so the SQL goes in as an escaped literal, not a parameter.
        cmd.CommandText = $"SELECT json_serialize_sql('{sql.Replace("'", "''")}')";
        var json = JsonNode.Parse((string)cmd.ExecuteScalar()!)!.AsObject();

        if (json["error"]?.GetValue<bool>() == true)
            return [Invalid($"Only one SELECT query is allowed: {json["error_message"]}")];
        var statements = json["statements"]!.AsArray();
        if (statements.Count != 1)
            return [Invalid("Exactly one statement is allowed.")];

        var errors = new List<QisError>();
        var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tables = new List<JsonObject>();
        Walk(statements[0], ctes, tables, errors);

        foreach (var t in tables)
        {
            var name = t["table_name"]?.GetValue<string>() ?? "";
            var schema = t["schema_name"]?.GetValue<string>() ?? "";
            var catalog = t["catalog_name"]?.GetValue<string>() ?? "";
            if (schema.Length > 0 || catalog.Length > 0)
                errors.Add(Invalid($"Qualified table {catalog}.{schema}.{name} isn't allowed."));
            else if (!allowed.Contains(name) && !ctes.Contains(name))
                errors.Add(Invalid($"{name} isn't a result set in this result.", allowed.Order().ToList()));
        }
        return errors;
    }

    private static void Walk(JsonNode? node, HashSet<string> ctes, List<JsonObject> tables, List<QisError> errors)
    {
        switch (node)
        {
            case JsonObject o:
                var type = o["type"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? o["type"]!.GetValue<string>() : null;
                if (type == "BASE_TABLE") tables.Add(o);
                if (type == "TABLE_FUNCTION") errors.Add(Invalid("Table functions such as read_csv aren't allowed."));
                if (o["cte_map"]?["map"] is JsonArray map)
                    foreach (var entry in map)
                        if (entry?["key"]?.GetValue<string>() is { } key) ctes.Add(key);
                foreach (var (_, child) in o) Walk(child, ctes, tables, errors);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child, ctes, tables, errors);
                break;
        }
    }

    /// <summary>Loads result sets as tables in a fresh DuckDB, locks it down, runs the SQL.</summary>
    public static QueryRows Run(string sql, IEnumerable<ResultSet> resultSets, int maxRows)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        foreach (var ds in resultSets)
        {
            var fields = CreateTable(con, ds.Id, ds.Descriptor);
            if (ds.Columnar is { } columnar) { Append(con, ds.Id, fields, columnar); continue; }
            // The appender writes the rows in bulk; one parameterized INSERT per row took about 1.5 ms each (plan 19 J21).
            using var appender = con.CreateAppender(ds.Id);
            foreach (var row in ds.Rows)
            {
                var line = appender.CreateRow();
                foreach (var f in fields) Append(line, row.GetValueOrDefault(f.Name), f.Type);
                line.EndRow();
            }
        }
        return Query(con, sql, maxRows);
    }

    /// <summary>A result set that arrived as Arrow columns (<see cref="IColumnarSourceExecutor"/>): its table id, descriptor and rows.</summary>
    public sealed record ColumnarTable(string Id, JsonObject Descriptor, ColumnarRows Rows);

    /// <summary>
    /// <see cref="Run"/> for tables read as Arrow: each value goes from its array into the appender typed, without a dictionary or a boxed value per cell
    /// (plan 19 J24). A column the descriptor types differently from the array is converted; a column the array lacks is NULL.
    /// </summary>
    public static QueryRows RunColumnar(string sql, IEnumerable<ColumnarTable> tables, int maxRows)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        foreach (var t in tables)
        {
            var fields = CreateTable(con, t.Id, t.Descriptor);
            Append(con, t.Id, fields, t.Rows);
        }
        return Query(con, sql, maxRows);
    }

    private static void Append(DuckDBConnection con, string id, List<(string Name, string Type)> fields, ColumnarRows rows)
    {
        var batch = rows.Batch;
        var writers = fields.Select(f => ColumnWriter(batch.Column(f.Name), f.Type)).ToArray();
        using var appender = con.CreateAppender(id);
        for (int r = 0; r < batch.Length; r++)
        {
            var line = appender.CreateRow();
            foreach (var w in writers) w(line, r);
            line.EndRow();
        }
    }

    private static List<(string Name, string Type)> CreateTable(DuckDBConnection con, string id, JsonObject descriptor)
    {
        var fields = descriptor["schema"]!["fields"]!.AsArray()
            .Select(f => (Name: f!["name"]!.GetValue<string>(), Type: f["type"]!.GetValue<string>())).ToList();
        Exec(con, $"CREATE TABLE \"{id}\" ({string.Join(", ", fields.Select(f => $"\"{f.Name}\" {TypeSql.GetValueOrDefault(f.Type, "VARCHAR")}"))})");
        return fields;
    }

    private static QueryRows Query(DuckDBConnection con, string sql, int maxRows)
    {
        Exec(con, "SET enable_external_access = false");
        Exec(con, "SET lock_configuration = true");
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return QueryRowReader.Read(cmd, maxRows);
    }

    /// <summary>The writer of one Arrow column into the appender as the descriptor's type; an Arrow type that already matches is read without boxing.</summary>
    private static Action<IDuckDBAppenderRow, int> ColumnWriter(Apache.Arrow.IArrowArray? array, string type)
    {
        switch (array, type)
        {
            case (null, _): return (row, _) => row.AppendNullValue();
            case (Apache.Arrow.Int64Array a, "integer"): return (row, i) => { if (a.GetValue(i) is { } v) row.AppendValue(v); else row.AppendNullValue(); };
            case (Apache.Arrow.DoubleArray a, "number"): return (row, i) => { if (a.GetValue(i) is { } v) row.AppendValue(v); else row.AppendNullValue(); };
            case (Apache.Arrow.BooleanArray a, "boolean"): return (row, i) => { if (a.GetValue(i) is { } v) row.AppendValue(v); else row.AppendNullValue(); };
            case (Apache.Arrow.StringArray a, not ("integer" or "number" or "boolean")): return (row, i) => { if (a.IsNull(i)) row.AppendNullValue(); else row.AppendValue(a.GetString(i)); };
            default: return (row, i) => Append(row, ArrowRows.Value(array, i), type);
        }
    }

    /// <summary>One value into the appender row as the column's type (the table is created from the descriptor: string, number, integer, boolean, else VARCHAR).</summary>
    private static void Append(IDuckDBAppenderRow row, object? value, string type)
    {
        if (value is null or DBNull) { row.AppendNullValue(); return; }
        switch (type)
        {
            case "integer": row.AppendValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "number": row.AppendValue(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "boolean": row.AppendValue(Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            default: row.AppendValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }

    private static void Exec(DuckDBConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
