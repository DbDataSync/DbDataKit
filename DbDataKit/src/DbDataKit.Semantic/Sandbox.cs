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
            var fields = ds.Descriptor["schema"]!["fields"]!.AsArray()
                .Select(f => (Name: f!["name"]!.GetValue<string>(), Type: f["type"]!.GetValue<string>())).ToList();
            Exec(con, $"CREATE TABLE \"{ds.Id}\" ({string.Join(", ", fields.Select(f => $"\"{f.Name}\" {TypeSql.GetValueOrDefault(f.Type, "VARCHAR")}"))})");
            // The appender writes the rows in bulk; one parameterized INSERT per row took about 1.5 ms each (plan 19 J21).
            using var appender = con.CreateAppender(ds.Id);
            foreach (var row in ds.Rows)
            {
                var line = appender.CreateRow();
                foreach (var f in fields) Append(line, row.GetValueOrDefault(f.Name), f.Type);
                line.EndRow();
            }
        }
        Exec(con, "SET enable_external_access = false");
        Exec(con, "SET lock_configuration = true");
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return QueryRowReader.Read(cmd, maxRows);
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
