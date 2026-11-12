using DbDataKit.Cli;

namespace DbDataKit.Cli.Tests;

/// <summary>Plan 27a AG01: the rule every command of every product keeps, and the list of what is offered to MCP.</summary>
public sealed class CommandSurfaceTests
{
    private static Task<int> Nothing(string[] args, CliContext context) => Task.FromResult(0);

    private static NounRouter Good() => new("demo",
    [
        new Noun("thing", "Things", [new Verb("show", "Show a thing", EffectClass.OfflineOnly, "<name>", Nothing, DataSchemas.None), new Verb("deploy", "Deploy a thing", EffectClass.TargetWrites, "<name>", Nothing, DataSchemas.None)]),
    ], "1");

    private sealed class Probe : IHealthProbe
    {
        public Task<HealthResult> CheckAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(new HealthResult(true, "ok"));
    }

    [Fact]
    public void A_router_whose_every_verb_has_a_summary_a_usage_a_schema_and_a_trait_passes()
    {
        Assert.Empty(CommandSurface.Audit("demo", Good()));
        Assert.Empty(CommandSurface.Audit("demo", new NounRouter("demo", [HealthNoun.Create(new Probe(), "http://localhost:1/health"), ServerNoun.Create((_, _) => Task.FromResult(0))], "1")));
    }

    [Fact]
    public void A_fake_verb_without_a_schema_a_summary_or_a_usage_fails_naming_the_product_and_the_verb()
    {
        // the router refuses a verb with no schema when it is made: the audit says so with the product's name
        var fake = CommandSurface.Audit("dbdatademo", () => new NounRouter("demo", [new Noun("thing", "Things", [new Verb("fake", "A fake verb", EffectClass.OfflineOnly, "<name>", Nothing)])], "1"));
        Assert.Equal(["dbdatademo: 'thing fake' has no data schema; every verb says what its JSON document holds (DataSchemas.None when it holds nothing)."], fake);

        var router = new NounRouter("demo",
        [
            new Noun("thing", "Things",
            [
                new Verb("silent", "", EffectClass.OfflineOnly, null!, Nothing, DataSchemas.None),
                new Verb("fine", "Fine", EffectClass.OfflineOnly, "", Nothing, DataSchemas.None),
            ]),
        ], "1");

        var problems = CommandSurface.Audit("dbdatademo", router);

        Assert.Contains(problems, p => p.StartsWith("dbdatademo thing silent: has no summary"));
        Assert.Contains(problems, p => p.StartsWith("dbdatademo thing silent: has no usage"));
        Assert.DoesNotContain(problems, p => p.Contains("thing fine"));
    }

    [Fact]
    public void A_verb_that_declares_no_trait_fails()
    {
        var router = new NounRouter("demo", [new Noun("thing", "Things", [new Verb("blind", "Blind", EffectClass.OfflineOnly, "", Nothing, DataSchemas.None, Traits.None)])], "1");
        Assert.Contains(CommandSurface.Audit("demo", router), p => p.Contains("thing blind: declares no trait"));
    }

    [Fact]
    public void The_golden_lists_what_is_offered_with_its_traits_and_changes_when_a_verb_is_added_or_its_traits_change()
    {
        var read = new McpOptions(new HashSet<string>());
        var writes = new McpOptions(new HashSet<string>(), AllowWrites: true);
        Assert.Equal("thing_show  reads-definitions\n", CommandSurface.Golden(Good(), read));          // a change is not offered unless writes are allowed
        Assert.Contains("thing_deploy", CommandSurface.Golden(Good(), writes));

        var added = new NounRouter("demo", [new Noun("thing", "Things", [.. Good().Nouns[0].Verbs, new Verb("list", "List", EffectClass.OfflineOnly, "", Nothing, DataSchemas.None)])], "1");
        Assert.NotEqual(CommandSurface.Golden(Good(), read), CommandSurface.Golden(added, read));
        Assert.Equal("", CommandSurface.Golden(Good(), new McpOptions(new HashSet<string> { "thing show" })));
    }
}
