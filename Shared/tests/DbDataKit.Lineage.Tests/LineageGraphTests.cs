namespace DbDataKit.Lineage.Tests;

public class LineageGraphTests
{
    internal static LineageNode N(string id, bool unknown = false) => new(id, NodeKind.Dataset, id, unknown);

    internal static LineageEdge E(string from, string to, EdgeKind kind = EdgeKind.Derives, Granularity g = Granularity.Table) => new(from, to, kind, g, Confidence.Derived);

    /// <summary>src -> stage -> mart -> report, stage -> audit; and `orphan` whose own feed nobody knows.</summary>
    internal static LineageGraph Fixture() => new([N("src"), N("stage"), N("mart"), N("report"), N("audit"), N("orphan", unknown: true)],
        [E("src", "stage", EdgeKind.Replicates), E("stage", "mart"), E("mart", "report", EdgeKind.Reads), E("stage", "audit"), E("orphan", "mart")]);

    [Fact]
    public void A_node_or_an_edge_given_twice_is_one()
    {
        var graph = new LineageGraph([N("a"), N("b"), new LineageNode("a", NodeKind.Job, "a, refined")], [E("a", "b"), E("a", "b") with { Detail = "second" }]);

        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(NodeKind.Job, graph.Node("a")!.Kind);          // the later wins
        Assert.Equal("second", Assert.Single(graph.Edges).Detail);
    }

    [Fact]
    public void An_edge_to_a_node_the_graph_does_not_have_is_refused_naming_the_edge()
    {
        var ex = Assert.Throws<LineageGraphException>(() => new LineageGraph([N("a")], [E("a", "ghost")]));
        Assert.Contains("'ghost'", ex.Message);
        Assert.Throws<LineageGraphException>(() => new LineageGraph([N("a")], [E("a", "a")]));
    }

    [Fact]
    public void A_cycle_is_refused_and_named()
    {
        var ex = Assert.Throws<LineageGraphException>(() => new LineageGraph([N("a"), N("b"), N("c")], [E("a", "b"), E("b", "c"), E("c", "a")]));
        Assert.Contains("a -> b -> c -> a", ex.Message);
    }

    [Fact]
    public void Graphs_of_different_servers_join_by_identity_and_a_node_is_unknown_only_if_every_graph_says_so()
    {
        var sync = new LineageGraph([N("db.orders"), N("db.orders_copy", unknown: true)], [E("db.orders", "db.orders_copy", EdgeKind.Replicates)]);
        var build = new LineageGraph([N("db.orders_copy", unknown: true), N("db.mart")], [E("db.orders_copy", "db.mart")]);
        var onlyBuildKnowsNothing = new LineageGraph([N("db.orders", unknown: true)], []);

        var joined = LineageGraph.Merge([sync, build, onlyBuildKnowsNothing]);

        Assert.Equal(3, joined.Nodes.Count);
        Assert.Equal(2, joined.Edges.Count);
        Assert.True(joined.Node("db.orders_copy")!.UnknownUpstream is false || joined.Upstream("db.orders_copy").Count == 1);
        Assert.Equal(["db.orders_copy"], joined.Downstream("db.orders").Select(e => e.To));
        // a join that makes a loop is refused like any graph
        Assert.Throws<LineageGraphException>(() => LineageGraph.Merge([build, new LineageGraph([N("db.mart"), N("db.orders_copy")], [E("db.mart", "db.orders_copy")])]));
    }

    [Fact]
    public void The_fingerprint_changes_with_anything_a_reader_could_see()
    {
        var one = Fixture().Fingerprint();
        Assert.Equal(one, Fixture().Fingerprint());
        Assert.NotEqual(one, new LineageGraph(Fixture().Nodes, Fixture().Edges.Append(E("audit", "report"))).Fingerprint());
        Assert.NotEqual(one, new LineageGraph(Fixture().Nodes.Select(n => n.Id == "orphan" ? n with { UnknownUpstream = false } : n), Fixture().Edges).Fingerprint());
    }
}
