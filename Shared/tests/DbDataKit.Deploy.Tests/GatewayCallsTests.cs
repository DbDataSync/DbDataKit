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

    [Fact]
    public async Task What_each_workspace_key_published_is_handed_to_the_console_one_call_per_client_and_another_client_is_left_alone()
    {
        using var link = new FakeLink(Gateway.Url)
        {
            Answer = (method, path, body) => (method, path) switch
            {
                ("GET", "clients") => (200, new JsonObject { ["clients"] = new JsonArray(new JsonObject { ["id"] = "workspace-0a1b", ["name"] = "dbdataworkspace@ws1" }, new JsonObject { ["id"] = "build-1", ["name"] = "a Build server" }) }),
                ("PUT", "settings/handovers/workspace-0a1b") => (200, new JsonObject
                {
                    ["handover"] = new JsonObject { ["from"] = "workspace-0a1b", ["to"] = (string?)JsonNode.Parse(body!)!["to"] }, ["unchanged"] = false,
                    ["definitions"] = new JsonArray(new JsonObject { ["kind"] = "data-source", ["name"] = "erp" }),
                }),
                _ => null,
            },
        };
        var steps = new List<string>();

        var done = await GatewayCalls.HandOverAsync(link, Gateway, "console-1", null, steps.Add);

        Assert.Equal(["GET clients", "PUT settings/handovers/workspace-0a1b"], link.Calls.Select(c => $"{c.Method} {c.Path}"));
        Assert.Equal("console-1", (string?)JsonNode.Parse(link.Calls[1].Body!)!["to"]);
        Assert.Equal("erp", (string?)done["handovers"]![0]!["definitions"]![0]!["name"]);
        Assert.Equal(["edge handed what 'workspace-0a1b' owns to 'console-1': 1 definition(s)."], steps);
    }

    [Fact]
    public async Task A_hand_over_the_gateway_refuses_comes_back_in_its_words_and_a_gateway_with_no_workspace_key_has_nothing_to_hand_over()
    {
        using var refusing = new FakeLink(Gateway.Url) { Answer = (_, _, _) => (403, new JsonObject { ["error"] = "This call is refused: this needs the admin role." }) };
        var refused = await Assert.ThrowsAsync<DeployException>(() => GatewayCalls.HandOverAsync(refusing, Gateway, "console-1", ["workspace-0a1b"], _ => { }));
        Assert.Equal((403, "edge refused to hand what 'workspace-0a1b' owns to 'console-1' (403): This call is refused: this needs the admin role."), (refused.Status, refused.Message));

        using var none = new FakeLink(Gateway.Url) { Answer = (_, path, _) => path == "clients" ? (200, new JsonObject { ["clients"] = new JsonArray() }) : null };
        var steps = new List<string>();
        var done = await GatewayCalls.HandOverAsync(none, Gateway, "console-1", null, steps.Add);
        Assert.Empty(done["handovers"]!.AsArray());
        Assert.Equal(["edge has no client of a Workspace server's publisher key, so there is nothing to hand over."], steps);

        var runner = await Assert.ThrowsAsync<DeployException>(() => GatewayCalls.HandOverAsync(none, Gateway with { Product = DeployProducts.Runner }, "console-1", null, _ => { }));
        Assert.Contains("is handed over on a Gateway", runner.Message);
    }
}
