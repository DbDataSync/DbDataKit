using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Catalog;

/// <summary>One column of a cached table: its name and the type text the database gave, whether it accepts null and whether it is part of the primary key.</summary>
public sealed record CacheColumn(string Name, string Type, bool Nullable, bool Key);

/// <summary>A unique key of a cached table (the primary key is the columns marked <see cref="CacheColumn.Key"/>): its name when the database gave one, and its columns in order.</summary>
public sealed record CacheKey(string? Name, IReadOnlyList<string> Columns);

/// <summary>A foreign key of a cached table: its columns, and the table and columns it references (<see cref="Database"/> empty: the same database as the table).</summary>
public sealed record CacheForeignKey(string? Name, IReadOnlyList<string> Columns, string Database, string Schema, string Table, IReadOnlyList<string> References);

/// <summary>One table or view of a cached connection. <see cref="Database"/> is empty when the connection lists no databases.</summary>
public sealed record CacheTable(string Schema, string Name, string Kind, IReadOnlyList<CacheColumn> Columns)
{
    public string Database { get; init; } = "";

    public IReadOnlyList<CacheKey> UniqueKeys { get; init; } = [];

    public IReadOnlyList<CacheForeignKey> ForeignKeys { get; init; } = [];
}

/// <summary>A database a connection reaches and every schema of it, the empty ones too.</summary>
public sealed record CacheDatabase(string Name, IReadOnlyList<string> Schemas);

/// <summary>Where and when a connection's shape was read: the paired server by its name and product (never an address), the channel whose binding gave the login (a Runner; none for a Gateway), and the time.</summary>
public sealed record CacheRead(string Server, string Product, string? Channel, string At);

/// <summary>What the cache knows of one connection of the project: the engine, where it was read, its databases and schemas, and the shape of its tables.</summary>
public sealed record CacheConnection(string Engine, IReadOnlyList<CacheTable> Tables)
{
    public CacheRead? Read { get; init; }

    public IReadOnlyList<CacheDatabase> Databases { get; init; } = [];
}

/// <summary>A catalog cache read: when the file was written and the connections it holds.</summary>
public sealed record CatalogCacheFile(string GeneratedAt, IReadOnlyDictionary<string, CacheConnection> Connections);

/// <summary>The result of reading a cache: the cache when the file is sound, and every finding with its file, line and key path.</summary>
public sealed record CatalogCacheLoad(CatalogCacheFile? Cache, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool Ok => Cache is not null && !Diagnostics.Any(d => d.Severity == ConfigSeverity.Error);
}

/// <summary>
/// The catalog cache, format <c>catalog-cache/2</c>: the shape of a project's connections as a paired server that holds the logins read it (a Runner's <c>discover</c> with the login a channel binds, or a Gateway's schema call),
/// written into the draft as one committed file, <c>catalog-cache.yaml</c> at the project's root, so the Workspace's build child, which has no network and no login, can still offer a mapping its columns and a model its tables.
/// Design: docs/authoring-metadata.md#the-catalog-cache
/// <para>
/// <b>It holds no secret and no row.</b> The reader accepts exactly the keys below and nothing else: a key that would carry a row or a login (<c>rows</c>, <c>values</c>, <c>sample</c>, <c>password</c>, <c>connection_string</c>,
/// <c>url</c> and the like) is refused by name with the reason, any other unknown key is refused, and a value that looks like a password assignment, a connection string, an address with a login in it or a secret reference is refused
/// wherever it stands. The strict reader is the format's definition; <c>Shared/web/workspace/src/catalogCache.ts</c> is its twin for the screens and both are run over the same fixtures
/// (<c>DbDataWorkspace/tests/DbDataWorkspace.Tests/Fixtures/catalog-cache</c>). A file of format 1 is refused naming the file: it is made again by a refresh.
/// </para>
/// <code>
/// schema: catalog-cache/2
/// generated_at: 2026-11-18T10:00:00Z
/// connections:
///   crm:
///     engine: sqlserver
///     read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-18T10:00:00Z}
///     databases:
///       - {name: crm, schemas: [dbo, archive]}   # every schema, the empty ones too
///     tables:
///       - database: crm
///         schema: dbo
///         name: orders
///         kind: table            # table or view
///         columns:
///           - {name: id, type: int, nullable: false, key: true}
///           - {name: customer_id, type: int, nullable: false}
///           - {name: number, type: 'nvarchar(20)', nullable: false}
///         unique_keys:
///           - {name: uq_orders_number, columns: [number]}
///         foreign_keys:
///           - {name: fk_orders_customers, columns: [customer_id], references: {schema: dbo, table: customers, columns: [id]}}
/// </code>
/// A connection that lists no databases (an engine the reader could not ask, or a Gateway's answer) has <c>databases: []</c> and tables without <c>database</c>; one that lists them names, on each table, a listed database and one of its schemas.
/// </summary>
public static partial class CatalogCache
{
    /// <summary>The format number this reads; a newer one is refused naming the file, and so is an older one.</summary>
    public const int Version = 2;

    public const string FileName = "catalog-cache.yaml";
    public const string SchemaPrefix = "catalog-cache/";

    public const string BadSchema = "DBD-CAT-001";
    public const string UnknownKey = "DBD-CAT-002";
    public const string BadShape = "DBD-CAT-003";
    public const string LooksLikeSecret = "DBD-CAT-004";
    public const string Duplicate = "DBD-CAT-005";
    public const string TooLarge = "DBD-CAT-006";
    public const string Unlisted = "DBD-CAT-007";

    public const int MaxTables = 20_000;
    public const int MaxColumns = 5_000;
    public const int MaxDatabases = 1_000;
    public const int MaxSchemas = 10_000;

    /// <summary>The products a cache may say it was read by.</summary>
    public static readonly IReadOnlyList<string> Products = ["runner", "gateway"];

    private static readonly string[] Keys = ["schema", "generated_at", "connections"];
    private static readonly string[] ReadKeys = ["server", "product", "channel", "at"];
    private static readonly string[] ConnectionKeys = ["engine", "read", "databases", "tables"];
    private static readonly string[] DatabaseKeys = ["name", "schemas"];
    private static readonly string[] TableKeys = ["database", "schema", "name", "kind", "columns", "unique_keys", "foreign_keys"];
    private static readonly string[] ColumnKeys = ["name", "type", "nullable", "key"];
    private static readonly string[] UniqueKeys = ["name", "columns"];
    private static readonly string[] ForeignKeyKeys = ["name", "columns", "references"];
    private static readonly string[] ReferenceKeys = ["database", "schema", "table", "columns"];

    /// <summary>Keys that would carry a row or a login: refused with the reason, not just as unknown.</summary>
    private static readonly HashSet<string> Forbidden = new(
        ["row", "rows", "value", "values", "sample", "samples", "data", "preview", "example", "examples", "password", "passwd", "pwd", "secret", "secrets", "token", "login", "user", "username", "credentials", "connection_string", "connectionstring", "url", "address", "host", "port",
         "min", "max", "minimum", "maximum", "distinct", "statistics", "stats", "histogram", "bounds"],
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key)\s*[=:]\s*\S")]
    private static partial Regex PasswordAssignment();

    [GeneratedRegex(@"[A-Za-z][\w ]*=[^;=]+;\s*[A-Za-z][\w ]*=")]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"(?i)[a-z][a-z0-9+.\-]*://[^/\s@]*@")]
    private static partial Regex AddressWithLogin();

    [GeneratedRegex(@"(?i)^(secret|env|file):\S")]
    private static partial Regex Reference();

    /// <summary>Why a scalar value cannot be in the cache, or null.</summary>
    internal static string? SecretLook(string value)
    {
        if (PasswordAssignment().IsMatch(value)) return "looks like a password or a token being set";
        if (ConnectionString().IsMatch(value)) return "looks like a connection string";
        if (AddressWithLogin().IsMatch(value)) return "looks like an address with a login in it";
        if (Reference().IsMatch(value)) return "is a reference to a secret";
        return null;
    }

    public static CatalogCacheLoad Load(string text, string file = FileName)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, file, diagnostics);
        if (root is null || diagnostics.Any(d => d.Severity == ConfigSeverity.Error)) return new(null, diagnostics);
        var reader = new Reader(file, diagnostics);
        var cache = reader.ReadRoot(root);
        return new(diagnostics.Any(d => d.Severity == ConfigSeverity.Error) ? null : cache, diagnostics);
    }

    private sealed class Reader(string file, List<ConfigDiagnostic> diagnostics)
    {
        private void Fail(string code, YamlNode at, string path, string message) =>
            diagnostics.Add(new ConfigDiagnostic(code, ConfigSeverity.Error, file, at.Line, at.Column, message) { KeyPath = path });

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        private YamlMapping? Map(YamlNode? node, string path, string what, YamlNode at)
        {
            if (node is YamlMapping m) return m;
            Fail(BadShape, node ?? at, path, $"{what} is a mapping.");
            return null;
        }

        /// <summary>The entries of a mapping with each unknown key refused, a forbidden one with the reason.</summary>
        private void Strict(YamlMapping map, string path, string[] known)
        {
            foreach (var e in map.Entries)
            {
                var here = Join(path, e.Key.Value);
                if (known.Contains(e.Key.Value, StringComparer.Ordinal)) continue;
                if (Forbidden.Contains(e.Key.Value)) Fail(UnknownKey, e.Key, here, $"`{e.Key.Value}` has no place in a catalog cache: it holds the shape of tables, never a row, a value or a statistic of values, and never a secret or an address with a login.");
                else Fail(UnknownKey, e.Key, here, $"`{e.Key.Value}` is not a key of a catalog cache here (known: {string.Join(", ", known)}).");
            }
        }

        private string? Scalar(YamlNode node, string here, string key)
        {
            if (node is not YamlScalar s || s.Value.Trim().Length == 0)
            {
                Fail(BadShape, node, here, $"`{key}` is a text, not empty.");
                return null;
            }
            if (SecretLook(s.Value) is { } why)
            {
                Fail(LooksLikeSecret, s, here, $"The value of `{key}` {why}; a catalog cache holds no secret and no login.");
                return null;
            }
            return s.Value;
        }

        private string? Text(YamlMapping map, string key, string path, bool required = true)
        {
            var entry = map.GetEntry(key);
            var here = Join(path, key);
            if (entry is null)
            {
                if (required) Fail(BadShape, map, here, $"`{key}` is missing.");
                return null;
            }
            return Scalar(entry.Value, here, key);
        }

        /// <summary>A list of names (columns of a key, schemas of a database): each a text, none twice.</summary>
        private List<string>? Names(YamlMapping map, string key, string path, bool required, bool nonEmpty, int max)
        {
            var entry = map.GetEntry(key);
            var here = Join(path, key);
            if (entry is null)
            {
                if (required) Fail(BadShape, map, here, $"`{key}` is missing.");
                return null;
            }
            if (entry.Value is not YamlSequence list || (nonEmpty && list.Items.Count == 0))
            {
                Fail(BadShape, entry.Value, here, nonEmpty ? $"`{key}` is a list of at least one name." : $"`{key}` is a list of names.");
                return null;
            }
            if (list.Items.Count > max) Fail(TooLarge, list, here, $"`{key}` holds at most {max} names in a cache.");
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < list.Items.Count && i < max; i++)
            {
                if (Scalar(list.Items[i], $"{here}[{i}]", key) is not { } name) continue;
                if (!seen.Add(name)) { Fail(Duplicate, list.Items[i], $"{here}[{i}]", $"`{name}` is listed twice in `{key}`."); continue; }
                names.Add(name);
            }
            return names;
        }

        private bool Flag(YamlMapping map, string key, string path, bool otherwise)
        {
            var entry = map.GetEntry(key);
            if (entry is null) return otherwise;
            var here = Join(path, key);
            if (entry.Value is YamlScalar { Value: "true" }) return true;
            if (entry.Value is YamlScalar { Value: "false" }) return false;
            Fail(BadShape, entry.Value, here, $"`{key}` is true or false.");
            return otherwise;
        }

        private string? Time(YamlMapping map, string key, string path)
        {
            var value = Text(map, key, path);
            if (value is not null && !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            {
                Fail(BadShape, map.Get(key)!, Join(path, key), $"`{key}` is a date and time (for example 2026-11-11T10:00:00Z).");
                return null;
            }
            return value;
        }

        public CatalogCacheFile? ReadRoot(YamlNode root)
        {
            if (Map(root, "", "A catalog cache file", root) is not { } map) return null;
            var schema = Text(map, "schema", "");
            if (schema is not null)
            {
                if (!schema.StartsWith(SchemaPrefix, StringComparison.Ordinal) || !int.TryParse(schema[SchemaPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
                    Fail(BadSchema, map.Get("schema")!, "schema", $"`schema` is `{SchemaPrefix}{Version}`, not `{schema}`.");
                else if (number > Version)
                {
                    Fail(BadSchema, map.Get("schema")!, "schema", $"{file} is in format {schema}, which is newer than this reads ({SchemaPrefix}{Version}); update the software or make the cache again.");
                    return null;
                }
                else if (number < Version)
                {
                    // a clean break: the file is not read at all, so its other keys are not reported as unknown
                    Fail(BadSchema, map.Get("schema")!, "schema", $"{file} is in format {schema}, which this no longer reads (it reads {SchemaPrefix}{Version}, which records where and when each connection was read, its databases and schemas, and foreign keys); delete the file and refresh the catalog of each connection to make it again.");
                    return null;
                }
            }
            Strict(map, "", Keys);
            var generated = Time(map, "generated_at", "");
            var connections = new Dictionary<string, CacheConnection>(StringComparer.Ordinal);
            if (map.GetEntry("connections") is { } connectionsEntry)
            {
                if (Map(connectionsEntry.Value, "connections", "`connections`", connectionsEntry.Key) is { } all)
                {
                    foreach (var e in all.Entries)
                    {
                        var path = "connections." + e.Key.Value;
                        if (connections.ContainsKey(e.Key.Value)) { Fail(Duplicate, e.Key, path, $"The connection `{e.Key.Value}` is listed twice."); continue; }
                        if (ReadConnection(e.Value, path, e.Key) is { } c) connections[e.Key.Value] = c;
                    }
                }
            }
            else Fail(BadShape, map, "connections", "`connections` is missing.");
            return new(generated ?? "", connections);
        }

        private CacheRead? ReadWhere(YamlMapping map, string path)
        {
            if (map.GetEntry("read") is not { } entry)
            {
                Fail(BadShape, map, path + ".read", "`read` is missing: it says which paired server read the connection, in which channel, and when.");
                return null;
            }
            if (Map(entry.Value, path + ".read", "`read`", entry.Key) is not { } read) return null;
            var here = path + ".read";
            Strict(read, here, ReadKeys);
            var server = Text(read, "server", here);
            var product = Text(read, "product", here);
            var channel = Text(read, "channel", here, required: false);
            var at = Time(read, "at", here);
            if (product is not null && !Products.Contains(product, StringComparer.Ordinal))
            {
                Fail(BadShape, read.Get("product")!, here + ".product", $"`product` is {string.Join(" or ", Products)}, not `{product}`.");
                product = null;
            }
            if (product == "runner" && channel is null && read.GetEntry("channel") is null)
                Fail(BadShape, read, here + ".channel", "`channel` is missing: a Runner reads with the login a channel binds, and the cache says which.");
            return server is null || product is null || at is null ? null : new(server, product, channel, at);
        }

        private CacheConnection? ReadConnection(YamlNode node, string path, YamlNode at)
        {
            if (Map(node, path, "A connection", at) is not { } map) return null;
            Strict(map, path, ConnectionKeys);
            var engine = Text(map, "engine", path);
            var read = ReadWhere(map, path);
            var databases = new List<CacheDatabase>();
            if (map.GetEntry("databases") is { } dbEntry)
            {
                if (dbEntry.Value is not YamlSequence list) Fail(BadShape, dbEntry.Value, path + ".databases", "`databases` is a list.");
                else
                {
                    if (list.Items.Count > MaxDatabases) Fail(TooLarge, list, path + ".databases", $"A connection holds at most {MaxDatabases} databases in a cache.");
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < list.Items.Count && i < MaxDatabases; i++)
                    {
                        var dpath = $"{path}.databases[{i}]";
                        if (Map(list.Items[i], dpath, "A database", list.Items[i]) is not { } d) continue;
                        Strict(d, dpath, DatabaseKeys);
                        var name = Text(d, "name", dpath);
                        var schemas = Names(d, "schemas", dpath, required: true, nonEmpty: false, MaxSchemas) ?? [];
                        if (name is null) continue;
                        if (!seen.Add(name)) { Fail(Duplicate, list.Items[i], dpath, $"The database `{name}` is listed twice on `{path["connections.".Length..]}`."); continue; }
                        databases.Add(new(name, schemas));
                    }
                }
            }
            else Fail(BadShape, map, path + ".databases", "`databases` is missing (an empty list when the connection's databases were not read).");

            var tables = new List<CacheTable>();
            if (map.GetEntry("tables") is { } entry)
            {
                if (entry.Value is not YamlSequence list) Fail(BadShape, entry.Value, path + ".tables", "`tables` is a list.");
                else
                {
                    if (list.Items.Count > MaxTables) Fail(TooLarge, list, path + ".tables", $"A connection holds at most {MaxTables} tables in a cache; cache the schemas the project uses.");
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < list.Items.Count && i < MaxTables; i++)
                    {
                        var tpath = $"{path}.tables[{i}]";
                        if (ReadTable(list.Items[i], tpath) is not { } t) continue;
                        if (!seen.Add(t.Database + "\u0001" + t.Schema + "\u0001" + t.Name)) Fail(Duplicate, list.Items[i], tpath, $"The table `{Label(t)}` is listed twice on `{path["connections.".Length..]}`.");
                        Placed(t, databases, list.Items[i], tpath);
                        tables.Add(t);
                    }
                }
            }
            else Fail(BadShape, map, path + ".tables", "`tables` is missing.");
            return new(engine ?? "", tables) { Read = read, Databases = databases };
        }

        /// <summary>A table names a listed database and one of its schemas when the connection lists databases, and no database when it lists none.</summary>
        private void Placed(CacheTable t, List<CacheDatabase> databases, YamlNode at, string path)
        {
            if (databases.Count == 0)
            {
                if (t.Database.Length > 0) Fail(Unlisted, at, path + ".database", $"`{Label(t)}` names the database `{t.Database}`, and the connection lists no databases.");
                return;
            }
            if (t.Database.Length == 0) { Fail(Unlisted, at, path + ".database", $"`{Label(t)}` names no database, and the connection lists its databases: say which one it is in."); return; }
            var database = databases.FirstOrDefault(d => d.Name == t.Database);
            if (database is null) Fail(Unlisted, at, path + ".database", $"`{Label(t)}` is in the database `{t.Database}`, which the connection does not list.");
            else if (t.Schema.Length > 0 && !database.Schemas.Contains(t.Schema, StringComparer.Ordinal)) Fail(Unlisted, at, path + ".schema", $"`{Label(t)}` is in the schema `{t.Schema}`, which the database `{t.Database}` does not list.");
        }

        private CacheTable? ReadTable(YamlNode node, string path)
        {
            if (Map(node, path, "A table", node) is not { } map) return null;
            Strict(map, path, TableKeys);
            var database = Text(map, "database", path, required: false) ?? "";
            var schema = Text(map, "schema", path, required: false) ?? "";
            var name = Text(map, "name", path);
            var kind = Text(map, "kind", path);
            if (kind is not null and not ("table" or "view")) { Fail(BadShape, map.Get("kind")!, path + ".kind", "`kind` is `table` or `view`."); kind = null; }
            var columns = new List<CacheColumn>();
            if (map.GetEntry("columns") is { } entry)
            {
                if (entry.Value is not YamlSequence list || list.Items.Count == 0) Fail(BadShape, entry.Value, path + ".columns", "`columns` is a list with at least one column: a table with no columns has no shape to cache.");
                else
                {
                    if (list.Items.Count > MaxColumns) Fail(TooLarge, list, path + ".columns", $"A table holds at most {MaxColumns} columns in a cache.");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < list.Items.Count && i < MaxColumns; i++)
                    {
                        var cpath = $"{path}.columns[{i}]";
                        if (Map(list.Items[i], cpath, "A column", list.Items[i]) is not { } c) continue;
                        Strict(c, cpath, ColumnKeys);
                        var cname = Text(c, "name", cpath);
                        var type = Text(c, "type", cpath);
                        var nullable = Flag(c, "nullable", cpath, true);
                        var key = Flag(c, "key", cpath, false);
                        if (cname is null || type is null) continue;
                        if (!seen.Add(cname)) Fail(Duplicate, c, cpath, $"The column `{cname}` is listed twice in `{name}`.");
                        columns.Add(new(cname, type, nullable, key));
                    }
                }
            }
            else Fail(BadShape, map, path + ".columns", "`columns` is missing.");

            var names = new HashSet<string>(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var unique = new List<CacheKey>();
            foreach (var (k, i, kpath) in Items(map, "unique_keys", path))
            {
                Strict(k, kpath, UniqueKeys);
                var kname = Text(k, "name", kpath, required: false);
                var kcolumns = Names(k, "columns", kpath, required: true, nonEmpty: true, MaxColumns);
                if (kcolumns is null) continue;
                Known(kcolumns, names, k, kpath + ".columns", name);
                unique.Add(new(kname, kcolumns));
            }
            var foreign = new List<CacheForeignKey>();
            foreach (var (k, i, kpath) in Items(map, "foreign_keys", path))
            {
                Strict(k, kpath, ForeignKeyKeys);
                var kname = Text(k, "name", kpath, required: false);
                var kcolumns = Names(k, "columns", kpath, required: true, nonEmpty: true, MaxColumns);
                if (kcolumns is not null) Known(kcolumns, names, k, kpath + ".columns", name);
                if (k.GetEntry("references") is not { } refEntry) { Fail(BadShape, k, kpath + ".references", "`references` is missing: the table and columns the key points at."); continue; }
                if (Map(refEntry.Value, kpath + ".references", "`references`", refEntry.Key) is not { } r) continue;
                var rpath = kpath + ".references";
                Strict(r, rpath, ReferenceKeys);
                var rdatabase = Text(r, "database", rpath, required: false) ?? "";
                var rschema = Text(r, "schema", rpath, required: false) ?? "";
                var rtable = Text(r, "table", rpath);
                var rcolumns = Names(r, "columns", rpath, required: true, nonEmpty: true, MaxColumns);
                if (kcolumns is null || rtable is null || rcolumns is null) continue;
                if (rcolumns.Count != kcolumns.Count) { Fail(BadShape, r, rpath + ".columns", $"The foreign key has {kcolumns.Count} column(s) and references {rcolumns.Count}: they pair one to one."); continue; }
                foreign.Add(new(kname, kcolumns, rdatabase, rschema, rtable, rcolumns));
            }
            return name is null || kind is null ? null : new(schema, name, kind, columns) { Database = database, UniqueKeys = unique, ForeignKeys = foreign };
        }

        private IEnumerable<(YamlMapping Map, int Index, string Path)> Items(YamlMapping map, string key, string path)
        {
            if (map.GetEntry(key) is not { } entry) yield break;
            if (entry.Value is not YamlSequence list) { Fail(BadShape, entry.Value, Join(path, key), $"`{key}` is a list."); yield break; }
            for (var i = 0; i < list.Items.Count; i++)
            {
                var here = $"{Join(path, key)}[{i}]";
                if (Map(list.Items[i], here, "A key", list.Items[i]) is { } m) yield return (m, i, here);
            }
        }

        private void Known(List<string> columns, HashSet<string> names, YamlNode at, string path, string? table)
        {
            foreach (var c in columns.Where(c => !names.Contains(c)))
                Fail(Unlisted, at, path, $"The key names the column `{c}`, which `{table}` does not list.");
        }
    }

    /// <summary><c>database.schema.table</c>, leaving out the parts that are empty.</summary>
    public static string Label(CacheTable t) => string.Join('.', new[] { t.Database, t.Schema, t.Name }.Where(p => p.Length > 0));
}
