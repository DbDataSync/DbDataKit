using System.Text.Json.Nodes;
using DbDataBuild.Models;
using DbDataKit.Connections.Descriptor;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 14a SL03: Build's engine keywords are the engines the suite's registry says Build builds on or reads. Adapter first: the lists stay Build's own
/// (their order is in messages and goldens), and these tests fail when a list and the registry disagree, naming the engine.
/// </summary>
public class EngineRegistryBuildTests
{
    private static readonly string[] BuildRoles = [EngineRoles.BuildTarget, EngineRoles.BuildOrigin];

    [Fact]
    public void The_drivers_of_engine_are_exactly_the_engines_the_registry_says_build_builds_on_or_reads()
    {
        var registry = ConnectionKeywords.RegistryEngines.Order(StringComparer.Ordinal).ToList();
        var drivers = ConnectionKeywords.Drivers.Order(StringComparer.Ordinal).ToList();
        foreach (var id in drivers.Except(registry)) Assert.Fail($"{id}: Build lists it as a driver, but the registry gives it no build_target or build_origin role (engines/{id}.engine.yaml).");
        foreach (var id in registry.Except(drivers)) Assert.Fail($"{id}: the registry says Build can use it, but ConnectionKeywords.Drivers lacks it.");
    }

    [Fact]
    public void The_connection_schemas_engine_list_equals_the_registrys_and_a_driver_is_built_exactly_when_the_registry_says_so()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(PolyglotBindingTests.RepoRoot(), "schemas", "connection.schema.json")))!;
        var listed = schema["properties"]!["engine"]!["enum"]!.AsArray().Select(n => (string)n!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(ConnectionKeywords.RegistryEngines.Order(StringComparer.Ordinal), listed);
        foreach (var engine in EngineRegistry.Builtin.Engines)
        {
            var built = BuildRoles.Any(r => engine.Role(r).Status >= EngineSupport.Unverified);
            Assert.True(ConnectionKeywords.IsBuilt(engine.Id) == built, $"{engine.Id}: IsBuilt says {ConnectionKeywords.IsBuilt(engine.Id)}, the registry says {built}.");
        }
    }

    [Fact]
    public void The_build_targets_of_the_registry_are_the_engines_the_tool_builds_tables_on()
    {
        var targets = EngineRegistry.Builtin.Supporting(EngineRoles.BuildTarget, EngineSupport.Unverified).Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(DbDataBuild.Models.TargetNames.All.Order(StringComparer.Ordinal), targets);
    }

    [Theory]
    [InlineData("oracle", "Oracle Database")]
    [InlineData("mysql", "MySQL")]
    public void An_engine_the_registry_knows_and_build_does_not_use_is_refused_with_the_registrys_words(string word, string display)
    {
        var (kind, error, _) = ConnectionKeywords.Parse([word], new HashSet<string>());
        Assert.Null(kind);
        Assert.Contains(display, error);
        Assert.Contains("does not build on or read yet", error);
    }

    [Fact]
    public void A_word_that_is_no_engine_keeps_the_old_message()
    {
        var (_, error, _) = ConnectionKeywords.Parse(["nosuch"], new HashSet<string>());
        Assert.Equal("`nosuch` is not a keyword of `engine`.", error);
    }
}
