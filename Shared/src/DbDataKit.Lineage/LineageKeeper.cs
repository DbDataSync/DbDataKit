namespace DbDataKit.Lineage;

/// <summary>What one <see cref="LineageKeeper.Keep"/> did: whether the design graph changed, how many runs were put along its edges, how many runs the retention removed, and how many runs named an edge the graph does not have.</summary>
public sealed record LineageKept(bool DesignChanged, int RunsAdded, int RunsPruned, int RunsLeftOut);

/// <summary>
/// What a server does to keep its own lineage between requests (plan 18, G07b and G16): its design graph in the <see cref="LineageStore"/> under its owner name (replaced only when it changed, so the version moves exactly when a definition does), the newest runs it
/// knows laid along the design's edges (a run of an edge the design does not have is not lineage and is left out, counted), and the runs older than its retention of run detail removed with <see cref="LineageStore.PruneRuns"/> (decision 1 of the lineage doc:
/// the server's retention of run detail, no setting of its own). <see cref="Read"/> is what the server then answers: the graph and the newest run of each edge, which outlives the run history the product itself keeps for a shorter time.
/// </summary>
public sealed class LineageKeeper(LineageStore store, string owner, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Owner => owner;

    /// <param name="design">The graph the definitions give now.</param>
    /// <param name="runs">The runs the server knows (the newest of each edge is enough; a run already stored at the same instant is stored once).</param>
    /// <param name="retention">How long run detail is kept; null prunes nothing.</param>
    public LineageKept Keep(LineageGraph design, IEnumerable<EdgeRun> runs, TimeSpan? retention = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(runs);
        var changed = store.Replace(owner, design);
        var edges = design.Edges.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var cutoff = retention is { } keep ? _clock.GetUtcNow() - keep : (DateTimeOffset?)null;
        int added = 0, left = 0;
        foreach (var run in runs)
        {
            if (!edges.Contains(run.EdgeKey)) { left++; continue; }
            // a run that is already past the retention would only be pruned again at once
            if (cutoff is { } limit && run.At < limit) continue;
            store.AddRun(owner, run);
            added++;
        }
        var pruned = cutoff is { } before ? store.PruneRuns(owner, before) : 0;
        return new LineageKept(changed, added, pruned, left);
    }

    /// <summary>The stored design graph and the newest run of each of its edges.</summary>
    public (LineageGraph Graph, IReadOnlyList<EdgeRun> Runs) Read() => (store.Load(owner), [.. store.LatestRuns(owner).Values]);
}
