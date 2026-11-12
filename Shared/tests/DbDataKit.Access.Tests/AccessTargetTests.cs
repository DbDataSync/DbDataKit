using DbDataKit.Config.Yaml;

namespace DbDataKit.Access.Tests;

/// <summary>Plan 22a GP01: the section is checked against the definition it is in: its datasets, its columns, the rules table and its source.</summary>
public sealed class AccessTargetTests
{
    private static string One(string rule, string dataset = "sales") =>
        $"row:\n  - dataset: {dataset}\n    groups:\n      - id: g\n        rules:\n          - {rule}\n";

    private const string Ok = "{ id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } }";

    private static ConfigDiagnostic Refused(string yaml, string code, bool shared = false)
    {
        var section = AccessSectionTests.Read(yaml, out var diagnostics, Fakes.Target(shared));
        Assert.Null(section);
        var found = diagnostics.Where(d => d.Code == code).ToList();
        Assert.True(found.Count > 0, $"expected {code}, got: {string.Join("; ", diagnostics)}");
        Assert.All(found, d => Assert.True(d.Line > 0 && d.Column > 0));
        return found[0];
    }

    [Fact]
    public void A_rule_that_matches_the_definition_loads()
    {
        Assert.NotNull(AccessSectionTests.Read(One(Ok), out var diagnostics, Fakes.Target()));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void An_unknown_dataset_is_refused_with_the_near_name()
    {
        var d = Refused(One(Ok, "sale"), AccessSectionReader.UnknownDataset);
        Assert.Contains("Did you mean `sales`", d.Message);
        Assert.Equal((2, 14), (d.Line, d.Column));
        Refused("columns:\n  - { subject: s, dataset: custmers, column: email, treatment: hide }\n", AccessSectionReader.UnknownDataset);
    }

    [Fact]
    public void An_unknown_column_of_the_dataset_or_of_the_rules_table_is_refused()
    {
        Assert.Contains("Did you mean `region`", Refused(One(Ok.Replace("{ region:", "{ regon:")), AccessSectionReader.UnknownColumn).Message);
        Assert.Contains("rules table `region_access`", Refused(One(Ok.Replace("region_code", "region_cod")), AccessSectionReader.UnknownColumn).Message);
        Assert.Contains("rules table", Refused(One(Ok.Replace("subject: who", "subject: whom")), AccessSectionReader.UnknownColumn).Message);
        Assert.Contains("`customers`", Refused("columns:\n  - { subject: s, dataset: customers, column: emial, treatment: hide }\n", AccessSectionReader.UnknownColumn).Message);
    }

    [Fact]
    public void A_rules_table_must_be_a_table_of_the_definition_in_the_same_source()
    {
        Refused(One(Ok.Replace("region_access", "nowhere")), AccessSectionReader.BadRulesTable);
        Assert.Contains("one target", Refused(One(Ok.Replace("region_access", "other_source")), AccessSectionReader.BadRulesTable).Message);
        Assert.Contains("the dataset the rule filters", Refused(One(Ok.Replace("region_access", "sales")), AccessSectionReader.BadRulesTable).Message);
    }

    [Fact]
    public void A_table_that_the_model_reads_cannot_be_a_rules_table() =>
        Assert.Contains("a measure reads it", Refused(One(Ok.Replace("region_access", "read_by_model")), AccessSectionReader.BadRulesTable).Message);

    [Fact]
    public void A_dataset_on_a_source_no_filter_reaches_is_refused() =>
        Assert.Contains("Analysis Services", Refused(One(Ok, "cube"), AccessSectionReader.CannotFilter).Message);

    [Fact]
    public void The_kinds_of_the_two_columns_must_agree_when_both_are_known() =>
        Assert.Contains("number", Refused(One(Ok.Replace("{ region: region_code }", "{ region: amount_limit }")), AccessSectionReader.TypeMismatch).Message, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Only_text_can_be_masked()
    {
        Assert.Contains("only text", Refused("columns:\n  - { subject: s, dataset: customers, column: salary, treatment: mask }\n", AccessSectionReader.MaskNotText).Message);
        Assert.NotNull(AccessSectionTests.Read("columns:\n  - { subject: s, dataset: customers, column: salary, treatment: hide }\n  - { subject: s, dataset: customers, column: salary, treatment: \"null\" }\n", out _, Fakes.Target()));
    }

    [Fact]
    public void A_shared_cache_beside_any_rule_is_refused_and_beside_none_is_not()
    {
        Assert.Contains("shared", Refused(One(Ok), AccessSectionReader.SharedCache, shared: true).Message);
        Refused("columns:\n  - { subject: s, dataset: customers, column: email, treatment: hide }\n", AccessSectionReader.SharedCache, shared: true);
        Assert.NotNull(AccessSectionTests.Read("max_age: 1h\n", out var diagnostics, Fakes.Target(shared: true)));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Without_a_target_only_the_shape_is_read() =>
        Assert.NotNull(AccessSectionTests.Read(One(Ok.Replace("region_access", "anything"), "whatever"), out _));
}
