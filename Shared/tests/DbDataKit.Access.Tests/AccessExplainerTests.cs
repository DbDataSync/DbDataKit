namespace DbDataKit.Access.Tests;

/// <summary>Plan 22a GP15: the explanation of what a person or group can read, made from the same resolver and predicate the enforcement uses; the coverage report.</summary>
public sealed class AccessExplainerTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Ledger : IRulesTableLedger
    {
        public readonly Dictionary<string, RulesTableRecord> Rows = new();

        public RulesTableRecord Touch(string key, DateTimeOffset now) => Rows.TryGetValue(key, out var row) ? row : Rows[key] = new RulesTableRecord(key, now, null);

        public void Changed(string key, DateTimeOffset at, string product, string reason) => Rows[key] = Touch(key, at) with { ChangedAt = at };
    }

    /// <summary>The rules tables and the fact table in memory: what a host reads from its data source.</summary>
    private sealed class Data : IRulesSubjects, IRulesTableReader
    {
        public readonly Dictionary<string, List<RulesTableRow>> Rules = new()
        {
            ["region_access"] =
            [
                new("East", new Dictionary<string, string?> { ["region_code"] = "EU", ["channel_code"] = "web" }),
                new("East", new Dictionary<string, string?> { ["region_code"] = "EU", ["channel_code"] = null }),
                new("west", new Dictionary<string, string?> { ["region_code"] = "US", ["channel_code"] = "shop" }),
            ],
            ["closed_accounts"] = [new("Auditors", new Dictionary<string, string?> { ["account_id"] = "7" })],
        };

        public readonly List<Dictionary<string, string?>> Sales =
        [
            new() { ["region"] = "EU", ["channel"] = "web", ["account"] = "1" },
            new() { ["region"] = "EU", ["channel"] = null, ["account"] = "2" },
            new() { ["region"] = "EU", ["channel"] = null, ["account"] = "3" },
            new() { ["region"] = "US", ["channel"] = "shop", ["account"] = "4" },
            new() { ["region"] = "ASIA", ["channel"] = "web", ["account"] = "5" },
            new() { ["region"] = null, ["channel"] = "web", ["account"] = "6" },
        ];

        public bool Broken;

        public IReadOnlyCollection<string> Names(string table, string column) => [.. Rules[table].Select(r => r.Subject).Distinct()];

        public IReadOnlyList<RulesTableRow> Rows(RowRule rule, IReadOnlyCollection<string> names, int limit)
        {
            if (Broken) throw new InvalidOperationException("no such table");
            return [.. Rules[rule.Table].Where(r => names.Contains(r.Subject)).Take(limit)];
        }

        public UncoveredValues Uncovered(string dataset, RowRule rule, int limit)
        {
            if (Broken) throw new InvalidOperationException("no such table");
            var covered = Rules[rule.Table].Select(r => string.Join('\u001f', rule.Columns.Select(c => r.Values.GetValueOrDefault(c.RulesColumn) ?? "\0NULL"))).ToHashSet();
            var groups = Sales.GroupBy(s => string.Join('\u001f', rule.Columns.Select(c => s[c.DatasetColumn] ?? "\0NULL"))).Where(g => !covered.Contains(g.Key)).ToList();
            return new UncoveredValues(groups.Count, [.. groups.Take(limit).Select(g => new UncoveredValue(rule.Columns.ToDictionary(c => c.DatasetColumn, c => g.First()[c.DatasetColumn]), g.Count()))], groups.Count > limit);
        }
    }

    private readonly Clock _clock = new(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
    private readonly Ledger _ledger = new();
    private readonly Data _data = new();

    private (AccessExplainer Explainer, AccessScope Scope) Make(string yaml = AccessSectionTests.Complete)
    {
        var section = AccessSectionTests.Read(yaml, out var diagnostics, Fakes.Target())!;
        Assert.Empty(diagnostics);
        return (new AccessExplainer(new AccessResolver(_ledger, null, _clock), _data), new AccessScope("semantic-model/sales", section, t => "warehouse/" + t, _data));
    }

    private static AccessCaller Group(params string[] groups) => new("", "", groups);

    private static Dictionary<string, string?> Row(string? region, string? channel, string account = "1") => new() { ["region"] = region, ["channel"] = channel, ["account"] = account };

    [Fact]
    public void A_person_with_no_rule_row_is_told_so_and_sees_no_rows()
    {
        var (explainer, scope) = Make();
        var explanation = explainer.Explain(new AccessCaller("u1", "ann", ["nobody"]), scope, "sales", Row("EU", "web"));
        var sales = Assert.Single(explanation.Datasets);
        Assert.Contains("No row of 'region_access' names them", sales.Summary);
        Assert.False(sales.Verdict!.Included);
        Assert.Contains("names them", sales.Verdict.Text);
        Assert.Empty(sales.Groups[0].Rules.SelectMany(r => r.Names));
    }

    [Fact]
    public void A_row_the_rules_table_has_a_row_for_is_included_and_the_rule_that_decided_is_named()
    {
        var (explainer, scope) = Make();
        var explanation = explainer.Explain(new AccessCaller("u1", "ann", ["EAST"]), scope, "sales", Row("EU", "web"));
        var sales = explanation.Datasets.Single();
        Assert.True(sales.Verdict!.Included);
        Assert.Equal(["regions/by_region"], sales.Verdict.DecidedBy);
        Assert.Contains("'region_access' has a row for 'East'", sales.Verdict.Text);
        var rule = sales.Groups[0].Rules.Single(r => r.Id == "by_region");
        Assert.Equal(["East"], rule.Names);   // as the table spells it
        Assert.Equal(2, rule.Rows.Count);
        Assert.True(rule.Holds);
        Assert.Equal("fresh", explanation.Tables.First(t => t.Table == "region_access").State.ToString().ToLowerInvariant());

        // a value no row of theirs has
        var other = explainer.Explain(new AccessCaller("u1", "ann", ["east"]), scope, "sales", Row("US", "shop")).Datasets.Single();
        Assert.False(other.Verdict!.Included);
        Assert.Contains("none of the 2 rows of 'region_access'", other.Verdict.Text);
    }

    [Fact]
    public void An_exclude_rule_subtracts_and_is_named_whatever_the_includes_say()
    {
        var (explainer, scope) = Make();
        var caller = new AccessCaller("u1", "ann", ["east", "auditors"]);
        var excluded = explainer.Explain(caller, scope, "sales", Row("EU", "web", "7")).Datasets.Single();
        Assert.False(excluded.Verdict!.Included);
        Assert.Equal(["regions/not_closed"], excluded.Verdict.DecidedBy);
        Assert.Contains("rule 'regions/not_closed' removes it", excluded.Verdict.Text);
        Assert.Equal(false, excluded.Groups[0].Included);
        // the same person, another account
        Assert.True(explainer.Explain(caller, scope, "sales", Row("EU", "web", "8")).Datasets.Single().Verdict!.Included);
    }

    [Fact]
    public void NULL_matches_only_a_NULL_rule_row_and_an_exclude_rule_keeps_a_NULL_value()
    {
        var (explainer, scope) = Make();
        // the rules table has a NULL channel for EU: a row with a NULL channel is included; the West person has no NULL row
        Assert.True(explainer.Explain(Group("east"), scope, "sales", Row("EU", null)).Datasets.Single().Verdict!.Included);
        Assert.False(explainer.Explain(Group("west"), scope, "sales", Row("US", null)).Datasets.Single().Verdict!.Included);
        // a NULL account is never excluded by the closed accounts (7 is a number, not NULL)
        Assert.True(explainer.Explain(Group("east", "auditors"), scope, "sales", Row("EU", null, null!)).Datasets.Single().Verdict!.Included);
        Assert.Equal("NULL", RuleValues.Show(null));
        Assert.True(RuleValues.Equal("10", "10.0"));
        Assert.False(RuleValues.Equal(null, ""));
    }

    [Fact]
    public void The_explanation_agrees_with_the_predicate_for_every_row()
    {
        var (explainer, scope) = Make();
        var resolver = new AccessResolver(_ledger, null, _clock);
        foreach (var groups in new[] { new[] { "east" }, ["west"], ["east", "auditors"], ["west", "auditors"], ["nobody"] })
        {
            var caller = Group(groups);
            var dataset = resolver.Resolve(caller, scope).DatasetOf("sales")!;
            var predicate = RowPredicate.Of(dataset);
            foreach (var sale in _data.Sales)
            {
                var row = sale.ToDictionary(e => e.Key, e => e.Value);
                bool Holds(RuleResolution rule) => rule.Names.Count > 0 && _data.Rules[rule.Rule.Table].Any(r => rule.Names.Contains(r.Subject) && rule.Rule.Columns.All(c => RuleValues.Equal(row[c.DatasetColumn], r.Values[c.RulesColumn])));
                Assert.Equal(predicate.Evaluate(Holds), explainer.Explain(caller, scope, "sales", row).Datasets.Single().Verdict!.Included);
            }
        }
    }

    [Fact]
    public void A_row_that_misses_a_column_a_rule_reads_is_not_judged_and_says_which()
    {
        var (explainer, scope) = Make();
        var sales = explainer.Explain(Group("east"), scope, "sales", new Dictionary<string, string?> { ["region"] = "EU" }).Datasets.Single();
        Assert.Null(sales.Verdict);
        Assert.Equal(["account", "channel"], sales.MissingColumns);
    }

    [Fact]
    public void A_table_past_its_maximum_age_refuses_the_dataset_and_the_explanation_says_so_and_a_refresh_changes_the_state()
    {
        var (explainer, scope) = Make();
        explainer.Explain(Group("east"), scope);   // first served now
        _clock.Now += TimeSpan.FromHours(3);
        var expired = explainer.Explain(Group("east"), scope, "sales", Row("EU", "web"));
        var sales = expired.Datasets.Single();
        Assert.NotNull(sales.Refusal);
        Assert.Contains("is not served to them", sales.Summary);
        Assert.Contains("past its maximum age", sales.Verdict!.Text);
        Assert.Equal(RulesTableState.Expired, expired.Tables.Single(t => t.Table == "region_access").State);
        Assert.Null(expired.Tables.Single(t => t.Table == "region_access").LastChanged);

        _ledger.Changed("warehouse/region_access", _clock.Now, "build", "refresh");
        _ledger.Changed("warehouse/closed_accounts", _clock.Now, "build", "refresh");
        var fresh = explainer.Explain(Group("east"), scope, "sales", Row("EU", "web"));
        Assert.Null(fresh.Datasets.Single().Refusal);
        Assert.Equal(RulesTableState.Fresh, fresh.Tables.Single(t => t.Table == "region_access").State);
        Assert.Equal(_clock.Now, fresh.Tables.Single(t => t.Table == "region_access").LastChanged);
        Assert.NotEqual(expired.Fingerprint, fresh.Fingerprint);
    }

    [Fact]
    public void An_unreadable_rules_table_is_reported_on_its_rule_and_never_makes_a_row_included()
    {
        var (explainer, scope) = Make();
        _data.Broken = true;
        var sales = explainer.Explain(Group("east"), scope, "sales", Row("EU", "web")).Datasets.Single();
        Assert.Contains("could not be read", sales.Groups[0].Rules[0].ReadProblem);
        Assert.False(sales.Verdict!.Included);
    }

    [Fact]
    public void The_column_rules_that_apply_are_listed_with_the_treatment_that_wins_hide_before_null_before_mask()
    {
        var (explainer, scope) = Make("""
            columns:
              - { subject: support, dataset: customers, column: email, treatment: mask, mask: hash }
              - { subject: interns, dataset: customers, column: email, treatment: "null" }
              - { subject: ann@example.com, subject_kind: person, dataset: customers, column: email, treatment: hide }
              - { subject: support, dataset: customers, column: phone, treatment: mask, mask: fixed }
            """);
        var explanation = explainer.Explain(new AccessCaller("u1", "ann@example.com", ["support", "interns"]), scope);
        Assert.Equal(["email:hide", "phone:mask:fixed"], explanation.Columns.Select(c => c.Column + ":" + c.Treatment));
        Assert.Equal(3, explanation.Columns[0].Rules.Count);
        var intern = explainer.Explain(new AccessCaller("u2", "bob", ["support", "interns"]), scope);
        Assert.Equal("null", intern.Columns.Single(c => c.Column == "email").Treatment);
        Assert.Empty(explainer.Explain(new AccessCaller("u3", "cy", ["other"]), scope).Columns);
    }

    [Fact]
    public void A_dataset_with_no_row_rules_says_every_row_is_read_as_it_is()
    {
        var (explainer, scope) = Make();
        var customers = explainer.Explain(Group("east"), scope, "customers").Datasets.Single();
        Assert.Contains("no row rules", customers.Summary);
    }

    [Fact]
    public void Two_callers_with_one_effective_rule_have_one_fingerprint_in_the_explanation()
    {
        var (explainer, scope) = Make();
        Assert.Equal(explainer.Explain(Group("east"), scope).Fingerprint, explainer.Explain(new AccessCaller("u9", "zed", ["EAST", "x"]), scope).Fingerprint);
        Assert.NotEqual(explainer.Explain(Group("east"), scope).Fingerprint, explainer.Explain(Group("west"), scope).Fingerprint);
    }

    [Fact]
    public void The_explanation_is_json_with_the_rules_the_rows_and_the_tables()
    {
        var (explainer, scope) = Make();
        var json = explainer.Explain(Group("east", "auditors"), scope, "sales", Row("EU", "web", "7"), ["a warning"], "east", SubjectKind.Group).ToJson();
        Assert.Equal("east", (string?)json["subject"]);
        Assert.Equal("group", (string?)json["subject_kind"]);
        Assert.Equal("a warning", (string?)json["warnings"]![0]);
        var dataset = json["datasets"]![0]!;
        Assert.False((bool)dataset["verdict"]!["included"]!);
        Assert.Equal("regions/not_closed", (string?)dataset["verdict"]!["decided_by"]![0]);
        Assert.Equal("EU", (string?)dataset["groups"]![0]!["rules"]![0]!["rows"]![0]!["values"]!["region_code"]);
        Assert.Equal("fresh", (string?)json["tables"]![0]!["state"]);
    }

    [Fact]
    public void Coverage_lists_the_values_no_rule_row_covers_NULL_counted_per_rule()
    {
        var (_, scope) = Make();
        var report = AccessCoverage.Report(scope.Section, _data);
        var byRegion = report.Single(r => r.Rule == "by_region");
        // EU/web, EU/NULL and US/shop are covered; ASIA/web and NULL/web are not
        Assert.Equal(2, byRegion.Uncovered);
        Assert.Contains(byRegion.Values, v => v.Values["region"] is null && v.Values["channel"] == "web");
        Assert.Contains(byRegion.Values, v => v.Values["region"] == "ASIA");
        Assert.All(byRegion.Values, v => Assert.Equal(1, v.Rows));
        var closed = report.Single(r => r.Rule == "not_closed");
        Assert.Equal(6, closed.Uncovered);   // accounts 1 to 6 are in the data, only 7 is in the table
        Assert.Equal(AccessEffect.Exclude, closed.Effect);

        // a rules table that gains the NULL region row covers it
        _data.Rules["region_access"].Add(new("East", new Dictionary<string, string?> { ["region_code"] = null, ["channel_code"] = "web" }));
        Assert.Equal(1, AccessCoverage.Report(scope.Section, _data, "sales").Single(r => r.Rule == "by_region").Uncovered);
        Assert.Equal("by_region", AccessCoverage.Report(scope.Section, _data).Single(r => r.Rule == "by_region").ToJson()["rule"]!.GetValue<string>());
    }

    [Fact]
    public void Coverage_reports_a_table_it_cannot_read_instead_of_a_count()
    {
        var (_, scope) = Make();
        _data.Broken = true;
        Assert.All(AccessCoverage.Report(scope.Section, _data), r => Assert.Contains("no such table", r.Problem));
    }
}
