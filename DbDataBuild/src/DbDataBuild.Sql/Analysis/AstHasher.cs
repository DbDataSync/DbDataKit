using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataBuild.Sql.Analysis;

/// <summary>
/// The definition hash of a model body (DESIGN.md 12.1): a hash of the normalized DuckDB-dialect AST, so comments, whitespace and
/// formatting changes do not register, while any change in meaning does.
/// </summary>
public static class AstHasher
{
    // fields polyglot attaches for positions and comments; they say nothing about what the query means
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "span", "trailing_comments", "left_comments", "operator_comments", "leading_comments", "comments", "comment",
    };

    public static (string? Hash, string? Error) Hash(string sql)
    {
        var parsed = Polyglot.Parse(sql, Dialects.Canonical);
        if (!parsed.Ok) return (null, parsed.Error);
        var node = JsonNode.Parse(parsed.Data!)!;
        Normalize(node);
        var canonical = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(), null);
    }

    private static void Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).Where(Ignored.Contains).ToList()) o.Remove(key);
                foreach (var (_, v) in o.ToList()) Normalize(v);
                break;
            case JsonArray a:
                foreach (var v in a) Normalize(v);
                break;
        }
    }
}
