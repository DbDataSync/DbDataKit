using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Tracking;

/// <summary>One target connection of a replication that keeps its state there, and the engine of its state table.</summary>
public sealed record SyncPositionLogTarget(string Connection, string Driver, PositionLogEngine Engine);

/// <summary>
/// Which targets of a replication hold its <c>dbdatasync.position_log</c> table (plan 28c E17): the replication's own target connection and the target connection each mapping names (a mapping's target resolves to the replication's
/// unless it says its own), once each, for the engines whose writers carry the table. A replication that keeps its state in the runner's state only (<c>tracking</c> absent or <c>mode: state</c>) has none. Both followers
/// (the Sync server's and the runner's drain) copy the entries of every such target.
/// </summary>
public static class SyncPositionLogTargets
{
    public static IReadOnlyList<SyncPositionLogTarget> Of(ConfigRepository config, ReplicationTaskConfig task)
    {
        var names = new List<string>();
        if (task.Endpoints.Target?.ConnectionName is { Length: > 0 } own) names.Add(own);
        IReadOnlyList<string> mappings;
        try { mappings = config.ListTableMappings(task.Name); }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException) { mappings = []; }
        foreach (var name in mappings)
        {
            TableMappingConfig mapping;
            try { mapping = config.LoadTableMapping(task.Name, name); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException or YamlDotNet.Core.YamlException) { continue; }
            foreach (var target in mapping.Targets)
            {
                try { names.Add(EndpointResolution.ResolveTarget(task, target).ConnectionName); }
                catch (Exception ex) when (ex is ConfigValidationException or InvalidOperationException) { }
            }
        }

        var result = new List<SyncPositionLogTarget>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            string driver;
            try { driver = config.LoadConnection(name).DriverType; }
            catch (Exception ex) when (ex is FileNotFoundException or ConfigFileException or ConfigValidationException) { continue; }
            if (DbDataSync.Core.Config.TrackingResolution.Resolve(task.Tracking, driver).EffectiveMode == "state") continue;
            if (TargetPositionLog.EngineOf(driver) is { } engine) result.Add(new SyncPositionLogTarget(name, driver, engine));
        }

        return result;
    }
}
