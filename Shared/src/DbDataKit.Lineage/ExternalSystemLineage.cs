namespace DbDataKit.Lineage;

/// <summary>An outside system a task touched: its kind (<c>http-api</c>, <c>sftp</c>, <c>queue</c>), its name.</summary>
public sealed record ExternalTouch(string Kind, string Name);

/// <summary>
/// The outside systems a task read from or wrote to, as lineage nodes (plan 27a IX12, plan 18): an <c>external_system</c> node for each (<see cref="LineageIdentity.ExternalSystem"/>) and a <c>job</c> node for the task. A system the task read from is
/// upstream of it (a <c>reads</c> edge from the system to the task); one it wrote to is downstream (a <c>serves</c> edge from the task to the system, detail <c>wrote to</c>). What an outside system is fed by is not ours to say, so each has an unknown
/// upstream. A rerun names the same ids and adds nothing; a task that reads and writes one system is a cycle, which the graph refuses naming it.
/// </summary>
public static class ExternalSystemLineage
{
    public static LineageGraph Design(string instanceId, string task, IEnumerable<ExternalTouch> readFrom, IEnumerable<ExternalTouch> wroteTo)
    {
        var job = LineageIdentity.SuiteObject(instanceId, "task", task);
        var nodes = new Dictionary<string, LineageNode>(StringComparer.Ordinal) { [job] = new LineageNode(job, NodeKind.Job, task, UnknownUpstream: false) };
        var edges = new List<LineageEdge>();

        string System(ExternalTouch touch)
        {
            var id = LineageIdentity.ExternalSystem(touch.Kind, touch.Name);
            nodes[id] = new LineageNode(id, NodeKind.ExternalSystem, touch.Name.Trim(), UnknownUpstream: true, new Dictionary<string, string> { ["system"] = touch.Kind.Trim().ToLowerInvariant() });
            return id;
        }

        foreach (var touch in readFrom)
            edges.Add(new LineageEdge(System(touch), job, EdgeKind.Reads, Granularity.Table, Confidence.Observed));
        foreach (var touch in wroteTo)
            edges.Add(new LineageEdge(job, System(touch), EdgeKind.Serves, Granularity.Table, Confidence.Observed, "wrote to"));
        return new LineageGraph(nodes.Values, edges);
    }
}
