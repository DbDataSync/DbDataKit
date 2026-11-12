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

    /// <summary>
    /// The whole check a caller makes before it runs a contract step (plan 21 CH06): for each change, the consumers each reachable graph shows; the refusals that name them; and the allowances that were used. <paramref name="graphs"/> is one graph per paired
    /// server (a Gateway's, by its pairing name), kept apart so one that cannot be joined with the rest does not hide the others; <paramref name="unreachable"/> names a paired server whose graph could not be read (its consumers are not known, so the step is refused
    /// until a person says it may go on, as <c>gateway:&lt;name&gt;</c>, written after <paramref name="allowWith"/>). <paramref name="allowed"/> are the allowances the person gave: <c>&lt;consumer&gt;</c> (in every channel), <c>&lt;consumer&gt;@&lt;channel&gt;</c> or <c>gateway:&lt;name&gt;</c>.
    /// </summary>
    public static ContractVerdict Check(IReadOnlyList<ContractedColumn> changes, IReadOnlyList<(string Server, LineageGraph Graph)> graphs, IReadOnlyList<(string Server, string Why)> unreachable, IReadOnlyCollection<string> allowed, string allowWith = "--allow-consumer")
    {
        ArgumentNullException.ThrowIfNull(changes);
        var refusals = new List<string>();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var consumers = graphs.SelectMany(g => Consumers(g.Graph, [change.ColumnId], change.DatasetId)).DistinctBy(c => (c.Name, c.Channel)).OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Channel, StringComparer.Ordinal).ToList();
            var open = new List<ContractConsumer>();
            foreach (var consumer in consumers)
            {
                var token = allowed.FirstOrDefault(a => a == consumer.Name || a == consumer.Name + "@" + consumer.Channel);
                if (token is null) open.Add(consumer); else used.Add(token);
            }
            if (Refusal(change.Description, open) is { } refusal) refusals.Add($"{refusal} To go on without that, allow {(open.Count == 1 ? "it" : "each")} by name and channel, which is recorded: {string.Join(" ", open.Select(c => $"{allowWith} {c.Name}@{c.Channel}"))}.");
        }
        if (changes.Count > 0)
            foreach (var (server, why) in unreachable)
            {
                var token = "gateway:" + server;
                if (allowed.Contains(token)) used.Add(token);
                else refusals.Add($"Cannot check who reads what this change removes: the paired server '{server}' did not give its lineage ({why}), so its published consumers are not known. Try again when it answers, or allow the change without the check with {allowWith} {token}.");
            }
        return new ContractVerdict(refusals, [.. used]);
    }
}

/// <summary>One column a contract step removes or retypes: what to say (<c>drop the column 'price' of 'sales.orders'</c>), the column's node and its table's node in the graph (<see cref="LineageIdentity"/>).</summary>
public sealed record ContractedColumn(string Description, string ColumnId, string? DatasetId);

/// <summary>What <see cref="ContractGuard.Check"/> found: the refusals (none means the step may run) and the allowances the person gave that it used, which the caller records.</summary>
public sealed record ContractVerdict(IReadOnlyList<string> Refusals, IReadOnlyList<string> AllowancesUsed)
{
    public bool Allowed => Refusals.Count == 0;
}
