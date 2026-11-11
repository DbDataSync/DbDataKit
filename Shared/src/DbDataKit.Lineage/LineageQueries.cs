namespace DbDataKit.Lineage;

/// <summary>A node reached by a query and how far from the start (1 is the start's neighbour).</summary>
public sealed record ReachedNode(LineageNode Node, int Depth);

/// <summary>
/// What a query found: the nodes and edges reached, <see cref="Gaps"/> (the nodes reached whose upstream is unknown, which is not the same as having none), and <see cref="Truncated"/> when the depth limit stopped it with more to follow.
/// </summary>
public sealed record LineageSlice(LineageNode Start, IReadOnlyList<ReachedNode> Reached, IReadOnlyList<LineageEdge> Edges, IReadOnlyList<LineageNode> Gaps, bool Truncated);

/// <summary>Impact (what a node feeds), provenance (what feeds a node) and the shortest path between two nodes.</summary>
public static class LineageQueries
{
    public const int DefaultDepth = 25;

    public static LineageSlice Impact(this LineageGraph graph, string nodeId, int maxDepth = DefaultDepth) => Walk(graph, nodeId, maxDepth, downstream: true);

    public static LineageSlice Provenance(this LineageGraph graph, string nodeId, int maxDepth = DefaultDepth) => Walk(graph, nodeId, maxDepth, downstream: false);

    private static LineageSlice Walk(LineageGraph graph, string nodeId, int maxDepth, bool downstream)
    {
        var start = graph.Node(nodeId) ?? throw new KeyNotFoundException($"The graph has no node '{nodeId}'.");
        if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth), "The depth is at least 1.");
        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [start.Id] = 0 };
        var edges = new Dictionary<string, LineageEdge>(StringComparer.Ordinal);
        var queue = new Queue<string>([start.Id]);
        var truncated = false;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var edge in downstream ? graph.Downstream(id) : graph.Upstream(id))
            {
                var next = downstream ? edge.To : edge.From;
                if (depth[id] >= maxDepth) { truncated = true; continue; }
                edges[edge.Key] = edge;
                if (depth.ContainsKey(next)) continue;
                depth[next] = depth[id] + 1;
                queue.Enqueue(next);
            }
        }
        var reached = depth.Where(d => d.Key != start.Id).Select(d => new ReachedNode(graph.Node(d.Key)!, d.Value)).OrderBy(r => r.Depth).ThenBy(r => r.Node.Id, StringComparer.Ordinal).ToList();
        // upstream is the question of provenance; for impact, the gaps are the ones that matter on the way there: a node whose own feed is unknown may feed from elsewhere too
        var gaps = reached.Select(r => r.Node).Append(start).Where(n => n.UnknownUpstream).DistinctBy(n => n.Id).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        return new LineageSlice(start, reached, [.. edges.Values.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)], gaps, truncated);
    }

    /// <summary>The shortest chain of edges that carries data from one node to another, or null when none does within the depth.</summary>
    public static IReadOnlyList<LineageEdge>? Path(this LineageGraph graph, string fromId, string toId, int maxDepth = DefaultDepth)
    {
        if (graph.Node(fromId) is null) throw new KeyNotFoundException($"The graph has no node '{fromId}'.");
        if (graph.Node(toId) is null) throw new KeyNotFoundException($"The graph has no node '{toId}'.");
        var via = new Dictionary<string, (string Previous, LineageEdge Edge)?>(StringComparer.Ordinal) { [fromId] = null };
        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [fromId] = 0 };
        var queue = new Queue<string>([fromId]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id == toId) break;
            if (depth[id] >= maxDepth) continue;
            foreach (var edge in graph.Downstream(id))
            {
                if (via.ContainsKey(edge.To)) continue;
                via[edge.To] = (id, edge);
                depth[edge.To] = depth[id] + 1;
                queue.Enqueue(edge.To);
            }
        }
        if (!via.ContainsKey(toId)) return null;
        var path = new List<LineageEdge>();
        for (var at = toId; via[at] is { } step; at = step.Previous) path.Insert(0, step.Edge);
        return path;
    }
}
