using DbDataKit.Cli;

namespace DbDataKit.Lineage.Cli;

/// <summary><c>lineage graph|impact|provenance</c> over whatever graph the host has. The host decides where the graph comes from (a store, a build, a pull from servers); the verbs only read it.</summary>
public static class LineageNoun
{
    private static readonly string NodeSchema = DataSchemas.Object(
        [("id", DataSchemas.String), ("kind", DataSchemas.String), ("name", DataSchemas.String), ("unknown_upstream", DataSchemas.Boolean)],
        "id", "kind", "name", "unknown_upstream");

    private static readonly string EdgeSchema = DataSchemas.Object(
        [("from", DataSchemas.String), ("to", DataSchemas.String), ("kind", DataSchemas.String), ("granularity", DataSchemas.String), ("confidence", DataSchemas.String), ("detail", DataSchemas.String)],
        "from", "to", "kind", "granularity", "confidence");

    private static readonly string ReachedSchema = DataSchemas.Object(
        [("id", DataSchemas.String), ("kind", DataSchemas.String), ("name", DataSchemas.String), ("depth", DataSchemas.Integer)], "id", "kind", "name", "depth");

    private static readonly string GraphSchema = DataSchemas.Object(
        [("nodes", DataSchemas.ListOf(NodeSchema)), ("edges", DataSchemas.ListOf(EdgeSchema)), ("fingerprint", DataSchemas.String)], "nodes", "edges", "fingerprint");

    private static readonly string SliceSchema = DataSchemas.Object(
        [("start", DataSchemas.String), ("reached", DataSchemas.ListOf(ReachedSchema)), ("edges", DataSchemas.ListOf(EdgeSchema)), ("gaps", DataSchemas.StringList), ("truncated", DataSchemas.Boolean)],
        "start", "reached", "edges", "gaps", "truncated");

    public static Noun Create(Func<CancellationToken, Task<LineageGraph>> source) => new("lineage", "What feeds what: the lineage graph, the impact of a change, the provenance of a result",
    [
        new Verb("graph", "Every node and edge of the graph", EffectClass.OfflineOnly, "[--kind <kind>]", (args, context) => GraphAsync(source, args, context), GraphSchema),
        new Verb("impact", "What a node feeds, downstream", EffectClass.OfflineOnly, "<node> [--depth <n>]", (args, context) => SliceAsync(source, args, context, downstream: true), SliceSchema),
        new Verb("provenance", "What feeds a node, upstream", EffectClass.OfflineOnly, "<node> [--depth <n>]", (args, context) => SliceAsync(source, args, context, downstream: false), SliceSchema),
    ]);

    private static Dictionary<string, object?> Node(LineageNode n) => new() { ["id"] = n.Id, ["kind"] = n.Kind.ToString().ToLowerInvariant(), ["name"] = n.Name, ["unknown_upstream"] = n.UnknownUpstream };

    private static Dictionary<string, object?> Edge(LineageEdge e)
    {
        var edge = new Dictionary<string, object?>
        {
            ["from"] = e.From, ["to"] = e.To, ["kind"] = e.Kind.ToString().ToLowerInvariant(),
            ["granularity"] = e.Granularity.ToString().ToLowerInvariant(), ["confidence"] = e.Confidence.ToString().ToLowerInvariant(),
        };
        if (e.Detail is not null) edge["detail"] = e.Detail;
        return edge;
    }

    private static async Task<int> GraphAsync(Func<CancellationToken, Task<LineageGraph>> source, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "kind" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) return await Usage(context, "lineage graph [--kind <kind>]");
        NodeKind? kind = null;
        if (parsed.Get("kind") is { } text)
        {
            if (!Enum.TryParse<NodeKind>(text, ignoreCase: true, out var k) || !Enum.IsDefined(k))
            {
                await context.Error.WriteLineAsync($"'{text}' is not a kind of node. The kinds are: {string.Join(", ", Enum.GetNames<NodeKind>().Select(n => n.ToLowerInvariant()))}.");
                return CliExit.Usage;
            }
            kind = k;
        }

        var graph = await source(context.Cancellation);
        var nodes = graph.Nodes.Where(n => kind is null || n.Kind == kind).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = graph.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToList();
        context.Data["nodes"] = nodes.Select(Node).ToList();
        context.Data["edges"] = edges.Select(Edge).ToList();
        context.Data["fingerprint"] = graph.Fingerprint();
        await context.Out.WriteLineAsync($"{nodes.Count} nodes, {edges.Count} edges");
        foreach (var edge in edges) await context.Out.WriteLineAsync($"{edge.From} -> {edge.To} ({edge.Kind.ToString().ToLowerInvariant()})");
        return CliExit.Ok;
    }

    private static async Task<int> SliceAsync(Func<CancellationToken, Task<LineageGraph>> source, string[] args, CliContext context, bool downstream)
    {
        var verb = downstream ? "impact" : "provenance";
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "depth" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) return await Usage(context, $"lineage {verb} <node> [--depth <n>]");
        var depth = LineageQueries.DefaultDepth;
        if (parsed.Get("depth") is { } text && (!int.TryParse(text, out depth) || depth < 1))
        {
            await context.Error.WriteLineAsync("The depth is a whole number from 1.");
            return CliExit.Usage;
        }

        var graph = await source(context.Cancellation);
        var id = parsed.Words[0];
        if (graph.Node(id) is null)
        {
            await context.Error.WriteLineAsync($"The graph has no node '{id}'.");
            return CliExit.Failure;
        }
        var slice = downstream ? graph.Impact(id, depth) : graph.Provenance(id, depth);
        context.Data["start"] = slice.Start.Id;
        context.Data["reached"] = slice.Reached.Select(r => new Dictionary<string, object?> { ["id"] = r.Node.Id, ["kind"] = r.Node.Kind.ToString().ToLowerInvariant(), ["name"] = r.Node.Name, ["depth"] = r.Depth }).ToList();
        context.Data["edges"] = slice.Edges.Select(Edge).ToList();
        context.Data["gaps"] = slice.Gaps.Select(n => n.Id).ToList();
        context.Data["truncated"] = slice.Truncated;

        await context.Out.WriteLineAsync($"{verb} of {slice.Start.Id}: {slice.Reached.Count} nodes");
        foreach (var r in slice.Reached) await context.Out.WriteLineAsync($"{new string(' ', r.Depth * 2)}{r.Node.Id}");
        foreach (var gap in slice.Gaps) await context.Out.WriteLineAsync($"what feeds {gap.Id} is not known");
        if (slice.Truncated) await context.Out.WriteLineAsync($"stopped at depth {depth}; there is more (--depth)");
        return CliExit.Ok;
    }

    private static async Task<int> Usage(CliContext context, string usage)
    {
        await context.Error.WriteLineAsync($"Usage: {usage}");
        return CliExit.Usage;
    }
}
