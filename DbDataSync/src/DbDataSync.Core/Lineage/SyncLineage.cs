using DbDataKit.Connections;
using DbDataKit.Lineage;
using DbDataSync.Core.Config;

namespace DbDataSync.Core.Lineage;

/// <summary>One replication with its table mappings, as the configuration holds them.</summary>
public sealed record SyncReplication(ReplicationTaskConfig Task, IReadOnlyList<TableMappingConfig> Mappings);

/// <summary>
/// Sync's part of the suite's lineage graph (plan 18, G07), in the shared <c>DbDataKit.Lineage</c> types and the identity <see cref="LineageIdentity"/> gives a table, so a table Sync writes and the same table a Build model
/// reads are one node. Design lineage is what the configuration says can flow: for every table mapping, a <c>replicates</c> edge from each source table to each target table (declared), and for every column mapping a column edge
/// from the source column to the target column, with its transform as the detail. A source that is a hand-written query has no table: it is a node of its own whose upstream is unknown. Without column mappings the mapping
/// is table-level only (the columns are the source's, and the config does not list them).
/// </summary>
public static class SyncLineage
{
    /// <summary>The design lineage of the replications. <paramref name="connections"/> are the configured connections by name; a mapping that names none, or one that is not configured, is left out and said in <paramref name="notes"/>.</summary>
    public static LineageGraph Design(IReadOnlyList<SyncReplication> replications, IReadOnlyDictionary<string, ConnectionConfig> connections, List<string>? notes = null)
    {
        var nodes = new Dictionary<string, LineageNode>(StringComparer.Ordinal);
        var edges = new List<LineageEdge>();
        var definitions = new Dictionary<string, ConnectionDefinition>(StringComparer.Ordinal);

        ConnectionDefinition? Definition(string? connection, string? database)
        {
            if (connection is null || !connections.TryGetValue(connection, out var c)) return null;
            var key = connection + "\0" + database;
            if (definitions.TryGetValue(key, out var known)) return known;
            var (engine, dialect) = SyncConnectionFile.EngineFor(c.DriverType);
            return definitions[key] = new ConnectionDefinition
            {
                Name = c.Name, Engine = engine, Dialect = dialect, ConnectionString = c.ConnectionString,
                Address = string.IsNullOrWhiteSpace(c.Host) ? null : new AddressSpec { Host = c.Host, Port = c.Port, Database = database ?? c.Database },
            };
        }

        foreach (var replication in replications.OrderBy(r => r.Task.Name, StringComparer.Ordinal))
            foreach (var mapping in replication.Mappings.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var where = $"{replication.Task.Name}/{mapping.Name}";
                var sources = new List<(string Id, DatasetIdentity? Identity, ConnectionDefinition? Definition)>();
                foreach (var source in mapping.Sources)
                {
                    var connection = source.ConnectionName ?? replication.Task.Endpoints.Source?.ConnectionName;
                    var definition = Definition(connection, source.Database ?? replication.Task.Endpoints.Source?.Database);
                    if (!string.IsNullOrWhiteSpace(source.Query))
                    {
                        // a hand-written query reads tables the configuration does not list: a node of its own, whose upstream is unknown
                        var id = $"sync-query://{where}";
                        nodes[id] = new LineageNode(id, NodeKind.Dataset, $"query of {where}", UnknownUpstream: true, new Dictionary<string, string> { ["replication"] = replication.Task.Name, ["mapping"] = mapping.Name });
                        sources.Add((id, null, definition));
                    }
                    else if (definition is null) notes?.Add($"{where}: the source connection `{connection}` is not configured, so the mapping has no source in the graph.");
                    else
                    {
                        var identity = LineageIdentity.Dataset(definition, source.Schema, source.Table);
                        nodes.TryAdd(identity.ToString(), new LineageNode(identity.ToString(), NodeKind.Dataset, $"{source.Schema}.{source.Table}", UnknownUpstream: true, new Dictionary<string, string> { ["connection"] = connection! }));
                        sources.Add((identity.ToString(), identity, definition));
                    }
                }
                var targets = new List<(string Id, DatasetIdentity Identity, ConnectionDefinition Definition)>();
                foreach (var target in mapping.Targets)
                {
                    var connection = target.ConnectionName ?? replication.Task.Endpoints.Target?.ConnectionName;
                    if (Definition(connection, target.Database ?? replication.Task.Endpoints.Target?.Database) is not { } definition) { notes?.Add($"{where}: the target connection `{connection}` is not configured, so the mapping has no target in the graph."); continue; }
                    var identity = LineageIdentity.Dataset(definition, target.Schema, target.Table);
                    // what Sync writes is fed by the source: the upstream of the target is known
                    nodes[identity.ToString()] = new LineageNode(identity.ToString(), NodeKind.Dataset, $"{target.Schema}.{target.Table}", UnknownUpstream: false, new Dictionary<string, string> { ["connection"] = connection! });
                    targets.Add((identity.ToString(), identity, definition));
                }

                foreach (var (from, _, _) in sources)
                    foreach (var (to, _, _) in targets)
                        edges.Add(new LineageEdge(from, to, EdgeKind.Replicates, Granularity.Table, Confidence.Declared, where));

                // column mappings relate the first source to the first target (the mappings of a replication are table to table; several sources or targets name columns of the one they belong to)
                if (sources.Count == 1 && targets.Count == 1 && sources[0].Identity is { } sourceIdentity)
                    foreach (var column in mapping.ColumnMappings)
                    {
                        var fromId = LineageIdentity.Column(sources[0].Definition!, sourceIdentity, column.SourceColumn);
                        var toId = LineageIdentity.Column(targets[0].Definition, targets[0].Identity, column.TargetColumn);
                        nodes.TryAdd(fromId, new LineageNode(fromId, NodeKind.Column, column.SourceColumn, UnknownUpstream: true));
                        nodes[toId] = new LineageNode(toId, NodeKind.Column, column.TargetColumn, UnknownUpstream: false);
                        edges.Add(new LineageEdge(fromId, toId, EdgeKind.Replicates, Granularity.Column, Confidence.Declared, column.Transform));
                    }
            }
        return new LineageGraph(nodes.Values, edges.Distinct());
    }
}
