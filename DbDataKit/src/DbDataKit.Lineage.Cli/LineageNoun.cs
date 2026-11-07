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

    private static readonly string InstanceSchema = DataSchemas.Object(
        [("instance", DataSchemas.String), ("host", DataSchemas.String), ("version", DataSchemas.Integer), ("state", DataSchemas.String), ("reported_at", DataSchemas.String), ("error", DataSchemas.String),
            ("runtime", DataSchemas.String), ("live", DataSchemas.Boolean), ("on_active", DataSchemas.Boolean)],
        "instance", "version", "state", "reported_at", "live", "on_active");

    private static readonly string ChannelSchema = DataSchemas.Object(
        [("group", DataSchemas.String), ("kind", DataSchemas.String), ("name", DataSchemas.String), ("channel", DataSchemas.String), ("active", DataSchemas.Integer), ("previous", DataSchemas.Integer), ("staged", DataSchemas.Integer),
            ("last_event", DataSchemas.String), ("last_by", DataSchemas.String), ("last_at", DataSchemas.String), ("history", DataSchemas.String), ("instances", DataSchemas.ListOf(InstanceSchema))],
        "group", "kind", "name", "channel", "instances");

    private static readonly string WhereSchema = DataSchemas.Object([("definition", DataSchemas.String), ("channels", DataSchemas.ListOf(ChannelSchema))], "definition", "channels");

    private static readonly string HoldingSchema = DataSchemas.Object(
        [("kind", DataSchemas.String), ("name", DataSchemas.String), ("channel", DataSchemas.String), ("version", DataSchemas.Integer), ("state", DataSchemas.String), ("reported_at", DataSchemas.String), ("error", DataSchemas.String),
            ("channel_active", DataSchemas.Integer), ("behind", DataSchemas.Boolean)],
        "kind", "name", "channel", "version", "state", "reported_at", "behind");

    private static readonly string RunnerSchema = DataSchemas.Object(
        [("instance", DataSchemas.String), ("group", DataSchemas.String), ("host", DataSchemas.String), ("runtime", DataSchemas.String), ("runner_version", DataSchemas.String), ("live", DataSchemas.Boolean), ("holdings", DataSchemas.ListOf(HoldingSchema))],
        "instance", "group", "live", "holdings");

    private static readonly string OnSchema = DataSchemas.Object([("runner", DataSchemas.String), ("instances", DataSchemas.ListOf(RunnerSchema))], "runner", "instances");

    private static readonly string RunsSchema = DataSchemas.Object(
        [("definition", DataSchemas.String), ("version", DataSchemas.Integer),
            ("runs", DataSchemas.ListOf(DataSchemas.Object([("run", DataSchemas.String), ("task", DataSchemas.String), ("channel", DataSchemas.String), ("outcome", DataSchemas.String), ("started_at", DataSchemas.String)], "run")))],
        "definition", "version", "runs");

    /// <param name="source">The host's graph.</param>
    /// <param name="deployment">Adds the deployment questions (<c>where</c>, <c>on</c>, <c>runs</c>, plan 28c E24) for a host whose graph has deployment nodes (a runner, a console).</param>
    public static Noun Create(Func<CancellationToken, Task<LineageGraph>> source, bool deployment = false)
    {
        var verbs = new List<Verb>
        {
            new("graph", "Every node and edge of the graph", EffectClass.OfflineOnly, "[--kind <kind>]", (args, context) => GraphAsync(source, args, context), GraphSchema),
            new("impact", "What a node feeds, downstream", EffectClass.OfflineOnly, "<node> [--depth <n>]", (args, context) => SliceAsync(source, args, context, downstream: true), SliceSchema),
            new("provenance", "What feeds a node, upstream", EffectClass.OfflineOnly, "<node> [--depth <n>]", (args, context) => SliceAsync(source, args, context, downstream: false), SliceSchema),
        };
        if (deployment)
        {
            verbs.Add(new("where", "Where a definition is deployed: its channels, versions and runners", EffectClass.OfflineOnly, "<definition> [--channel <channel>]", (args, context) => WhereAsync(source, args, context), WhereSchema));
            verbs.Add(new("on", "What is deployed on a runner (an instance id or a host)", EffectClass.OfflineOnly, "<runner>", (args, context) => OnAsync(source, args, context), OnSchema));
            verbs.Add(new("runs", "The runs that named a version of a definition", EffectClass.OfflineOnly, "<definition> <version>", (args, context) => RunsAsync(source, args, context), RunsSchema));
        }
        return new("lineage", "What feeds what: the lineage graph, the impact of a change, the provenance of a result" + (deployment ? ", and where definitions are deployed" : ""), verbs);
    }

    private static async Task<int> WhereAsync(Func<CancellationToken, Task<LineageGraph>> source, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "channel" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) return await Usage(context, "lineage where <definition> [--channel <channel>]");
        var graph = await source(context.Cancellation);
        var found = graph.WhereDeployed(parsed.Words[0], parsed.Get("channel"));
        if (found.Count == 0)
        {
            await context.Error.WriteLineAsync($"'{parsed.Words[0]}' is not deployed anywhere the graph knows{(parsed.Get("channel") is { } c ? $" in the channel '{c}'" : "")}.");
            return CliExit.Failure;
        }
        context.Data["definition"] = parsed.Words[0];
        context.Data["channels"] = found.Select(DeploymentDocuments.Of).ToList();
        foreach (var channel in found)
        {
            await context.Out.WriteLineAsync($"{channel.Kind}/{channel.Name} on {channel.Channel}: active {Version(channel.Active)}, previous {Version(channel.Previous)}" + (channel.Staged is { } staged ? $", staged v{staged}" : ""));
            if (channel.LastEvent is not null) await context.Out.WriteLineAsync($"  last {channel.LastEvent} by {channel.LastBy} at {channel.LastAt:u}");
            foreach (var i in channel.Instances)
                await context.Out.WriteLineAsync($"  {i.Host ?? i.InstanceId} ({i.InstanceId}): v{i.Version} {i.State}{(i.Gone ? " (gone)" : "")} since {i.At:u}{(i.Error is null ? "" : $": {i.Error}")}{(i.Runtime is null ? "" : $", runtime {i.Runtime}")}");
        }
        return CliExit.Ok;
    }

    private static string Version(int? version) => version is { } v ? $"v{v}" : "none";

    private static async Task<int> OnAsync(Func<CancellationToken, Task<LineageGraph>> source, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) return await Usage(context, "lineage on <runner>");
        var found = (await source(context.Cancellation)).OnRunner(parsed.Words[0]);
        if (found.Count == 0)
        {
            await context.Error.WriteLineAsync($"The graph has no runner '{parsed.Words[0]}' (an instance id or a host name).");
            return CliExit.Failure;
        }
        context.Data["runner"] = parsed.Words[0];
        context.Data["instances"] = found.Select(DeploymentDocuments.Of).ToList();
        foreach (var runner in found)
        {
            await context.Out.WriteLineAsync($"{runner.Host ?? runner.InstanceId} ({runner.InstanceId}){(runner.Live ? "" : " not live")}, runtime {runner.Runtime ?? "unknown"}");
            if (runner.Holdings.Count == 0) await context.Out.WriteLineAsync("  nothing deployed");
            foreach (var h in runner.Holdings)
                await context.Out.WriteLineAsync($"  {h.Kind}/{h.Name} on {h.Channel}: v{h.Version} {h.State}" + (h.ChannelActive is { } a && (a != h.Version || h.State != "active") ? $" (the channel is on v{a})" : "") + (h.Error is null ? "" : $": {h.Error}"));
        }
        return CliExit.Ok;
    }

    private static async Task<int> RunsAsync(Func<CancellationToken, Task<LineageGraph>> source, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 2 || !int.TryParse(parsed.Words[1], out var version) || version < 1) return await Usage(context, "lineage runs <definition> <version>");
        var runs = (await source(context.Cancellation)).RunsOf(parsed.Words[0], version);
        context.Data["definition"] = parsed.Words[0];
        context.Data["version"] = version;
        context.Data["runs"] = runs.Select(DeploymentRun).ToList();
        await context.Out.WriteLineAsync($"{runs.Count} runs of {parsed.Words[0]} v{version}");
        foreach (var r in runs) await context.Out.WriteLineAsync($"  {r.PropertyMap.GetValueOrDefault(DeploymentProperties.StartedAt)} {r.Name} {r.PropertyMap.GetValueOrDefault(DeploymentProperties.Outcome)}");
        return CliExit.Ok;
    }

    private static Dictionary<string, object?> DeploymentRun(LineageNode run)
    {
        var d = new Dictionary<string, object?> { ["run"] = run.Id, ["task"] = run.Name };
        foreach (var (key, name) in new[] { ("channel", DeploymentProperties.Channel), ("outcome", DeploymentProperties.Outcome), ("started_at", DeploymentProperties.StartedAt) })
            if (run.PropertyMap.TryGetValue(name, out var value) && value.Length > 0) d[key] = value;
        return d;
    }

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
