using System.Text;
using DbDataKit.Authoring;
using DbDataKit.Catalog;
using DbDataKit.Config.Yaml;
using DbDataKit.Dialect;
using DbDataKit.Transfer;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Descriptor;
using DbDataSync.Drivers.Descriptor;
using DbDataKit.Sync.Drivers.Generic;
using DbDataKit.Sync.Provisioning;
using Sql = DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The offline checks of a Sync draft: each table mapping read against the draft's <c>catalog-cache.yaml</c>, its connection files and its driver
/// descriptors (<c>drivers/&lt;id&gt;/driver.yaml</c>), with no connection and no program, in the Workspace's own process. Per mapping: the tables and
/// the mapped columns are in the cache, the columns a source filter names are columns of its table (a filter the lexer cannot read is not checked
/// offline), every mapped source type is one the driver reads, each source type fits the target column's cached type, the key columns are not nullable,
/// the mapping's key is the target's primary key or one of its unique keys, and a reader or writer that matches rows on a key has one.
/// <para>
/// Types are read by Sync's own code, so the check and the engine agree on a type: a side's columns through Sync's column reader over the cache
/// (<see cref="MappingColumnReader"/>, <see cref="SyncCatalogColumns"/>), each native type through the driver's Sync dialect (the built-in engines' dialects
/// over the shared type tables, the type map of a <c>driver.yaml</c>, ANSI first for a Flight SQL server), then the two sides compared by the shared
/// <see cref="FidelityRules"/>. Which readers and writers need a key is asked of Sync's generic drivers (<c>DbDataKit.Sync.Drivers.Generic</c>), in their own
/// words; an engine's own reader or writer (change tracking, a logical slot, an engine's merge) by the key the built-in drivers' table
/// (<see cref="BuiltInDriverCapabilities"/>) says it needs. A Kind of a driver the project carries is reported as not checked offline.
/// </para>
/// <para>
/// A table or column the mapping names that the cache does not hold is an error, so a build never carries a mapping the cache contradicts: the author
/// refreshes the cache or corrects the name. A type that does not fit and a key that does not agree are warnings, since the live checks of the set's
/// <c>preflight</c> judge them again on the databases themselves. Files that do not parse are left to the program's check, which names them.
/// Design: docs/authoring-metadata.md#a-sync-project
/// </para>
/// </summary>
public static class SyncOfflineCheck
{
    /// <summary>The draft has no catalog cache, so none of the offline checks ran.</summary>
    public const string NoCache = "DBD-WS-SYNC-NOCACHE";

    /// <summary>Something was not checked offline: a connection with no entry in the cache, a driver with no <c>driver.yaml</c> in the draft, a filter the
    /// lexer cannot read, the key of a reader or writer that is not a built-in driver's.</summary>
    public const string NotChecked = "DBD-WS-SYNC-NOTCHECKED";

    /// <summary>A table a mapping names is not in the cache.</summary>
    public const string Table = "DBD-WS-SYNC-TABLE";

    /// <summary>A mapped column, or a column a filter names, is not in the cached table.</summary>
    public const string Column = "DBD-WS-SYNC-COLUMN";

    /// <summary>A source column's type is one the driver's type map does not know.</summary>
    public const string UnknownType = "DBD-WS-SYNC-TYPE";

    /// <summary>A source column's type does not fit, or may not fit, the target column's cached type.</summary>
    public const string Types = "DBD-WS-SYNC-TYPES";

    /// <summary>The mapping's key: a nullable key column, a key that is not the target's primary key or a unique key, or no key where the reader or writer
    /// matches rows on one.</summary>
    public const string Key = "DBD-WS-SYNC-KEY";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The Sync dialect of each built-in engine, under the name a connection file gives it.</summary>
    private static readonly Dictionary<string, Sql.SqlDialect> BuiltInEngines = new(StringComparer.Ordinal)
    {
        ["sqlserver"] = Sql.MsSqlDialect.Instance,
        ["postgres"] = Sql.PostgresDialect.Instance,
        ["mysql"] = Sql.MySqlDialect.Instance,
        ["oracle"] = Sql.OracleDialect.Instance,
        ["duckdb"] = Sql.DuckDbDialect.Instance,
    };

    /// <summary>The readers and writers of Sync's generic drivers, which every engine's driver and every <c>driver.yaml</c> offers. Any other kind is an
    /// engine's own, and whether it needs a key is not known offline.</summary>
    private static readonly HashSet<string> GenericKinds = new(StringComparer.Ordinal)
    {
        GenericDriverKinds.Watermark, GenericDriverKinds.TriggerAudit, GenericDriverKinds.BatchReload, GenericDriverKinds.KeyReconcile,
        GenericDriverKinds.DeleteInsert, GenericDriverKinds.ChangeApply, GenericDriverKinds.KeyReconcileDelete, GenericDriverKinds.KeyReconcileScd2Close,
        GenericDriverKinds.Snapshot, GenericDriverKinds.Scd2,
    };

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

    /// <summary>
    /// How a connection's driver reads a native type and which dialect writes its DDL, as the engine resolves the driver from the connection file:
    /// a built-in engine, Flight SQL (an ANSI type name first, then the dialect of the engine behind it), or a <c>driver.yaml</c> of the draft. The
    /// reason instead when the draft does not say (a compiled driver, a connection file that does not read).
    /// </summary>
    internal static (Sql.SqlDialect? Dialect, Func<string, TypeMapping<CanonicalType>>? ReadType, string? Unknown) TypesOf(string root, string connection)
    {
        var info = new Run(root, new CatalogCacheFile("", new Dictionary<string, CacheConnection>())).Connection(connection);
        if (info is null) return (null, null, $"config/connections/{connection}.yaml does not name an engine");
        return (info.Dialect, info.ToCanonical, info.Unchecked ?? (info.ToCanonical is null ? "its types are not known offline" : null));
    }

    private sealed record Side(string Connection, string Database, string Schema, string Table, string? Query, string? Filter, int Line, int FilterLine, int TableLine)
    {
        public TableRef Ref => new() { ConnectionName = Connection, Database = Database, Schema = Schema, Table = Table };
    }

    /// <summary>How a filter's text is read: the lexer's syntax, the reserved words, and how a quoted name is unwrapped.</summary>
    internal sealed record FilterReader(string Name, SqlSyntax Syntax, Func<string, bool> IsReserved, Func<string, IReadOnlyList<string>> Split)
    {
        public static FilterReader Of(ISqlDialect dialect) => new(dialect.Id.Value, dialect.Syntax, dialect.Identifiers.IsReserved, dialect.SplitQualifiedName);
    }

    /// <param name="Engine">The connection file's <c>engine</c>: a built-in engine's name, <c>flightsql</c>, or a driver's id.</param>
    /// <param name="Dialect">The Sync dialect the engine's driver builds its statements with; null when the check cannot know it.</param>
    /// <param name="ToCanonical">How the driver reads a native type, as the engine does; null when the types are not checked offline.</param>
    /// <param name="Filter">How the driver's filters are read; null when they are not checked offline.</param>
    /// <param name="TypeMap">How a finding names the type map: "the sqlserver driver's type map".</param>
    /// <param name="Unchecked">Why the types and filters are not checked offline, when they are not.</param>
    private sealed record ConnectionInfo(string Engine, Sql.SqlDialect? Dialect, Func<string, TypeMapping<CanonicalType>>? ToCanonical, FilterReader? Filter, string TypeMap, string? Unchecked);

    private sealed class Run(string root, CatalogCacheFile cache)
    {
        public List<ProjectDiagnostic> Found { get; } = [];
        private readonly Dictionary<string, ConnectionInfo?> connections = new(StringComparer.Ordinal);
        private readonly HashSet<string> noted = new(StringComparer.Ordinal);
        private readonly MappingColumnReader reader = new(new SyncCatalogColumns(cache));
        private Dictionary<string, (DriverDescriptorYaml? Descriptor, string File, string? Error)>? descriptors;

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

        /// <summary>The draft's driver descriptors by their id, read once: where the engine loads them, <c>drivers/&lt;folder&gt;/driver.yaml</c>. One that does
        /// not read is kept under its folder's name with the reason.</summary>
        private Dictionary<string, (DriverDescriptorYaml? Descriptor, string File, string? Error)> Descriptors()
        {
            if (descriptors is not null) return descriptors;
            descriptors = new(StringComparer.Ordinal);
            var folder = Path.Combine(root, "drivers");
            if (!Directory.Exists(folder)) return descriptors;
            foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
            {
                var path = Path.Combine(dir, "driver.yaml");
                if (!File.Exists(path)) continue;
                var file = Relative(root, path);
                try
                {
                    var descriptor = DriverDescriptors.Read(path);
                    descriptors.TryAdd(descriptor.Id, (descriptor, file, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or YamlDotNet.Core.YamlException)
                {
                    descriptors.TryAdd(Path.GetFileName(dir), (null, file, ex.Message));
                }
            }
            return descriptors;
        }

        internal ConnectionInfo? Connection(string name)
        {
            if (connections.TryGetValue(name, out var known)) return known;
            ConnectionInfo? info = null;
            var path = Path.Combine(root, "config", "connections", name + ".yaml");
            if (SyncProjectKind.IsName(name) && File.Exists(path) && Read(path) is { } map && Text(map, "engine") is { Length: > 0 } engine)
                info = Resolve(engine, Text(map, "dialect"));
            connections[name] = info;
            return info;
        }

        /// <summary>The driver a connection file names, as the engine resolves it: a built-in engine, Flight SQL with or without the dialect of the engine
        /// behind it, or a driver's id, which is a <c>driver.yaml</c> of the draft or a driver the check cannot read.</summary>
        private ConnectionInfo Resolve(string engine, string? dialectName)
        {
            var ansi = DialectCatalog.Builtin.Get("ansi");
            if (BuiltInEngines.TryGetValue(engine, out var builtIn))
                return new(engine, builtIn, builtIn.ReadColumnType, FilterReader.Of(DialectCatalog.Builtin.Get(engine)), $"the {engine} driver's type map", null);
            if (engine == "flightsql")
            {
                if (dialectName is null)
                    return new(engine, Sql.AnsiDialect.Instance, native => Sql.FlightSqlTypes.Read(Sql.AnsiDialect.Instance, native), FilterReader.Of(ansi),
                        "the flightsql driver's type map", null);
                if (BuiltInEngines.TryGetValue(dialectName, out var inner))
                    return new(engine, inner, native => Sql.FlightSqlTypes.Read(inner, native),
                        FilterReader.Of(DialectCatalog.Builtin.Get(dialectName)), $"the flightsql driver's type map (with the {dialectName} dialect)", null);
                return new(engine, null, null, null, "", $"the dialect '{dialectName}' is not one a flightsql connection takes");
            }
            if (!Descriptors().TryGetValue(engine, out var found))
                return new(engine, null, null, null, "",
                    $"the driver '{engine}' has no driver.yaml under drivers/ in the draft (a compiled driver, or one installed where the set runs), so its type map is not known here");
            if (found.Descriptor is not { } descriptor)
                return new(engine, null, null, null, "", $"{found.File} does not read ({found.Error})");
            Sql.SqlDialect dialect;
            try { dialect = new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap); }
            catch (NotSupportedException ex) { return new(engine, null, null, null, "", $"{found.File} does not read ({ex.Message})"); }
            return new(engine, dialect, dialect.ReadColumnType, DescriptorFilter(descriptor, dialect, ansi), $"the {engine} driver's type map ({found.File})", null);
        }

        /// <summary>
        /// A <c>driver.yaml</c> names how its engine quotes a name but not the rest of its grammar, so its filters are read by the ANSI lexer with that quoting:
        /// brackets as well as double quotes (SQL Server's way), or backticks with double quotes as strings (MySQL's way).
        /// </summary>
        private static FilterReader DescriptorFilter(DriverDescriptorYaml descriptor, Sql.SqlDialect dialect, ISqlDialect ansi)
        {
            var syntax = descriptor.Dialect.QuoteIdentifier switch
            {
                "bracket" => ansi.Syntax | SqlSyntax.BracketIdentifier,
                "backtick" => (ansi.Syntax & ~SqlSyntax.DoubleQuoteIdentifier) | SqlSyntax.BacktickIdentifier | SqlSyntax.DoubleQuoteString,
                _ => ansi.Syntax,
            };
            return new(descriptor.Id, syntax, ansi.Identifiers.IsReserved,
                quoted => quoted.StartsWith('"') && descriptor.Dialect.QuoteIdentifier != "doubleQuote" ? ansi.SplitQualifiedName(quoted) : dialect.SplitQualifiedName(quoted));
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

        /// <summary>A side's columns as Sync's column reader reads them from the cache: the answer the engine's provisioning and key rules start from.
        /// Null when the table is not there. The cache answers in memory, so the read completes before it returns.</summary>
        private List<ColumnMetadata>? ColumnsOf(Side side) =>
            reader.ReadAsync(side.Ref, CancellationToken.None).GetAwaiter().GetResult().Shape?
                .Select(c => new ColumnMetadata(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity, c.KeyOrdinal)).ToList();

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
                        Found.Add(new("error", Table, $"The source table {Label(source)} of the connection '{source.Connection}' is not in {CatalogCache.FileName}{Where(sourceCache)}. Refresh the connection's catalog, or correct the table's name.", file, source.TableLine));
                    else SourceColumns(file, mapping, source, sourceCache, sourceTable, sourceInfo, columns);
                }
            }

            CacheTable? targetTable = null;
            var targetCache = Cached(target, file);
            if (targetCache is not null) targetTable = Find(targetCache, target.Database, target.Schema, target.Table);   // a target not there yet is one provisioning creates
            if (sourceTable is not null && targetTable is not null) TypesAgree(file, source, target, sourceInfo, targetInfo, columns);
            Keys(file, mapping, sourceTable, targetTable, target, columns);
            KeysNeeded(file, task, replicationFolder, mappingPath, mapping, source, target, sourceTable is not null, targetTable is not null, targetCache is not null, sourceInfo, targetInfo);
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
                        Note($"rel:{file}:{relationship}", "error", Table, $"The table {fromLabel} of the relationship '{relationship}' is not in {CatalogCache.FileName}{Where(sourceCache)}.", file, LineOf(joined, "table"));
                        continue;
                    }
                    from = found;
                }
                if (ColumnOf(from, name) is not { } cached)
                {
                    Found.Add(new("error", Column, $"The source column '{name}' is not a column of {fromLabel} in {CatalogCache.FileName}{Where(sourceCache)}.", file, at));
                    continue;
                }
                if (info?.ToCanonical is not { } read || !checkedTypes.Add(fromLabel + "." + cached.Name)) continue;
                var mapped = read(cached.Type);
                if (!mapped.IsMapped)
                    Found.Add(new("warning", UnknownType, $"The source column '{name}' of {fromLabel} has the type {cached.Type}, which {info.TypeMap} does not know{(mapped.Note is { Length: > 0 } n ? $" ({n})" : "")}. Leave the column out, or give it a transform that reads it as a type the driver knows.", file, at));
            }
            if (info?.ToCanonical is null)
                Note("types:" + source.Connection, "info", NotChecked,
                    $"The connection '{source.Connection}' uses the driver '{info?.Engine ?? "unknown"}': {info?.Unchecked ?? "its connection file does not name an engine"}. Its source types and filters are not checked offline, only by the set's preflight.", file, source.Line);
            if (source.Filter is { Length: > 0 } filter && info?.Filter is { } filterReader) Filter(file, source, sourceCache, table, filter, filterReader);
        }

        private void Filter(string file, Side source, CacheConnection sourceCache, CacheTable table, string filter, FilterReader dialect)
        {
            var (names, unreadable) = FilterColumns(filter, dialect, n => ColumnOf(table, n) is not null);
            if (unreadable is not null)
            {
                Found.Add(new("info", NotChecked, $"The source filter is not checked offline: the {dialect.Name} lexer cannot read it ({unreadable}).", file, source.FilterLine));
                return;
            }
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                Found.Add(new("error", Column, $"The source filter names '{name}', which is not a column of {Label(source)} in {CatalogCache.FileName}{Where(sourceCache)}.", file, source.FilterLine));
        }

        /// <summary>
        /// Each mapped source column's type, as the source's driver reads it (the type provisioning would give the target column), against the target
        /// column's type as the target's driver reads it, judged by the shared <see cref="FidelityRules"/>.
        /// </summary>
        private void TypesAgree(string file, Side source, Side target, ConnectionInfo? sourceInfo, ConnectionInfo? targetInfo, List<YamlMapping> columns)
        {
            if (sourceInfo?.ToCanonical is not { } fromSource || targetInfo?.ToCanonical is not { } fromTarget) return;
            if (ColumnsOf(source) is not { } sourceColumns || ColumnsOf(target) is not { } targetColumns) return;
            foreach (var column in columns)
            {
                // a transform's result, or a column of a joined table, has a type the cache does not give
                if (Text(column, "transform") is { Length: > 0 } || Text(column, "relationship") is { Length: > 0 }) continue;
                if (Text(column, "sourceColumn") is not { Length: > 0 } sourceName || Text(column, "targetColumn") is not { Length: > 0 } targetName) continue;
                var s = sourceColumns.FirstOrDefault(c => string.Equals(c.Name, sourceName, StringComparison.OrdinalIgnoreCase));
                var t = targetColumns.FirstOrDefault(c => string.Equals(c.Name, targetName, StringComparison.OrdinalIgnoreCase));
                if (s is null || t is null) continue;
                var sourceType = fromSource(s.NativeType);
                var targetType = fromTarget(t.NativeType);
                if (sourceType.Result is not { } sourceShared || !sourceType.IsMapped || targetType.Result is not { } targetShared || !targetType.IsMapped) continue;
                var fit = FidelityRules.Assess(sourceShared, targetShared);
                var pair = $"'{sourceName}' ({s.NativeType} on {Label(source)}) into '{targetName}' ({t.NativeType} on {Label(target)})";
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
            var at = KeyLine(mapping);
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

        private static int KeyLine(YamlMapping mapping) => mapping.GetEntry("primaryKey") is { } entry ? entry.Value.Line : LineOf(mapping, "targets");

        /// <summary>The replication and the mapping as Sync's configuration model reads them, or null when a file does not read (the program's check names it).</summary>
        private static (ReplicationTaskConfig Task, TableMappingConfig Mapping)? LoadConfig(YamlMapping? taskYaml, string replicationFolder, string mappingPath)
        {
            // a task with no change-processing reader has no pipeline to resolve; the program's check refuses it
            if (taskYaml?.Get("changeProcessing") is not YamlMapping { } processing || processing.Get("reader") is not YamlMapping) return null;
            try
            {
                var repository = ConfigRepository.ReadOnly(Path.GetDirectoryName(Path.GetDirectoryName(replicationFolder))!, SecretStore.ForProviders());
                var replication = Path.GetFileName(replicationFolder);
                var task = repository.LoadReplicationTask(replication);
                var mapping = repository.LoadTableMapping(replication, Path.GetFileNameWithoutExtension(mappingPath));
                return (task, mapping);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ConfigValidationException or YamlDotNet.Core.YamlException
                or ArgumentException or FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// The readers and writers the mapping runs (its change processing, its bulk load, and its reconcile sweep when that is on), each asked whether it
        /// matches rows on a key and whether the cache gives it one. A target that is not in the cache is checked as provisioning would create it: the mapped
        /// columns with the source's key.
        /// </summary>
        private void KeysNeeded(string file, YamlMapping? taskYaml, string replicationFolder, string mappingPath, YamlMapping yaml, Side source, Side target, bool sourceInCache, bool targetInCache, bool targetConnectionCached, ConnectionInfo? sourceInfo, ConnectionInfo? targetInfo)
        {
            if (LoadConfig(taskYaml, replicationFolder, mappingPath) is not { } loaded) return;
            var (task, mapping) = loaded;
            var stages = new List<(string Role, string Kind, IReadOnlyDictionary<string, string> Options)>
            {
                ("reader", PipelineResolution.Reader(task, mapping).Kind, PipelineResolution.Reader(task, mapping).Options),
                ("writer", PipelineResolution.Writer(task, mapping).Kind, PipelineResolution.Writer(task, mapping).Options),
            };
            if (PipelineResolution.BulkLoadReader(task, mapping) is { } bulkReader) stages.Add(("reader", bulkReader.Kind, bulkReader.Options));
            if (PipelineResolution.BulkLoadWriter(task, mapping) is { } bulkWriter) stages.Add(("writer", bulkWriter.Kind, bulkWriter.Options));
            if (PipelineResolution.Reconcile(task, mapping).Enabled)
            {
                stages.Add(("reader", PipelineResolution.ReconcileReaderKind(task, mapping), new Dictionary<string, string>()));
                stages.Add(("writer", PipelineResolution.ReconcileWriterKind(task, mapping), PipelineResolution.Writer(task, mapping).Options));
            }

            IReadOnlyList<ColumnMetadata>? sourceColumns = sourceInCache && ColumnsOf(source) is { } read ? read.WithKey(mapping.PrimaryKey) : null;
            var targetColumns = targetInCache ? TargetAsCached(target, mapping) : targetConnectionCached ? TargetAsProvisioned(sourceColumns, sourceInfo, mapping) : null;
            foreach (var (role, kind, options) in stages.DistinctBy(s => (s.Role, s.Kind)))
            {
                if (string.IsNullOrEmpty(kind)) continue;
                var onTarget = kind is GenericDriverKinds.ChangeApply or GenericDriverKinds.KeyReconcileDelete;
                string? problem;
                if (GenericKinds.Contains(kind))
                    problem = KeyProblem(kind, options, mapping, source, target, sourceColumns, targetColumns, targetInfo);
                else if (EngineKeyNeed(role == "reader" ? source.Connection : target.Connection, role, kind) is { } need)
                {
                    // an engine's own reader or writer, whose key need the built-in drivers' table states: the same rules as the generic Kinds that need that key
                    onTarget = need == KeyNeed.TargetKey;
                    problem = need switch
                    {
                        KeyNeed.SourceKey when sourceInCache && ColumnsOf(source) is { } raw && !raw.Any(c => c.IsPrimaryKey) =>
                            $"the source table {Label(source)} has no primary key in {CatalogCache.FileName}, and the {role} reports rows by the source's primary key.",
                        KeyNeed.TargetKey when targetColumns is not null => TargetKeyProblem(kind, mapping, target, targetColumns),
                        KeyNeed.NaturalKey => KeyProblem(GenericDriverKinds.Scd2, options, mapping, source, target, sourceColumns, targetColumns, targetInfo),
                        _ => null,
                    };
                }
                else
                {
                    Note($"kind:{replicationFolder}:{role}:{kind}", "info", NotChecked,
                        $"The {role} '{kind}' is not a Kind of the built-in driver of its connection (a driver the project carries, or one installed where the set runs): whether it needs a key is not checked offline, only by the set's preflight.", file, null);
                    continue;
                }
                if (problem is null) continue;
                var where = onTarget && !targetInCache
                    ? $"The target {Label(target)} is not in {CatalogCache.FileName}, so it is checked as provisioning would create it, with the source's key. "
                    : "";
                Found.Add(new("warning", Key, $"{where}The {role} '{kind}' would refuse this mapping: {problem}", file, onTarget ? KeyLine(yaml) : source.TableLine));
            }
        }

        /// <summary>The key an engine's own reader or writer needs, from the built-in drivers' table for the driver of the side's connection; null when the
        /// driver is not built in, the Kind is not one of its own, or the connection does not read.</summary>
        private KeyNeed? EngineKeyNeed(string connection, string role, string kind)
        {
            string driverType;
            try { driverType = ConfigRepository.ReadOnly(Path.Combine(root, "config"), SecretStore.ForProviders()).LoadConnection(connection).DriverType; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ConfigValidationException or YamlDotNet.Core.YamlException or ArgumentException)
            {
                return null;
            }
            if (BuiltInDriverCapabilities.For(driverType) is not { } driver) return null;
            return role == "reader"
                ? driver.Readers.FirstOrDefault(r => r.Kind == kind)?.NeedsKey
                : driver.Writers.FirstOrDefault(w => w.Kind == kind)?.NeedsKey;
        }

        /// <summary>Why a writer that matches rows on the target's primary key would refuse the mapping: the target has none, or a key column is not mapped.</summary>
        private static string? TargetKeyProblem(string kind, TableMappingConfig mapping, Side target, List<CachedColumn> targetColumns)
        {
            var key = targetColumns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (key.Count == 0)
                return $"Table mapping '{mapping.Name}': target table '{target.Schema}.{target.Table}' has no primary key, and the '{kind}' writer matches rows on it.";
            var missing = key.Where(k => !mapping.ColumnMappings.Any(m => string.Equals(m.TargetColumn, k, StringComparison.OrdinalIgnoreCase))).ToList();
            return missing.Count == 0 ? null
                : $"Table mapping '{mapping.Name}': the column mappings must include the target primary key column(s) {string.Join(", ", missing)}; the '{kind}' writer matches rows on them.";
        }

        /// <summary>Why a generic reader or writer would refuse the mapping for want of a key, in the words of Sync's code where it has a rule to call; null when it has a key or the cache cannot tell.</summary>
        private static string? KeyProblem(string kind, IReadOnlyDictionary<string, string> options, TableMappingConfig mapping, Side source, Side target,
            IReadOnlyList<ColumnMetadata>? sourceColumns, List<CachedColumn>? targetColumns, ConnectionInfo? targetInfo)
        {
            switch (kind)
            {
                case GenericDriverKinds.TriggerAudit when sourceColumns is not null:
                    var plan = TriggerAuditPlan.Build(Label(source), new TriggerAuditState(false, false, [.. sourceColumns.InKeyOrder().Select(c => c.Name)]), []);
                    return plan.State == ProvisioningState.Unsupported ? string.Join(" ", plan.Warnings) : null;
                case GenericDriverKinds.KeyReconcile when sourceColumns is not null:
                    return Refused(() => KeyReconcileReader.KeyColumnMappings(mapping.ColumnMappings, sourceColumns, mapping.Name));
                case GenericDriverKinds.ChangeApply when targetColumns is not null:
                    return Refused(() => ChangeApplyWriter.RequireKey(Shape(targetColumns), mapping.Name));
                case GenericDriverKinds.KeyReconcileDelete when targetColumns is not null:
                    return Refused(() => KeyReconcileDeleteWriter.RequireKey(Shape(targetColumns), mapping.Name));
                case GenericDriverKinds.Scd2 or GenericDriverKinds.KeyReconcileScd2Close when sourceColumns is not null:
                    // the run derives the natural key from the source's key when it is not stated, before the writer asks for it
                    return NaturalKeyDerivation.Derive(sourceColumns, mapping.ColumnMappings).Count > 0 ? null : Refused(() => Scd2Writer.RequireNaturalKey(options));
                default:
                    return null;
            }

            // The target as the writer sees it. A mapped column whose source is not in the cache is not in a table provisioning would create; the
            // column checks name it.
            TargetShape Shape(List<CachedColumn> columns) =>
                TargetShape.FromCachedColumns(targetInfo?.Dialect ?? Sql.AnsiDialect.Instance, mapping.Name, columns, target.Ref,
                    [.. mapping.ColumnMappings.Where(m => columns.Any(c => string.Equals(c.Name, m.TargetColumn, StringComparison.OrdinalIgnoreCase)))]);
        }

        private static string? Refused(Action rule)
        {
            try { rule(); return null; }
            catch (InvalidOperationException ex) { return ex.Message; }
            catch (MetadataNotCachedException) { return null; }   // a side with no columns to judge: the column checks name it
        }

        /// <summary>The target's cached columns, with each mapped column the table lacks as the column provisioning would add: nullable, outside the key.</summary>
        private List<CachedColumn>? TargetAsCached(Side target, TableMappingConfig mapping)
        {
            if (ColumnsOf(target) is not { } columns) return null;
            var result = columns.Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity, c.KeyOrdinal)).ToList();
            foreach (var name in mapping.ColumnMappings.Select(m => m.TargetColumn).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!result.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new CachedColumn(name, "", isNullable: true, isPrimaryKey: false, isIdentity: false));
            return result;
        }

        /// <summary>The table provisioning would create for a target that is not there yet, by the engine's own column builder: each mapped source column
        /// under its target name, with the source's key.</summary>
        private static List<CachedColumn>? TargetAsProvisioned(IReadOnlyList<ColumnMetadata>? sourceColumns, ConnectionInfo? sourceInfo, TableMappingConfig mapping)
        {
            if (sourceColumns is null) return null;
            var mapped = mapping.ColumnMappings
                .Where(m => m.Relationship is null && sourceColumns.Any(c => string.Equals(c.Name, m.SourceColumn, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (mapped.Count == 0) return null;
            var (built, _) = ProvisioningColumnBuilder.Build(sourceInfo?.Dialect ?? Sql.AnsiDialect.Instance, sourceColumns, mapped);
            return [.. built.Select(c => new CachedColumn(c.Name, c.TypeOverride ?? "", c.IsNullable, c.IsPrimaryKey, isIdentity: false, c.KeyOrdinal))];
        }
    }

    /// <summary>
    /// The names a filter uses as columns that <paramref name="isColumn"/> does not know, or why the lexer cannot read it. A word is a column unless it is a
    /// keyword, a function's name, a qualifier, a type (after <c>AS</c> or <c>::</c>, or before a typed literal); inside a function's parentheses a word that is
    /// no column is taken for an argument the function names (a date part), since a lexer cannot tell the two apart.
    /// </summary>
    internal static (List<string> Unknown, string? Unreadable) FilterColumns(string filter, FilterReader dialect, Func<string, bool> isColumn)
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
                    || previous.Kind == SqlTokenKind.Word && !dialect.IsReserved(previous.Text) && !FilterWords.Contains(previous.Text));
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
                try { name = dialect.Split(token.Text) is [var only] ? only : token.Text; }
                catch (FormatException) { return ([], "a quoted name cannot be read"); }
            }
            else
            {
                name = token.Text;
                if (name.StartsWith('@') || dialect.IsReserved(name) || FilterWords.Contains(name)) continue;
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
