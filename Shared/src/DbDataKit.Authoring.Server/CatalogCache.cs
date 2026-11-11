using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Authoring;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Authoring.Server;

/// <summary>One column of a cached table: its name and the type text the database gave, whether it accepts null and whether it is part of the primary key.</summary>
public sealed record CacheColumn(string Name, string Type, bool Nullable, bool Key);

/// <summary>One table or view of a cached connection.</summary>
public sealed record CacheTable(string Schema, string Name, string Kind, IReadOnlyList<CacheColumn> Columns);

/// <summary>What the cache knows of one connection of the project: the engine and the shape of its tables.</summary>
public sealed record CacheConnection(string Engine, IReadOnlyList<CacheTable> Tables);

/// <summary>A catalog cache read: when it was made, by which paired server (its name and product, never an address) and the connections it holds.</summary>
public sealed record CatalogCacheFile(string GeneratedAt, string Server, string Product, IReadOnlyDictionary<string, CacheConnection> Connections);

/// <summary>The result of reading a cache: the cache when the file is sound, and every finding with its file, line and key path.</summary>
public sealed record CatalogCacheLoad(CatalogCacheFile? Cache, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool Ok => Cache is not null && !Diagnostics.Any(d => d.Severity == ConfigSeverity.Error);
}

/// <summary>
/// The catalog cache, format <c>catalog-cache/1</c> (plan 33 D20 and SC20i): the answer of a paired server that holds the logins to a read-only question (which tables and columns does this connection have), written into the
/// draft as one committed file, <c>catalog-cache.yaml</c> at the project's root, so the Workspace's build child, which has no network and no login, can still offer a mapping its columns and a model its tables.
/// <para>
/// <b>It holds no secret and no row.</b> The reader accepts exactly the keys below and nothing else: a key that would carry a row or a login (<c>rows</c>, <c>values</c>, <c>sample</c>, <c>password</c>, <c>connection_string</c>,
/// <c>url</c> and the like) is refused by name with the reason, any other unknown key is refused, and a value that looks like a password assignment, a connection string, an address with a login in it or a secret reference is refused
/// wherever it stands. The strict reader is the format's definition; <c>Shared/web/workspace/src/catalogCache.ts</c> is its twin for the screens and both are run over the same fixtures
/// (<c>DbDataWorkspace/tests/DbDataWorkspace.Tests/Fixtures/catalog-cache</c>).
/// </para>
/// <code>
/// schema: catalog-cache/1
/// generated_at: 2026-11-11T10:00:00Z
/// source: {server: prod-gateway, product: gateway}
/// connections:
///   source-db:
///     engine: postgres
///     tables:
///       - schema: public
///         name: orders
///         kind: table            # table or view
///         columns:
///           - {name: id, type: integer, nullable: false, key: true}
///           - {name: placed_at, type: timestamp, nullable: true}
/// </code>
/// </summary>
public static partial class CatalogCache
{
    /// <summary>The format number this reads; a newer one is refused naming the file.</summary>
    public const int Version = 1;

    public const string FileName = "catalog-cache.yaml";
    public const string SchemaPrefix = "catalog-cache/";

    public const string BadSchema = "DBD-CAT-001";
    public const string UnknownKey = "DBD-CAT-002";
    public const string BadShape = "DBD-CAT-003";
    public const string LooksLikeSecret = "DBD-CAT-004";
    public const string Duplicate = "DBD-CAT-005";
    public const string TooLarge = "DBD-CAT-006";

    public const int MaxTables = 20_000;
    public const int MaxColumns = 5_000;

    private static readonly string[] Keys = ["schema", "generated_at", "source", "connections"];
    private static readonly string[] SourceKeys = ["server", "product"];
    private static readonly string[] ConnectionKeys = ["engine", "tables"];
    private static readonly string[] TableKeys = ["schema", "name", "kind", "columns"];
    private static readonly string[] ColumnKeys = ["name", "type", "nullable", "key"];

    /// <summary>Keys that would carry a row or a login: refused with the reason, not just as unknown.</summary>
    private static readonly HashSet<string> Forbidden = new(
        ["row", "rows", "value", "values", "sample", "samples", "data", "preview", "example", "examples", "password", "passwd", "pwd", "secret", "secrets", "token", "login", "user", "username", "credentials", "connection_string", "connectionstring", "url", "address", "host", "port"],
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

    /// <summary>
    /// The finding of the cache in a draft, as the kinds' check reports them: nothing when the draft has none (a cache is optional), the reader's findings (file, line) when it has one. Never throws for a damaged file.
    /// </summary>
    public static IReadOnlyList<ProjectDiagnostic> Check(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return [];
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException) { return [new("error", "DBD-CAT-READ", $"{FileName} cannot be read: {ex.Message}", FileName)]; }
        return [.. Load(text, FileName).Diagnostics.Select(d => new ProjectDiagnostic(d.Severity switch { ConfigSeverity.Error => "error", ConfigSeverity.Warning => "warning", _ => "info" }, d.Code, d.Message, FileName, d.Line > 0 ? d.Line : null))];
    }

    private sealed class Reader(string file, List<ConfigDiagnostic> diagnostics)
    {
        private void Fail(string code, YamlNode at, string path, string message) =>
            diagnostics.Add(new ConfigDiagnostic(code, ConfigSeverity.Error, file, at.Line, at.Column, message) { KeyPath = path });

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
                var here = path.Length == 0 ? e.Key.Value : path + "." + e.Key.Value;
                if (known.Contains(e.Key.Value, StringComparer.Ordinal)) continue;
                if (Forbidden.Contains(e.Key.Value)) Fail(UnknownKey, e.Key, here, $"`{e.Key.Value}` has no place in a catalog cache: it holds the shape of tables, never a row and never a secret or an address with a login.");
                else Fail(UnknownKey, e.Key, here, $"`{e.Key.Value}` is not a key of a catalog cache here (known: {string.Join(", ", known)}).");
            }
        }

        private string? Text(YamlMapping map, string key, string path, bool required = true)
        {
            var entry = map.GetEntry(key);
            var here = path.Length == 0 ? key : path + "." + key;
            if (entry is null)
            {
                if (required) Fail(BadShape, map, here, $"`{key}` is missing.");
                return null;
            }
            if (entry.Value is not YamlScalar s || s.Value.Trim().Length == 0)
            {
                Fail(BadShape, entry.Value, here, $"`{key}` is a text, not empty.");
                return null;
            }
            if (SecretLook(s.Value) is { } why)
            {
                Fail(LooksLikeSecret, s, here, $"The value of `{key}` {why}; a catalog cache holds no secret and no login.");
                return null;
            }
            return s.Value;
        }

        private bool Flag(YamlMapping map, string key, string path, bool otherwise)
        {
            var entry = map.GetEntry(key);
            if (entry is null) return otherwise;
            var here = path + "." + key;
            if (entry.Value is YamlScalar { Value: "true" }) return true;
            if (entry.Value is YamlScalar { Value: "false" }) return false;
            Fail(BadShape, entry.Value, here, $"`{key}` is true or false.");
            return otherwise;
        }

        public CatalogCacheFile? ReadRoot(YamlNode root)
        {
            if (Map(root, "", "A catalog cache file", root) is not { } map) return null;
            Strict(map, "", Keys);
            var schema = Text(map, "schema", "");
            if (schema is not null)
            {
                if (!schema.StartsWith(SchemaPrefix, StringComparison.Ordinal) || !int.TryParse(schema[SchemaPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    Fail(BadSchema, map.Get("schema")!, "schema", $"`schema` is `{SchemaPrefix}{Version}`, not `{schema}`.");
                else if (number > Version) Fail(BadSchema, map.Get("schema")!, "schema", $"{file} is in format {schema}, which is newer than this reads ({SchemaPrefix}{Version}); update the software or make the cache again.");
                else if (number < 1) Fail(BadSchema, map.Get("schema")!, "schema", $"`schema` is `{SchemaPrefix}{Version}`, not `{schema}`.");
            }
            var generated = Text(map, "generated_at", "");
            if (generated is not null && !DateTimeOffset.TryParse(generated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                Fail(BadShape, map.Get("generated_at")!, "generated_at", "`generated_at` is a date and time (for example 2026-11-11T10:00:00Z).");
            string server = "", product = "";
            if (map.GetEntry("source") is { } sourceEntry)
            {
                if (Map(sourceEntry.Value, "source", "`source`", sourceEntry.Key) is { } src)
                {
                    Strict(src, "source", SourceKeys);
                    server = Text(src, "server", "source") ?? "";
                    product = Text(src, "product", "source") ?? "";
                }
            }
            else Fail(BadShape, map, "source", "`source` is missing: it names the paired server the answer came from.");
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
            return new(generated ?? "", server, product, connections);
        }

        private CacheConnection? ReadConnection(YamlNode node, string path, YamlNode at)
        {
            if (Map(node, path, "A connection", at) is not { } map) return null;
            Strict(map, path, ConnectionKeys);
            var engine = Text(map, "engine", path);
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
                        if (ReadTable(list.Items[i], $"{path}.tables[{i}]") is not { } t) continue;
                        if (!seen.Add(t.Schema + "\u0001" + t.Name)) Fail(Duplicate, list.Items[i], $"{path}.tables[{i}]", $"The table `{t.Schema}.{t.Name}` is listed twice on `{path["connections.".Length..]}`.");
                        tables.Add(t);
                    }
                }
            }
            else Fail(BadShape, map, path + ".tables", "`tables` is missing.");
            return new(engine ?? "", tables);
        }

        private CacheTable? ReadTable(YamlNode node, string path)
        {
            if (Map(node, path, "A table", node) is not { } map) return null;
            Strict(map, path, TableKeys);
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
            return name is null || kind is null ? null : new(schema, name, kind, columns);
        }
    }
}

/// <summary>The check of a draft as the server runs it: the kind's own validators, then the catalog cache when the draft has one (every kind may carry it, so it is checked here and not in each kind).</summary>
public static class DraftCheck
{
    public static async Task<IReadOnlyList<ProjectDiagnostic>> RunAsync(IProjectKind kind, string root, CancellationToken cancellationToken)
    {
        var found = await kind.CheckAsync(root, cancellationToken).ConfigureAwait(false);
        var cache = CatalogCache.Check(root);
        return cache.Count == 0 ? found : [.. found, .. cache];
    }
}
