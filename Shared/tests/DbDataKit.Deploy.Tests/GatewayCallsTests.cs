using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.State;

namespace DbDataKit.Deploy.Tests;

public sealed class GatewayCallsTests
{
    private static readonly DeployTarget Gateway = new("edge", "https://edge:8443", "g1", DeployProducts.Gateway);

    [Fact]
    public async Task A_data_source_s_catalog_is_read_through_the_link_and_no_row_is_asked_for()
    {
        using var link = new FakeLink(Gateway.Url) { Answer = (method, path, _) => (method, path) == ("POST", "data-sources/erp/schema") ? (200, new JsonObject { ["tables"] = new JsonArray(new JsonObject { ["name"] = "orders" }) }) : null };
        var steps = new List<string>();

        var schema = await GatewayCalls.ReadSchemaAsync(link, Gateway, "erp", adopt: false, steps.Add);

        Assert.Equal("orders", (string?)schema["tables"]![0]!["name"]);
        Assert.Null(JsonNode.Parse(link.Calls.Single().Body!)!["adopt"]);
        Assert.Equal(["Asked https://edge:8443 for the tables and columns of the data source 'erp' (no row is read)."], steps);
    }

    [Fact]
    public async Task A_data_source_the_caller_did_not_make_is_refused_with_what_an_adoption_needs()
    {
        using var link = new FakeLink(Gateway.Url)
        {
            Answer = (_, _, _) => (403, new JsonObject { ["error"] = "The data source 'erp' was made by hand.", ["adopt"] = new JsonObject { ["data_source"] = "erp", ["made_by"] = "hand" } }),
        };

        var refused = await Assert.ThrowsAsync<DeployException>(() => GatewayCalls.ReadSchemaAsync(link, Gateway, "erp", adopt: false, _ => { }));

        Assert.Equal((403, "The Gateway refused to give the catalog of 'erp' (403): The data source 'erp' was made by hand."), (refused.Status, refused.Message));
        Assert.Equal("hand", (string?)refused.Adopt!["made_by"]);

        await Assert.ThrowsAsync<DeployException>(() => GatewayCalls.ReadSchemaAsync(link, Gateway, "erp", adopt: true, _ => { }));
        Assert.Equal("erp", (string?)JsonNode.Parse(link.Calls[^1].Body!)!["adopt"]);
    }

    [Fact]
    public async Task The_catalog_of_a_data_source_is_not_read_from_a_runner()
    {
        using var link = new FakeLink();

        var refused = await Assert.ThrowsAsync<DeployException>(() => GatewayCalls.ReadSchemaAsync(link, Gateway with { Product = DeployProducts.Runner }, "erp", false, _ => { }));

        Assert.Equal("'edge' is a runner (https://edge:8443); the catalog of a data source is read from a Gateway.", refused.Message);
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task Each_gateway_s_lineage_is_read_and_one_that_cannot_be_checked_is_named_with_why()
    {
        var answering = new FakeLink("https://a:8443") { Answer = (_, path, _) => path == "lineage" ? (200, LineageJson.Write(LineageGraph.Empty)) : null };
        var failing = new FakeLink("https://b:8443") { Answer = (_, _, _) => (500, new JsonObject { ["error"] = "The lineage store is not open." }) };
        var down = new FakeLink("https://c:8443") { Unreachable = true };
        GatewayLineageSource Source(string name, FakeLink link) => new(name, link.Name, _ => Task.FromResult<IDefinitionsLink>(link));

        var (graphs, unreachable) = await GatewayCalls.LineageAsync(
        [
            Source("a", answering), Source("b", failing), Source("c", down),
            new GatewayLineageSource("d", "https://d:8443", _ => throw new DeployException("There is no publisher key called 'd'.", 409)),
        ]);

        Assert.Equal("a", Assert.Single(graphs).Server);
        Assert.Equal(
            [("b", "it answered 500 The lineage store is not open."), ("c", "https://c:8443 could not be reached (Connection refused)"), ("d", "There is no publisher key called 'd'.")],
            unreachable);
        Assert.True(answering.Disposed && failing.Disposed && down.Disposed);
    }
}
