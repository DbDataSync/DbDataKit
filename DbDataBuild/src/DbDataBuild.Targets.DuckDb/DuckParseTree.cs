using System.Text.Json;
using DuckDB.NET.Data;

namespace DbDataBuild.Targets.DuckDb;

/// <summary>
/// The tables a query reads, according to DuckDB's own parser (`json_serialize_sql`), which knows every syntax DuckDB does (PIVOT and UNPIVOT, table functions, nested subqueries) where the
/// offline SQL parser knows a subset. The query is parsed, never bound or run, and the engine is locked down first like every other use of it.
/// </summary>
public static class DuckParseTree
{
    public sealed record Table(string? SchemaName, string Name);

    public static (IReadOnlyList<Table>? Tables, string? Error) BaseTables(string sql)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" })
            {
                using var lockDown = connection.CreateCommand();
                lockDown.CommandText = setting;
                lockDown.ExecuteNonQuery();
            }
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT json_serialize_sql(CAST($sql AS VARCHAR))";
            var p = cmd.CreateParameter();
            p.ParameterName = "sql";
            p.Value = sql;
            cmd.Parameters.Add(p);
            var json = (string?)cmd.ExecuteScalar();
            if (json == null) return (null, "DuckDB returned no parse tree");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
                return (null, doc.RootElement.TryGetProperty("error_message", out var m) ? m.GetString() : "DuckDB could not parse the query");
            var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tables = new List<Table>();
            Walk(doc.RootElement, ctes, tables);
            return (tables.Where(t => t.SchemaName != null || !ctes.Contains(t.Name)).DistinctBy(t => (t.SchemaName ?? "").ToLowerInvariant() + "." + t.Name.ToLowerInvariant()).ToList(), null);
        }
        catch (DuckDBException ex) { return (null, ex.Message.Split('\n')[0]); }
    }

    /// <summary>A table the query names, with the alias the author gave it (null: none), in the order the planner reaches them: `FROM` (left to right, into subqueries), then `WHERE`, `GROUP BY`, `HAVING`, `QUALIFY`, then the select list.</summary>
    public sealed record AliasedTable(string? SchemaName, string Name, string? Alias);

    /// <summary>
    /// The tables of a query with the aliases written for them (DuckDB's plan does not carry aliases, so the lowered query would name each table after itself). Null when the order cannot be trusted to line up
    /// with the plan: the query has a CTE (it is planned once per use) or cannot be parsed.
    /// </summary>
    public static IReadOnlyList<AliasedTable>? TableAliases(string sql)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" })
            {
                using var lockDown = connection.CreateCommand();
                lockDown.CommandText = setting;
                lockDown.ExecuteNonQuery();
            }
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT json_serialize_sql(CAST($sql AS VARCHAR))";
            var p = cmd.CreateParameter();
            p.ParameterName = "sql";
            p.Value = sql;
            cmd.Parameters.Add(p);
            if (cmd.ExecuteScalar() is not string json) return null;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True) return null;
            var found = new List<AliasedTable>();
            var cte = false;
            WalkAliases(doc.RootElement, found, ref cte);
            return cte ? null : found;
        }
        catch (DuckDBException) { return null; }
    }

    private static readonly string[] SelectOrder = ["from_table", "where_clause", "group_expressions", "having", "qualify", "select_list"];

    private static void WalkAliases(JsonElement e, List<AliasedTable> found, ref bool cte)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    if (type.GetString() == "BASE_TABLE" && e.TryGetProperty("table_name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        string? Text(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
                        found.Add(new AliasedTable(Text("schema_name"), name.GetString()!, Text("alias")));
                    }
                }
                if (e.TryGetProperty("cte_map", out var map) && map.ValueKind == JsonValueKind.Object && map.TryGetProperty("map", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0) cte = true;
                var select = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "SELECT_NODE";
                var visited = new HashSet<string>();
                if (select)
                    foreach (var key in SelectOrder)
                        if (e.TryGetProperty(key, out var part)) { visited.Add(key); WalkAliases(part, found, ref cte); }
                foreach (var property in e.EnumerateObject())
                    if (!visited.Contains(property.Name)) WalkAliases(property.Value, found, ref cte);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) WalkAliases(item, found, ref cte);
                break;
        }
    }

    private static void Walk(JsonElement e, HashSet<string> ctes, List<Table> tables)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "BASE_TABLE" && e.TryGetProperty("table_name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var schema = e.TryGetProperty("schema_name", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } sn ? sn : null;
                    tables.Add(new Table(schema, name.GetString()!));
                }
                if (e.TryGetProperty("cte_map", out var map) && map.ValueKind == JsonValueKind.Object && map.TryGetProperty("map", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var entry in entries.EnumerateArray())
                        if (entry.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String) ctes.Add(key.GetString()!);
                foreach (var property in e.EnumerateObject()) Walk(property.Value, ctes, tables);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Walk(item, ctes, tables);
                break;
        }
    }
}
