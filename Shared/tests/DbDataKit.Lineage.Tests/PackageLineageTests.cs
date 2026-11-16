using DbDataKit.Connections;

namespace DbDataKit.Lineage.Tests;

/// <summary>Plan 18 G18: the design lineage a Runner package carries, its tables named by their slot until the Runner names them by the connection the channel binds.</summary>
public class PackageLineageTests
{
    private static readonly ConnectionDefinition Slot = PackageLineage.SlotConnection("wh", "sqlserver");
    private static readonly ConnectionDefinition Warehouse = new() { Name = "warehouse", Engine = "sqlserver", Address = new AddressSpec { Host = "WH.example", Database = "dw" } };

    private static LineageGraph Design()
    {
        var raw = LineageIdentity.Dataset(Slot, "dbo", "raw");
        var orders = LineageIdentity.Dataset(Slot, "dbo", "orders");
        var rawId = LineageIdentity.Column(Slot, raw, "amount");
        var amount = LineageIdentity.Column(Slot, orders, "amount");
        return new LineageGraph(
            [
                new LineageNode(raw.ToString(), NodeKind.Dataset, "dbo.raw", UnknownUpstream: true),
                new LineageNode(orders.ToString(), NodeKind.Dataset, "dbo.orders"),
                new LineageNode(rawId, NodeKind.Column, "amount", UnknownUpstream: true),
                new LineageNode(amount, NodeKind.Column, "amount"),
            ],
            [
                new LineageEdge(raw.ToString(), orders.ToString(), EdgeKind.Derives, Granularity.Table, Confidence.Derived),
                new LineageEdge(rawId, amount, EdgeKind.Derives, Granularity.Column, Confidence.Derived, "sum"),
            ]);
    }

    [Fact]
    public void A_document_is_written_the_same_every_time_and_read_back_and_another_schema_is_refused_naming_the_file()
    {
        var text = PackageLineage.Write(Design());
        Assert.Equal(text, PackageLineage.Write(Design()));
        Assert.DoesNotContain("\r", text);
        Assert.Contains("\"schema\": \"package-lineage/1\"", text);
        Assert.Equal(Design().Fingerprint(), PackageLineage.Read(text, "x").Fingerprint());

        var newer = Assert.Throws<LineageGraphException>(() => PackageLineage.Read(text.Replace("package-lineage/1", "package-lineage/2"), "lineage/design.json of p v3"));
        Assert.Contains("lineage/design.json of p v3", newer.Message);
        Assert.Contains("package-lineage/2", newer.Message);
        Assert.Contains("lineage/design.json", Assert.Throws<LineageGraphException>(() => PackageLineage.Read("{}", "lineage/design.json")).Message);
    }

    [Fact]
    public void A_table_named_by_its_slot_is_named_by_the_bound_connection_and_an_unbound_slot_is_said_and_left_apart()
    {
        Assert.Equal("wh", PackageLineage.SlotOf(LineageIdentity.Dataset(Slot, "dbo", "orders").ToString()));
        Assert.Null(PackageLineage.SlotOf(LineageIdentity.Dataset(Warehouse, "dbo", "orders").ToString()));

        var problems = new List<string>();
        var resolved = PackageLineage.Resolve(Design(), slot => slot == "wh" ? Warehouse : null, problems);
        Assert.Empty(problems);
        var orders = LineageIdentity.Dataset(Warehouse, "dbo", "orders");
        Assert.NotNull(resolved.Node(orders.ToString()));
        Assert.NotNull(resolved.Node(LineageIdentity.Column(Warehouse, orders, "amount")));
        Assert.Contains(resolved.Edges, e => e.To == orders.ToString() && e.From == LineageIdentity.Dataset(Warehouse, "dbo", "raw").ToString());
        Assert.DoesNotContain(resolved.Nodes, n => n.Id.Contains("@", StringComparison.Ordinal));

        var unbound = PackageLineage.Resolve(Design(), _ => null, problems);
        Assert.Contains("`wh`", Assert.Single(problems));
        Assert.Equal(Design().Fingerprint(), unbound.Fingerprint());
    }

    [Fact]
    public void The_newest_run_that_wrote_a_table_is_laid_on_the_edges_into_it_and_its_columns()
    {
        var graph = Design();
        var orders = LineageIdentity.Dataset(Slot, "dbo", "orders").ToString();
        var at = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var runs = PackageLineage.Runs(graph, [(orders, at, RunOutcome.Failed), (orders, at.AddHours(1), RunOutcome.Succeeded), ("sqlserver://elsewhere/x", at, RunOutcome.Failed)]);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal((at.AddHours(1), RunOutcome.Succeeded), (r.At, r.Outcome)));
        Assert.Contains(runs, r => r.To == orders);
        Assert.Contains(runs, r => r.To.EndsWith("#amount", StringComparison.Ordinal));
    }
}
