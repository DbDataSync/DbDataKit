namespace DbDataKit.Lineage;

/// <summary>
/// A lineage graph: nodes and the edges between them, with no cycle and no edge to a node that is not there. A node given twice is one node (the later wins, so a product can refine what another said); an edge given twice is one edge.
/// Built once and not changed; <see cref="Merge"/> joins graphs by identity.
/// </summary>
public sealed class LineageGraph
{
    private readonly Dictionary<string, LineageNode> _nodes;
    private readonly Dictionary<string, List<LineageEdge>> _out = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LineageEdge>> _in = new(StringComparer.Ordinal);

    public static LineageGraph Empty { get; } = new([], []);

    public LineageGraph(IEnumerable<LineageNode> nodes, IEnumerable<LineageEdge> edges)
    {
        _nodes = [];
        foreach (var node in nodes) _nodes[node.Id] = node;
        var seen = new Dictionary<string, LineageEdge>(StringComparer.Ordinal);
        foreach (var edge in edges) seen[edge.Key] = edge;
        Edges = [.. seen.Values.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Kind)];
        foreach (var edge in Edges)
        {
            if (!_nodes.ContainsKey(edge.From)) throw new LineageGraphException($"The edge {edge.Kind} from '{edge.From}' to '{edge.To}' starts at a node the graph does not have.");
            if (!_nodes.ContainsKey(edge.To)) throw new LineageGraphException($"The edge {edge.Kind} from '{edge.From}' to '{edge.To}' ends at a node the graph does not have.");
            if (edge.From == edge.To) throw new LineageGraphException($"'{edge.From}' flows into itself.");
            (_out.TryGetValue(edge.From, out var o) ? o : _out[edge.From] = []).Add(edge);
            (_in.TryGetValue(edge.To, out var i) ? i : _in[edge.To] = []).Add(edge);
        }
        if (FindCycle() is { } cycle) throw new LineageGraphException($"Data cannot flow in a circle: {string.Join(" -> ", cycle)}.");
    }

    public IReadOnlyCollection<LineageNode> Nodes => _nodes.Values;

    public IReadOnlyList<LineageEdge> Edges { get; }

    public LineageNode? Node(string id) => _nodes.GetValueOrDefault(id);

    /// <summary>The edges that leave a node (what it feeds).</summary>
    public IReadOnlyList<LineageEdge> Downstream(string id) => _out.TryGetValue(id, out var edges) ? edges : [];

    /// <summary>The edges that enter a node (what feeds it).</summary>
    public IReadOnlyList<LineageEdge> Upstream(string id) => _in.TryGetValue(id, out var edges) ? edges : [];

    /// <summary>A node and every edge, to compare two versions of a graph.</summary>
    public string Fingerprint()
    {
        var text = string.Join("\n", Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => $"n|{n.Id}|{n.Kind}|{n.Name}|{n.UnknownUpstream}|{string.Join(",", n.PropertyMap.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))}")
            .Concat(Edges.Select(e => $"e|{e.Key.Replace('\n', '|')}|{e.Granularity}|{e.Confidence}|{e.Detail}")));
        return LineageEdge.Hash(text);
    }

    /// <summary>Joins graphs by identity: a node in several is one node (unknown upstream only if every graph that has it says so, since one that knows its upstream has an edge for it), and their edges are one set. Throws if the union has a cycle.</summary>
    public static LineageGraph Merge(IEnumerable<LineageGraph> graphs)
    {
        var all = graphs.ToList();
        var nodes = new Dictionary<string, LineageNode>(StringComparer.Ordinal);
        foreach (var node in all.SelectMany(g => g.Nodes))
        {
            nodes[node.Id] = nodes.TryGetValue(node.Id, out var earlier)
                ? node with { UnknownUpstream = earlier.UnknownUpstream && node.UnknownUpstream, Properties = earlier.PropertyMap.Concat(node.PropertyMap).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value) }
                : node;
        }
        return new LineageGraph(nodes.Values, all.SelectMany(g => g.Edges));
    }

    /// <summary>One cycle as the ids along it (the first repeated at the end), or null.</summary>
    private List<string>? FindCycle()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);      // 1 on the stack, 2 finished
        var stack = new List<string>();
        List<string>? Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            foreach (var edge in _out.GetValueOrDefault(id) ?? [])
            {
                if (state.GetValueOrDefault(edge.To) == 1) return [.. stack.SkipWhile(s => s != edge.To), edge.To];
                if (!state.ContainsKey(edge.To) && Visit(edge.To) is { } found) return found;
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            return null;
        }
        foreach (var id in _nodes.Keys.Order(StringComparer.Ordinal))
            if (!state.ContainsKey(id) && Visit(id) is { } cycle) return cycle;
        return null;
    }
}
