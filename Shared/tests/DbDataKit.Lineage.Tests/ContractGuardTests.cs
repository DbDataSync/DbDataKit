namespace DbDataKit.Lineage.Tests;

/// <summary>Plan 21 CH06: the contract step asks the lineage graph for published consumers in every channel and refuses, naming each and its channel.</summary>
public class ContractGuardTests
{
    private const string Table = "sqlserver://dw/sales.orders";
    private const string Price = Table + "#price";
    private const string Qty = Table + "#qty";

    private static LineageNode Consumer(string name, string kind, string? channel = null) =>
        new($"suite://g/{kind}/{name}/{channel}", NodeKind.Consumer, name, false, channel is null ? new Dictionary<string, string> { ["consumer"] = kind } : new Dictionary<string, string> { ["consumer"] = kind, [ContractGuard.ChannelProperty] = channel });

    private static LineageGraph Graph(out LineageNode dashboardProd, out LineageNode dashboardTest, out LineageNode report)
    {
        dashboardProd = Consumer("sales", "dashboard");
        dashboardTest = Consumer("sales", "dashboard", "test");
        report = Consumer("table-only", "report");
        var member = new LineageNode("suite://g/semantic_member/sales: revenue", NodeKind.SemanticMember, "sales: revenue");
        LineageNode[] nodes = [new(Table, NodeKind.Dataset, "sales.orders"), new(Price, NodeKind.Column, "price"), new(Qty, NodeKind.Column, "qty"), member, new("suite://g/serving/sales", NodeKind.Serving, "sales"), dashboardProd, dashboardTest, report];
        LineageEdge[] edges =
        [
            new(Price, member.Id, EdgeKind.Binds, Granularity.Column, Confidence.Derived),
            new(member.Id, "suite://g/serving/sales", EdgeKind.Serves, Granularity.Column, Confidence.Derived),
            new("suite://g/serving/sales", dashboardProd.Id, EdgeKind.Reads, Granularity.Table, Confidence.Declared),
            new("suite://g/serving/sales", dashboardTest.Id, EdgeKind.Reads, Granularity.Table, Confidence.Declared),
            new(Table, report.Id, EdgeKind.Reads, Granularity.Table, Confidence.Declared),
        ];
        return new LineageGraph(nodes, edges);
    }

    [Fact]
    public void A_column_a_published_consumer_reads_is_refused_naming_each_consumer_and_its_channel()
    {
        var graph = Graph(out _, out _, out _);
        var consumers = ContractGuard.Consumers(graph, [Price], Table);
        Assert.Equal([("sales", "prod"), ("sales", "test"), ("sales: revenue", "prod"), ("table-only", "prod")], consumers.Select(c => (c.Name, c.Channel)));
        var refusal = ContractGuard.Refusal("drop the column 'price' of 'sales.orders'", consumers)!;
        Assert.Contains("the dashboard 'sales' published in channel 'test'", refusal);
        Assert.Contains("the dashboard 'sales' published in channel 'prod'", refusal);
        Assert.Contains("the model member 'sales: revenue'", refusal);
        Assert.Contains("reads sales.orders as a table", ContractGuard.Consumers(graph, [Price], Table).Single(c => c.Name == "table-only").Via + " ");
    }

    [Fact]
    public void A_column_nobody_binds_has_only_the_table_readers_and_no_refusal_without_any()
    {
        var graph = Graph(out _, out _, out _);
        Assert.Equal(["table-only"], ContractGuard.Consumers(graph, [Qty], Table).Select(c => c.Name));
        Assert.Empty(ContractGuard.Consumers(graph, [Qty]));            // without the table, a column with no edges has no consumers
        Assert.Empty(ContractGuard.Consumers(graph, ["no-such-column"]));
        Assert.Null(ContractGuard.Refusal("drop the column", []));
    }

    [Fact]
    public void After_the_consumer_is_republished_without_the_column_the_change_goes_through()
    {
        var graph = Graph(out var prod, out var test, out var report);
        // the republished graph: the member no longer binds the price, and the dashboards and the report no longer read the table
        var republished = new LineageGraph(graph.Nodes.Where(n => n.Id != "suite://g/semantic_member/sales: revenue"), graph.Edges.Where(e => e.To != prod.Id && e.To != test.Id && e.To != report.Id && e.To != "suite://g/semantic_member/sales: revenue" && e.From != "suite://g/semantic_member/sales: revenue"));
        Assert.Empty(ContractGuard.Consumers(republished, [Price], Table));
    }

    [Fact]
    public void Check_refuses_naming_package_and_channel_and_the_escape_that_allows_each()
    {
        var graph = Graph(out _, out _, out _);
        var drop = new ContractedColumn("drop the column 'price' of 'sales.orders'", Price, Table);
        var verdict = ContractGuard.Check([drop], [("gw", graph)], [], []);
        Assert.False(verdict.Allowed);
        var refusal = Assert.Single(verdict.Refusals);
        Assert.Contains("the dashboard 'sales' published in channel 'test'", refusal);
        Assert.Contains("--allow-consumer sales@test", refusal);
        Assert.Empty(verdict.AllowancesUsed);
    }

    [Fact]
    public void An_allowance_by_name_and_channel_lets_only_that_consumer_through_and_is_reported()
    {
        var graph = Graph(out _, out _, out _);
        var drop = new ContractedColumn("drop the column 'price' of 'sales.orders'", Price, Table);
        var partial = ContractGuard.Check([drop], [("gw", graph)], [], ["sales@test", "sales@prod"]);
        var refusal = Assert.Single(partial.Refusals);
        Assert.DoesNotContain("dashboard 'sales' published", refusal);
        Assert.Contains("table-only", refusal);
        var all = ContractGuard.Check([drop], [("gw", graph)], [], ["sales", "sales: revenue", "table-only"]);
        Assert.True(all.Allowed);
        Assert.Equal(["sales", "sales: revenue", "table-only"], all.AllowancesUsed);
    }

    [Fact]
    public void A_paired_server_that_did_not_answer_refuses_the_step_until_it_is_allowed_by_name()
    {
        var drop = new ContractedColumn("drop the column 'price' of 'sales.orders'", Price, Table);
        var refused = ContractGuard.Check([drop], [], [("gw", "it could not be reached")], [], "allow_consumer=");
        Assert.Contains("'gw' did not give its lineage", Assert.Single(refused.Refusals));
        Assert.Contains("allow_consumer= gateway:gw", refused.Refusals[0]);
        var allowed = ContractGuard.Check([drop], [], [("gw", "it could not be reached")], ["gateway:gw"]);
        Assert.True(allowed.Allowed);
        Assert.Equal(["gateway:gw"], allowed.AllowancesUsed);
        // no change, nothing to ask: an unreachable server is no reason to refuse
        Assert.True(ContractGuard.Check([], [], [("gw", "x")], []).Allowed);
    }
}
