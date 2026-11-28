using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbDataKit.Catalog;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.Generic;
using DbDataSync.Provisioning;
using DbDataSync.Scripting.Abstractions;

namespace DbDataKit.Authoring.Server;

/// <summary>A question of the sync Designer that cannot be answered as asked: the words for the person and the HTTP status they come back with.</summary>
public sealed class SyncDesignerRefusal(string message, int status, Exception? inner = null) : Exception(message, inner)
{
    public int Status { get; } = status;
}

/// <summary>
/// What the sync Designer asks the Workspace server about a draft, answered in the server's process by Sync's own libraries
/// (<c>DbDataKit.Sync.*</c>) and never by a database: the script slots a binding can name, what a connection's driver offers, the target types and
/// natural key inferred for a mapping, and the files Create mappings makes. The databases' shape is the draft's <c>catalog-cache.yaml</c>, read by
/// <see cref="SyncCatalogColumns"/>; a built-in driver's capabilities are <see cref="BuiltInDriverCapabilities"/>; a <c>driver.yaml</c> the draft
/// carries is read without its library. Nothing here compiles or runs a script a person wrote.
/// <para>
/// Each answer reads the draft as the person sees it: a question may carry the person's unsaved edits (<see cref="DraftView"/>), so a refresh of the
/// cache or a new replication not saved yet is part of the answer. Nothing is written into the draft: Create mappings answers the files' texts and
/// the screen keeps them as unsaved edits.
/// </para>
/// Design: docs/authoring-metadata.md#a-sync-project
/// </summary>
public static class SyncDesigner
{
    /// <summary>How the answers are written: camelCase names and enum values by name, the shapes Sync's screens read.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>At most this many tables in one Create mappings.</summary>
    public const int MaxTables = 200;

    /// <summary>The slots a script binding can name, each with the levels it may be bound at, its label and what it does.</summary>
    public static JsonObject ScriptSlotsAnswer() => new()
    {
        ["slots"] = new JsonArray([.. ScriptSlots.All.Select(slot => (JsonNode)new JsonObject
        {
            ["slot"] = slot,
            ["levels"] = new JsonArray([.. BindingLevels.All.Where(level => ScriptSlots.IsBindableAt(slot, level)).Select(l => (JsonNode)JsonValue.Create(l))]),
            ["label"] = ScriptSlots.Describe(slot).Label,
            ["description"] = ScriptSlots.Describe(slot).Description,
        })]),
    };

    private static ConfigRepository Repository(string root) => ConfigRepository.ReadOnly(Path.Combine(root, "config"), SecretStore.ForProviders());

    private static void RequireName(string? name, string what)
    {
        if (!SyncProjectKind.IsName(name))
            throw new SyncDesignerRefusal($"The {what} '{name}' is not a name of the draft: letters, digits, '.', '_' and '-', starting with a letter or a digit.", 400);
    }

    /// <summary>
    /// What a connection's driver can do: its readers, staging providers and writers, with their settings and the key each needs. A built-in
    /// driver answers from <see cref="BuiltInDriverCapabilities"/>; a driver the draft carries as <c>drivers/&lt;id&gt;/driver.yaml</c> answers what its
    /// descriptor declares. Any other driver (a compiled one, or one installed where the set runs) is not known offline and is refused with the reason.
    /// </summary>
    public static JsonNode CapabilitiesAnswer(string root, string? connection)
    {
        RequireName(connection, "connection");
        ConnectionConfig config;
        try { config = Repository(root).LoadConnection(connection!); }
        catch (FileNotFoundException ex) { throw new SyncDesignerRefusal($"The draft has no connection '{connection}' (config/connections/{connection}.yaml).", 404, ex); }
        catch (Exception ex) when (ex is ConfigValidationException or InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            throw new SyncDesignerRefusal($"config/connections/{connection}.yaml does not read: {ex.Message}", 400, ex);
        }

        var capabilities = BuiltInDriverCapabilities.For(config.DriverType) ?? FromDescriptor(root, config.DriverType);
        return JsonSerializer.SerializeToNode(capabilities, Json)!;
    }

    /// <summary>The capabilities a draft's <c>driver.yaml</c> declares, as the engine would register them, built with no library: a driver of the
    /// generic Kinds needs its provider only to open a connection.</summary>
    private static DriverCapabilities FromDescriptor(string root, string driverType)
    {
        var folder = Path.Combine(root, "drivers");
        var unknown = $"The driver '{driverType}' is not one of Sync's built-in drivers and the draft has no drivers/<folder>/driver.yaml with that id (a compiled driver, or one installed where the set runs), so what it can do is not known offline.";
        if (!Directory.Exists(folder)) throw new SyncDesignerRefusal(unknown, 404);
        foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(dir, "driver.yaml");
            if (!File.Exists(path)) continue;
            var file = Path.GetRelativePath(root, path).Replace('\\', '/');
            DriverDescriptorYaml descriptor;
            try { descriptor = DriverDescriptors.Read(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or YamlDotNet.Core.YamlException)
            {
                if (Path.GetFileName(dir) == driverType) throw new SyncDesignerRefusal($"{file} does not read: {ex.Message}", 400, ex);
                continue;
            }
            if (descriptor.Id != driverType) continue;
            if (descriptor.Base is not null)
                throw new SyncDesignerRefusal($"{file} builds the driver from the compiled type '{descriptor.Base}', so what it can do is not known offline.", 404);
            try
            {
                var registry = new DriverRegistry();
                registry.Register(new GenericDriver(DriverDescriptors.ToSpec(descriptor, NoProvider.Instance)));
                return registry.Describe(descriptor.Id)!;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                throw new SyncDesignerRefusal($"{file} does not describe a driver the engine can build: {ex.Message}", 400, ex);
            }
        }
        throw new SyncDesignerRefusal(unknown, 404);
    }

    /// <summary>A provider that is never asked for anything: describing a driver reads its Kinds, never its connections.</summary>
    private sealed class NoProvider : DbProviderFactory
    {
        public static readonly NoProvider Instance = new();
    }

    /// <summary>
    /// The type each source column of a mapping would get on the target if nobody set one, and the natural key the SCD Type 2 writer would derive,
    /// by the same code the provisioning plan and a run use (<see cref="MappingInference"/>), over the source's columns in the draft's cache. A
    /// column whose type is not known offline, and a table the cache does not hold, come back with the reason.
    /// </summary>
    public static async Task<JsonNode> InferredAnswerAsync(string root, string? replication, string? mapping, CancellationToken cancellationToken)
    {
        RequireName(replication, "replication");
        RequireName(mapping, "mapping");
        var repository = Repository(root);
        ReplicationTaskConfig task;
        TableMappingConfig config;
        try
        {
            task = repository.LoadReplicationTask(replication!);
            config = repository.LoadTableMapping(replication!, mapping!);
        }
        catch (FileNotFoundException ex) { throw new SyncDesignerRefusal(ex.Message, 404, ex); }
        catch (Exception ex) when (ex is ConfigValidationException or InvalidOperationException or YamlDotNet.Core.YamlException or MappingLoadingException)
        {
            throw new SyncDesignerRefusal($"The mapping does not read: {ex.Message}", 400, ex);
        }
        if (config.Sources.Count != 1 || config.Targets.Count != 1)
            throw new SyncDesignerRefusal($"Table mapping '{config.Name}' has {config.Sources.Count} source(s) and {config.Targets.Count} target(s): types and a key are inferred for a mapping of one source and one target.", 400);

        SourceTableRef source;
        TableRef target;
        try { (source, target) = (EndpointResolution.ResolveSource(task, config.Sources[0]), EndpointResolution.ResolveTarget(task, config.Targets[0])); }
        catch (ConfigValidationException ex) { throw new SyncDesignerRefusal(ex.Message, 400, ex); }

        var table = string.IsNullOrEmpty(source.Schema) ? source.Table : $"{source.Schema}.{source.Table}";
        var answer = new JsonObject
        {
            ["replication"] = replication,
            ["mapping"] = config.Name,
            ["source"] = new JsonObject { ["connection"] = source.ConnectionName, ["database"] = source.Database, ["table"] = table },
            ["target"] = new JsonObject { ["connection"] = target.ConnectionName, ["database"] = target.Database },
        };
        var columns = await SourceColumnsAsync(root, source, cancellationToken);
        if (columns.Problem is { } problem)
        {
            answer["problem"] = problem;
            answer["columns"] = new JsonArray();
            answer["naturalKey"] = null;
            return answer;
        }

        answer["problem"] = null;
        answer["columns"] = JsonSerializer.SerializeToNode(TargetTypes(root, source.ConnectionName, target.ConnectionName, columns.Columns!), Json);
        answer["naturalKey"] = JsonSerializer.SerializeToNode(MappingInference.NaturalKey(columns.Columns!, config, table), Json);
        return answer;
    }

    /// <summary>The target types of <paramref name="columns"/>: the source's driver reads each native type and the target's writes it, as the engine's
    /// do. A driver whose types are not known offline gives each column that reason.</summary>
    private static IReadOnlyList<InferredColumnType> TargetTypes(string root, string sourceConnection, string targetConnection, IReadOnlyList<ColumnMetadata> columns)
    {
        var from = SyncOfflineCheck.TypesOf(root, sourceConnection);
        var to = SyncOfflineCheck.TypesOf(root, targetConnection);
        if (from.ReadType is { } read && to.Dialect is { } write) return MappingInference.TargetTypes(read, write, columns);
        var why = from.ReadType is null
            ? $"The source connection '{sourceConnection}': {from.Unknown}"
            : $"The target connection '{targetConnection}': {to.Unknown}";
        return [.. columns.Select(c => new InferredColumnType(c.Name, c.NativeType, null, null, why))];
    }

    private static async Task<(IReadOnlyList<ColumnMetadata>? Columns, string? Problem)> SourceColumnsAsync(string root, TableRef source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Table)) return (null, "This mapping reads a query, which has no table in the catalog cache to infer from.");
        if (SyncCatalogColumns.Read(root) is not { } cache)
            return (null, $"The draft has no {CatalogCache.FileName} that reads: refresh the catalog of the mapping's connections.");
        try
        {
            var columns = await cache.ListColumnsAsync(source.ConnectionName, source.Database, source.Schema, source.Table, cancellationToken);
            return columns.Count == 0
                ? (null, $"The source table {source.Schema}.{source.Table} is not in {CatalogCache.FileName}: refresh the catalog of '{source.ConnectionName}'.")
                : (columns, null);
        }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
    }

    /// <summary>One table Create mappings is asked for.</summary>
    public sealed record TableToMap(string Schema, string Table);

    /// <summary>
    /// The mapping files Create mappings makes for the ticked tables of a replication's source, one per table named <c>schema.table</c>, written as
    /// Sync writes a mapping (<see cref="ConfigRepository.TableMappingText"/>) and checked against the replication as a save is. Each states only its
    /// tables (the target mirrors the source's schema and table, everything else follows the replication), the columns of both sides from the cache,
    /// and the columns of the same name paired; a target the cache does not hold is one provisioning creates, so every source column is mapped.
    /// No target type and no key is written: they are inferred when the target is provisioned and when a pass runs, and the answer carries what
    /// would be inferred for each mapping. A table that already has a mapping of its name is skipped. Nothing is written into the draft.
    /// </summary>
    public static async Task<JsonNode> BulkCreateAnswerAsync(string root, string? replication, IReadOnlyList<TableToMap> tables, CancellationToken cancellationToken)
    {
        RequireName(replication, "replication");
        if (tables.Count is 0 or > MaxTables) throw new SyncDesignerRefusal($"Name from 1 to {MaxTables} tables.", 400);
        var repository = Repository(root);
        ReplicationTaskConfig task;
        try { task = repository.LoadReplicationTask(replication!); }
        catch (FileNotFoundException ex) { throw new SyncDesignerRefusal($"The draft has no replication '{replication}' (config/replications/{replication}/task.yaml).", 404, ex); }
        catch (Exception ex) when (ex is ConfigValidationException or InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            throw new SyncDesignerRefusal($"config/replications/{replication}/task.yaml does not read: {ex.Message}", 400, ex);
        }

        var existing = repository.ListTableMappings(replication!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IColumnCatalog catalog = SyncCatalogColumns.Read(root) is { } cache ? cache : new NoCache();
        var reader = new MappingColumnReader(catalog);
        var (files, created, skipped, notes) = (new JsonArray(), new JsonArray(), new JsonArray(), new JsonArray());
        var inferred = new JsonObject();
        foreach (var table in tables)
        {
            var name = string.IsNullOrEmpty(table.Schema) ? table.Table : $"{table.Schema}.{table.Table}";
            if (!existing.Add(name)) { skipped.Add(name); continue; }
            var mapping = new TableMappingConfig
            {
                Name = name,
                Sources = [new SourceTableSpec { Schema = table.Schema, Table = table.Table }],
                Targets = [new TableSpec { Schema = table.Schema, Table = table.Table }],
            };
            SourceTableRef source;
            TableRef target;
            try { (source, target) = (EndpointResolution.ResolveSource(task, mapping.Sources[0]), EndpointResolution.ResolveTarget(task, mapping.Targets[0])); }
            catch (ConfigValidationException ex) { throw new SyncDesignerRefusal($"Creating '{name}' failed: {ex.Message}", 400, ex); }

            var sourceRead = await reader.ReadAsync(source, cancellationToken);
            var targetRead = await reader.ReadAsync(target, cancellationToken);
            if (sourceRead.Shape is not null) mapping.SourceColumns = sourceRead.Shape;
            if (targetRead.Shape is not null) mapping.TargetColumns = targetRead.Shape;
            mapping.ColumnMappings = ColumnAutoMap.Between(sourceRead.Shape, targetRead.Shape);

            string text;
            try { text = repository.TableMappingText(replication!, mapping); }
            catch (ConfigValidationException ex) { throw new SyncDesignerRefusal($"Creating '{name}' failed: {ex.Message}", 400, ex); }
            files.Add(new JsonObject { ["path"] = $"config/replications/{replication}/table-mappings/{name}.yaml", ["text"] = text });
            created.Add(name);
            Note(notes, name, "source", sourceRead, "the source table is not in the catalog cache, so nothing was captured and no columns were mapped");
            Note(notes, name, "target", targetRead, "the target table is not in the catalog cache: it is created with these columns when the target is provisioned");

            if (sourceRead.Shape is { } shape)
            {
                var columns = shape.Select(c => new ColumnMetadata(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity, c.KeyOrdinal)).ToList();
                inferred[name] = new JsonObject
                {
                    ["columns"] = JsonSerializer.SerializeToNode(TargetTypes(root, source.ConnectionName, target.ConnectionName, columns), Json),
                    ["naturalKey"] = JsonSerializer.SerializeToNode(MappingInference.NaturalKey(columns, mapping, name), Json),
                };
            }
        }
        return new JsonObject { ["files"] = files, ["created"] = created, ["skipped"] = skipped, ["notes"] = notes, ["inferred"] = inferred };
    }

    /// <summary>Nothing for a side that read cleanly, so the list holds only the exceptions.</summary>
    private static void Note(JsonArray notes, string mapping, string side, SideRead read, string missing)
    {
        var reason = read.Unavailable ?? (read.IsMissingTable ? missing : null);
        if (reason is not null) notes.Add(new JsonObject { ["mapping"] = mapping, ["side"] = side, ["reason"] = reason });
    }

    /// <summary>The catalog of a draft with no cache that reads: every side is a stated reason, never a failure.</summary>
    private sealed class NoCache : IColumnCatalog
    {
        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(string connectionName, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"The draft has no {CatalogCache.FileName} that reads: refresh the catalog of the replication's connections.");
    }

    /// <summary>
    /// The draft as the person sees it: the files of the draft with the person's unsaved edits over them. With no edits it is the draft itself, read
    /// in place; with edits it is a copy of the files the answers read (<c>config/</c>, <c>drivers/*/driver.yaml</c> and <c>catalog-cache.yaml</c>) in a
    /// folder of its own, deleted when the view is disposed. An edit names one of those files by its path in the draft and holds its text, or null
    /// for a file the person deleted.
    /// </summary>
    public sealed class DraftView : IDisposable
    {
        /// <summary>At most this many unsaved files, and this many bytes of them, in one question.</summary>
        public const int MaxFiles = 500;
        public const int MaxBytes = 16 * 1024 * 1024;

        private readonly string? copy;

        public string Root { get; }

        private DraftView(string root, string? copy) => (Root, this.copy) = (root, copy);

        /// <summary>Whether <paramref name="path"/> is a file an answer reads and so one an unsaved edit may replace.</summary>
        public static bool IsReadPath(string path) =>
            path.Length <= 512 && !path.Contains('\\') && !path.Contains("..", StringComparison.Ordinal) && !path.StartsWith('/') && !path.Contains(':')
            && path.Split('/').All(part => part.Length > 0 && part != ".")
            && (path == CatalogCache.FileName
                || (path.StartsWith("config/", StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal))
                || (path.StartsWith("drivers/", StringComparison.Ordinal) && path.Split('/') is [_, _, "driver.yaml"]));

        /// <summary>The view of the draft at <paramref name="draftRoot"/> with <paramref name="unsaved"/> over it. A path that is not one an answer
        /// reads, or more than <see cref="MaxFiles"/> files or <see cref="MaxBytes"/> bytes, is refused.</summary>
        public static DraftView Open(string draftRoot, IReadOnlyDictionary<string, string?> unsaved)
        {
            if (unsaved.Count == 0) return new DraftView(draftRoot, null);
            if (unsaved.Count > MaxFiles || unsaved.Values.Sum(t => (long)Encoding.UTF8.GetByteCount(t ?? "")) > MaxBytes)
                throw new SyncDesignerRefusal($"The unsaved edits sent are more than {MaxFiles} files or {MaxBytes / (1024 * 1024)} MiB.", 400);
            if (unsaved.Keys.FirstOrDefault(p => !IsReadPath(p)) is { } bad)
                throw new SyncDesignerRefusal($"'{bad}' is not a file the answer reads: an unsaved edit is of catalog-cache.yaml, a file under config/ or a drivers/<folder>/driver.yaml.", 400);

            var copy = Path.Combine(Path.GetTempPath(), "ddw-sync-view-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(copy);
                var config = Path.Combine(draftRoot, "config");
                if (Directory.Exists(config))
                    foreach (var file in Directory.EnumerateFiles(config, "*.yaml", SearchOption.AllDirectories))
                        CopyInto(draftRoot, copy, file);
                var drivers = Path.Combine(draftRoot, "drivers");
                if (Directory.Exists(drivers))
                    foreach (var dir in Directory.EnumerateDirectories(drivers))
                        if (File.Exists(Path.Combine(dir, "driver.yaml"))) CopyInto(draftRoot, copy, Path.Combine(dir, "driver.yaml"));
                if (File.Exists(Path.Combine(draftRoot, CatalogCache.FileName))) CopyInto(draftRoot, copy, Path.Combine(draftRoot, CatalogCache.FileName));
                foreach (var (path, text) in unsaved)
                {
                    var full = Path.Combine(copy, path);
                    if (text is null) { File.Delete(full); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, text);
                }
                return new DraftView(copy, copy);
            }
            catch
            {
                Delete(copy);
                throw;
            }
        }

        private static void CopyInto(string from, string to, string file)
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        private static void Delete(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            if (copy is not null) Delete(copy);
        }
    }
}
