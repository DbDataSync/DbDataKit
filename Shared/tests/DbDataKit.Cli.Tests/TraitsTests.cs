using System.Text.Json.Nodes;
using DbDataKit.Cli;

namespace DbDataKit.Cli.Tests;

/// <summary>Plan 27a AG02: traits on a verb, the fixed table from the six effect classes, and the MCP offer by traits.</summary>
public sealed class TraitsTests
{
    private static Verb V(string name, EffectClass effect, Traits? traits = null) =>
        new(name, name, effect, "", (a, c) => Task.FromResult(0), DataSchemas.None, traits);

    private static NounRouter Router(params Verb[] verbs) => new("demo", [new Noun("thing", "Things", verbs)], "1");

    [Fact]
    public void The_table_maps_every_effect_class_and_a_class_that_changes_has_a_changing_trait()
    {
        foreach (var effect in Enum.GetValues<EffectClass>())
        {
            var traits = TraitTable.For(effect);
            Assert.NotEqual(Traits.None, traits);
            bool reads = effect is EffectClass.OfflineOnly or EffectClass.TargetReadOnly;
            Assert.Equal(!reads, traits.Changes());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => TraitTable.For((EffectClass)99));
    }

    [Fact]
    public void A_verb_without_traits_has_those_of_its_effect_class_and_the_spec_says_so()
    {
        var router = Router(V("read", EffectClass.TargetReadOnly), V("write", EffectClass.TargetWrites));
        var specs = router.Specs().ToDictionary(s => s.Name);

        Assert.Equal(Traits.ReadsDefinitions | Traits.ReadsData, specs["thing read"].Traits);
        Assert.True(specs["thing write"].Traits.HasFlag(Traits.ChangesStructure));
        Assert.Equal(["reads-definitions", "reads-data"], specs["thing read"].Traits.Names());
    }

    [Fact]
    public void A_verb_may_add_traits_but_not_drop_the_changing_ones_its_effect_class_implies()
    {
        Router(V("send", EffectClass.OfflineOnly, Traits.ReadsDefinitions | Traits.Outward));

        var ex = Assert.Throws<ArgumentException>(() => Router(V("lie", EffectClass.RepoFilesOnly, Traits.ReadsDefinitions)));
        Assert.Contains("thing lie", ex.Message);
        Assert.Contains("writes-definitions", ex.Message);
    }

    [Fact]
    public void A_verb_with_outward_is_not_offered_without_allow_writes_even_when_its_effect_class_reads()
    {
        var router = Router(V("show", EffectClass.OfflineOnly), V("notify", EffectClass.OfflineOnly, Traits.ReadsDefinitions | Traits.Outward));

        Assert.Equal(["thing_show"], new McpServer(router, new McpOptions(new HashSet<string>()), "demo", "1").Tools.Select(t => t.Name));
        Assert.Equal(["thing_show", "thing_notify"], new McpServer(router, new McpOptions(new HashSet<string>(), AllowWrites: true), "demo", "1").Tools.Select(t => t.Name));
    }

    [Fact]
    public void A_verb_that_reads_data_is_left_out_for_an_agent_whose_policy_refuses_data()
    {
        var router = Router(V("meta", EffectClass.OfflineOnly), V("rows", EffectClass.TargetReadOnly));

        Assert.Equal(["thing_meta", "thing_rows"], new McpServer(router, new McpOptions(new HashSet<string>()), "demo", "1").Tools.Select(t => t.Name));
        Assert.Equal(["thing_meta"], new McpServer(router, new McpOptions(new HashSet<string>(), RefuseData: true), "demo", "1").Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task The_tool_list_carries_the_traits_of_each_tool()
    {
        var server = new McpServer(Router(V("rows", EffectClass.TargetReadOnly)), new McpOptions(new HashSet<string>()), "demo", "1");
        var reply = (JsonObject)(await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)).Single();
        var tool = reply["result"]!["tools"]![0]!;

        Assert.Equal(["reads-definitions", "reads-data"], tool["annotations"]!["traits"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(Traits.ReadsData, TraitTable.Parse("reads-data"));
        Assert.Null(TraitTable.Parse("nonsense"));
    }
}
