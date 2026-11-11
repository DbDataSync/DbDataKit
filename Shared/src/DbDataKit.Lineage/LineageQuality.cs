using System.Globalization;
using DbDataKit.State;

namespace DbDataKit.Lineage;

/// <summary>What a block stops: the node whose check blocks, the checks, and the nodes downstream of it (plan 23a FQ16, D7: dependents are stopped, the dataset's own writers are not).</summary>
public sealed record BlockImpact(LineageNode Blocked, IReadOnlyList<string> Checks, DateTimeOffset? Since, IReadOnlyList<ReachedNode> Stops);

/// <summary>One check's latest result as the graph needs it: <paramref name="Status"/> is <c>pass</c>, <c>fail</c> or <c>error</c>.</summary>
public sealed record NodeCheck(string Dataset, string CheckId, string Status, bool Blocking, DateTimeOffset? BlockedSince = null)
{
    public static NodeCheck From(CheckStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new NodeCheck(row.Dataset, row.CheckId, QualityStore.Name(row.Outcome), row.Blocking, row.BlockedSince);
    }
}

/// <summary>
/// Check results on the graph (plan 23a FQ16): <c>WithChecks</c> puts the status of a dataset's checks on its node as properties, and <see cref="Blocks"/> lists, for every blocked node, what its block stops, from the same impact walk
/// as <see cref="LineageQueries.Impact"/>. A check names its dataset by the lineage identity (D3), so a status finds its node by id; a status whose dataset has no node in the graph is left out (the graph does not know it).
/// </summary>
public static class LineageQuality
{
    /// <summary>The worst status of the node's checks: <c>blocked</c>, <c>fail</c>, <c>error</c> or <c>pass</c>.</summary>
    public const string StatusProperty = "check_status";

    /// <summary>How many checks the node has, and how many of them fail or could not run, and how many block now.</summary>
    public const string CountProperty = "check_count", FailingProperty = "check_failing", BlockingProperty = "check_blocking";

    /// <summary>The ids of the blocking checks (comma separated) and the oldest <c>blocked_since</c> among them (round-trip date-time).</summary>
    public const string BlockingIdsProperty = "check_blocking_ids", BlockedSinceProperty = "check_blocked_since";

    public static LineageGraph WithChecks(this LineageGraph graph, IEnumerable<CheckStatusRow> statuses) =>
        graph.WithChecks((statuses ?? throw new ArgumentNullException(nameof(statuses))).Select(NodeCheck.From));

    /// <summary>The same from the results as a console reads them from the servers, which send them as JSON.</summary>
    public static LineageGraph WithChecks(this LineageGraph graph, IEnumerable<NodeCheck> statuses)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(statuses);
        var byNode = statuses.GroupBy(s => s.Dataset, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(s => s.CheckId, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var nodes = graph.Nodes.Select(n =>
        {
            if (!byNode.TryGetValue(n.Id, out var rows)) return n;
            var blocking = rows.Where(r => r.Blocking).ToList();
            var failing = rows.Count(r => r.Status != "pass");
            var status = blocking.Count > 0 ? "blocked" : rows.Any(r => r.Status == "fail") ? "fail" : failing > 0 ? "error" : "pass";
            var properties = n.PropertyMap.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            properties[StatusProperty] = status;
            properties[CountProperty] = rows.Count.ToString(CultureInfo.InvariantCulture);
            properties[FailingProperty] = failing.ToString(CultureInfo.InvariantCulture);
            properties[BlockingProperty] = blocking.Count.ToString(CultureInfo.InvariantCulture);
            if (blocking.Count > 0)
            {
                properties[BlockingIdsProperty] = string.Join(',', blocking.Select(b => b.CheckId));
                var sinces = blocking.Where(b => b.BlockedSince is not null).Select(b => b.BlockedSince!.Value).ToList();
                if (sinces.Count > 0) properties[BlockedSinceProperty] = sinces.Min().ToString("O", CultureInfo.InvariantCulture);
            }
            return n with { Properties = properties };
        });
        return new LineageGraph(nodes, graph.Edges);
    }

    /// <summary>
    /// For every node with a blocking check (a graph that went through <c>WithChecks</c>), the nodes downstream of it that its block stops. A column is not a thing that runs or is published, so columns are not listed; everything else the impact
    /// walk reaches is (a model that is not run, a published model that is refused, a served dataset that keeps its last good version).
    /// </summary>
    public static IReadOnlyList<BlockImpact> Blocks(this LineageGraph graph, int maxDepth = LineageQueries.DefaultDepth)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var impacts = new List<BlockImpact>();
        foreach (var node in graph.Nodes.Where(n => n.PropertyMap.TryGetValue(BlockingProperty, out var count) && count != "0").OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var checks = node.PropertyMap.TryGetValue(BlockingIdsProperty, out var ids) ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
            DateTimeOffset? since = node.PropertyMap.TryGetValue(BlockedSinceProperty, out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            var stops = graph.Impact(node.Id, maxDepth).Reached.Where(r => r.Node.Kind != NodeKind.Column).ToList();
            impacts.Add(new BlockImpact(node, checks, since, stops));
        }
        return impacts;
    }
}
