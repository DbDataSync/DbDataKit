using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Lineage;

/// <summary>The tables of <see cref="LineageStore"/>, a fragment a product includes in its <see cref="StateSchema"/>. Keys are the fixed-length hashes of the identities, which are long.</summary>
public static class LineageSchema
{
    public const string Tables =
        """
        -- The design lineage of one server: its nodes and edges, replaced whole when a definition changes, and the version (a fingerprint) of what was last stored.
        CREATE TABLE LineageNodes (
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            NodeHash {{key}} NOT NULL,
            NodeId {{text}} NOT NULL,
            Kind {{key}} NOT NULL,
            Name {{text}} NOT NULL,
            UnknownUpstream {{int}} NOT NULL,
            PropertiesJson {{text}} NULL,
            PRIMARY KEY (Service, Owner, NodeHash)
        );
        CREATE TABLE LineageEdges (
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            EdgeHash {{key}} NOT NULL,
            FromId {{text}} NOT NULL,
            ToId {{text}} NOT NULL,
            Kind {{key}} NOT NULL,
            Granularity {{key}} NOT NULL,
            Confidence {{key}} NOT NULL,
            Detail {{text}} NULL,
            PRIMARY KEY (Service, Owner, EdgeHash)
        );
        CREATE TABLE LineageVersions (
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            Version {{key}} NOT NULL,
            UpdatedUtc {{text}} NOT NULL,
            PRIMARY KEY (Service, Owner)
        );
        -- The run lineage: what flowed along an edge, when and how it ended. Kept by the server's retention for run detail, and dropped with its edge.
        CREATE TABLE LineageRuns (
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            EdgeHash {{key}} NOT NULL,
            AtUtc {{key}} NOT NULL,
            Outcome {{key}} NOT NULL,
            Rows {{int}} NULL,
            PRIMARY KEY (Service, Owner, EdgeHash, AtUtc)
        );
        """;
}

/// <summary>
/// A server's lineage (plan 18, G01), on the <c>State</c> dialects: the design graph of each <em>owner</em> (a server keeps one under its own name; the console keeps one per server it pulls from) and the runs along its edges. Replacing a design graph
/// is one transaction; storing one that has not changed writes nothing, and <see cref="Version"/> changes exactly when a graph does, so a console knows whether to pull.
/// </summary>
public sealed class LineageStore(StateDatabase database, string service)
{
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
            foreach (var table in new[] { "LineageNodes", "LineageEdges", "LineageVersions" })
            {
                using var delete = database.Command(connection, transaction, $"DELETE FROM {table} WHERE Service = $service AND Owner = $owner;");
                Bind(delete, owner);
                delete.ExecuteNonQuery();
            }
            foreach (var node in graph.Nodes)
            {
                using var insert = database.Command(connection, transaction,
                    "INSERT INTO LineageNodes (Service, Owner, NodeHash, NodeId, Kind, Name, UnknownUpstream, PropertiesJson) VALUES ($service, $owner, $hash, $id, $kind, $name, $unknown, $properties);");
                Bind(insert, owner);
                insert.Bind(database, "hash", LineageEdge.Hash(node.Id));
                insert.Bind(database, "id", node.Id);
                insert.Bind(database, "kind", node.Kind.ToString());
                insert.Bind(database, "name", node.Name);
                insert.Bind(database, "unknown", node.UnknownUpstream ? 1L : 0L);
                insert.Bind(database, "properties", node.PropertyMap.Count == 0 ? null : JsonSerializer.Serialize(node.PropertyMap, Json));
                insert.ExecuteNonQuery();
            }
            foreach (var edge in graph.Edges)
            {
                using var insert = database.Command(connection, transaction,
                    "INSERT INTO LineageEdges (Service, Owner, EdgeHash, FromId, ToId, Kind, Granularity, Confidence, Detail) VALUES ($service, $owner, $hash, $from, $to, $kind, $granularity, $confidence, $detail);");
                Bind(insert, owner);
                insert.Bind(database, "hash", edge.KeyHash);
                insert.Bind(database, "from", edge.From);
                insert.Bind(database, "to", edge.To);
                insert.Bind(database, "kind", edge.Kind.ToString());
                insert.Bind(database, "granularity", edge.Granularity.ToString());
                insert.Bind(database, "confidence", edge.Confidence.ToString());
                insert.Bind(database, "detail", edge.Detail);
                insert.ExecuteNonQuery();
            }
            // the runs of an edge that is gone are gone with it
            using (var prune = database.Command(connection, transaction,
                "DELETE FROM LineageRuns WHERE Service = $service AND Owner = $owner AND EdgeHash NOT IN (SELECT EdgeHash FROM LineageEdges WHERE Service = $service AND Owner = $owner);"))
            {
                Bind(prune, owner);
                prune.ExecuteNonQuery();
            }
            using (var stamp = database.Command(connection, transaction, "INSERT INTO LineageVersions (Service, Owner, Version, UpdatedUtc) VALUES ($service, $owner, $version, $at);"))
            {
                Bind(stamp, owner);
                stamp.Bind(database, "version", version);
                stamp.Bind(database, "at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
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
            using var cmd = database.Command(connection, "SELECT Version FROM LineageVersions WHERE Service = $service AND Owner = $owner;");
            Bind(cmd, owner);
            return cmd.ExecuteScalar() as string;
        });

    /// <summary>The owner's design graph; an empty one when nothing is stored.</summary>
    public LineageGraph Load(string owner) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var nodes = new List<LineageNode>();
            using (var cmd = database.Command(connection, "SELECT NodeId, Kind, Name, UnknownUpstream, PropertiesJson FROM LineageNodes WHERE Service = $service AND Owner = $owner;"))
            {
                Bind(cmd, owner);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    nodes.Add(new LineageNode(reader.GetString(0), Enum.Parse<NodeKind>(reader.GetString(1)), reader.GetString(2), Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture) != 0,
                        reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), Json)));
            }
            var edges = new List<LineageEdge>();
            using (var cmd = database.Command(connection, "SELECT FromId, ToId, Kind, Granularity, Confidence, Detail FROM LineageEdges WHERE Service = $service AND Owner = $owner;"))
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
            using (var exists = database.Command(connection, "SELECT COUNT(*) FROM LineageEdges WHERE Service = $service AND Owner = $owner AND EdgeHash = $hash;"))
            {
                Bind(exists, owner);
                exists.Bind(database, "hash", hash);
                if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    throw new InvalidOperationException($"The design graph of '{owner}' has no {run.Kind} edge from '{run.From}' to '{run.To}', so a run along it is not recorded.");
            }
            // a second run in the same instant replaces the first
            using (var delete = database.Command(connection, "DELETE FROM LineageRuns WHERE Service = $service AND Owner = $owner AND EdgeHash = $hash AND AtUtc = $at;"))
            {
                Bind(delete, owner);
                delete.Bind(database, "hash", hash);
                delete.Bind(database, "at", Stamp(run.At));
                delete.ExecuteNonQuery();
            }
            using var insert = database.Command(connection, "INSERT INTO LineageRuns (Service, Owner, EdgeHash, AtUtc, Outcome, Rows) VALUES ($service, $owner, $hash, $at, $outcome, $rows);");
            Bind(insert, owner);
            insert.Bind(database, "hash", hash);
            insert.Bind(database, "at", Stamp(run.At));
            insert.Bind(database, "outcome", run.Outcome.ToString());
            insert.Bind(database, "rows", run.Rows);
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
            using var cmd = database.Command(connection, "SELECT EdgeHash, AtUtc, Outcome, Rows FROM LineageRuns WHERE Service = $service AND Owner = $owner ORDER BY EdgeHash, AtUtc;");
            Bind(cmd, owner);
            using var reader = cmd.ExecuteReader();
            var latest = new Dictionary<string, EdgeRun>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (!edges.TryGetValue(reader.GetString(0), out var edge)) continue;
                latest[edge.Key] = new EdgeRun(edge.From, edge.To, edge.Kind, DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), Enum.Parse<RunOutcome>(reader.GetString(2)),
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
            using var cmd = database.Command(connection, "DELETE FROM LineageRuns WHERE Service = $service AND Owner = $owner AND AtUtc < $before;");
            Bind(cmd, owner);
            cmd.Bind(database, "before", Stamp(before));
            return cmd.ExecuteNonQuery();
        });

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private void Bind(DbCommand cmd, string owner)
    {
        cmd.Bind(database, "service", service);
        cmd.Bind(database, "owner", owner);
    }
}
