namespace DbDataKit.Lineage;

/// <summary>A published thing that reads what a change would remove: its name, what it is (a dashboard, a report, an app, a model's measure), the channel it is published in and how the graph reaches it.</summary>
public sealed record ContractConsumer(string Name, string Kind, string Channel, string Via);

/// <summary>
/// The contract step's question (plan 21 decision 4, CH06): before a column is dropped or retyped, which published consumers still read it, in any channel. It walks the joined lineage graph downstream from the column (and from
/// the table, whose readers are known only per table), keeps the consumers and the semantic members bound to it, and writes the refusal that names each by name and channel. A node says its channel in the property
/// <see cref="ChannelProperty"/>; one that says nothing is production's (what a serving group publishes by default). The graph is only as complete as what its servers emit: a consumer the graph does not know is not found, which
/// the refusal does not claim otherwise (it says "the lineage graph shows").
/// </summary>
public static class ContractGuard
{
    public const string ChannelProperty = "channel";

    /// <summary>The consumers that read any of <paramref name="columnIds"/> (column nodes), or the table <paramref name="datasetId"/> as a whole when it is given and a consumer reads it at table granularity.</summary>
    public static IReadOnlyList<ContractConsumer> Consumers(LineageGraph graph, IEnumerable<string> columnIds, string? datasetId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var found = new Dictionary<(string Name, string Channel), ContractConsumer>();
        void Add(LineageNode node, string via)
        {
            if (node.Kind is not (NodeKind.Consumer or NodeKind.SemanticMember)) return;
            var channel = node.PropertyMap.GetValueOrDefault(ChannelProperty) is { Length: > 0 } c ? c : "prod";
            var kind = node.Kind == NodeKind.SemanticMember ? "model member" : node.PropertyMap.GetValueOrDefault("consumer") ?? "consumer";
            found.TryAdd((node.Name, channel), new ContractConsumer(node.Name, kind, channel, via));
        }
        foreach (var id in columnIds.Distinct(StringComparer.Ordinal))
        {
            if (graph.Node(id) is null) continue;
            foreach (var reached in graph.Impact(id).Reached) Add(reached.Node, $"reads {graph.Node(id)!.Name}");
        }
        if (datasetId is not null && graph.Node(datasetId) is not null)
            foreach (var reached in graph.Impact(datasetId).Reached.Where(r => r.Node.Kind == NodeKind.Consumer))
                Add(reached.Node, $"reads {graph.Node(datasetId)!.Name} as a table, so the column cannot be ruled out");
        return [.. found.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Channel, StringComparer.Ordinal)];
    }

    /// <summary>The refusal for a change (<paramref name="change"/>: "drop the column 'price' of 'sales.orders'") that published consumers still read; null when none does.</summary>
    public static string? Refusal(string change, IReadOnlyList<ContractConsumer> consumers) =>
        consumers.Count == 0 ? null
            : $"Cannot {change}: the lineage graph shows {string.Join("; ", consumers.Select(c => $"the {c.Kind} '{c.Name}' published in channel '{c.Channel}' ({c.Via})"))}. Publish {(consumers.Count == 1 ? "it" : "them")} without it first, then contract (expand, then contract).";
}
