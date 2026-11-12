using System.Text.Json.Nodes;

namespace DbDataKit.Lineage.Tests;

// plan 18 G10b: the wire form carries `runs`, additively
public class LineageRunJsonTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Runs_are_written_ordered_and_read_back_the_same()
    {
        var runs = new[]
        {
            new EdgeRun("b", "c", EdgeKind.Derives, At, RunOutcome.Succeeded, 7),
            new EdgeRun("a", "b", EdgeKind.Replicates, At.AddMinutes(1), RunOutcome.Failed),
        };

        var written = LineageJson.WriteRuns(runs);
        Assert.Equal("a", (string)written[0]!["from"]!);
        Assert.Equal("failed", (string)written[0]!["outcome"]!);
        Assert.Null(written[0]!["rows"]);

        var back = LineageJson.ReadRuns(new JsonObject { ["runs"] = JsonNode.Parse(written.ToJsonString()) });
        Assert.Equal(runs.OrderBy(r => r.From).ToList(), back);
    }

    [Fact]
    public void Late_is_written_only_when_it_is_true_and_a_document_without_it_reads_as_not_late()
    {
        var written = LineageJson.WriteRuns([new EdgeRun("a", "b", EdgeKind.Derives, At, RunOutcome.Succeeded, Late: true), new EdgeRun("b", "c", EdgeKind.Derives, At, RunOutcome.Succeeded)]);
        Assert.True((bool)written[0]!["late"]!);
        Assert.False(((JsonObject)written[1]!).ContainsKey("late"));
        var back = LineageJson.ReadRuns(new JsonObject { ["runs"] = JsonNode.Parse(written.ToJsonString()) });
        Assert.Equal([true, false], back.Select(r => r.Late));
    }

    [Fact]
    public void Freshness_marks_an_edge_late_by_the_overdue_rule_of_monitoring_and_leaves_one_with_no_expectation_alone()
    {
        var now = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(now);
        var runs = new[]
        {
            new EdgeRun("a", "b", EdgeKind.Replicates, now.AddMinutes(-10), RunOutcome.Succeeded),      // every 5 minutes: ten minutes ago is late
            new EdgeRun("b", "c", EdgeKind.Replicates, now.AddMinutes(-2), RunOutcome.Succeeded),       // two minutes ago is not
            new EdgeRun("c", "d", EdgeKind.Replicates, now.AddDays(-9), RunOutcome.Failed),             // no expectation: nothing to be late at
        };

        var marked = LineageFreshness.Mark(runs, r => r.From == "c" ? null : TimeSpan.FromMinutes(5), clock);

        Assert.Equal([true, false, false], marked.Select(r => r.Late));
        Assert.Equal(runs.Select(r => r.At), marked.Select(r => r.At));
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void A_document_without_runs_has_none_and_a_graph_still_reads_with_runs_in_it()
    {
        var graph = new LineageGraph([new("a", NodeKind.Dataset, "a"), new("b", NodeKind.Dataset, "b")], [new("a", "b", EdgeKind.Replicates, Granularity.Table, Confidence.Declared)]);
        var document = LineageJson.Write(graph);
        Assert.Empty(LineageJson.ReadRuns(document));

        document["runs"] = LineageJson.WriteRuns([new EdgeRun("a", "b", EdgeKind.Replicates, At, RunOutcome.Failed)]);
        Assert.Single(LineageJson.ReadRuns(document));
        Assert.Equal(graph.Fingerprint(), LineageJson.Read(document).Fingerprint());               // the graph is read as before
    }

    [Theory]
    [InlineData("""{"runs": 3}""", "`runs` is not a list")]
    [InlineData("""{"runs": [3]}""", "runs[0] is not an object")]
    [InlineData("""{"runs": [{"from": "a", "to": "b", "kind": "derives", "outcome": "failed"}]}""", "runs[0] has no `at`")]
    [InlineData("""{"runs": [{"from": "a", "to": "b", "kind": "derives", "at": "2026-11-10T08:00:00Z", "outcome": "boom"}]}""", "runs[0].outcome is `boom`")]
    [InlineData("""{"runs": [{"from": "a", "kind": "derives", "at": "2026-11-10T08:00:00Z", "outcome": "failed"}]}""", "runs[0] has no `to`")]
    public void A_runs_list_of_another_shape_is_refused_saying_where(string json, string message)
    {
        var ex = Assert.Throws<LineageGraphException>(() => LineageJson.ReadRuns(JsonNode.Parse(json)));
        Assert.Contains(message, ex.Message);
    }
}
