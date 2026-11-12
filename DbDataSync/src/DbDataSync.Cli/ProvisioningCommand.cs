using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Catalog;
using DbDataKit.Cli;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync provisioning plan &lt;replication&gt; --from-cache</c> (plan 33 SC22j): the plan of what a replication needs of its two databases, made offline. The state of the target comes from the catalog cache of the project
/// (<c>catalog-cache.yaml</c> at the root of the repository, format <c>catalog-cache/1</c>, which has the tables and columns of each connection and no index, foreign key or row) in place of the live catalog the server's planner reads, and the source's columns are the mapping's
/// own column cache. The statements are the product's own: <see cref="CreateTargetTablePlanner"/> and <see cref="AlterTargetTablePlanner"/>, which every driver's <see cref="IProvisioner"/> calls with its dialect, so the DDL shown is the DDL
/// the server would apply. It never opens a connection and writes nothing; <b>apply</b> stays the server's.
/// <para>
/// What it cannot say from a cache, it says: a reader that needs the source's cooperation (change tracking, a trigger, a logical slot) depends on settings of the live server, so that side is <c>unknown</c> with the reason; a connection or table the cache does not
/// hold is <c>unknown</c> (no entry for the connection) or planned as a create (the connection is cached and the table is not there).
/// </para>
/// </summary>
public static class ProvisioningCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public sealed record Step(string Title, string CommandText, string? Rationale, string Scope);

    public sealed record SidePlan(string Action, string State, IReadOnlyList<Step> Steps, IReadOnlyList<string> Warnings);

    public sealed record Endpoint(string Connection, string Table);

    public sealed record MappingPlan(string Mapping, Endpoint? Source, Endpoint? Target, SidePlan? SourcePlan, SidePlan? TargetPlan, string? Excluded);

    public sealed record PlanReport(string Replication, string? CacheGeneratedAt, string? CacheServer, IReadOnlyList<MappingPlan> Mappings);

    /// <summary>The <c>data</c> schema of the verb (<see cref="DataSchemas"/>), and the whole document <c>--json</c> prints.</summary>
    public static readonly string PlanSchema = DataSchemas.Object(
    [
        ("replication", DataSchemas.String),
        ("cache_generated_at", Nullable(DataSchemas.String)),
        ("cache_server", Nullable(DataSchemas.String)),
        ("mappings", DataSchemas.ListOf(DataSchemas.Object(
        [
            ("mapping", DataSchemas.String), ("source", Nullable(EndpointSchema)), ("target", Nullable(EndpointSchema)),
            ("source_plan", Nullable(SideSchema)), ("target_plan", Nullable(SideSchema)), ("excluded", Nullable(DataSchemas.String)),
        ], "mapping", "source", "target", "source_plan", "target_plan", "excluded"))),
    ], "replication", "cache_generated_at", "cache_server", "mappings");

    private static string EndpointSchema => DataSchemas.Object([("connection", DataSchemas.String), ("table", DataSchemas.String)], "connection", "table");

    private static string SideSchema => DataSchemas.Object(
    [
        ("action", DataSchemas.String), ("state", DataSchemas.String), ("warnings", DataSchemas.StringList),
        ("steps", DataSchemas.ListOf(DataSchemas.Object([("title", DataSchemas.String), ("command_text", DataSchemas.String), ("rationale", Nullable(DataSchemas.String)), ("scope", DataSchemas.String)], "title", "command_text", "rationale", "scope"))),
    ], "action", "state", "warnings", "steps");

    private static string Nullable(string schema) => $$"""{ "anyOf": [{{schema}}, { "type": "null" }] }""";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, null);

    public static Task<int> RunAsync(string[] args, IDictionary<string, object?>? data)
    {
        if (args.Length == 0 || !string.Equals(args[0], "plan", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Usage());
        var rest = args[1..];
        if (rest.Length == 0 || rest[0].StartsWith("--", StringComparison.Ordinal)) return Task.FromResult(Usage());
        if (!CliOptions.Has(rest, "--from-cache"))
        {
            Console.Error.WriteLine("Planning from a live catalog is the server's: use the Provisioning card of the replication. This command plans offline, from the catalog cache of the repository: add --from-cache.");
            return Task.FromResult(2);
        }
        var repository = ScriptCommand.OpenRepository(rest);
        var repoRoot = DbDataSyncRoot.Resolve(rest);
        var name = rest[0];
        var path = Path.Combine(repoRoot, CatalogCache.FileName);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"{CatalogCache.FileName} is not in {repoRoot}: refresh the catalog cache of the project first (the Catalog tab of the workspace).");
            return Task.FromResult(1);
        }
        var load = CatalogCache.Load(File.ReadAllText(path), CatalogCache.FileName);
        if (!load.Ok)
        {
            foreach (var d in load.Diagnostics) Console.Error.WriteLine($"{CatalogCache.FileName}:{d.Line}: {d.Code}: {d.Message}");
            return Task.FromResult(1);
        }
        ReplicationTaskConfig task;
        try { task = repository.LoadReplicationTask(name); }
        catch (FileNotFoundException) { Console.Error.WriteLine($"The replication '{name}' does not exist in {repoRoot}."); return Task.FromResult(1); }

        var report = Plan(repository, task, load.Cache!);
        if (data is not null) { data["replication"] = report.Replication; data["cache_generated_at"] = report.CacheGeneratedAt; data["cache_server"] = report.CacheServer; data["mappings"] = report.Mappings; }
        if (CliOptions.Has(rest, "--json")) Console.WriteLine(JsonSerializer.Serialize(report, Json));
        else WriteText(report);
        return Task.FromResult(0);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: dbdatasync provisioning plan <replication> --from-cache [--repo <path>] [--json]");
        return 2;
    }

    /// <summary>The plan of every mapping of the replication, offline.</summary>
    public static PlanReport Plan(ConfigRepository repository, ReplicationTaskConfig task, CatalogCacheFile cache)
    {
        var mappings = new List<MappingPlan>();
        foreach (var mappingName in repository.ListTableMappings(task.Name).Order(StringComparer.Ordinal))
        {
            TableMappingConfig mapping;
            try { mapping = repository.LoadTableMapping(task.Name, mappingName); }
            catch (Exception ex) when (ex is ConfigValidationException or FileNotFoundException) { mappings.Add(new(mappingName, null, null, null, null, ex.Message)); continue; }
            mappings.Add(PlanMapping(repository, task, mapping, cache));
        }
        return new(task.Name, cache.GeneratedAt, cache.Server, mappings);
    }

    private static MappingPlan PlanMapping(ConfigRepository repository, ReplicationTaskConfig task, TableMappingConfig mapping, CatalogCacheFile cache)
    {
        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
            return new(mapping.Name, null, null, null, null, $"The mapping has {mapping.Sources.Count} source(s) and {mapping.Targets.Count} target(s): provisioning only plans 1:1 mappings.");
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
        var from = new Endpoint(source.ConnectionName, $"{source.Schema}.{source.Table}");
        var to = new Endpoint(target.ConnectionName, $"{target.Schema}.{target.Table}");
        try
        {
            var sourceDialect = DialectOf(repository, source.ConnectionName, out var sourceNote);
            var targetDialect = DialectOf(repository, target.ConnectionName, out var targetNote);
            var sourcePlan = SourceSide(task, mapping);
            if (targetDialect is null || sourceDialect is null)
                return new(mapping.Name, from, to, sourcePlan, Unknown(ProvisioningActions.CreateTargetTable, sourceNote ?? targetNote ?? "A driver of this mapping names no SQL dialect."), null);
            return new(mapping.Name, from, to, sourcePlan, TargetSide(task, mapping, target, sourceDialect, targetDialect, cache), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ConfigValidationException or NotSupportedException or FileNotFoundException)
        {
            return new(mapping.Name, from, to, null, null, ex.Message);
        }
    }

    private static SqlDialect? DialectOf(ConfigRepository repository, string connectionName, out string? note)
    {
        note = null;
        var connection = repository.LoadConnection(connectionName);
        var driver = BuiltInDrivers.All.FirstOrDefault(d => string.Equals(d.DriverType, connection.DriverType, StringComparison.OrdinalIgnoreCase));
        if (driver is IDialectProvider provider) return provider.Dialect;
        note = $"The '{connection.DriverType}' driver of connection '{connectionName}' is not a built-in driver this command knows the dialect of (a descriptor or plugin driver is planned by the server).";
        return null;
    }

    /// <summary>The source's side: a reader that scans (<c>Watermark</c>, <c>BatchReload</c>) needs nothing of the source, as every driver's planner answers; any other needs the live server's settings.</summary>
    private static SidePlan SourceSide(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        var reader = PipelineResolution.Reader(task, mapping);
        if (reader.Kind is GenericDriverKinds.Watermark or GenericDriverKinds.BatchReload)
            return new(ProvisioningActions.EnableSourceChangeCapture, "satisfied", [], []);
        return Unknown(ProvisioningActions.EnableSourceChangeCapture, $"The reader '{reader.Kind}' needs the source's own settings (change tracking, a trigger, wal_level), which a catalog cache does not hold: plan it on the Sync server.");
    }

    private static SidePlan TargetSide(ReplicationTaskConfig task, TableMappingConfig mapping, TableRef target, SqlDialect sourceDialect, SqlDialect targetDialect, CatalogCacheFile cache)
    {
        if (!cache.Connections.TryGetValue(target.ConnectionName, out var cached))
            return Unknown(ProvisioningActions.CreateTargetTable, $"The catalog cache has no entry for the connection '{target.ConnectionName}', so it cannot say whether the target table exists: refresh the cache of that connection.");
        var sourceColumns = mapping.SourceColumns.Select(c => new ColumnMetadata(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity) with { KeyOrdinal = c.KeyOrdinal }).ToList();
        if (sourceColumns.Count == 0)
            return Unknown(ProvisioningActions.CreateTargetTable, "The mapping has no cache of its source columns (sourceColumns), so the columns to create are not known: refresh the mapping's columns first.");
        var (columns, warnings) = ProvisioningColumnBuilder.Build(sourceDialect, sourceColumns.WithKey(mapping.PrimaryKey), mapping.ColumnMappings);
        var provisioned = HistorizedProvisioning.Extend(columns, PipelineResolution.Writer(task, mapping).Kind);

        var table = cached.Tables.FirstOrDefault(t => string.Equals(t.Schema, target.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, target.Table, StringComparison.OrdinalIgnoreCase));
        ProvisioningPlan plan;
        if (table is null) plan = CreateTargetTablePlanner.Plan(targetDialect, target, provisioned);
        else
        {
            var existing = table.Columns.Select(c => new ColumnMetadata(c.Name, c.Type, c.Nullable, c.Key, false)).ToList();
            plan = AlterTargetTablePlanner.Plan(targetDialect, target, provisioned, existing);
        }
        return Describe(plan with { Warnings = [.. warnings, .. plan.Warnings, .. table is null ? [] : (IEnumerable<string>)["The catalog cache holds columns, types and nullability only: no index, foreign key or default is compared."]] });
    }

    private static SidePlan Describe(ProvisioningPlan plan) =>
        new(plan.Action, plan.State.ToString().ToLowerInvariant(), [.. plan.Steps.Select(s => new Step(s.Title, s.CommandText, s.Rationale, s.Scope.ToString().ToLowerInvariant()))], plan.Warnings);

    private static SidePlan Unknown(string action, string why) => new(action, "unknown", [], [why]);

    private static void WriteText(PlanReport report)
    {
        Console.WriteLine($"Provisioning plan of '{report.Replication}' from the catalog cache (made {report.CacheGeneratedAt}, by {report.CacheServer}); nothing was connected to.");
        foreach (var m in report.Mappings)
        {
            Console.WriteLine($"{m.Mapping}: {m.Source?.Connection}.{m.Source?.Table} -> {m.Target?.Connection}.{m.Target?.Table}");
            if (m.Excluded is not null) { Console.WriteLine($"  not planned: {m.Excluded}"); continue; }
            foreach (var side in new[] { ("source", m.SourcePlan), ("target", m.TargetPlan) })
            {
                if (side.Item2 is null) continue;
                Console.WriteLine($"  {side.Item1}: {side.Item2.Action} {side.Item2.State}");
                foreach (var step in side.Item2.Steps) Console.WriteLine($"    {step.Title}\n      {step.CommandText.ReplaceLineEndings(" ")}");
                foreach (var warning in side.Item2.Warnings) Console.WriteLine($"    note: {warning}");
            }
        }
    }
}
