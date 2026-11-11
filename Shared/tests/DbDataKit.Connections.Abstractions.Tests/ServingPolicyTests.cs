using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Plan 13c D19 (owner decision of 2026-11-09, corrected the same day): a definition may carry the Gateway's serving policy under <c>extensions.gateway</c> except <c>allow_writes</c> and <c>allowed_directories</c>, which are
/// not part of any definition (a publish replaces the whole file on the Gateway). The shared rules: what a definition may carry (<see cref="ServingPolicy.Check"/>) and the text a publish sends.
/// </summary>
public sealed class ServingPolicyTests
{
    private const string Head = "schema: connection/1\nname: warehouse\nengine: duckdb\n";

    private static YamlMapping Gateway(string body) => ServingPolicy.BlockOf(Head + "extensions:\n  gateway:\n" + body)!;

    [Fact]
    public void The_published_fields_pass_and_the_two_blocked_ones_are_named_with_their_line()
    {
        Assert.Empty(ServingPolicy.Check(Gateway("    pass_through: true\n    bitness: x86\n    classifier: strict\n    metadata: auto\n    batch_size: 1000\n    max_concurrent_queries: 4\n    compression:\n      enabled: true\n")));

        var problems = ServingPolicy.Check(Gateway("    batch_size: 1000\n    allow_writes: true\n    allowed_directories: [/data]\n"));
        Assert.Equal(["allow_writes", "allowed_directories"], problems.Select(p => p.Field));
        Assert.All(problems, p => Assert.True(p.IsBlocked));
        Assert.Equal(7, problems[0].At.Line);          // the line of the key in the file above
        Assert.Contains("Build does not author or publish it", problems[0].Message);
    }

    [Fact]
    public void Groups_and_everyone_belong_to_grants_and_an_unknown_key_or_a_value_of_the_wrong_kind_is_named()
    {
        var problems = ServingPolicy.Check(Gateway("    groups: [a]\n    everyone: true\n    colour: red\n    batch_size: many\n    pass_through: maybe\n"));
        Assert.Equal(["groups", "everyone", "colour", "batch_size", "pass_through"], problems.Select(p => p.Field));
        Assert.Contains("`grants`", problems[0].Message);
        Assert.Contains("not a serving policy field", problems[2].Message);
        Assert.All(problems, p => Assert.False(p.IsBlocked));
    }

    [Fact]
    public void The_text_a_publish_sends_is_the_block_the_definition_has_and_loads_back_as_it_was()
    {
        var block = Gateway("    classifier: strict\n    batch_size: 1000\n    compression:\n      enabled: true\n");
        var text = Head + "connection_string: 'Data Source=:memory:'\n" + ServingPolicy.Emit(block);
        Assert.Equal("extensions:\n  gateway:\n    classifier: strict\n    batch_size: 1000\n    compression:\n      enabled: true\n", ServingPolicy.Emit(block));
        var loaded = ConnectionDefinitionLoader.LoadDatabaseConnection(text); Assert.True(loaded.Ok, string.Join("\n", loaded.Diagnostics));
        Assert.Equal(ServingPolicy.Describe(block), ServingPolicy.Describe(ServingPolicy.BlockOf(text)!));
    }
}
