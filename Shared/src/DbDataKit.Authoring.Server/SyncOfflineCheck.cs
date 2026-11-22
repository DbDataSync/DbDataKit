using System.Text;
using DbDataKit.Authoring;
using DbDataKit.Catalog;
using DbDataKit.Config.Yaml;
using DbDataKit.Dialect;
using DbDataKit.Transfer;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The offline checks of a Sync draft: each table mapping read against the draft's <c>catalog-cache.yaml</c> and its connection files, with no
/// connection and no program, in the Workspace's own process. Per mapping: the tables and the mapped columns are in the cache, the columns a source filter
/// names are columns of its table (a filter the dialect's lexer cannot read is not checked offline), every mapped source type is one the driver's type map
/// knows, the key columns are not nullable, each source type fits the target column's cached type, and the mapping's key is the target's primary key or one of
/// its unique keys.
/// <para>
/// The cache is a picture of the databases taken in one channel at one moment, so every finding is a warning: the live checks of the set's
/// <c>preflight</c> read the databases again. A connection whose driver is Sync's own (a YAML driver) has its type map in Sync, which the Workspace does not
/// load, so its types and filters are reported as not checked offline. Files that do not parse are left to the program's check, which names them.
/// Design: docs/authoring-metadata.md#a-sync-project
/// </para>
/// </summary>
public static class SyncOfflineCheck
{
    /// <summary>The draft has no catalog cache, so none of the offline checks ran.</summary>
    public const string NoCache = "DBD-WS-SYNC-NOCACHE";

    /// <summary>Something was not checked offline: a connection with no entry in the cache, a driver whose type map is Sync's own, a filter the lexer cannot read.</summary>
    public const string NotChecked = "DBD-WS-SYNC-NOTCHECKED";

    /// <summary>A table a mapping names is not in the cache.</summary>
    public const string Table = "DBD-WS-SYNC-TABLE";

    /// <summary>A mapped column, or a column a filter names, is not in the cached table.</summary>
    public const string Column = "DBD-WS-SYNC-COLUMN";

    /// <summary>A source column's type is one the driver's type map does not know.</summary>
    public const string UnknownType = "DBD-WS-SYNC-TYPE";

    /// <summary>A source column's type does not fit, or may not fit, the target column's cached type.</summary>
    public const string Types = "DBD-WS-SYNC-TYPES";

    /// <summary>The mapping's key: a nullable key column, or a key that is not the target's primary key or a unique key.</summary>
    public const string Key = "DBD-WS-SYNC-KEY";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The engines whose Sync driver reads its types through the shared dialect of the same name.</summary>
    private static readonly HashSet<string> BuiltInEngines = new(StringComparer.Ordinal) { "sqlserver", "postgres", "mysql", "oracle", "duckdb" };

    /// <summary>Words a filter uses that are neither columns nor reserved in every dialect: literals, operators spelled as words, date parts, the clock.</summary>
    private static readonly HashSet<string> FilterWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRUE", "FALSE", "UNKNOWN", "ESCAPE", "ILIKE", "SIMILAR", "RLIKE", "REGEXP", "GLOB", "DIV", "MOD", "XOR", "COLLATE", "INTERVAL", "AT", "TIME", "ZONE",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "LOCALTIME", "LOCALTIMESTAMP", "SYSDATE", "SYSTIMESTAMP", "ROWNUM",
        "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND",
    };

    /// <summary>The offline findings of the draft at <paramref name="root"/>. Never throws for a damaged or missing file.</summary>
    public static IReadOnlyList<ProjectDiagnostic> Check(string root)
    {
        var replications = Path.Combine(root, "config", "replications");
        var mappings = Directory.Exists(replications)
            ? Directory.EnumerateDirectories(replications).Order(StringComparer.Ordinal)
                .SelectMany(r => Directory.Exists(Path.Combine(r, "table-mappings"))
                    ? Directory.EnumerateFiles(Path.Combine(r, "table-mappings"), "*.yaml").Order(StringComparer.Ordinal).Select(m => (Replication: r, Mapping: m))
                    : [])
                .ToList()
            : [];
        if (mappings.Count == 0) return [];

        var cachePath = Path.Combine(root, CatalogCache.FileName);
        if (!File.Exists(cachePath))
            return [new("info", NoCache, $"The draft has no {CatalogCache.FileName}, so its mappings were not checked against the databases' shape (tables, columns, types, keys). Refresh the catalog of the replication's connections to check them offline.")];
        CatalogCacheLoad load;
        try { load = CatalogCache.Load(File.ReadAllText(cachePath, Utf8), CatalogCache.FileName); }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException) { return []; }   // the cache's own check reports a file it cannot read
        if (!load.Ok || load.Cache is not { } cache)
            return [new("info", NotChecked, $"The mappings were not checked against {CatalogCache.FileName}: the file has errors, reported with it.", CatalogCache.FileName)];

        var run = new Run(root, cache);
        foreach (var (replication, mapping) in mappings) run.Mapping(replication, mapping);
        return run.Found;
    }

    private sealed record Side(string Connection, string Database, string Schema, string Table, string? Query, string? Filter, int Line, int FilterLine, int TableLine);

    private sealed record ConnectionInfo(string? Engine, ISqlDialect? Dialect);

    private sealed class Run(string root, CatalogCacheFile cache)
    {
        public List<ProjectDiagnostic> Found { get; } = [];
        private readonly Dictionary<string, ConnectionInfo?> connections = new(StringComparer.Ordinal);
        private readonly HashSet<string> noted = new(StringComparer.Ordinal);

        private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        private static YamlMapping? Read(string path)
        {
            string text;
            try { text = File.ReadAllText(path, Utf8); }
            catch (Exception ex) when (ex is IOException or DecoderFallbackException or UnauthorizedAccessException) { return null; }
            var diagnostics = new List<ConfigDiagnostic>();
            return StrictYamlReader.Read(text, Path.GetFileName(path), diagnostics) as YamlMapping;
        }

        private static string? Text(YamlMapping? map, string key) => map?.Get(key) is YamlScalar s && !(s.Value is "null" or "~" && !s.Quoted) ? s.Value : null;

        private static int LineOf(YamlMapping map, string key) => map.GetEntry(key) is { } e ? e.Value.Line : map.Line;

        private void Note(string once, string severity, string code, string message, string file, int? line)
        {
            if (noted.Add(once)) Found.Add(new(severity, code, message, file, line));
        }

        private ConnectionInfo? Connection(string name)
        {
            if (connections.TryGetValue(name, out var known)) return known;
            ConnectionInfo? info = null;
            var path = Path.Combine(root, "config", "connections", name + ".yaml");
            if (SyncProjectKind.IsName(name) && File.Exists(path) && Read(path) is { } map)
            {
                var engine = Text(map, "engine");
                var dialect = Text(map, "dialect");
                var id = engine == "flightsql" ? dialect : engine;
                info = new(engine, id is not null && BuiltInEngines.Contains(id) ? DialectCatalog.Builtin.Get(id) : null);
            }
            connections[name] = info;
            return info;
        }

        private static Side? SideOf(YamlNode? list, YamlMapping? inherited, bool source)
        {
            if (list is not YamlSequence { Items: [YamlMapping spec] }) return null;
            var connection = Text(spec, "connectionName") is { Length: > 0 } own ? own : Text(inherited, "connectionName");
            if (string.IsNullOrWhiteSpace(connection)) return null;
            // an explicit "" on the mapping is a real answer (no database to name); null inherits the replication's
            var database = spec.Get("database") is YamlScalar d && (d.Value.Length > 0 || d.Quoted) ? d.Value : Text(inherited, "database") ?? "";
            return new(connection, database, Text(spec, "schema") ?? "dbo", Text(spec, "table") ?? "", source ? Text(spec, "query") : null, source ? Text(spec, "filter") : null,
                spec.Line, LineOf(spec, "filter"), LineOf(spec, "table"));
        }

        private CacheConnection? Cached(Side side, string file)
        {
            if (cache.Connections.TryGetValue(side.Connection, out var cached)) return cached;
            Note("cache:" + side.Connection, "info", NotChecked,
                $"The connection '{side.Connection}' has no entry in {CatalogCache.FileName}, so the mappings on it were not checked offline. Refresh its catalog to check them.", file, side.Line);
            return null;
        }

        private static CacheTable? Find(CacheConnection connection, string database, string schema, string table) =>
            connection.Tables.FirstOrDefault(t =>
                string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)
                && (database.Length == 0 || t.Database.Length == 0 || string.Equals(t.Database, database, StringComparison.OrdinalIgnoreCase)));

        private static CacheColumn? ColumnOf(CacheTable table, string name) => table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        private static string Where(CacheConnection connection) => connection.Read is { } r ? $" (read from {r.Server}{(r.Channel is { } c ? $", channel {c}" : "")} at {r.At})" : "";

        private static string Label(Side side) => string.Join('.', new[] { side.Database, side.Schema, side.Table }.Where(p => p.Length > 0));

        public void Mapping(string replicationFolder, string mappingPath)
        {
            var file = Relative(root, mappingPath);
            if (Read(mappingPath) is not { } mapping) return;
            var task = Read(Path.Combine(replicationFolder, "task.yaml"));
            var endpoints = task?.Get("endpoints") as YamlMapping;
            var source = SideOf(mapping.Get("sources"), endpoints?.Get("source") as YamlMapping, source: true);
            var target = SideOf(mapping.Get("targets"), endpoints?.Get("target") as YamlMapping, source: false);
            var columns = (mapping.Get("columnMappings") as YamlSequence)?.Items.OfType<YamlMapping>().ToList() ?? [];
            if (source is null || target is null)
            {
                // a mapping of several sources or targets, or one whose sides do not resolve: the program's check names what is wrong
                if (mapping.Get("sources") is YamlSequence { Items.Count: > 1 } || mapping.Get("targets") is YamlSequence { Items.Count: > 1 })
                    Found.Add(new("info", NotChecked, "The mapping has more than one source or target; only a mapping of one source and one target is checked offline.", file, mapping.Line));
                return;
            }

            var sourceInfo = Connection(source.Connection);
            var targetInfo = Connection(target.Connection);
            CacheTable? sourceTable = null;
            if (source.Query is null || source.Table.Length > 0)
            {
                if (Cached(source, file) is { } sourceCache)
                {
                    sourceTable = Find(sourceCache, source.Database, source.Schema, source.Table);
                    if (sourceTable is null)
                        Found.Add(new("warning", Table, $"The source table {Label(source)} of the connection '{source.Connection}' is not in {CatalogCache.FileName}{Where(sourceCache)}. Refresh the connection's catalog, or correct the table's name.", file, source.TableLine));
                    else SourceColumns(file, mapping, source, sourceCache, sourceTable, sourceInfo, columns);
                }
            }

            CacheTable? targetTable = null;
            if (Cached(target, file) is { } targetCache) targetTable = Find(targetCache, target.Database, target.Schema, target.Table);   // a target not there yet is one provisioning creates
            if (sourceTable is not null && targetTable is not null) TypesAgree(file, source, target, sourceTable, targetTable, sourceInfo, targetInfo, columns);
            Keys(file, mapping, sourceTable, targetTable, target, columns);
        }

        private void SourceColumns(string file, YamlMapping mapping, Side source, CacheConnection sourceCache, CacheTable table, ConnectionInfo? info, List<YamlMapping> columns)
        {
            var relationships = (mapping.Get("relationships") as YamlSequence)?.Items.OfType<YamlMapping>()
                .Where(r => Text(r, "name") is not null).GroupBy(r => Text(r, "name")!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal) ?? [];
            var checkedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                if (Text(column, "sourceColumn") is not { Length: > 0 } name) continue;
                var at = LineOf(column, "sourceColumn");
                var from = table;
                var fromLabel = Label(source);
                if (Text(column, "relationship") is { Length: > 0 } relationship)
                {
                    if (!relationships.TryGetValue(relationship, out var joined) || Text(joined, "table") is not { Length: > 0 } joinedTable) continue;   // the program's check refuses an unknown relationship
                    var joinedSide = source with { Schema = Text(joined, "schema") ?? "dbo", Table = joinedTable };
                    fromLabel = Label(joinedSide);
                    var found = Find(sourceCache, joinedSide.Database, joinedSide.Schema, joinedSide.Table);
                    if (found is null)
                    {
                        Note($"rel:{file}:{relationship}", "warning", Table, $"The table {fromLabel} of the relationship '{relationship}' is not in {CatalogCache.FileName}{Where(sourceCache)}.", file, LineOf(joined, "table"));
                        continue;
                    }
                    from = found;
                }
                if (ColumnOf(from, name) is not { } cached)
                {
                    Found.Add(new("warning", Column, $"The source column '{name}' is not a column of {fromLabel} in {CatalogCache.FileName}{Where(sourceCache)}.", file, at));
                    continue;
                }
                if (info?.Dialect is not { } dialect || !checkedTypes.Add(fromLabel + "." + cached.Name)) continue;
                var mapped = dialect.ToCanonical(cached.Type);
                if (!mapped.IsMapped)
                    Found.Add(new("warning", UnknownType, $"The source column '{name}' of {fromLabel} has the type {cached.Type}, which the {info.Engine} driver's type map does not know{(mapped.Note is { Length: > 0 } n ? $" ({n})" : "")}. Leave the column out, or give it a transform that reads it as a type the driver knows.", file, at));
            }
            if (info?.Dialect is null)
                Note("types:" + source.Connection, "info", NotChecked,
                    $"The connection '{source.Connection}' uses the driver '{info?.Engine ?? "unknown"}', whose type map is Sync's own: its source types and filters are not checked offline, only by the set's preflight.", file, source.Line);
            if (source.Filter is { Length: > 0 } filter && info?.Dialect is { } filterDialect) Filter(file, source, sourceCache, table, filter, filterDialect);
        }

        private void Filter(string file, Side source, CacheConnection sourceCache, CacheTable table, string filter, ISqlDialect dialect)
        {
            var (names, unreadable) = FilterColumns(filter, dialect, n => ColumnOf(table, n) is not null);
            if (unreadable is not null)
            {
                Found.Add(new("info", NotChecked, $"The source filter is not checked offline: the {dialect.Id.Value} lexer cannot read it ({unreadable}).", file, source.FilterLine));
                return;
            }
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                Found.Add(new("warning", Column, $"The source filter names '{name}', which is not a column of {Label(source)} in {CatalogCache.FileName}{Where(sourceCache)}.", file, source.FilterLine));
        }

        private void TypesAgree(string file, Side source, Side target, CacheTable sourceTable, CacheTable targetTable, ConnectionInfo? sourceInfo, ConnectionInfo? targetInfo, List<YamlMapping> columns)
        {
            if (sourceInfo?.Dialect is not { } from || targetInfo?.Dialect is not { } to) return;
            foreach (var column in columns)
            {
                // a transform's result, or a column of a joined table, has a type the cache does not give
                if (Text(column, "transform") is { Length: > 0 } || Text(column, "relationship") is { Length: > 0 }) continue;
                if (Text(column, "sourceColumn") is not { Length: > 0 } sourceName || Text(column, "targetColumn") is not { Length: > 0 } targetName) continue;
                if (ColumnOf(sourceTable, sourceName) is not { } s || ColumnOf(targetTable, targetName) is not { } t) continue;
                var sourceType = from.ToCanonical(s.Type);
                var targetType = to.ToCanonical(t.Type);
                if (!sourceType.IsMapped || !targetType.IsMapped) continue;
                var fit = FidelityRules.Assess(sourceType.Result!, targetType.Result!);
                var pair = $"'{sourceName}' ({s.Type} on {Label(source)}) into '{targetName}' ({t.Type} on {Label(target)})";
                if (!fit.IsMapped)
                    Found.Add(new("warning", Types, $"The column {pair} is not a type the target column can hold{(fit.Note is { Length: > 0 } n ? $": {n}" : "")}.", file, LineOf(column, "targetColumn")));
                else if (fit.Fidelity == Fidelity.Lossy)
                    Found.Add(new("warning", Types, $"The column {pair} may not fit{(fit.Note is { Length: > 0 } n ? $": {n}" : "")}.", file, LineOf(column, "targetColumn")));
            }
        }

        private void Keys(string file, YamlMapping mapping, CacheTable? sourceTable, CacheTable? targetTable, Side target, List<YamlMapping> columns)
        {
            // the mapping's key names source columns: its primaryKey, else its own column cache's key, else the catalog cache's key of the source
            var stated = (mapping.Get("primaryKey") as YamlSequence)?.Items.OfType<YamlScalar>().Select(s => s.Value).Where(v => v.Length > 0).ToList() ?? [];
            var key = stated.Count > 0 ? stated
                : (mapping.Get("sourceColumns") as YamlSequence)?.Items.OfType<YamlMapping>().Where(c => Text(c, "isPrimaryKey") == "true").Select(c => Text(c, "name")).OfType<string>().ToList() is { Count: > 0 } own ? own
                : sourceTable?.Columns.Where(c => c.Key).Select(c => c.Name).ToList() ?? [];
            if (key.Count == 0) return;
            var at = mapping.GetEntry("primaryKey") is { } entry ? entry.Value.Line : LineOf(mapping, "targets");
            if (sourceTable is not null)
                foreach (var name in key)
                    if (ColumnOf(sourceTable, name) is { Nullable: true } c)
                        Found.Add(new("warning", Key, $"The key column '{c.Name}' is nullable on the source in {CatalogCache.FileName}: a row whose key is null cannot be matched on the target.", file, at));
            if (targetTable is null) return;
            var targetKey = new List<string>();
            foreach (var name in key)
            {
                var mapped = columns.FirstOrDefault(c => string.Equals(Text(c, "sourceColumn"), name, StringComparison.OrdinalIgnoreCase) && Text(c, "relationship") is null);
                if (Text(mapped, "targetColumn") is not { Length: > 0 } targetName) return;   // a key column that is not mapped is refused by the program's check
                targetKey.Add(targetName);
            }
            foreach (var name in targetKey)
                if (ColumnOf(targetTable, name) is { Nullable: true } c)
                    Found.Add(new("warning", Key, $"The key column '{c.Name}' is nullable on the target {Label(target)} in {CatalogCache.FileName}: a key column must not be nullable.", file, at));
            var primary = targetTable.Columns.Where(c => c.Key).Select(c => c.Name).ToList();
            var candidates = targetTable.UniqueKeys.Select(u => u.Columns.ToList()).ToList();
            if (primary.Count > 0) candidates.Insert(0, primary);
            if (candidates.Count == 0) return;
            var wanted = new HashSet<string>(targetKey, StringComparer.OrdinalIgnoreCase);
            if (candidates.Any(k => wanted.SetEquals(k))) return;
            Found.Add(new("warning", Key, $"The mapping's key ({string.Join(", ", targetKey)}) is not the primary key ({(primary.Count > 0 ? string.Join(", ", primary) : "none")}) or a unique key of the target {Label(target)} in {CatalogCache.FileName}: rows would be matched on columns the target does not keep unique.", file, at));
        }
    }

    /// <summary>
    /// The names a filter uses as columns that <paramref name="isColumn"/> does not know, or why the lexer cannot read it. A word is a column unless it is a
    /// keyword, a function's name, a qualifier, a type (after <c>AS</c> or <c>::</c>, or before a typed literal); inside a function's parentheses a word that is
    /// no column is taken for an argument the function names (a date part), since a lexer cannot tell the two apart.
    /// </summary>
    internal static (List<string> Unknown, string? Unreadable) FilterColumns(string filter, ISqlDialect dialect, Func<string, bool> isColumn)
    {
        var tokens = SqlLexer.Tokenize(filter, dialect.Syntax).Where(t => !t.IsTrivia).ToList();
        if (tokens.Any(t => !t.Terminated)) return ([], "a string, a quoted name or a comment is not closed");
        if (tokens.Any(t => t.Kind == SqlTokenKind.Separator)) return ([], "it holds a ';'");
        if (tokens.Any(t => t.Executable)) return ([], "it holds an executable comment");
        if (tokens.Any(t => t.IsWord("SELECT"))) return ([], "it holds a subquery");
        if (tokens.Any(t => t.Kind == SqlTokenKind.Operator && t.Text is "{" or "}")) return ([], "it holds braces");
        var unknown = new List<string>();
        var calls = new Stack<bool>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var previous = i > 0 ? tokens[i - 1] : default;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            if (token is { Kind: SqlTokenKind.Punctuation, Text: "(" })
            {
                calls.Push(previous.Kind == SqlTokenKind.QuotedIdentifier
                    || previous.Kind == SqlTokenKind.Word && !dialect.Identifiers.IsReserved(previous.Text) && !FilterWords.Contains(previous.Text));
                continue;
            }
            if (token is { Kind: SqlTokenKind.Punctuation, Text: ")" })
            {
                if (calls.Count == 0) return ([], "its parentheses do not match");
                calls.Pop();
                continue;
            }
            if (token.Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)) continue;
            if (next is { Kind: SqlTokenKind.Punctuation, Text: "(" or "." }) continue;   // a function's name, or a qualifier
            string name;
            if (token.Kind == SqlTokenKind.QuotedIdentifier)
            {
                try { name = dialect.SplitQualifiedName(token.Text) is [var only] ? only : token.Text; }
                catch (FormatException) { return ([], "a quoted name cannot be read"); }
            }
            else
            {
                name = token.Text;
                if (name.StartsWith('@') || dialect.Identifiers.IsReserved(name) || FilterWords.Contains(name)) continue;
                if (previous.IsWord("AS") || next.Kind == SqlTokenKind.String) continue;   // CAST(x AS int), DATE '2026-01-01'
                if (previous is { Kind: SqlTokenKind.Operator, Text: ":" } && i > 1 && tokens[i - 2] is { Kind: SqlTokenKind.Operator, Text: ":" }) continue;   // x::date
            }
            if (isColumn(name)) continue;
            if (token.Kind == SqlTokenKind.Word && calls.Contains(true)) continue;
            unknown.Add(name);
        }
        return calls.Count == 0 ? (unknown, null) : ([], "its parentheses do not match");
    }
}
