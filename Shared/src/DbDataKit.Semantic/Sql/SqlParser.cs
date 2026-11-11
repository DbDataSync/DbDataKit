using System.Text.Json.Nodes;
using DuckDB.NET.Data;

namespace DbDataKit.Semantic.Sql;

/// <summary>SQL that DuckDB's parser refused.</summary>
public sealed class SqlSyntaxException(string message) : Exception(message);

/// <summary>DuckDB's own parser (<c>json_serialize_sql</c>), so semantic SQL needs no grammar of its own: SQL text to the syntax tree of one SELECT.</summary>
internal static class SqlParser
{
    /// <summary>The node of the single SELECT statement in <paramref name="sql"/>.</summary>
    public static JsonObject ParseSelect(string sql)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        // json_serialize_sql only accepts a constant, so the text goes in as an escaped literal
        cmd.CommandText = $"SELECT json_serialize_sql('{sql.Replace("'", "''")}')";
        var json = JsonNode.Parse((string)cmd.ExecuteScalar()!)!.AsObject();
        if (json["error"]?.GetValue<bool>() == true)
            throw new SqlSyntaxException((json["error_message"]?.GetValue<string>() ?? "the text is not a SELECT query").Split('\n')[0]);
        var statements = json["statements"]!.AsArray();
        if (statements.Count != 1) throw new SqlSyntaxException("exactly one statement is allowed");
        return statements[0]!["node"]!.AsObject();
    }
}

