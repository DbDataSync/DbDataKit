using DbDataKit.State;

namespace DbDataKit.Lineage.Tests;

// plan 23a FQ16: nodes carry the status of their checks and impact analysis lists what a block stops
public class LineageQualityTests
{
    private const string Source = "sqlserver://erp/Sales.dbo.Orders";
    private const string Target = "postgres://dw/dw.public.orders";
    private const string Model = "postgres://dw/dw.public.orders_by_day";
    private const string Served = "suite://gw1/serving/orders_by_day";
    private const string Unrelated = "postgres://dw/dw.public.customers";
    private static readonly DateTimeOffset Since = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static LineageGraph Graph() => new(
    [
        new(Source, NodeKind.Dataset, "Orders"), new(Target, NodeKind.Dataset, "orders"), new(Target + "#id", NodeKind.Column, "id"),
        new(Model, NodeKind.Dataset, "orders_by_day"), new(Served, NodeKind.Serving, "orders_by_day"), new(Unrelated, NodeKind.Dataset, "customers"),
    ],
    [
        new(Source, Target, EdgeKind.Replicates, Granularity.Table, Confidence.Derived),
        new(Target, Target + "#id", EdgeKind.Derives, Granularity.Column, Confidence.Derived),
        new(Target, Model, EdgeKind.Derives, Granularity.Table, Confidence.Derived),
        new(Model, Served, EdgeKind.Serves, Granularity.Table, Confidence.Derived),
    ]);

    private static CheckStatusRow Row(string dataset, string id, CheckOutcome outcome, bool blocking, string severity = "block") =>
        new(id, dataset, severity, "alert", outcome, Since, Guid.NewGuid(), outcome == CheckOutcome.Fail ? 3 : 0, 0, Since.AddHours(1), blocking, blocking ? Since : null);

    [Fact]
    public void A_node_carries_the_worst_status_of_its_checks_and_a_node_without_checks_or_an_unknown_dataset_is_untouched()
    {
        var graph = Graph().WithChecks(
        [
            Row(Target, "not-null", CheckOutcome.Fail, blocking: true), Row(Target, "unique", CheckOutcome.Pass, blocking: false),
            Row(Model, "range", CheckOutcome.Fail, blocking: false, severity: "warn"), Row(Source, "fresh", CheckOutcome.Error, blocking: false),
            Row(Unrelated, "ok", CheckOutcome.Pass, blocking: false), Row("postgres://dw/dw.public.not_in_the_graph", "x", CheckOutcome.Fail, blocking: true),
        ]);
        string P(string id, string key) => graph.Node(id)!.PropertyMap.GetValueOrDefault(key) ?? "";

        Assert.Equal("blocked", P(Target, LineageQuality.StatusProperty));
        Assert.Equal("2", P(Target, LineageQuality.CountProperty));
        Assert.Equal("1", P(Target, LineageQuality.FailingProperty));
        Assert.Equal("not-null", P(Target, LineageQuality.BlockingIdsProperty));
        Assert.Equal(Since.ToString("O"), P(Target, LineageQuality.BlockedSinceProperty));
        Assert.Equal("fail", P(Model, LineageQuality.StatusProperty));
        Assert.Equal("error", P(Source, LineageQuality.StatusProperty));
        Assert.Equal("pass", P(Unrelated, LineageQuality.StatusProperty));
        Assert.Equal("", P(Served, LineageQuality.StatusProperty));
        Assert.Equal(Graph().Nodes.Count, graph.Nodes.Count);
        Assert.Equal(Graph().Edges.Count, graph.Edges.Count);
    }

    [Fact]
    public void The_impact_of_a_blocked_node_lists_the_dependents_it_stops_without_its_columns_and_a_warn_stops_nothing()
    {
        var graph = Graph().WithChecks([Row(Target, "not-null", CheckOutcome.Fail, blocking: true), Row(Model, "range", CheckOutcome.Fail, blocking: false, severity: "warn")]);

        var block = Assert.Single(graph.Blocks());
        Assert.Equal(Target, block.Blocked.Id);
        Assert.Equal(["not-null"], block.Checks);
        Assert.Equal(Since, block.Since);
        Assert.Equal([Model, Served], block.Stops.Select(s => s.Node.Id).ToArray());
        Assert.Equal([1, 2], block.Stops.Select(s => s.Depth).ToArray());
    }

    [Fact]
    public void Clearing_the_block_clears_what_it_stops_and_nothing_is_blocked_in_a_graph_without_results()
    {
        Assert.Empty(Graph().Blocks());
        var cleared = Graph().WithChecks([Row(Target, "not-null", CheckOutcome.Pass, blocking: false)]);
        Assert.Empty(cleared.Blocks());
        Assert.Equal("pass", cleared.Node(Target)!.PropertyMap[LineageQuality.StatusProperty]);
    }

    [Fact]
    public void The_wire_form_carries_the_status_and_reads_back()
    {
        var graph = Graph().WithChecks([Row(Target, "not-null", CheckOutcome.Fail, blocking: true)]);
        var json = LineageJson.Write(graph);
        var node = json["nodes"]!.AsArray().Single(n => (string)n!["id"]! == Target)!;
        Assert.Equal("blocked", (string)node["properties"]![LineageQuality.StatusProperty]!);
        Assert.Equal("not-null", (string)node["properties"]![LineageQuality.BlockingIdsProperty]!);
        var back = LineageJson.Read(json);
        Assert.Equal("blocked", back.Node(Target)!.PropertyMap[LineageQuality.StatusProperty]);
        Assert.Single(back.Blocks());
    }
}
