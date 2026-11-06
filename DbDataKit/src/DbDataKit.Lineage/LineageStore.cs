using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Lineage;

/// <summary>
/// The <c>lineage</c> module (plan 29 section 3.4, plan 18): the design graph of each owner and the runs along its edges. One copy per install; <c>service</c> and <c>owner</c> are in every
/// key, so a console that keeps one graph per server it pulls from and a server that keeps its own share the tables. Keys are the fixed-length hashes of the identities, which are long.
/// </summary>
public static class LineageModule
{
    public const string Name = "lineage";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- The design lineage of one server: its nodes and edges, replaced whole when a definition changes, and the version (a fingerprint) of what was last stored.
            CREATE TABLE {table:node} (
                service          {{key}} NOT NULL,
                owner            {{key}} NOT NULL,
                node_hash        {{key}} NOT NULL,
                node_id          {{text}} NOT NULL,
                kind             {{key}} NOT NULL,
                name             {{text}} NOT NULL,
                unknown_upstream {{bool}} NOT NULL,
                properties       {{json}} NULL,
                PRIMARY KEY (service, owner, node_hash)
            );

            CREATE TABLE {table:edge} (
                service      {{key}} NOT NULL,
                owner        {{key}} NOT NULL,
                edge_hash    {{key}} NOT NULL,
                from_id      {{text}} NOT NULL,
                to_id        {{text}} NOT NULL,
                kind         {{key}} NOT NULL,
                granularity  {{key}} NOT NULL,
                confidence   {{key}} NOT NULL,
                detail       {{text}} NULL,
                PRIMARY KEY (service, owner, edge_hash)
            );

            CREATE TABLE {table:design_version} (
                service     {{key}} NOT NULL,
                owner       {{key}} NOT NULL,
                version     {{key}} NOT NULL,
                updated_at  {{time}} NOT NULL,
                PRIMARY KEY (service, owner)
            );

            -- The run lineage: what flowed along an edge, when and how it ended. Removed by retention, and with its edge when the design graph is replaced.
            CREATE TABLE {table:run} (
                service      {{key}} NOT NULL,
                owner        {{key}} NOT NULL,
                edge_hash    {{key}} NOT NULL,
                happened_at  {{time}} NOT NULL,
                outcome      {{key}} NOT NULL,
                row_count    {{int}} NULL,
                PRIMARY KEY (service, owner, edge_hash, happened_at)
            );
            """,
        ],
        retention: [new RetentionRule("run", "happened_at")]);
}

/// <summary>
/// A server's lineage (plan 18, G01), on the <c>State</c> dialects: the design graph of each <em>owner</em> (a server keeps one under its own name; the console keeps one per server it pulls from) and the runs along its edges. Replacing a design graph
/// is one transaction; storing one that has not changed writes nothing, and <see cref="Version"/> changes exactly when a graph does, so a console knows whether to pull.
/// </summary>
public sealed class LineageStore(StateDatabase database, string service)
{
    private const string Module = LineageModule.Name;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Makes the owner's design graph this one. False when it already was (nothing was written).</summary>
    public bool Replace(string owner, LineageGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var version = graph.Fingerprint();
        if (Version(owner) == version) return false;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "node", "edge", "design_version" })
            {
                using var delete = database.Command(Module, connection, transaction, $"DELETE FROM {{table:{table}}} WHERE service = $service AND owner = $owner;");
                Bind(delete, owner);
                delete.ExecuteNonQuery();
            }
            foreach (var node in graph.Nodes)
            {
                using var insert = database.Command(Module, connection, transaction,
                    "INSERT INTO {table:node} (service, owner, node_hash, node_id, kind, name, unknown_upstream, properties) VALUES ($service, $owner, $hash, $id, $kind, $name, $unknown, CAST($properties AS {{json}}));");
                Bind(insert, owner);
                insert.Bind(database, "hash", LineageEdge.Hash(node.Id));
                insert.Bind(database, "id", node.Id);
                insert.Bind(database, "kind", node.Kind.ToString());
                insert.Bind(database, "name", node.Name);
                insert.Bind(database, "unknown", node.UnknownUpstream);
                insert.Bind(database, "properties", node.PropertyMap.Count == 0 ? null : JsonSerializer.Serialize(node.PropertyMap, Json), System.Data.DbType.String);
                insert.ExecuteNonQuery();
            }
            foreach (var edge in graph.Edges)
            {
                using var insert = database.Command(Module, connection, transaction,
                    "INSERT INTO {table:edge} (service, owner, edge_hash, from_id, to_id, kind, granularity, confidence, detail) VALUES ($service, $owner, $hash, $from, $to, $kind, $granularity, $confidence, $detail);");
                Bind(insert, owner);
                insert.Bind(database, "hash", edge.KeyHash);
                insert.Bind(database, "from", edge.From);
                insert.Bind(database, "to", edge.To);
                insert.Bind(database, "kind", edge.Kind.ToString());
                insert.Bind(database, "granularity", edge.Granularity.ToString());
                insert.Bind(database, "confidence", edge.Confidence.ToString());
                insert.Bind(database, "detail", edge.Detail, System.Data.DbType.String);
                insert.ExecuteNonQuery();
            }
            // the runs of an edge that is gone are gone with it
            using (var prune = database.Command(Module, connection, transaction,
                "DELETE FROM {table:run} WHERE service = $service AND owner = $owner AND edge_hash NOT IN (SELECT edge_hash FROM {table:edge} WHERE service = $service AND owner = $owner);"))
            {
                Bind(prune, owner);
                prune.ExecuteNonQuery();
            }
            using (var stamp = database.Command(Module, connection, transaction, "INSERT INTO {table:design_version} (service, owner, version, updated_at) VALUES ($service, $owner, $version, $at);"))
            {
                Bind(stamp, owner);
                stamp.Bind(database, "version", version);
                stamp.Bind(database, "at", database.Time.GetUtcNow());
                stamp.ExecuteNonQuery();
            }
            transaction.Commit();
        });
        return true;
    }

    /// <summary>The fingerprint of the owner's stored design graph, or null when there is none.</summary>
    public string? Version(string owner) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT version FROM {table:design_version} WHERE service = $service AND owner = $owner;");
            Bind(cmd, owner);
            return cmd.ExecuteScalar() as string;
        });

    /// <summary>The owner's design graph; an empty one when nothing is stored.</summary>
    public LineageGraph Load(string owner) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var nodes = new List<LineageNode>();
            using (var cmd = database.Command(Module, connection, "SELECT node_id, kind, name, unknown_upstream, properties FROM {table:node} WHERE service = $service AND owner = $owner;"))
            {
                Bind(cmd, owner);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    nodes.Add(new LineageNode(reader.GetString(0), Enum.Parse<NodeKind>(reader.GetString(1)), reader.GetString(2), reader.Bool(3),
                        reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), Json)));
            }
            var edges = new List<LineageEdge>();
            using (var cmd = database.Command(Module, connection, "SELECT from_id, to_id, kind, granularity, confidence, detail FROM {table:edge} WHERE service = $service AND owner = $owner;"))
            {
                Bind(cmd, owner);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    edges.Add(new LineageEdge(reader.GetString(0), reader.GetString(1), Enum.Parse<EdgeKind>(reader.GetString(2)), Enum.Parse<Granularity>(reader.GetString(3)), Enum.Parse<Confidence>(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
            return new LineageGraph(nodes, edges);
        });

    /// <summary>Records what flowed along an edge of the owner's design graph. The edge must be in it: a run of something no definition says can flow is not lineage.</summary>
    public void AddRun(string owner, EdgeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var hash = LineageEdge.Hash(run.EdgeKey);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var exists = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:edge} WHERE service = $service AND owner = $owner AND edge_hash = $hash;"))
            {
                Bind(exists, owner);
                exists.Bind(database, "hash", hash);
                if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    throw new InvalidOperationException($"The design graph of '{owner}' has no {run.Kind} edge from '{run.From}' to '{run.To}', so a run along it is not recorded.");
            }
            // a second run in the same instant replaces the first
            using (var delete = database.Command(Module, connection, "DELETE FROM {table:run} WHERE service = $service AND owner = $owner AND edge_hash = $hash AND happened_at = $at;"))
            {
                Bind(delete, owner);
                delete.Bind(database, "hash", hash);
                delete.Bind(database, "at", run.At);
                delete.ExecuteNonQuery();
            }
            using var insert = database.Command(Module, connection, "INSERT INTO {table:run} (service, owner, edge_hash, happened_at, outcome, row_count) VALUES ($service, $owner, $hash, $at, $outcome, $rows);");
            Bind(insert, owner);
            insert.Bind(database, "hash", hash);
            insert.Bind(database, "at", run.At);
            insert.Bind(database, "outcome", run.Outcome.ToString());
            insert.Bind(database, "rows", run.Rows, System.Data.DbType.Int64);
            insert.ExecuteNonQuery();
        });
    }

    /// <summary>The newest run of each edge that has one, keyed by <see cref="LineageEdge.Key"/>.</summary>
    public IReadOnlyDictionary<string, EdgeRun> LatestRuns(string owner)
    {
        var edges = Load(owner).Edges.ToDictionary(e => e.KeyHash, e => e, StringComparer.Ordinal);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT edge_hash, happened_at, outcome, row_count FROM {table:run} WHERE service = $service AND owner = $owner ORDER BY edge_hash, happened_at;");
            Bind(cmd, owner);
            using var reader = cmd.ExecuteReader();
            var latest = new Dictionary<string, EdgeRun>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (!edges.TryGetValue(reader.GetString(0), out var edge)) continue;
                latest[edge.Key] = new EdgeRun(edge.From, edge.To, edge.Kind, reader.Time(1), Enum.Parse<RunOutcome>(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture));
            }
            return latest;
        });
    }

    /// <summary>Removes the runs older than <paramref name="before"/> (the server's retention for run detail). Returns how many.</summary>
    public int PruneRuns(string owner, DateTimeOffset before) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:run} WHERE service = $service AND owner = $owner AND happened_at < $before;");
            Bind(cmd, owner);
            cmd.Bind(database, "before", before);
            return cmd.ExecuteNonQuery();
        });

    private void Bind(DbCommand cmd, string owner)
    {
        cmd.Bind(database, "service", service);
        cmd.Bind(database, "owner", owner);
    }
}
