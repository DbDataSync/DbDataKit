namespace DbDataKit.Lineage.Tests;

public class LineageQueryTests
{
    private static readonly LineageGraph Graph = LineageGraphTests.Fixture();

    [Fact]
    public void Impact_is_everything_downstream_by_distance()
    {
        var slice = Graph.Impact("stage");

        Assert.Equal([("audit", 1), ("mart", 1), ("report", 2)], slice.Reached.Select(r => (r.Node.Id, r.Depth)));
        Assert.Equal(["stage->audit", "stage->mart", "mart->report"], slice.Edges.OrderBy(e => e.From == "stage" ? 0 : 1).ThenBy(e => e.To).Select(e => $"{e.From}->{e.To}"));
        Assert.False(slice.Truncated);
        Assert.Empty(slice.Gaps);
    }

    [Fact]
    public void Provenance_is_everything_upstream_and_reports_the_places_where_it_does_not_know()
    {
        var slice = Graph.Provenance("report");

        Assert.Equal(["mart", "orphan", "stage", "src"], slice.Reached.Select(r => r.Node.Id));
        Assert.Equal(["orphan"], slice.Gaps.Select(g => g.Id));       // the feed of `orphan` is unknown: that is not the same as having none
        Assert.Empty(Graph.Provenance("src").Gaps);                   // `src` has no feed and is not marked unknown: a source
    }

    [Fact]
    public void A_depth_limit_stops_the_walk_and_says_there_was_more()
    {
        var slice = Graph.Provenance("report", maxDepth: 1);

        Assert.Equal(["mart"], slice.Reached.Select(r => r.Node.Id));
        Assert.True(slice.Truncated);
        Assert.False(Graph.Impact("src", maxDepth: 3).Truncated);     // exactly as deep as the graph
        Assert.True(Graph.Impact("src", maxDepth: 2).Truncated);
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.Impact("src", 0));
        Assert.Throws<KeyNotFoundException>(() => Graph.Impact("nowhere"));
    }

    [Fact]
    public void A_path_is_the_shortest_chain_of_edges_or_null()
    {
        var shortcut = new LineageGraph(Graph.Nodes, Graph.Edges.Append(LineageGraphTests.E("src", "mart", EdgeKind.Copies)));

        Assert.Equal(["src->stage", "stage->mart", "mart->report"], Graph.Path("src", "report")!.Select(e => $"{e.From}->{e.To}"));
        Assert.Equal(["src->mart", "mart->report"], shortcut.Path("src", "report")!.Select(e => $"{e.From}->{e.To}"));
        Assert.Null(Graph.Path("report", "src"));                    // data does not flow back
        Assert.Null(Graph.Path("src", "report", maxDepth: 2));
        Assert.Empty(Graph.Path("src", "src")!);
    }
}
