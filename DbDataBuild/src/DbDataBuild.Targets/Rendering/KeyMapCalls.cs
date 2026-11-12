using System.Text.RegularExpressions;
using DbDataKit.Dialect;

namespace DbDataBuild.Targets.Rendering;

/// <summary>A key kept in a map (plan 25 K10) that a lowered query selects: the key's name, the schema name of its owner (where its map is) and the output column that carries it.</summary>
public sealed record KeyMapSelection(string KeyName, string OwnerSchema, string Column)
{
    /// <summary>The map table as a model-style name (`marts.customer_key_map`).</summary>
    public string MapTable
    {
        get
        {
            var (schema, table) = SurrogateKey.KeyMapTable(OwnerSchema + ".x", KeyName);
            return schema.Length == 0 ? table : schema + "." + table;
        }
    }
}

/// <summary>
/// The calls of keys kept in a map in a query lowered for the step that adds new keys to the map (the query where each call is `ddb_keytext_...`, the business key's text). That step selects the text of each call as the
/// output column the call is named by, so a call must be a selected column, `customer_key(...) AS customer_key`; a call inside an expression, a join or a filter has no column to read it from and is refused.
/// </summary>
public static class KeyMapCalls
{
    private static readonly Regex Marker = new(@"\bddb_keytext_[tidm]+_\w+(?=\s*\()", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>True when the text holds a marker of a key kept in a map (the read or the text).</summary>
    public static bool Holds(string sql) => sql.Contains("ddb_keymap_", StringComparison.OrdinalIgnoreCase) || sql.Contains("ddb_keytext_", StringComparison.OrdinalIgnoreCase);

    public static (IReadOnlyList<KeyMapSelection> Selections, string? Problem) Find(string keyTextSql)
    {
        var found = new List<KeyMapSelection>();
        foreach (Match m in Marker.Matches(keyTextSql))
        {
            var parsed = SurrogateKey.ParseKeyMapMarker(m.Value)!.Value;
            var open = keyTextSql.IndexOf('(', m.Index + m.Length);
            var close = Close(keyTextSql, open);
            if (close < 0) return ([], $"The call of `{parsed.KeyName}` is not closed.");
            var schema = Regex.Match(keyTextSql[(open + 1)..close], @"^\s*'((?:[^']|'')*)'");
            if (!schema.Success) return ([], $"The call of `{parsed.KeyName}` has no owner schema. This is a tool bug.");

            var before = keyTextSql[..m.Index].TrimEnd();
            var selected = before.EndsWith(',') || before.EndsWith("SELECT", StringComparison.OrdinalIgnoreCase) || before.EndsWith("DISTINCT", StringComparison.OrdinalIgnoreCase);
            var alias = Regex.Match(keyTextSql[(close + 1)..], @"^\s*AS\s+(?:""((?:[^""]|"""")+)""|(\w+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!selected || !alias.Success)
                return ([], $"`{parsed.KeyName}` is kept in a map (`method: key_map`), so a query that calls it selects the call as a column, `{parsed.KeyName}(...) AS {parsed.KeyName}`: new keys are added to the map from the column, and a call in an expression, a join or a filter has none.");
            var selection = new KeyMapSelection(parsed.KeyName, schema.Groups[1].Value.Replace("''", "'"), alias.Groups[1].Success ? alias.Groups[1].Value.Replace("\"\"", "\"") : alias.Groups[2].Value);
            if (!found.Contains(selection)) found.Add(selection);
        }
        return (found, null);
    }

    private static int Close(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString) { if (c == '\'') inString = false; continue; }
            if (c == '\'') inString = true;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }
}
