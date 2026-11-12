using System.Globalization;
using DbDataKit.Lineage;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataKit.Runner.Deployments;

/// <summary>
/// The deployment lineage of one runner group (plan 28c E24, ED18; plan 18 section 3): what the <c>definitions</c> module and the runner module already hold, laid out as nodes and edges of the plan 18 graph
/// (<see cref="DeploymentIds"/> says the shape): for every package of kind <c>integration</c>, its versions, the channels with their active, previous and staged version and the latest activate, switch or rollback
/// (who, when), the runner instances with the version they report in each channel and whether it is ready, active, preparing or failed, the runtime each runs on (its software version, the module versions beside it), and the
/// newest runs, each joined to the version it names. Nothing is stored: the graph is read from the state every time, so it is never behind what the runner knows.
/// </summary>
public static class RunnerDeploymentLineage
{
    /// <summary>The newest runs of a package that are laid in the graph.</summary>
    public const int RunsPerPackage = 25;

    /// <summary>The events that move what a channel serves: a publish that serves at once, an activation, a switch, a rollback, a retire (a stage does not).</summary>
    private static readonly string[] ChangesOfTheServingVersion = ["publish", "activate", "switch", "rollback", "retire"];

    /// <param name="state">The group's state database.</param>
    /// <param name="group">The runner group's name, the instance id of the identities.</param>
    /// <param name="lease">How long after its last heartbeat an instance is live.</param>
    /// <param name="runsPerPackage">How many of the newest runs of each package are laid in the graph.</param>
    public static LineageGraph Build(StateDatabase state, string group, TimeSpan lease, int runsPerPackage = RunsPerPackage) => BuildWithProblems(state, group, lease, runsPerPackage).Graph;

    /// <summary>
    /// <see cref="Build"/> and what was left out, in words (plan 27a IX12): the outside systems the tasks of the packages reported reading and writing (<see cref="OutsideSystems"/>), as nodes beside the deployment, and a task whose systems
    /// would make data flow in a circle with another's, which the graph refuses and this names instead of failing the whole graph.
    /// </summary>
    public static (LineageGraph Graph, List<string> Problems) BuildWithProblems(StateDatabase state, string group, TimeSpan lease, int runsPerPackage = RunsPerPackage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var publisher = new DefinitionPublisher(state, RunnerPackages.Kinds(), null, "runner");
        var instances = new RunnerInstances(state);
        var runs = new RunStore(state, RunnerNames.EngineService);
        var kind = DefinitionVersionSource.Kind;

        var nodes = new Dictionary<string, LineageNode>(StringComparer.Ordinal);
        var edges = new List<LineageEdge>();
        var packages = new HashSet<string>(StringComparer.Ordinal);

        LineageNode Put(string id, string name, string type, params (string Key, string? Value)[] properties)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal) { [DeploymentProperties.Type] = type };
            foreach (var (key, value) in properties)
                if (!string.IsNullOrEmpty(value)) map[key] = value;
            return nodes[id] = new LineageNode(id, NodeKind.Deployment, name, false, map);
        }

        string Time(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        // the group, and every instance of it with the runtime it runs on
        var groupId = DeploymentIds.Group(group);
        Put(groupId, group, DeploymentTypes.Group, (DeploymentProperties.Group, group));
        var known = instances.ListAll();
        var liveIds = instances.ListLive(lease).Select(i => i.Id).ToHashSet();
        foreach (var instance in known)
        {
            var id = DeploymentIds.Instance(group, instance.Id.ToString());
            var runtime = instance.SoftwareVersion ?? instance.Version;
            var modules = ModuleVersionText.Parse(instance.ModuleVersions);
            Put(id, instance.Host, DeploymentTypes.Instance, (DeploymentProperties.InstanceId, instance.Id.ToString()), (DeploymentProperties.Group, group), (DeploymentProperties.Host, instance.Host),
                (DeploymentProperties.RunnerVersion, instance.Version), (DeploymentProperties.Runtime, runtime), (DeploymentProperties.Live, liveIds.Contains(instance.Id) ? "true" : "false"),
                (DeploymentProperties.Modules, modules is null ? null : string.Join(",", modules.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}={m.Value}"))));
            edges.Add(new LineageEdge(groupId, id, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, "member"));
            if (!string.IsNullOrEmpty(runtime))
            {
                var runtimeId = DeploymentIds.Runtime(group, runtime);
                Put(runtimeId, $"runtime {runtime}", DeploymentTypes.Runtime, (DeploymentProperties.Version, runtime), (DeploymentProperties.Group, group));
                edges.Add(new LineageEdge(runtimeId, id, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, "runs on"));
            }
        }

        foreach (var name in publisher.Names(kind))
        {
            var packageId = DeploymentIds.Package(group, kind, name);
            packages.Add(name);
            Put(packageId, name, DeploymentTypes.Package, (DeploymentProperties.Kind, kind), (DeploymentProperties.Name, name), (DeploymentProperties.Group, group));
            var events = publisher.EventsOf(kind, name, 200);

            string VersionNode(int version)
            {
                var id = DeploymentIds.Version(group, kind, name, version);
                if (nodes.ContainsKey(id)) return id;
                var published = events.Where(e => e.Version == version && e.Action is "publish" or "stage").OrderBy(e => e.Id).FirstOrDefault();
                Put(id, $"{name} v{version}", DeploymentTypes.Version, (DeploymentProperties.Kind, kind), (DeploymentProperties.Name, name), (DeploymentProperties.Version, version.ToString(CultureInfo.InvariantCulture)),
                    (DeploymentProperties.Group, group), (DeploymentProperties.PublishedBy, published?.ActorId), (DeploymentProperties.PublishedAt, published is null ? null : Time(published.At)));
                edges.Add(new LineageEdge(packageId, id, EdgeKind.Deploys, Granularity.Table, Confidence.Declared, "version"));
                return id;
            }

            foreach (var head in publisher.HeadsOf(kind, name))
            {
                var channelId = DeploymentIds.Channel(group, kind, name, head.Channel);
                var channelEvents = events.Where(e => e.Channel == head.Channel).ToList();
                var last = channelEvents.FirstOrDefault(e => ChangesOfTheServingVersion.Contains(e.Action));
                var history = string.Join("; ", channelEvents.Take(5).Select(e => $"{Time(e.At)} {e.Action} v{e.Version} by {e.ActorId}"));
                Put(channelId, $"{name} on {head.Channel}", DeploymentTypes.Channel, (DeploymentProperties.Kind, kind), (DeploymentProperties.Name, name), (DeploymentProperties.Channel, head.Channel), (DeploymentProperties.Group, group),
                    (DeploymentProperties.Active, head.Active?.ToString(CultureInfo.InvariantCulture)), (DeploymentProperties.Previous, head.Previous?.ToString(CultureInfo.InvariantCulture)), (DeploymentProperties.Staged, head.Staged?.ToString(CultureInfo.InvariantCulture)),
                    (DeploymentProperties.LastEvent, last?.Action), (DeploymentProperties.LastBy, last?.ActorId), (DeploymentProperties.LastAt, last is null ? null : Time(last.At)), (DeploymentProperties.History, history));
                foreach (var (version, role) in new[] { (head.Active, "active"), (head.Previous, "previous"), (head.Staged, "staged") })
                    if (version is { } v) edges.Add(new LineageEdge(VersionNode(v), channelId, EdgeKind.Deploys, Granularity.Table, Confidence.Declared, role));

                foreach (var status in publisher.Status.List(kind, name, head.Channel))
                {
                    var instanceId = DeploymentIds.Instance(group, status.Instance);
                    if (!nodes.ContainsKey(instanceId))      // an instance that reported and is no longer registered
                    {
                        Put(instanceId, status.Instance, DeploymentTypes.Instance, (DeploymentProperties.InstanceId, status.Instance), (DeploymentProperties.Group, group), (DeploymentProperties.Live, "false"));
                        edges.Add(new LineageEdge(groupId, instanceId, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, "member"));
                    }
                    edges.Add(new LineageEdge(channelId, instanceId, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, new DeploymentDetail(status.Version, status.State, status.At, status.Error, status.Gone).Format()));
                }
            }

            // a run names the version it ran (its package_version): join it to that version
            foreach (var stored in runs.Read(new RunQuery(RunnerNames.EngineService, Package: name, Limit: runsPerPackage)))
            {
                if (!int.TryParse(stored.PackageVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version)) continue;
                var runId = DeploymentIds.Run(group, stored.Id.ToString());
                nodes[runId] = new LineageNode(runId, NodeKind.Job, stored.Run.JobId, false, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DeploymentProperties.Type] = DeploymentTypes.Run, [DeploymentProperties.Name] = name, [DeploymentProperties.Version] = version.ToString(CultureInfo.InvariantCulture),
                    [DeploymentProperties.Channel] = stored.Channel ?? "", [DeploymentProperties.Outcome] = stored.Run.Outcome.ToString(), [DeploymentProperties.StartedAt] = Time(stored.Run.Started), [DeploymentProperties.Task] = stored.Run.JobId,
                });
                edges.Add(new LineageEdge(VersionNode(version), runId, EdgeKind.Runs, Granularity.Table, Confidence.Observed, stored.Channel));
            }
        }

        var problems = new List<string>();
        var graph = OutsideSystems(new LineageGraph(nodes.Values, edges), new TaskLineageStore(state).Read(), group, kind, packages, problems);
        return (graph, problems);
    }

    /// <summary>The prefix of a lineage line that names an outside system: <c>external://&lt;kind&gt;/&lt;name&gt;</c> (<see cref="LineageIdentity.ExternalSystem"/>).</summary>
    public const string ExternalPrefix = "external://";

    /// <summary>
    /// Lays the outside systems the tasks reported beside <paramref name="deployment"/> (plan 27a IX12): for each task of a package that is still deployed, a job node (under its package, an edge <c>deploys</c> with the detail <c>task</c>) and
    /// an <c>external_system</c> node for every <c>external://kind/name</c> line it read from or wrote to (<see cref="ExternalSystemLineage"/>); a rerun names the same ids and adds nothing. Lines that are not outside systems (a task's own name for a table)
    /// are not nodes. A task whose systems would close a circle with the others is left out and named in <paramref name="problems"/>.
    /// </summary>
    public static LineageGraph OutsideSystems(LineageGraph deployment, IReadOnlyList<TaskLineageRow> rows, string group, string kind, IReadOnlySet<string> packages, List<string> problems)
    {
        var graph = deployment;
        foreach (var task in rows.Where(r => packages.Contains(r.Package)).GroupBy(r => (r.Package, r.Task)).OrderBy(g => g.Key.Package, StringComparer.Ordinal).ThenBy(g => g.Key.Task, StringComparer.Ordinal))
        {
            ExternalTouch? Parse(string name)
            {
                if (!name.StartsWith(ExternalPrefix, StringComparison.Ordinal)) return null;
                var rest = name[ExternalPrefix.Length..];
                var slash = rest.IndexOf('/');
                return slash > 0 && slash < rest.Length - 1 ? new ExternalTouch(rest[..slash], rest[(slash + 1)..]) : null;
            }

            var reads = task.Where(r => r.Direction == TaskLineageRow.Reads).Select(r => Parse(r.Name)).OfType<ExternalTouch>().Distinct().ToList();
            var writes = task.Where(r => r.Direction == TaskLineageRow.Writes).Select(r => Parse(r.Name)).OfType<ExternalTouch>().Distinct().ToList();
            if (reads.Count == 0 && writes.Count == 0) continue;
            var label = $"{task.Key.Package}/{task.Key.Task}";
            try
            {
                var systems = ExternalSystemLineage.Design(group, label, reads, writes);
                var jobId = LineageIdentity.SuiteObject(group, "task", label);
                var packageId = DeploymentIds.Package(group, kind, task.Key.Package);
                var attach = new LineageGraph([deployment.Node(packageId)!, systems.Node(jobId)!], [new LineageEdge(packageId, jobId, EdgeKind.Deploys, Granularity.Table, Confidence.Declared, "task")]);
                graph = LineageGraph.Merge([graph, systems, attach]);
            }
            catch (LineageGraphException ex)
            {
                problems.Add($"The outside systems of task '{label}' are left out: {ex.Message}");
            }
        }

        return graph;
    }
}
