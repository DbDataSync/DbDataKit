using System.Globalization;
using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>
/// The "unknown" row of a model that owns a key with <c>unknown_member: true</c> (plan 25 section 1a, K08): a call of the key with a NULL part gives <see cref="DbDataKit.Dialect.SurrogateKey.UnknownKey"/>, and the
/// owning model holds a row with that key, so a fact that points at the unknown member joins to something. The row is added to the model's query (<c>UNION ALL BY NAME</c>, so the order of the query's columns does not
/// matter) before it is lowered, so it is in the view, the table and the rendered load alike. Its key columns are the reserved key; a column that can be NULL is NULL; one that cannot is given the plainest value of its type.
/// </summary>
public static class UnknownMembers
{
    /// <summary>True when the model owns a key that asks for the row.</summary>
    public static bool Wanted(ModelDefinition model) => model.Keys.Any(k => k.UnknownMember && k.Method == KeyMethods.Hash);

    /// <summary>The query with the unknown row added (the query itself when the model wants none).</summary>
    public static string Apply(ModelDefinition model, string query)
    {
        if (!Wanted(model) || model.Columns.Count == 0) return query;
        var keyColumns = model.Keys.Where(k => k.UnknownMember).SelectMany(k => new[] { k.Name, k.VersionKey }).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var row = string.Join(", ", model.Columns.Select(c => $"CAST({ValueFor(c, keyColumns.Contains(c.Name))} AS {c.Type}) AS \"{c.Name.Replace("\"", "\"\"")}\""));
        var body = query.TrimEnd().TrimEnd(';');
        return $"SELECT * FROM (\n{body}\n) AS \"unknown_member_source\"\nUNION ALL BY NAME\nSELECT {row}\n";
    }

    private static string ValueFor(ColumnDefinition column, bool isKey)
    {
        if (isKey) return DbDataKit.Dialect.SurrogateKey.UnknownKey.ToString(CultureInfo.InvariantCulture);
        if (column.Nullable) return "NULL";
        var type = Regex.Replace(column.Type.Trim().ToUpperInvariant(), @"\(.*$", "").Trim();
        return type switch
        {
            "TINYINT" or "SMALLINT" or "INTEGER" or "INT" or "BIGINT" or "HUGEINT" or "DECIMAL" or "NUMERIC" or "DOUBLE" or "FLOAT" or "REAL" => "0",
            "BOOLEAN" or "BOOL" => "false",
            "DATE" => "DATE '1900-01-01'",
            "TIMESTAMP" or "DATETIME" or "TIMESTAMPTZ" => "TIMESTAMP '1900-01-01 00:00:00'",
            "TIME" => "TIME '00:00:00'",
            "VARCHAR" or "CHAR" or "TEXT" or "STRING" or "NVARCHAR" or "NCHAR" => $"substr('Unknown', 1, {Math.Min(7, LengthOf(column.Type))})",
            _ => "NULL",
        };
    }

    private static int LengthOf(string type) => Regex.Match(type, @"\((\d+)\)") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : 7;
}
