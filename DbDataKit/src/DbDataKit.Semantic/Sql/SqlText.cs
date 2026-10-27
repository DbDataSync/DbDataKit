using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace DbDataKit.Semantic.Sql;

/// <summary>The writing of semantic SQL: identifiers, dotted member names and JSON values as SQL literals.</summary>
internal static partial class SqlText
{
    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex PlainIdentifier();

    // Words DuckDB's parser will not take as a column name, from DuckDB itself, so the list is the pinned version's.
    private static readonly Lazy<HashSet<string>> Reserved = new(() =>
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT keyword_name FROM duckdb_keywords() WHERE keyword_category IN ('reserved', 'type_function')";
        using var reader = cmd.ExecuteReader();
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) words.Add(reader.GetString(0));
        return words;
    });

    /// <summary>One identifier: as it is when it is a plain lower-case word that is not reserved, else in double quotes.</summary>
    public static string Ident(string name) =>
        PlainIdentifier().IsMatch(name) && !Reserved.Value.Contains(name) ? name : "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>A member name such as <c>customers.region</c>: each part an identifier.</summary>
    public static string Path(string dotted) => string.Join('.', dotted.Split('.').Select(Ident));

    /// <summary>A string literal.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>A JSON value as a SQL literal: a string, a number as written, TRUE, FALSE, NULL, a list <c>[a, b]</c>, an object <c>{'k': v}</c>.</summary>
    public static string Literal(JsonNode? node)
    {
        switch (node)
        {
            case null: return "NULL";
            case JsonValue v:
                return v.GetValueKind() switch
                {
                    JsonValueKind.String => Quote(v.GetValue<string>()),
                    JsonValueKind.Number => v.ToJsonString(),
                    JsonValueKind.True => "TRUE",
                    JsonValueKind.False => "FALSE",
                    _ => "NULL",
                };
            case JsonArray a: return "[" + string.Join(", ", a.Select(Literal)) + "]";
            case JsonObject o: return "{" + string.Join(", ", o.Select(p => $"{Quote(p.Key)}: {Literal(p.Value)}")) + "}";
            default: throw new ArgumentException("not a JSON value");
        }
    }

    /// <summary>The parameter name of a named argument: it must be a plain word, as the query's own keys are.</summary>
    public static bool IsArgumentName(string name) => PlainIdentifier().IsMatch(name);

    public static StringBuilder Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
