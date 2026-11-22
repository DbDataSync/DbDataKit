using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DbDataKit.Catalog;

/// <summary>
/// The writing side of the catalog cache: what a paired server answered about a connection's shape (a Runner's <c>discover</c>, a Gateway's schema call) becomes a connection of the file <c>catalog-cache.yaml</c> of a draft; a refresh of one connection
/// keeps the others as they were, and a refresh of one table keeps the rest of its connection.
/// </summary>
public static partial class CatalogCache
{
    /// <summary>The time as the cache writes it: UTC to the second.</summary>
    public static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// A connection as an answer describes it. The answer is the shape a connection has in the cache, as JSON: <c>engine</c>, <c>databases</c> (each <c>name</c> and <c>schemas</c>) and <c>tables</c> (each <c>database</c>, <c>schema</c>,
    /// <c>name</c>, <c>kind</c>, <c>columns</c> of <c>name</c>, <c>type</c>, <c>nullable</c>, <c>key</c>, and <c>unique_keys</c> and <c>foreign_keys</c>). Only those fields are read: whatever else the answer says is not copied. A table whose
    /// columns could not be read is left out (the format wants columns).
    /// </summary>
    public static CacheConnection FromAnswer(JsonObject answer, CacheRead read)
    {
        var databases = new List<CacheDatabase>();
        foreach (var d in (answer["databases"] as JsonArray)?.OfType<JsonObject>() ?? [])
            if ((string?)d["name"] is { Length: > 0 } name)
                databases.Add(new(name, [.. Strings(d["schemas"]).Distinct(StringComparer.Ordinal)]));
        var tables = new List<CacheTable>();
        foreach (var t in (answer["tables"] as JsonArray)?.OfType<JsonObject>() ?? [])
            if (TableFromAnswer(t, databases.Count > 0) is { } table) tables.Add(table);
        return new CacheConnection((string?)answer["engine"] is { Length: > 0 } engine ? engine : "unknown", tables) { Read = read, Databases = databases };
    }

    /// <summary>One table of an answer, or null when it has no name or no column it could read.</summary>
    public static CacheTable? TableFromAnswer(JsonObject t, bool withDatabase = true)
    {
        var columns = (t["columns"] as JsonArray)?.OfType<JsonObject>().Select(c => new CacheColumn((string?)c["name"] ?? "", (string?)c["type"] ?? "unknown", (bool?)c["nullable"] ?? true, (bool?)c["key"] ?? false)).Where(c => c.Name.Length > 0).ToList() ?? [];
        if (columns.Count == 0 || (string?)t["name"] is not { Length: > 0 } name) return null;
        var unique = (t["unique_keys"] as JsonArray)?.OfType<JsonObject>().Select(k => new CacheKey((string?)k["name"] is { Length: > 0 } n ? n : null, [.. Strings(k["columns"])])).Where(k => k.Columns.Count > 0).ToList() ?? [];
        var foreign = new List<CacheForeignKey>();
        foreach (var k in (t["foreign_keys"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var r = k["references"] as JsonObject;
            var from = Strings(k["columns"]).ToList();
            var to = Strings(r?["columns"]).ToList();
            if (r is null || from.Count == 0 || from.Count != to.Count || (string?)r["table"] is not { Length: > 0 } target) continue;
            foreign.Add(new((string?)k["name"] is { Length: > 0 } n ? n : null, from, (string?)r["database"] ?? "", (string?)r["schema"] ?? "", target, to));
        }
        return new CacheTable((string?)t["schema"] ?? "", name, (string?)t["kind"] == "view" ? "view" : "table", columns)
        {
            Database = withDatabase ? (string?)t["database"] ?? "" : "",
            UniqueKeys = unique,
            ForeignKeys = foreign,
        };
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray)?.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0) ?? [];

    /// <summary>The cache with the connection <paramref name="name"/> replaced by <paramref name="connection"/>; the other connections of <paramref name="existing"/> stay, <c>generated_at</c> is <paramref name="now"/>.</summary>
    public static CatalogCacheFile WithConnection(CatalogCacheFile? existing, string name, CacheConnection connection, DateTimeOffset now) =>
        new(Stamp(now), new Dictionary<string, CacheConnection>(existing?.Connections ?? new Dictionary<string, CacheConnection>(), StringComparer.Ordinal) { [name] = connection });

    /// <summary>
    /// The cache with one table of the connection <paramref name="name"/> read again: <paramref name="table"/> replaces the table of the same database, schema and name (or is added), and when it is null the table <paramref name="gone"/> is
    /// removed (it is no longer there). The connection's other tables and its databases stay; its <c>read</c> becomes <paramref name="read"/>. A connection the cache does not have yet is made with that one table.
    /// </summary>
    public static CatalogCacheFile WithTable(CatalogCacheFile? existing, string name, string engine, CacheTable? table, (string Database, string Schema, string Name) gone, CacheRead read, DateTimeOffset now)
    {
        var old = existing?.Connections.GetValueOrDefault(name);
        var key = table is null ? gone : (table.Database, table.Schema, table.Name);
        var tables = (old?.Tables ?? []).Where(t => (t.Database, t.Schema, t.Name) != key).ToList();
        if (table is not null) tables.Add(table);
        var connection = new CacheConnection(old?.Engine is { Length: > 0 } e && e != "unknown" ? e : engine, tables) { Read = read, Databases = old?.Databases ?? [] };
        return WithConnection(existing, name, connection, now);
    }

    /// <summary>The cache with the connection <paramref name="connection"/> replaced by the answer of a paired Gateway's schema call (no channel: a Gateway reads with its own login).</summary>
    public static CatalogCacheFile Refreshed(CatalogCacheFile? existing, string connection, JsonObject schema, string server, string product, DateTimeOffset now) =>
        WithConnection(existing, connection, FromAnswer(schema, new CacheRead(server, product, null, Stamp(now))), now);

    /// <summary>The file's text for a cache: the keys of the format in a fixed order, connections and tables in name order, every text quoted. A value with a control character is refused (a name that cannot be a line of the file).</summary>
    public static string Write(CatalogCacheFile cache)
    {
        var b = new StringBuilder();
        b.Append("schema: ").Append(SchemaPrefix).Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        b.Append("generated_at: ").Append(Q(cache.GeneratedAt)).Append('\n');
        if (cache.Connections.Count == 0) { b.Append("connections: {}\n"); return b.ToString(); }
        b.Append("connections:\n");
        foreach (var (name, connection) in cache.Connections.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            b.Append("  ").Append(Q(name)).Append(":\n");
            b.Append("    engine: ").Append(Q(connection.Engine)).Append('\n');
            if (connection.Read is { } read)
            {
                b.Append("    read: {server: ").Append(Q(read.Server)).Append(", product: ").Append(Q(read.Product));
                if (read.Channel is { } channel) b.Append(", channel: ").Append(Q(channel));
                b.Append(", at: ").Append(Q(read.At)).Append("}\n");
            }
            if (connection.Databases.Count == 0) b.Append("    databases: []\n");
            else
            {
                b.Append("    databases:\n");
                foreach (var d in connection.Databases.OrderBy(d => d.Name, StringComparer.Ordinal))
                    b.Append("      - {name: ").Append(Q(d.Name)).Append(", schemas: ").Append(List(d.Schemas.Order(StringComparer.Ordinal))).Append("}\n");
            }
            if (connection.Tables.Count == 0) { b.Append("    tables: []\n"); continue; }
            b.Append("    tables:\n");
            foreach (var t in connection.Tables.OrderBy(t => t.Database, StringComparer.Ordinal).ThenBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
            {
                var first = true;
                void Line(string key, string value)
                {
                    b.Append(first ? "      - " : "        ").Append(key).Append(": ").Append(value).Append('\n');
                    first = false;
                }
                if (t.Database.Length > 0) Line("database", Q(t.Database));
                if (t.Schema.Length > 0) Line("schema", Q(t.Schema));
                Line("name", Q(t.Name));
                Line("kind", t.Kind);
                b.Append("        columns:\n");
                foreach (var c in t.Columns)
                    b.Append("          - {name: ").Append(Q(c.Name)).Append(", type: ").Append(Q(c.Type)).Append(", nullable: ").Append(c.Nullable ? "true" : "false").Append(c.Key ? ", key: true" : "").Append("}\n");
                if (t.UniqueKeys.Count > 0)
                {
                    b.Append("        unique_keys:\n");
                    foreach (var k in t.UniqueKeys)
                        b.Append("          - {").Append(k.Name is null ? "" : "name: " + Q(k.Name) + ", ").Append("columns: ").Append(List(k.Columns)).Append("}\n");
                }
                if (t.ForeignKeys.Count > 0)
                {
                    b.Append("        foreign_keys:\n");
                    foreach (var k in t.ForeignKeys)
                    {
                        b.Append("          - {").Append(k.Name is null ? "" : "name: " + Q(k.Name) + ", ").Append("columns: ").Append(List(k.Columns)).Append(", references: {");
                        if (k.Database.Length > 0) b.Append("database: ").Append(Q(k.Database)).Append(", ");
                        if (k.Schema.Length > 0) b.Append("schema: ").Append(Q(k.Schema)).Append(", ");
                        b.Append("table: ").Append(Q(k.Table)).Append(", columns: ").Append(List(k.References)).Append("}}\n");
                    }
                }
            }
        }
        return b.ToString();
    }

    private static string List(IEnumerable<string> names) => "[" + string.Join(", ", names.Select(Q)) + "]";

    private static string Q(string text)
    {
        if (text.Any(char.IsControl)) throw new InvalidDataException("A name in the catalog has a control character, which a catalog cache cannot hold.");
        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>The connection as the JSON an answer uses (<see cref="FromAnswer"/> reads it back the same), for a caller that hands a connection on: <c>engine</c>, <c>read</c>, <c>databases</c>, <c>tables</c>.</summary>
    public static JsonObject ToJson(CacheConnection connection) => new()
    {
        ["engine"] = connection.Engine,
        ["read"] = connection.Read is { } r ? new JsonObject { ["server"] = r.Server, ["product"] = r.Product, ["channel"] = r.Channel, ["at"] = r.At } : null,
        ["databases"] = new JsonArray([.. connection.Databases.Select(d => (JsonNode?)new JsonObject { ["name"] = d.Name, ["schemas"] = Array(d.Schemas) })]),
        ["tables"] = new JsonArray([.. connection.Tables.Select(t => (JsonNode?)ToJson(t))]),
    };

    public static JsonObject ToJson(CacheTable t) => new()
    {
        ["database"] = t.Database, ["schema"] = t.Schema, ["name"] = t.Name, ["kind"] = t.Kind,
        ["columns"] = new JsonArray([.. t.Columns.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["type"] = c.Type, ["nullable"] = c.Nullable, ["key"] = c.Key })]),
        ["unique_keys"] = new JsonArray([.. t.UniqueKeys.Select(k => (JsonNode?)new JsonObject { ["name"] = k.Name, ["columns"] = Array(k.Columns) })]),
        ["foreign_keys"] = new JsonArray([.. t.ForeignKeys.Select(k => (JsonNode?)new JsonObject
        {
            ["name"] = k.Name, ["columns"] = Array(k.Columns),
            ["references"] = new JsonObject { ["database"] = k.Database, ["schema"] = k.Schema, ["table"] = k.Table, ["columns"] = Array(k.References) },
        })]),
    };

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(s => (JsonNode?)s)]);
}
