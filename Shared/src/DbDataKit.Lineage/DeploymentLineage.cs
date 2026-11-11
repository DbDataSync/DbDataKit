using System.Globalization;

namespace DbDataKit.Lineage;

/// <summary>What a deployment node is (its <c>deployment</c> property): the types of the one node kind <see cref="NodeKind.Deployment"/> (plan 18 section 3, plan 28c ED18).</summary>
public static class DeploymentTypes
{
    public const string Package = "package";
    public const string Version = "version";
    public const string Channel = "channel";
    public const string Group = "group";
    public const string Instance = "instance";
    public const string Runtime = "runtime";
    public const string Run = "run";
}

/// <summary>
/// The identities of deployment lineage, so the host that builds it (the runner) and everything that reads it (the command line, the console) agree. Every identity is <see cref="LineageIdentity.SuiteObject"/>:
/// the runner group is the server's instance id. The edges run the way a definition travels:
/// <c>package -[deploys]-> version -[deploys]-> channel -[deploys]-> runner instance</c>, with <c>group -[deploys]-> instance</c> and
/// <c>runtime -[deploys]-> instance</c> beside, and <c>version -[runs]-> run</c> for a run that names its version. The edge into an instance says which version it has in that channel and in what state
/// (<see cref="DeploymentDetail"/>); the edge into a channel says whether the version is its active, previous or staged one.
/// </summary>
public static class DeploymentIds
{
    public static string Package(string group, string kind, string name) => LineageIdentity.SuiteObject(group, "package", $"{kind}/{name}");
    public static string Version(string group, string kind, string name, int version) => LineageIdentity.SuiteObject(group, "version", $"{kind}/{name}/{version}");
    public static string Channel(string group, string kind, string name, string channel) => LineageIdentity.SuiteObject(group, "channel", $"{kind}/{name}/{channel}");
    public static string Group(string group) => LineageIdentity.SuiteObject(group, "group", group);
    public static string Instance(string group, string instance) => LineageIdentity.SuiteObject(group, "instance", instance);
    public static string Runtime(string group, string version) => LineageIdentity.SuiteObject(group, "runtime", version);
    public static string Run(string group, string run) => LineageIdentity.SuiteObject(group, "run", run);
}

/// <summary>The property names of deployment nodes.</summary>
public static class DeploymentProperties
{
    public const string Type = "deployment";
    public const string Kind = "kind";
    public const string Name = "name";
    public const string Channel = "channel";
    public const string Group = "group";
    public const string Version = "version";
    public const string Active = "active";
    public const string Previous = "previous";
    public const string Staged = "staged";
    public const string PublishedBy = "published_by";
    public const string PublishedAt = "published_at";
    public const string LastEvent = "last_event";
    public const string LastBy = "last_by";
    public const string LastAt = "last_at";
    public const string History = "history";
    public const string InstanceId = "instance_id";
    public const string Host = "host";
    public const string Live = "live";
    public const string RunnerVersion = "runner_version";
    public const string Runtime = "runtime";
    public const string Modules = "module_versions";
    public const string Outcome = "outcome";
    public const string StartedAt = "started_at";
    public const string Task = "task";
}

/// <summary>What an edge into a runner instance says: the version the instance has of a definition in the channel, its state (ready, active, preparing, failed), when it reported it, whether it is gone, and why it failed.</summary>
public sealed record DeploymentDetail(int Version, string State, DateTimeOffset At, string? Error = null, bool Gone = false)
{
    public string Format() => string.Join('|', Version, State, At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), Gone ? "gone" : "live", Error?.Replace('|', '/').Replace('\n', ' ') ?? "");

    public static DeploymentDetail? Parse(string? text)
    {
        var parts = text?.Split('|');
        return parts is { Length: 5 } && int.TryParse(parts[0], out var version) && DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? new DeploymentDetail(version, parts[1], at, parts[4].Length == 0 ? null : parts[4], parts[3] == "gone") : null;
    }
}

/// <summary>One runner instance's place in a channel's deployment of a definition.</summary>
public sealed record DeployedOn(string InstanceId, string Group, string? Host, int Version, string State, DateTimeOffset At, string? Error, bool Gone, string? Runtime, bool Live);

/// <summary>A definition in one channel: its active, previous and staged versions, the latest activate, switch or rollback with who and when, and the instances with their versions and states.</summary>
public sealed record ChannelDeployment(string Group, string Kind, string Name, string Channel, int? Active, int? Previous, int? Staged, string? LastEvent, string? LastBy, DateTimeOffset? LastAt, string? History, IReadOnlyList<DeployedOn> Instances)
{
    /// <summary>The instances that are on the active version and say active.</summary>
    public IReadOnlyList<DeployedOn> OnActive => [.. Instances.Where(i => !i.Gone && Active is { } a && i.Version == a && i.State == "active")];

    /// <summary>The instances that are not on the active version or do not say active (ready on another version, failed, preparing).</summary>
    public IReadOnlyList<DeployedOn> Behind => [.. Instances.Where(i => !i.Gone && !(Active is { } a && i.Version == a && i.State == "active"))];
}

/// <summary>What one runner instance has: for every definition and channel the version and the state, with the runtime it runs on.</summary>
public sealed record DeploymentOfInstance(string InstanceId, string Group, string? Host, string? Runtime, string? RunnerVersion, bool Live, IReadOnlyList<InstanceHolding> Holdings);

/// <summary>A definition in a channel as an instance holds it, with the channel's active version, so a reader sees an instance that lags.</summary>
public sealed record InstanceHolding(string Kind, string Name, string Channel, int Version, string State, DateTimeOffset At, string? Error, int? ChannelActive);

/// <summary>The two questions deployment lineage answers, over any graph that has deployment nodes: where is a definition deployed, and what is deployed on a runner.</summary>
public static class DeploymentQueries
{
    private static string Prop(LineageNode node, string key) => node.PropertyMap.TryGetValue(key, out var v) ? v : "";

    private static string? Opt(LineageNode node, string key) => Prop(node, key) is { Length: > 0 } v ? v : null;

    private static int? Number(LineageNode node, string key) => int.TryParse(Prop(node, key), out var n) ? n : null;

    private static bool Is(LineageNode node, string type) => node.Kind == NodeKind.Deployment && Prop(node, DeploymentProperties.Type) == type;

    private static bool Names(LineageNode node, string definition) => definition == Prop(node, DeploymentProperties.Name) || definition == $"{Prop(node, DeploymentProperties.Kind)}/{Prop(node, DeploymentProperties.Name)}";

    /// <summary>The channels a definition is deployed in. <paramref name="definition"/> is its name or <c>kind/name</c>; <paramref name="channel"/> narrows to one channel. Empty when the graph has no such definition.</summary>
    public static IReadOnlyList<ChannelDeployment> WhereDeployed(this LineageGraph graph, string definition, string? channel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        var found = new List<ChannelDeployment>();
        foreach (var node in graph.Nodes.Where(n => Is(n, DeploymentTypes.Channel) && Names(n, definition)).OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            if (channel is not null && Prop(node, DeploymentProperties.Channel) != channel) continue;
            var instances = new List<DeployedOn>();
            foreach (var edge in graph.Downstream(node.Id).Where(e => e.Kind == EdgeKind.Deploys))
            {
                var target = graph.Node(edge.To);
                if (target is null || !Is(target, DeploymentTypes.Instance) || DeploymentDetail.Parse(edge.Detail) is not { } detail) continue;
                instances.Add(new DeployedOn(Prop(target, DeploymentProperties.InstanceId), Prop(target, DeploymentProperties.Group), Opt(target, DeploymentProperties.Host), detail.Version, detail.State, detail.At, detail.Error, detail.Gone,
                    Opt(target, DeploymentProperties.Runtime), Prop(target, DeploymentProperties.Live) != "false"));
            }
            DateTimeOffset? at = DateTimeOffset.TryParse(Prop(node, DeploymentProperties.LastAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            found.Add(new ChannelDeployment(Prop(node, DeploymentProperties.Group), Prop(node, DeploymentProperties.Kind), Prop(node, DeploymentProperties.Name), Prop(node, DeploymentProperties.Channel),
                Number(node, DeploymentProperties.Active), Number(node, DeploymentProperties.Previous), Number(node, DeploymentProperties.Staged),
                Opt(node, DeploymentProperties.LastEvent), Opt(node, DeploymentProperties.LastBy), at, Opt(node, DeploymentProperties.History), [.. instances.OrderBy(i => i.InstanceId, StringComparer.Ordinal)]));
        }
        return found;
    }

    /// <summary>What is deployed on the runner instances that <paramref name="runner"/> names: an instance id or a host name (every instance on it). Empty when none matches.</summary>
    public static IReadOnlyList<DeploymentOfInstance> OnRunner(this LineageGraph graph, string runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runner);
        var found = new List<DeploymentOfInstance>();
        foreach (var node in graph.Nodes.Where(n => Is(n, DeploymentTypes.Instance)).OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            if (!string.Equals(Prop(node, DeploymentProperties.InstanceId), runner, StringComparison.OrdinalIgnoreCase) && !string.Equals(Prop(node, DeploymentProperties.Host), runner, StringComparison.OrdinalIgnoreCase)) continue;
            var holdings = new List<InstanceHolding>();
            foreach (var edge in graph.Upstream(node.Id).Where(e => e.Kind == EdgeKind.Deploys))
            {
                var from = graph.Node(edge.From);
                if (from is null || !Is(from, DeploymentTypes.Channel) || DeploymentDetail.Parse(edge.Detail) is not { } detail) continue;
                holdings.Add(new InstanceHolding(Prop(from, DeploymentProperties.Kind), Prop(from, DeploymentProperties.Name), Prop(from, DeploymentProperties.Channel), detail.Version, detail.State, detail.At, detail.Error, Number(from, DeploymentProperties.Active)));
            }
            found.Add(new DeploymentOfInstance(Prop(node, DeploymentProperties.InstanceId), Prop(node, DeploymentProperties.Group), Opt(node, DeploymentProperties.Host), Opt(node, DeploymentProperties.Runtime), Opt(node, DeploymentProperties.RunnerVersion),
                Prop(node, DeploymentProperties.Live) != "false", [.. holdings.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Channel, StringComparer.Ordinal)]));
        }
        return found;
    }

    /// <summary>The runs that name a version of a definition (<c>version -[runs]-> run</c>), newest first.</summary>
    public static IReadOnlyList<LineageNode> RunsOf(this LineageGraph graph, string definition, int version)
    {
        var runs = new List<LineageNode>();
        foreach (var node in graph.Nodes.Where(n => Is(n, DeploymentTypes.Version) && Names(n, definition) && Number(n, DeploymentProperties.Version) == version))
            runs.AddRange(graph.Downstream(node.Id).Where(e => e.Kind == EdgeKind.Runs).Select(e => graph.Node(e.To)).OfType<LineageNode>());
        return [.. runs.OrderByDescending(r => Prop(r, DeploymentProperties.StartedAt), StringComparer.Ordinal)];
    }
}
