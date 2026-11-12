using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DbDataKit.Catalog;

/// <summary>The writing side of the catalog cache (plan 33 SC20j): what a paired Gateway answered to the read-only schema call becomes the file <c>catalog-cache.yaml</c> of a draft, and a refresh of one connection keeps the others as they were.</summary>
public static partial class CatalogCache
{
    /// <summary>
    /// The cache with the connection <paramref name="connection"/> replaced by the shape in <paramref name="schema"/> (the answer of the Gateway's <c>POST /api/v1/data-sources/{name}/schema</c>: <c>engine</c> and <c>tables</c> each with <c>schema</c>, <c>name</c>,
    /// <c>kind</c> and <c>columns</c> of <c>name</c>, <c>type</c>, <c>nullable</c>, <c>key</c>). The other connections of <paramref name="existing"/> stay; <c>source</c> is the server of this refresh (a file has one), <c>generated_at</c> is <paramref name="now"/>.
    /// Only those fields are read from the answer: whatever else it says is not copied.
    /// </summary>
    public static CatalogCacheFile Refreshed(CatalogCacheFile? existing, string connection, JsonObject schema, string server, string product, DateTimeOffset now)
    {
        var tables = new List<CacheTable>();
        foreach (var t in (schema["tables"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var columns = (t["columns"] as JsonArray)?.OfType<JsonObject>().Select(c => new CacheColumn((string?)c["name"] ?? "", (string?)c["type"] ?? "unknown", (bool?)c["nullable"] ?? true, (bool?)c["key"] ?? false)).Where(c => c.Name.Length > 0).ToList() ?? [];
            if (columns.Count == 0) continue;                 // a table whose columns could not be read is not cached: the format wants columns
            tables.Add(new CacheTable((string?)t["schema"] is { Length: > 0 } schemaName ? schemaName : "default", (string?)t["name"] ?? "", (string?)t["kind"] == "view" ? "view" : "table", columns));
        }
        var connections = new Dictionary<string, CacheConnection>(existing?.Connections ?? new Dictionary<string, CacheConnection>(), StringComparer.Ordinal)
        {
            [connection] = new CacheConnection((string?)schema["engine"] is { Length: > 0 } engine ? engine : "unknown", tables),
        };
        return new CatalogCacheFile(now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), server, product, connections);
    }

    /// <summary>The file's text for a cache: the keys of the format in a fixed order, connections and tables in name order, every text quoted. A value with a control character is refused (a name that cannot be a line of the file).</summary>
    public static string Write(CatalogCacheFile cache)
    {
        var b = new StringBuilder();
        b.Append("schema: ").Append(SchemaPrefix).Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        b.Append("generated_at: ").Append(Q(cache.GeneratedAt)).Append('\n');
        b.Append("source: {server: ").Append(Q(cache.Server)).Append(", product: ").Append(Q(cache.Product)).Append("}\n");
        b.Append("connections:\n");
        foreach (var (name, connection) in cache.Connections.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            b.Append("  ").Append(Q(name)).Append(":\n");
            b.Append("    engine: ").Append(Q(connection.Engine)).Append('\n');
            if (connection.Tables.Count == 0) { b.Append("    tables: []\n"); continue; }
            b.Append("    tables:\n");
            foreach (var t in connection.Tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
            {
                b.Append("      - schema: ").Append(Q(t.Schema)).Append('\n');
                b.Append("        name: ").Append(Q(t.Name)).Append('\n');
                b.Append("        kind: ").Append(t.Kind).Append('\n');
                b.Append("        columns:\n");
                foreach (var c in t.Columns)
                    b.Append("          - {name: ").Append(Q(c.Name)).Append(", type: ").Append(Q(c.Type)).Append(", nullable: ").Append(c.Nullable ? "true" : "false").Append(c.Key ? ", key: true" : "").Append("}\n");
            }
        }
        return b.ToString();
    }

    private static string Q(string text)
    {
        if (text.Any(char.IsControl)) throw new InvalidDataException("A name in the catalog has a control character, which a catalog cache cannot hold.");
        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
