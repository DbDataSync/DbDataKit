using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using Json.Schema;

namespace DbDataKit.Access.Tests;

/// <summary>Plan 22a GP01: the JSON Schema agrees with the loader (`SchemaAgreement`): what the loader reads the schema accepts, and the keys and choices are the same ones.</summary>
public sealed class AccessSchemaTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(AccessSchema.Text());
    private static readonly JsonNode Document = JsonNode.Parse(AccessSchema.Text())!;

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMapping m => new JsonObject(m.Entries.Select(e => KeyValuePair.Create<string, JsonNode?>(e.Key.Value, ToJson(e.Value)))),
        YamlSequence s => new JsonArray(s.Items.Select(ToJson).ToArray()),
        YamlScalar sc => JsonValue.Create(sc.Value),
        _ => null,
    };

    private static bool SchemaAccepts(string yaml)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, "x.yaml", diagnostics)!;
        Assert.Empty(diagnostics);
        using var document = JsonDocument.Parse(ToJson(root)!.ToJsonString());
        return Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    [Fact]
    public void What_the_loader_reads_the_schema_accepts()
    {
        Assert.NotNull(AccessSectionTests.Read(AccessSectionTests.Complete, out var diagnostics, Fakes.Target()));
        Assert.Empty(diagnostics);
        Assert.True(SchemaAccepts(AccessSectionTests.Complete));
        Assert.True(SchemaAccepts("max_age: 1h\n"));
    }

    [Theory]
    [InlineData("max_age: 0h\n")]
    [InlineData("max_age: 32\n")]
    [InlineData("typo: 1\n")]
    [InlineData("row:\n  - dataset: sales\n    groups: []\n")]
    [InlineData("row:\n  - dataset: sales\n    match: some\n    groups:\n      - { id: g, rules: [ { id: r, effect: include, table: t, subject: s, columns: { a: b } } ] }\n")]
    [InlineData("row:\n  - dataset: sales\n    groups:\n      - { id: g, rules: [ { id: r, effect: allow, table: t, subject: s, columns: { a: b } } ] }\n")]
    [InlineData("row:\n  - dataset: sales\n    groups:\n      - { id: g, rules: [ { id: r, effect: include, table: t, subject: s, columns: {} } ] }\n")]
    [InlineData("row:\n  - dataset: sales\n    groups:\n      - { id: g, rules: [ { id: r, effect: include, table: t, subject: s } ] }\n")]
    [InlineData("row:\n  - dataset: sales\n    groups:\n      - { id: 1, rules: [ { id: r, effect: include, table: t, subject: s, columns: { a: b } } ] }\n")]
    [InlineData("columns:\n  - { subject: s, dataset: d, column: c, treatment: blur }\n")]
    [InlineData("columns:\n  - { subject: s, dataset: d, column: c, treatment: mask, mask: rot13 }\n")]
    [InlineData("columns:\n  - { subject: s, dataset: d, column: c }\n")]
    public void What_the_schema_refuses_the_loader_refuses(string yaml)
    {
        Assert.False(SchemaAccepts(yaml));
        Assert.Null(AccessSectionTests.Read(yaml, out var diagnostics));
        Assert.NotEmpty(diagnostics);
    }

    private static IReadOnlyList<string> Keys(string def) => [.. ((JsonObject)Document["$defs"]![def]!["properties"]!).Select(p => p.Key).Order()];

    private static IReadOnlyList<string> Choices(string def, string property) =>
        [.. ((JsonArray)(Document["$defs"]![def]!["properties"]![property]!["enum"] ?? Document["$defs"]!["accessSubjectKind"]!["enum"] ?? Document["$defs"]!["accessMatch"]!["enum"])!).Select(n => n?.GetValue<string>() ?? "null").Distinct()];

    [Fact]
    public void The_keys_and_choices_are_the_loaders()
    {
        Assert.Equal(["columns", "max_age", "row"], ((JsonObject)Document["$defs"]!["access"]!["properties"]!).Select(p => p.Key).Order());
        Assert.Equal(["dataset", "groups", "match"], Keys("accessDataset"));
        Assert.Equal(["id", "match", "rules"], Keys("accessGroup"));
        Assert.Equal(["columns", "effect", "id", "max_age", "subject", "subject_kind", "table"], Keys("accessRule"));
        Assert.Equal(["column", "dataset", "mask", "subject", "subject_kind", "treatment"], Keys("accessColumnRule"));
        Assert.Equal(["include", "exclude"], Choices("accessRule", "effect"));
        Assert.Equal(["hide", "mask", "null"], Choices("accessColumnRule", "treatment"));
        Assert.Equal(["fixed", "hash"], Choices("accessColumnRule", "mask"));
        Assert.Equal(["all", "any"], ((JsonArray)Document["$defs"]!["accessMatch"]!["enum"]!).Select(n => n!.GetValue<string>()));
        Assert.Equal(["group", "person"], ((JsonArray)Document["$defs"]!["accessSubjectKind"]!["enum"]!).Select(n => n!.GetValue<string>()));
        Assert.Equal(Enum.GetNames<AccessEffect>().Select(n => n.ToLowerInvariant()), Choices("accessRule", "effect"));
        Assert.Equal(Enum.GetNames<ColumnTreatment>().Select(n => n.ToLowerInvariant()), Choices("accessColumnRule", "treatment"));
        Assert.Equal(Enum.GetNames<MaskKind>().Select(n => n.ToLowerInvariant()), Choices("accessColumnRule", "mask"));
    }
}
