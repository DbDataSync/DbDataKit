using DbDataKit.Config.Yaml;

namespace DbDataKit.Access.Tests;

/// <summary>Plan 22a GP01: the section of rules (QG2) loads, and each bad part is refused with the file, line and column of what is wrong.</summary>
public sealed class AccessSectionTests
{
    internal const string Complete = """
        max_age: 2h
        row:
          - dataset: sales
            match: all
            groups:
              - id: regions
                match: any
                rules:
                  - id: by_region
                    effect: include
                    table: region_access
                    subject: who
                    subject_kind: group
                    columns: { region: region_code, channel: channel_code }
                    max_age: 30m
                  - id: not_closed
                    effect: exclude
                    table: closed_accounts
                    subject: who
                    columns: { account: account_id }
        columns:
          - { subject: support, dataset: customers, column: email, treatment: mask, mask: hash }
          - { subject: ann@example.com, subject_kind: person, dataset: customers, column: salary, treatment: hide }
          - { subject: interns, dataset: customers, column: phone, treatment: "null" }
        """;

    internal static AccessSection? Read(string yaml, out List<ConfigDiagnostic> diagnostics, IAccessTarget? target = null)
    {
        diagnostics = [];
        return AccessSectionReader.ReadText(yaml, "model.yaml", diagnostics, target);
    }

    private static ConfigDiagnostic Refused(string yaml, string code, IAccessTarget? target = null)
    {
        var section = Read(yaml, out var diagnostics, target);
        Assert.Null(section);
        var found = diagnostics.Where(d => d.Code == code).ToList();
        Assert.True(found.Count > 0, $"expected {code}, got: {string.Join("; ", diagnostics)}");
        Assert.All(found, d => Assert.True(d.Line > 0 && d.Column > 0 && d.Path == "model.yaml"));
        return found[0];
    }

    [Fact]
    public void A_complete_section_loads()
    {
        var section = Read(Complete, out var diagnostics, Fakes.Target());
        Assert.Empty(diagnostics);
        Assert.NotNull(section);
        Assert.Equal(TimeSpan.FromHours(2), section.MaxAge);
        var sales = Assert.Single(section.Row);
        Assert.Equal(("sales", AccessMatch.All), (sales.Dataset, sales.Match));
        var group = Assert.Single(sales.Groups);
        Assert.Equal(("regions", AccessMatch.Any, 2), (group.Id, group.Match, group.Rules.Count));
        var include = group.Rules[0];
        Assert.Equal((AccessEffect.Include, "region_access", "who", SubjectKind.Group, TimeSpan.FromMinutes(30)), (include.Effect, include.Table, include.Subject, include.SubjectKind, include.MaxAge));
        Assert.Equal(new[] { new RuleColumn("region", "region_code"), new RuleColumn("channel", "channel_code") }, include.Columns);
        Assert.Equal(AccessEffect.Exclude, group.Rules[1].Effect);
        Assert.Equal(new[] { ColumnTreatment.Mask, ColumnTreatment.Hide, ColumnTreatment.Null }, section.Columns.Select(c => c.Treatment));
        Assert.Equal(new MaskKind?[] { MaskKind.Hash, null, null }, section.Columns.Select(c => c.Mask));
        Assert.Equal(SubjectKind.Person, section.Columns[1].SubjectKind);
        Assert.Equal(TimeSpan.FromMinutes(30), section.RulesTables()["region_access"]);
        Assert.Equal(TimeSpan.FromHours(2), section.RulesTables()["closed_accounts"]);
    }

    [Fact]
    public void A_plain_null_is_the_treatment_null()
    {
        var section = Read("columns:\n  - { subject: s, dataset: customers, column: phone, treatment: null }\n", out var diagnostics, Fakes.Target());
        Assert.Empty(diagnostics);
        Assert.Equal(ColumnTreatment.Null, section!.Columns[0].Treatment);
    }

    [Fact]
    public void The_defaults_are_all_group_and_one_hour_and_a_mask_is_fixed()
    {
        var section = Read("""
            row:
              - dataset: sales
                groups:
                  - id: g
                    rules:
                      - { id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } }
            columns:
              - { subject: s, dataset: customers, column: email, treatment: mask }
            """, out var diagnostics, Fakes.Target());
        Assert.Empty(diagnostics);
        Assert.Equal(AccessSection.DefaultMaxAge, section!.MaxAge);
        Assert.Equal((AccessMatch.All, AccessMatch.All, SubjectKind.Group), (section.Row[0].Match, section.Row[0].Groups[0].Match, section.Row[0].Groups[0].Rules[0].SubjectKind));
        Assert.Equal(MaskKind.Fixed, section.Columns[0].Mask);
    }

    [Fact]
    public void An_unknown_key_is_refused_with_a_suggestion_and_its_position()
    {
        var d = Refused("row:\n  - dataset: sales\n    grups: []\n    groups:\n      - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } } ] }\n", SharedCodes.UnknownKey);
        Assert.Equal((3, 5), (d.Line, d.Column));
        Assert.Contains("Did you mean `groups`", d.Message);
        Assert.Contains("max_age", Refused("maxage: 1h", SharedCodes.UnknownKey).Message);
    }

    [Theory]
    [InlineData("match: some", "`match` is `some`")]
    [InlineData("effect: allow", "`effect` is `allow`")]
    [InlineData("subject_kind: team", "`subject_kind` is `team`")]
    public void A_choice_that_is_not_one_is_refused_naming_the_choices(string wrong, string message)
    {
        var key = wrong.Split(':')[0];
        var rule = "{ id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } }";
        var yaml = key switch
        {
            "match" => $"row:\n  - dataset: sales\n    {wrong}\n    groups:\n      - {{ id: g, rules: [ {rule} ] }}\n",
            "effect" => $"row:\n  - dataset: sales\n    groups:\n      - {{ id: g, rules: [ {rule.Replace("effect: include", wrong)} ] }}\n",
            _ => $"row:\n  - dataset: sales\n    groups:\n      - {{ id: g, rules: [ {rule.Replace("subject: who", "subject: who, " + wrong)} ] }}\n",
        };
        var d = Refused(yaml, AccessSectionReader.BadSetting);
        Assert.Contains(message, d.Message);
    }

    [Theory]
    [InlineData("0h")]
    [InlineData("32d")]
    [InlineData("1.5h")]
    [InlineData("soon")]
    public void A_maximum_age_is_a_whole_number_and_a_unit_of_at_most_31_days(string age) =>
        Assert.Contains("max_age", Refused($"max_age: {age}\n", AccessSectionReader.BadSetting).Message);

    [Fact]
    public void A_group_without_an_include_rule_is_refused()
    {
        var d = Refused("""
            row:
              - dataset: sales
                groups:
                  - id: only_excludes
                    rules:
                      - { id: r, effect: exclude, table: closed_accounts, subject: who, columns: { account: account_id } }
            """, AccessSectionReader.NoIncludeRule);
        Assert.Contains("only_excludes", d.Message);
        Assert.Equal(6, d.Line);
    }

    [Fact]
    public void A_rule_without_columns_or_with_an_empty_or_repeated_mapping_is_refused()
    {
        string Rule(string columns) => $"row:\n  - dataset: sales\n    groups:\n      - id: g\n        rules:\n          - {{ id: r, effect: include, table: region_access, subject: who{columns} }}\n";
        Assert.Contains("columns", Refused(Rule(""), SharedCodes.MissingKey).Message);
        Refused(Rule(", columns: {}"), AccessSectionReader.BadMapping);
        Refused(Rule(", columns: [region]"), AccessSectionReader.BadMapping);
        Assert.Contains("region_code", Refused(Rule(", columns: { region: region_code, area: region_code }"), AccessSectionReader.BadMapping).Message);
        Refused(Rule(", columns: { region: [a] }"), AccessSectionReader.BadMapping);
    }

    [Fact]
    public void A_duplicate_id_or_dataset_entry_is_refused()
    {
        var rule = "{ id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } }";
        var d = Refused($"row:\n  - dataset: sales\n    groups:\n      - {{ id: g, rules: [ {rule}, {rule.Replace("region_access", "other")} ] }}\n", AccessSectionReader.Duplicate);
        Assert.Contains("rule id `r`", d.Message);
        Refused($"row:\n  - dataset: sales\n    groups:\n      - {{ id: g, rules: [ {rule} ] }}\n  - dataset: sales\n    groups:\n      - {{ id: h, rules: [ {rule.Replace("id: r", "id: s")} ] }}\n", AccessSectionReader.Duplicate);
        Refused($"row:\n  - dataset: sales\n    groups:\n      - {{ id: g, rules: [ {rule} ] }}\n      - {{ id: g, rules: [ {rule.Replace("id: r", "id: s")} ] }}\n", AccessSectionReader.Duplicate);
    }

    [Fact]
    public void A_bad_id_or_a_missing_part_is_refused()
    {
        Refused("row:\n  - dataset: sales\n    groups:\n      - { id: 1g, rules: [] }\n", SharedCodes.InvalidValue);
        Refused("row:\n  - groups: []\n", SharedCodes.MissingKey);
        Refused("row:\n  - dataset: sales\n", SharedCodes.MissingKey);
        Refused("row: sales\n", SharedCodes.InvalidValue);
        Refused("columns:\n  - { subject: s, dataset: customers, column: email }\n", SharedCodes.MissingKey);
        Refused("columns:\n  - { subject: s, dataset: customers, column: email, treatment: blur }\n", AccessSectionReader.BadSetting);
        Refused("columns:\n  - { subject: s, dataset: customers, column: email, treatment: hide, mask: hash }\n", AccessSectionReader.BadSetting);
        Refused("columns:\n  - { subject: s, dataset: customers, column: email, treatment: mask, mask: rot13 }\n", AccessSectionReader.BadSetting);
        Refused("- row\n", SharedCodes.InvalidValue);
    }

    [Fact]
    public void A_section_in_a_format_1_file_is_refused_naming_the_fix()
    {
        var at = new YamlScalar("x", 7, 1);
        var d = AccessSectionReader.RefusedInFormat1("sales.model.yaml", at, "model/2", "Set `schemaVersion: 2`.");
        Assert.Equal((AccessSectionReader.NotInThisFormat, "sales.model.yaml", 7), (d.Code, d.Path, d.Line));
        Assert.Contains("format 1", d.Message);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("6h", 21600)]
    [InlineData("31d", 2678400)]
    public void Ages_are_read_and_written_the_same_way(string text, int seconds)
    {
        Assert.True(AccessSectionReader.TryParseAge(text, out var age));
        Assert.Equal(TimeSpan.FromSeconds(seconds), age);
        Assert.Equal(text == "90s" ? "90s" : text, AccessSectionReader.AgeText(age));
    }
}
