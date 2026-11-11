using System.Text.Json.Nodes;

namespace DbDataKit.Lineage.Tests;

public class LineageJsonTests
{
    private static LineageGraph Sample() => new(
        [
            new LineageNode("sqlserver://crm/dbo.customers", NodeKind.Dataset, "dbo.customers", UnknownUpstream: true, new Dictionary<string, string> { ["connection"] = "crm" }),
            new LineageNode("postgres://dwh/public.customers", NodeKind.Dataset, "public.customers"),
        ],
        [new LineageEdge("sqlserver://crm/dbo.customers", "postgres://dwh/public.customers", EdgeKind.Replicates, Granularity.Table, Confidence.Declared, "r/customers")]);

    [Fact]
    public void A_graph_is_written_and_read_back_the_same()
    {
        var graph = Sample();

        var back = LineageJson.Read(JsonNode.Parse(LineageJson.Write(graph).ToJsonString()));

        Assert.Equal(graph.Fingerprint(), back.Fingerprint());
        Assert.Equal("crm", back.Node("sqlserver://crm/dbo.customers")!.PropertyMap["connection"]);
        Assert.Equal("replicates", (string)LineageJson.Write(graph)["edges"]![0]!["kind"]!);
    }

    [Theory]
    [InlineData("""{"nodes": [], "edges": 3}""", "`nodes` and `edges` lists")]
    [InlineData("""{"nodes": [{"id": "a", "kind": "table", "name": "a"}], "edges": []}""", "nodes[0].kind is `table`")]
    [InlineData("""{"nodes": [{"id": "a", "kind": "dataset", "name": "a"}], "edges": [{"from": "a", "to": "b", "kind": "derives", "granularity": "table", "confidence": "derived"}]}""", "ends at a node the graph does not have")]
    [InlineData("""{"nodes": [{"id": "a", "kind": "dataset"}], "edges": []}""", "nodes[0] has no `name`")]
    public void A_document_of_another_shape_is_refused_saying_where(string json, string expected)
    {
        var ex = Assert.Throws<LineageGraphException>(() => LineageJson.Read(JsonNode.Parse(json)));

        Assert.Contains(expected, ex.Message);
    }
}
