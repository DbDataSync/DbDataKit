using DbDataKit.Lineage;

namespace DbDataSync.State;

/// <summary>
/// The run layer of Sync's lineage (plan 18, G07): for each table mapping, its newest finished run from the run history, laid along the mapping's table-level <c>replicates</c> edges (the design edges carry
/// <c>replication/mapping</c> as their detail): when it ended, whether it succeeded, and the rows it wrote. A mapping that has not run has no run; one still queued or running is not a finished run and is skipped.
/// </summary>
public static class SyncLineageRuns
{
    public static IReadOnlyList<EdgeRun> Runs(LineageGraph design, TaskRunStore store)
    {
        var runs = new List<EdgeRun>();
        foreach (var edge in design.Edges.Where(e => e.Kind == EdgeKind.Replicates && e.Granularity == Granularity.Table && e.Detail is { } d && d.Contains('/')))
        {
            var (task, mapping) = (edge.Detail![..edge.Detail.IndexOf('/')], edge.Detail[(edge.Detail.IndexOf('/') + 1)..]);
            var last = new[] { RunKind.Primary, RunKind.BulkLoad }
                .SelectMany(kind => store.GetMappingRunHistory(task, kind, mapping, 5))
                .Where(r => r.Status is RunStatus.Succeeded or RunStatus.Failed && r.EndedAtUtc is not null)
                .OrderByDescending(r => r.EndedAtUtc)
                .FirstOrDefault();
            if (last is null) continue;
            runs.Add(new EdgeRun(edge.From, edge.To, EdgeKind.Replicates, last.EndedAtUtc!.Value, last.Status == RunStatus.Succeeded ? DbDataKit.Lineage.RunOutcome.Succeeded : DbDataKit.Lineage.RunOutcome.Failed, last.RowsWritten));
        }
        return runs;
    }
}
