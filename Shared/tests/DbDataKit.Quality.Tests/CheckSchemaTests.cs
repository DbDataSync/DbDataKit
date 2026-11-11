using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections;
using DbDataKit.Lineage;
using Json.Schema;

namespace DbDataKit.Quality.Tests;

/// <summary>Plan 23a FQ01: the JSON Schema agrees with the loader, and the identities a check names are the ones the lineage graph makes (D3).</summary>
public sealed class CheckSchemaTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(CheckSchema.Text());
    private static readonly JsonNode SchemaDocument = JsonNode.Parse(CheckSchema.Text())!;

    /// <summary>The way the suite reads YAML as JSON (the semantic catalog does the same): plain true, false and numbers are typed, the rest is text.</summary>
    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMapping m => new JsonObject(m.Entries.Select(e => KeyValuePair.Create<string, JsonNode?>(e.Key.Value, ToJson(e.Value)))),
        YamlSequence s => new JsonArray(s.Items.Select(ToJson).ToArray()),
        YamlScalar { Quoted: false, Value: "true" or "false" } b => JsonValue.Create(b.Value == "true"),
        YamlScalar { Quoted: false } sc when decimal.TryParse(sc.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
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

    private static string File(string entries) => "schema: check/1\nchecks:\n" + entries;

    public static TheoryData<string> Valid => new()
    {
        "  - { id: a, kind: not_null, dataset: orders, column: c }",
        "  - { id: a, kind: not_null, dataset: orders, columns: [c, d], severity: block, on_error: block, description: text }",
        "  - { id: a, kind: unique, dataset: orders, columns: [c, d] }",
        "  - { id: a, kind: accepted_values, dataset: orders, column: c, values: [x, y, 3, true] }",
        "  - { id: a, kind: range, dataset: orders, column: c, min: 0 }",
        "  - { id: a, kind: range, dataset: orders, column: c, min: -1.5, max: 10 }",
        "  - { id: a, kind: range, dataset: orders, column: c, min: 2026-01-01, max: 2026-12-31T23:59:59Z }",
        "  - { id: a, kind: referential, dataset: orders, column: c, references: { dataset: customers, column: id } }",
        "  - { id: a, kind: referential, dataset: orders, columns: [c, d], references: { dataset: 'postgres://h/db.public.t', columns: [x, y] } }",
        "  - { id: a, kind: freshness, dataset: orders, max_age: 6h }",
        "  - { id: a, kind: freshness, dataset: orders, column: loaded_at, max_age: 1d }",
        "  - { id: a, kind: row_count_change, dataset: orders, max_increase_percent: 50 }",
        "  - { id: a, kind: row_count_change, dataset: orders, max_decrease_percent: 20.5, max_increase_percent: 0 }",
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void What_the_loader_reads_the_schema_accepts(string entry)
    {
        Assert.True(CheckLoader.Load(File(entry), "x.yaml").Ok);
        Assert.True(SchemaAccepts(File(entry)), "the schema refuses what the loader reads");
    }

    public static TheoryData<string> Invalid => new()
    {
        "  - { id: a, kind: sql, dataset: orders, column: c }",
        "  - { id: a, dataset: orders, column: c }",
        "  - { kind: not_null, dataset: orders, column: c }",
        "  - { id: 9a, kind: not_null, dataset: orders, column: c }",
        "  - { id: a, kind: not_null, dataset: orders }",
        "  - { id: a, kind: not_null, dataset: orders, column: c, columns: [d] }",
        "  - { id: a, kind: not_null, dataset: orders, column: c, severity: critical }",
        "  - { id: a, kind: not_null, dataset: orders, column: c, on_error: ignore }",
        "  - { id: a, kind: not_null, dataset: orders, column: c, max_age: 6h }",
        "  - { id: a, kind: accepted_values, dataset: orders, column: c }",
        "  - { id: a, kind: accepted_values, dataset: orders, column: c, values: [] }",
        "  - { id: a, kind: range, dataset: orders, column: c }",
        "  - { id: a, kind: range, dataset: orders, column: c, min: abc }",
        "  - { id: a, kind: range, dataset: orders, columns: [c], min: 1 }",
        "  - { id: a, kind: referential, dataset: orders, column: c }",
        "  - { id: a, kind: referential, dataset: orders, column: c, references: { column: id } }",
        "  - { id: a, kind: referential, dataset: orders, column: c, references: { dataset: t } }",
        "  - { id: a, kind: freshness, dataset: orders }",
        "  - { id: a, kind: freshness, dataset: orders, max_age: 0h }",
        "  - { id: a, kind: freshness, dataset: orders, max_age: 6w }",
        "  - { id: a, kind: row_count_change, dataset: orders }",
        "  - { id: a, kind: row_count_change, dataset: orders, max_decrease_percent: 150 }",
        "  - { id: a, kind: row_count_change, dataset: orders, max_increase_percent: -1 }",
        "  - { id: a, kind: row_count_change, dataset: orders, column: c, max_decrease_percent: 5 }",
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void What_the_schema_refuses_the_loader_refuses(string entry)
    {
        Assert.False(SchemaAccepts(File(entry)), "the schema accepts what it should refuse");
        Assert.False(CheckLoader.Load(File(entry), "x.yaml").Ok, "the loader accepts what the schema refuses");
    }

    /// <summary>What a schema cannot say: a comparison between two keys, or between entries. The loader refuses these and the schema documents that it does not.</summary>
    public static TheoryData<string> LoaderOnly => new()
    {
        "  - { id: a, kind: range, dataset: orders, column: c, min: 9, max: 3 }",
        "  - { id: a, kind: range, dataset: orders, column: c, min: 1, max: 2026-01-01 }",
        "  - { id: a, kind: referential, dataset: orders, columns: [c, d], references: { dataset: t, column: id } }",
        "  - { id: a, kind: not_null, dataset: orders, columns: [c, c] }",
        "  - { id: a, kind: not_null, dataset: orders, column: c }\n  - { id: a, kind: not_null, dataset: orders, column: d }",
    };

    [Theory]
    [MemberData(nameof(LoaderOnly))]
    public void What_only_the_loader_can_check_is_refused_by_the_loader(string entry)
    {
        Assert.Equal(entry.Contains("columns: [c, c]", StringComparison.Ordinal), !SchemaAccepts(File(entry)));
        Assert.False(CheckLoader.Load(File(entry), "x.yaml").Ok);
    }

    [Fact]
    public void The_schema_lists_the_kinds_settings_and_keys_of_the_loader()
    {
        var variants = SchemaDocument["$defs"]!["check"]!["oneOf"]!.AsArray().Select(v => v!.AsObject()).ToList();
        Assert.Equal(CheckNames.Kinds, variants.Select(v => v["properties"]!["kind"]!["const"]!.GetValue<string>()));
        foreach (var variant in variants)
        {
            var kind = variant["properties"]!["kind"]!["const"]!.GetValue<string>();
            CheckNames.TryKind(kind, out var parsed);
            string[] common = ["id", "kind", "dataset", "severity", "on_error", "description"];
            Assert.Equal(common.Concat(CheckSection.KeysOf(parsed)).Order(StringComparer.Ordinal), variant["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.False(variant["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(CheckNames.Severities, variant["properties"]!["severity"]!["enum"]!.AsArray().Select(s => s!.GetValue<string>()));
            Assert.Equal(CheckNames.OnErrors, variant["properties"]!["on_error"]!["enum"]!.AsArray().Select(s => s!.GetValue<string>()));
        }
    }

    [Theory]
    [InlineData("90s", true)]
    [InlineData("6h", true)]
    [InlineData("2d", true)]
    [InlineData("999999m", true)]
    [InlineData("0h", false)]
    [InlineData("05m", false)]
    [InlineData("6w", false)]
    [InlineData("h", false)]
    [InlineData("1.5h", false)]
    [InlineData("1000000s", false)]
    public void The_freshness_pattern_agrees_with_the_parser(string age, bool valid)
    {
        var pattern = new System.Text.RegularExpressions.Regex(SchemaDocument["$defs"]!["check"]!["oneOf"]!.AsArray().Single(v => v!["properties"]!["kind"]!["const"]!.GetValue<string>() == "freshness")!["properties"]!["max_age"]!["pattern"]!.GetValue<string>());
        Assert.Equal(valid, pattern.IsMatch(age));
        Assert.Equal(valid && age.Length <= 7, FreshnessParameters.TryParse(age, out _) && pattern.IsMatch(age));
        Assert.Equal($"^{FreshnessParameters.Pattern}$", pattern.ToString());
    }

    [Fact]
    public void The_identities_of_the_lineage_graph_are_accepted_as_the_dataset()
    {
        var connection = new ConnectionDefinition { Name = "sales", Engine = "sqlserver", Address = new AddressSpec { Host = "SQL1", Database = "Sales" } };
        var dataset = LineageIdentity.Dataset(connection, "dbo", "Orders").ToString();
        var entity = LineageIdentity.SuiteObject("store-1", "entity", "customer");
        var postgres = LineageIdentity.Dataset(new ConnectionDefinition { Name = "p", Engine = "postgres", Address = new AddressSpec { Host = "pg", Database = "db" } }, "public", "t").ToString();
        var file = $"schema: check/1\nchecks:\n  - {{ id: a, kind: not_null, dataset: '{dataset}', column: x }}\n  - {{ id: b, kind: unique, dataset: '{entity}', column: code }}\n  - {{ id: c, kind: referential, dataset: '{dataset}', column: x, references: {{ dataset: '{postgres}', column: y }} }}\n";
        var result = CheckLoader.Load(file, "x.yaml");
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        Assert.True(SchemaAccepts(file));
        Assert.All(result.Checks!, c => Assert.True(c.Attachment.IsIdentity));
        Assert.Equal(dataset, result.Checks![0].Attachment.Dataset);
        Assert.Equal(CheckTargetKind.Dataset, result.Checks[0].Attachment.TargetKind);
        Assert.Equal(CheckTargetKind.Entity, result.Checks[1].Attachment.TargetKind);
        Assert.Equal(postgres, ((ReferentialParameters)result.Checks[2].Parameters).Dataset);
    }

    [Fact]
    public void A_file_of_a_newer_format_is_refused_naming_it()
    {
        var result = CheckLoader.Load("schema: check/2\nchecks: []\n", "future.checks.yaml");
        var at = Assert.Single(result.Diagnostics);
        Assert.Equal(SharedCodes.SchemaVersionNewer, at.Code);
        Assert.Equal(("future.checks.yaml", 1, 9), (at.Path, at.Line, at.Column));
        Assert.Contains("check/2", at.Message);
    }
}
