using DbDataKit.Config.Yaml;

namespace DbDataKit.Quality.Tests;

/// <summary>Plan 23a FQ01: each kind loads with its parameters; each bad part is refused with file and line, and a suggestion for a near key or kind.</summary>
public sealed class CheckLoaderTests
{
    private const string File = "orders.checks.yaml";

    private static CheckLoadResult Load(string checks, string header = "schema: check/1\n") => CheckLoader.Load(header + "checks:\n" + checks, File);

    private static CheckDefinition One(string entry)
    {
        var result = Load(entry);
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        return Assert.Single(result.Checks!);
    }

    [Fact]
    public void Not_null_takes_a_column_or_columns()
    {
        var one = One("  - { id: a, kind: not_null, dataset: orders, column: customer_id }");
        Assert.Equal(CheckKind.NotNull, one.Kind);
        Assert.Equal(["customer_id"], one.Attachment.Columns);
        Assert.Equal("customer_id", one.Attachment.Column);
        Assert.Equal(CheckSeverity.Warn, one.Severity);
        Assert.Equal(CheckOnError.Alert, one.OnError);
        Assert.Same(NoParameters.Instance, one.Parameters);
        Assert.Equal(["a", "b"], One("  - { id: a, kind: not_null, dataset: orders, columns: [a, b] }").Attachment.Columns);
    }

    [Fact]
    public void Unique_takes_several_columns()
    {
        var check = One("  - { id: u, kind: unique, dataset: orders, columns: [order_no, line_no] }");
        Assert.Equal(CheckKind.Unique, check.Kind);
        Assert.Equal(["order_no", "line_no"], check.Attachment.Columns);
        Assert.Null(check.Attachment.Column);
    }

    [Fact]
    public void Accepted_values_keeps_the_values_as_written()
    {
        var check = One("  - { id: s, kind: accepted_values, dataset: orders, column: status, values: [open, shipped, 3, true] }");
        Assert.Equal(["open", "shipped", "3", "true"], Assert.IsType<AcceptedValuesParameters>(check.Parameters).Values);
    }

    [Fact]
    public void A_range_has_numeric_or_date_bounds_and_either_may_be_left_out()
    {
        var numbers = Assert.IsType<RangeParameters>(One("  - { id: r, kind: range, dataset: orders, column: qty, min: 0, max: 99.5 }").Parameters);
        Assert.Equal(0m, numbers.Min!.Number);
        Assert.Equal(99.5m, numbers.Max!.Number);
        var dates = Assert.IsType<RangeParameters>(One("  - { id: d, kind: range, dataset: orders, column: placed, min: 2026-01-01 }").Parameters);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), dates.Min!.Moment);
        Assert.Null(dates.Max);
        Assert.Equal(-5m, Assert.IsType<RangeParameters>(One("  - { id: m, kind: range, dataset: orders, column: t, max: -5 }").Parameters).Max!.Number);
        Assert.Equal(7m, Assert.IsType<RangeParameters>(One("  - { id: e, kind: range, dataset: orders, column: t, min: 7, max: 7 }").Parameters).Min!.Number);
    }

    [Fact]
    public void A_referential_check_names_its_target()
    {
        var check = One("""
              - id: fk
                kind: referential
                dataset: orders
                column: customer_id
                references: { dataset: customers, column: id }
            """);
        var target = Assert.IsType<ReferentialParameters>(check.Parameters);
        Assert.Equal("customers", target.Dataset);
        Assert.Equal(["id"], target.Columns);
        var two = Assert.IsType<ReferentialParameters>(One("  - { id: fk2, kind: referential, dataset: orders, columns: [a, b], references: { dataset: 'postgres://db1/sales.public.parts', columns: [x, y] } }").Parameters);
        Assert.Equal("postgres://db1/sales.public.parts", two.Dataset);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("15m", 900)]
    [InlineData("6h", 21600)]
    [InlineData("2d", 172800)]
    public void A_freshness_check_has_a_maximum_age_and_a_column_is_optional(string age, int seconds)
    {
        var check = One($"  - {{ id: f, kind: freshness, dataset: orders, max_age: {age} }}");
        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.IsType<FreshnessParameters>(check.Parameters).MaxAge);
        Assert.Empty(check.Attachment.Columns);
        Assert.Equal(["loaded_at"], One($"  - {{ id: f, kind: freshness, dataset: orders, column: loaded_at, max_age: {age} }}").Attachment.Columns);
    }

    [Fact]
    public void A_row_count_change_check_has_limits_in_percent()
    {
        var both = Assert.IsType<RowCountChangeParameters>(One("  - { id: c, kind: row_count_change, dataset: orders, max_increase_percent: 50, max_decrease_percent: 20.5 }").Parameters);
        Assert.Equal(50m, both.MaxIncreasePercent);
        Assert.Equal(20.5m, both.MaxDecreasePercent);
        var down = Assert.IsType<RowCountChangeParameters>(One("  - { id: c, kind: row_count_change, dataset: orders, max_decrease_percent: 100 }").Parameters);
        Assert.Null(down.MaxIncreasePercent);
        Assert.Empty(One("  - { id: c, kind: row_count_change, dataset: orders, max_decrease_percent: 10 }").Attachment.Columns);
    }

    [Fact]
    public void Severity_on_error_and_description_are_read()
    {
        var check = One("  - { id: x, kind: not_null, dataset: orders, column: a, severity: block, on_error: block, description: Orders have a customer }");
        Assert.Equal(CheckSeverity.Block, check.Severity);
        Assert.Equal(CheckOnError.Block, check.OnError);
        Assert.Equal("Orders have a customer", check.Description);
    }

    [Fact]
    public void A_dataset_is_a_lineage_identity_or_a_name_and_tells_its_kind()
    {
        var identity = One("  - { id: i, kind: not_null, dataset: 'sqlserver://sql1/sales.dbo.orders', column: a }").Attachment;
        Assert.True(identity.IsIdentity);
        Assert.Equal(CheckTargetKind.Dataset, identity.TargetKind);
        var entity = One("  - { id: e, kind: not_null, dataset: 'suite://store-1/entity/customer', column: a }").Attachment;
        Assert.Equal(CheckTargetKind.Entity, entity.TargetKind);
        var local = One("  - { id: l, kind: not_null, dataset: orders, column: a }").Attachment;
        Assert.False(local.IsIdentity);
    }

    [Fact]
    public void A_file_can_give_the_dataset_of_its_checks_once()
    {
        var result = CheckLoader.Load("schema: check/1\ndataset: orders\nchecks:\n  - { id: a, kind: not_null, column: x }\n  - { id: b, kind: not_null, dataset: other, column: x }\n", File);
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        Assert.Equal(["orders", "other"], result.Checks!.Select(c => c.Attachment.Dataset));
    }

    [Fact]
    public void An_empty_list_of_checks_is_a_file_with_no_checks()
    {
        var result = CheckLoader.Load("schema: check/1\nchecks: []\n", File);
        Assert.True(result.Ok);
        Assert.Empty(result.Checks!);
    }

    /// <summary>The entry of one check (a <c>⟦</c> marks where the refusal is reported; it is removed before the text is read), and what the message says.</summary>
    public static TheoryData<string, string> Refusals => new()
    {
        { "  - { id: a, kind: ⟦notnull, dataset: orders, column: x }", "Did you mean `not_null`?" },
        { "  - { id: a, kind: ⟦sql, dataset: orders, column: x }", "is not a kind of check" },
        { "  - { id: a, kind: range, dataset: orders, column: x, min: ⟦9, max: 3 }", "`min` (9) is above `max` (3)" },
        { "  - ⟦{ id: a, kind: range, dataset: orders, column: x }", "needs `min`, `max` or both" },
        { "  - { id: a, kind: range, dataset: orders, column: x, min: 1, max: ⟦2024-01-01 }", "both numbers or both dates" },
        { "  - { id: a, kind: range, dataset: orders, column: x, min: ⟦abc }", "write a number, or a date" },
        { "  - ⟦{ id: a, kind: referential, dataset: orders, column: x }", "names no target" },
        { "  - { id: a, kind: referential, dataset: orders, column: x, references: ⟦{ column: id } }", "names no dataset" },
        { "  - { id: a, kind: referential, dataset: orders, column: x, references: ⟦{ dataset: c } }", "names no column of the target" },
        { "  - { id: a, kind: referential, dataset: orders, columns: [x, y], references: { dataset: c, column: ⟦id } }", "pair one to one" },
        { "  - ⟦{ id: a, kind: not_null, column: x }", "names no dataset" },
        { "  - { id: a, kind: not_null, dataset: orders, column: x, severity: ⟦critical }", "`severity` is `critical`; it is `warn` or `block`" },
        { "  - { id: a, kind: not_null, dataset: orders, column: x, severity: ⟦blok }", "Did you mean `block`?" },
        { "  - { id: a, kind: not_null, dataset: orders, column: x, on_error: ⟦ignore }", "`on_error` is `ignore`; it is `alert` or `block`" },
        { "  - { id: a, kind: not_null, dataset: orders, ⟦colum: x }", "Did you mean `column`?" },
        { "  - { id: a, kind: not_null, dataset: orders, ⟦max_age: 6h, column: x }", "Unknown key `max_age` in a `not_null` check" },
        { "  - ⟦{ id: a, kind: not_null, dataset: orders }", "needs the column it is about" },
        { "  - { id: a, kind: not_null, dataset: orders, column: x, columns: ⟦[y] }", "`column` or `columns`, not both" },
        { "  - { id: a, kind: range, dataset: orders, columns: ⟦[x], min: 1 }", "write `column`, not `columns`" },
        { "  - ⟦{ id: a, kind: accepted_values, dataset: orders, column: x }", "needs `values`" },
        { "  - { id: a, kind: accepted_values, dataset: orders, column: x, values: ⟦[] }", "one or more values" },
        { "  - { id: a, kind: accepted_values, dataset: orders, column: x, values: [a, ⟦a] }", "listed more than once" },
        { "  - ⟦{ id: a, kind: freshness, dataset: orders }", "needs `max_age`" },
        { "  - { id: a, kind: freshness, dataset: orders, max_age: ⟦0h }", "whole number above zero" },
        { "  - { id: a, kind: freshness, dataset: orders, max_age: ⟦6w }", "`s`, `m`, `h` or `d`" },
        { "  - ⟦{ id: a, kind: row_count_change, dataset: orders }", "needs `max_increase_percent`" },
        { "  - { id: a, kind: row_count_change, dataset: orders, max_decrease_percent: ⟦150 }", "from 0 to 100" },
        { "  - { id: a, kind: row_count_change, dataset: orders, max_increase_percent: ⟦-1 }", "a percentage" },
        { "  - { id: a, kind: row_count_change, dataset: orders, ⟦column: x, max_decrease_percent: 5 }", "Unknown key `column`" },
        { "  - ⟦{ kind: not_null, dataset: orders, column: x }", "needs an `id`" },
        { "  - { id: ⟦9x, kind: not_null, dataset: orders, column: x }", "starts with a letter" },
        { "  - ⟦{ id: a, dataset: orders, column: x }", "needs a `kind`" },
        { "  - { id: a, kind: not_null, dataset: ⟦'a://', column: x }", "not a lineage identity" },
        { "  - ⟦not_null", "Each entry of `checks` is a mapping" },
        { "  - { id: a, kind: not_null, dataset: orders, column: x }\n  - { id: ⟦a, kind: unique, dataset: orders, column: x }", "used twice" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Each_bad_part_is_refused_with_file_and_line(string marked, string says)
    {
        var before = marked[..marked.IndexOf('⟦')];
        var entry = marked.Replace("⟦", "", StringComparison.Ordinal);
        // the two header lines come first; line and column are 1-based
        var line = 3 + before.Count(c => c == '\n');
        var column = before.Length - (before.LastIndexOf('\n') + 1) + 1;
        var result = Load(entry);
        Assert.False(result.Ok);
        Assert.Null(result.Checks);
        var found = result.Diagnostics.Where(d => d.Message.Contains(says, StringComparison.Ordinal) || d.Fix?.Contains(says, StringComparison.Ordinal) == true).ToList();
        Assert.True(found.Count > 0, $"no diagnostic says '{says}': {string.Join("\n", result.Diagnostics)}");
        var at = found[0];
        Assert.Equal(File, at.Path);
        Assert.Equal(ConfigSeverity.Error, at.Severity);
        Assert.Equal((line, column), (at.Line, at.Column));
        Assert.Contains($"{File}:{line}:{column}", at.ToString());
    }

    [Fact]
    public void The_diagnostics_have_codes_of_the_check_model()
    {
        Assert.Equal(CheckSection.UnknownKind, Load("  - { id: a, kind: sql, dataset: o, column: x }").Diagnostics[0].Code);
        Assert.Equal(CheckSection.BadParameter, Load("  - { id: a, kind: range, dataset: o, column: x, min: 3, max: 1 }").Diagnostics[0].Code);
        Assert.Equal(CheckSection.NoDataset, Load("  - { id: a, kind: referential, dataset: o, column: x }").Diagnostics[0].Code);
        Assert.Equal(CheckSection.BadSetting, Load("  - { id: a, kind: not_null, dataset: o, column: x, severity: low }").Diagnostics[0].Code);
        Assert.Equal(CheckSection.DuplicateId, Load("  - { id: a, kind: not_null, dataset: o, column: x }\n  - { id: a, kind: not_null, dataset: o, column: y }").Diagnostics[0].Code);
    }

    [Fact]
    public void Every_problem_of_a_file_is_reported_not_only_the_first()
    {
        var result = Load("  - { id: a, kind: range, dataset: o, column: x, min: 3, max: 1 }\n  - { id: b, kind: nope, dataset: o, column: x }\n  - { id: c, kind: not_null, column: x }");
        Assert.Equal([3, 4, 5], result.Diagnostics.Select(d => d.Line).Order());
    }

    [Theory]
    [InlineData("schema: check/2\nchecks: []\n", "up to version 1")]
    [InlineData("schema: other/1\nchecks: []\n", "Use `schema: check/1`")]
    [InlineData("checks: []\n", "`schema` is missing")]
    [InlineData("schema: check/1\n", "`checks` is missing")]
    [InlineData("schema: check/1\nchecks: yes\n", "`checks` is a list of checks")]
    [InlineData("schema: check/1\nchecs: []\nchecks: []\n", "Did you mean `checks`?")]
    [InlineData("- a\n- b\n", "is a mapping")]
    public void A_bad_file_is_refused_naming_the_file(string text, string says)
    {
        var result = CheckLoader.Load(text, File);
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Path == File && d.Line >= 1 && (d.Message.Contains(says, StringComparison.Ordinal) || d.Fix?.Contains(says, StringComparison.Ordinal) == true));
    }

    [Fact]
    public void A_damaged_file_names_the_line_of_the_syntax_error()
    {
        var result = CheckLoader.Load("schema: check/1\nchecks:\n  - { id: a, kind: [", File);
        Assert.False(result.Ok);
        Assert.Equal(File, result.Diagnostics[0].Path);
        Assert.True(result.Diagnostics[0].Line >= 3);
    }
}
