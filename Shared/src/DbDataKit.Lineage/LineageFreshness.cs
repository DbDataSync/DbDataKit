using DbDataKit.Monitoring;

namespace DbDataKit.Lineage;

/// <summary>
/// Which edges are late (plan 18, G10b): the product says how often an edge should run (a replication's frequency, a plan's <c>expected_every</c>) and the rule is the shared one, <see cref="HealthRule.Overdue"/> of <see cref="HealthEvaluator"/>:
/// the edge's last run ended more than that ago. An edge the product has no expectation for (null) is never late: with no limit there is nothing to be late at. A run that failed is marked failed by its outcome; lateness is its own mark, so a
/// line can say <em>failed</em>, <em>late</em> or <em>ok</em>.
/// </summary>
public static class LineageFreshness
{
    public static IReadOnlyList<EdgeRun> Mark(IEnumerable<EdgeRun> runs, Func<EdgeRun, TimeSpan?> expectedEvery, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(expectedEvery);
        var evaluator = new HealthEvaluator(clock);
        var marked = new List<EdgeRun>();
        foreach (var run in runs)
        {
            if (expectedEvery(run) is not { } every) { marked.Add(run); continue; }
            var record = new RunRecord(run.EdgeKey, run.EdgeKey, run.At, run.At, run.Outcome == RunOutcome.Succeeded ? Monitoring.RunOutcome.Succeeded : Monitoring.RunOutcome.Failed, run.Rows);
            marked.Add(run with { Late = evaluator.Evaluate(new JobExpectation(run.EdgeKey, every), record).Has(HealthRule.Overdue) });
        }
        return marked;
    }
}
