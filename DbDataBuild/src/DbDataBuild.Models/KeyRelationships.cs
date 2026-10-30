using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>A relationship a model's query states by calling another model's key (plan 25 section 1a, 5): its output column holds the key of a row of <see cref="Owner"/>.</summary>
/// <param name="Role">The output column's name when it is not the key's own name (`order_date_key` and `return_date_key` are two roles of `date_key`); null when the column is named like the key.</param>
public sealed record KeyRelationship(string Model, string Column, string Owner, string Key, string? Role);

/// <summary>
/// Relationships are inferred from calls, never declared: a call of a key function that is given an output column name (`customer_key(...) AS customer_key`) says that column refers to the model that owns the key. A call
/// in the owning model itself is the key's own column, not a relationship. A call with no alias (inside an expression, or in a condition) names no column and records nothing.
/// </summary>
public static class KeyRelationships
{
    private static readonly Regex Alias = new(@"^\s+AS\s+(?:""((?:[^""]|"""")+)""|([A-Za-z_]\w*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Call = new(@"(?<![\w.])([A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant);

    public static IReadOnlyList<KeyRelationship> Infer(IEnumerable<(ModelDefinition Model, string Sql)> queries, IEnumerable<ModelDefinition> owners)
    {
        var ownerOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in owners)
            foreach (var key in m.Keys)
                foreach (var name in new[] { key.Name, key.VersionKey }.Where(n => n != null))
                    ownerOf[name!] = m.Name;
        var found = new List<KeyRelationship>();
        if (ownerOf.Count == 0) return found;
        foreach (var (model, sql) in queries)
        {
            var scrubbed = MacroLibrary.Scrub(sql);
            foreach (Match call in Call.Matches(scrubbed))
            {
                if (!ownerOf.TryGetValue(call.Groups[1].Value, out var owner) || owner == model.Name) continue;
                var close = Closing(scrubbed, call.Index + call.Length);
                if (close < 0 || Alias.Match(scrubbed[(close + 1)..]) is not { Success: true } alias) continue;
                // the alias is read from the original text: the scrubbed one blanks the insides of a quoted name
                var column = alias.Groups[1].Success ? sql.Substring(close + 1 + alias.Groups[1].Index, alias.Groups[1].Length).Replace("\"\"", "\"") : alias.Groups[2].Value;
                var key = call.Groups[1].Value;
                found.Add(new KeyRelationship(model.Name, column, owner, key, string.Equals(column, key, StringComparison.OrdinalIgnoreCase) ? null : column));
            }
        }
        return found.Distinct().OrderBy(r => r.Model, StringComparer.Ordinal).ThenBy(r => r.Column, StringComparer.Ordinal).ToList();
    }

    private static int Closing(string text, int from)
    {
        var depth = 1;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }
}
