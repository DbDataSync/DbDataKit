using DbDataKit.Config.Yaml;

namespace DbDataKit.Quality.Tests;

/// <summary>Plan 33 UE07: a check as a screen sends it (<see cref="CheckForm"/>) is written as a <c>checks:</c> section the strict reader reads back as the same check, whatever the text in it, and what is wrong is the reader's refusal, not a second validator.</summary>
public sealed class CheckFormTests
{
    private const string Identity = "postgres://pg01:5432/dwh.public.orders";

    private static (IReadOnlyList<CheckDefinition>? Checks, List<ConfigDiagnostic> Diagnostics, string Yaml) RoundTrip(params CheckForm[] forms)
    {
        var yaml = CheckForms.ToYaml(forms);
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, "task.yaml", diagnostics);
        Assert.Empty(diagnostics);
        var checks = CheckSection.Read(((YamlMapping)root!).GetEntry("checks")!.Value, "task.yaml", diagnostics);
        return (checks, diagnostics, yaml);
    }

    public static TheoryData<string, CheckForm> Kinds() => new()
    {
        { "not_null", new CheckForm { Id = "a", Kind = "not_null", Dataset = Identity, Column = "name", Severity = "block", OnError = "alert" } },
        { "unique, two columns", new CheckForm { Id = "b", Kind = "unique", Dataset = "dbo.orders", Columns = ["region", "order_no"], Severity = "warn", OnError = "alert" } },
        { "accepted_values", new CheckForm { Id = "c", Kind = "accepted_values", Dataset = "dbo.orders", Column = "status", Values = ["open", "yes", "no", "in progress", "a,b"], Severity = "warn", OnError = "alert" } },
        { "range of numbers", new CheckForm { Id = "d", Kind = "range", Dataset = "dbo.orders", Column = "amount", Min = "-5", Max = "1000.5", Severity = "block", OnError = "block" } },
        { "range of dates", new CheckForm { Id = "e", Kind = "range", Dataset = "dbo.orders", Column = "placed", Min = "2026-01-31", Severity = "warn", OnError = "alert" } },
        { "referential", new CheckForm { Id = "f", Kind = "referential", Dataset = "dbo.orders", Column = "customer_id", References = new CheckReferenceForm { Dataset = Identity, Column = "id" }, Severity = "block", OnError = "alert" } },
        { "freshness in hours", new CheckForm { Id = "g", Kind = "freshness", Dataset = "dbo.orders", MaxAge = "6h", Severity = "block", OnError = "alert", Description = "Loaded by 06:00: \"the morning load\"" } },
        { "freshness by a column", new CheckForm { Id = "h", Kind = "freshness", Dataset = "dbo.orders", Column = "loaded_at", MaxAge = "1d", Severity = "warn", OnError = "alert" } },
        { "row_count_change", new CheckForm { Id = "i", Kind = "row_count_change", Dataset = "dbo.orders", MaxIncreasePercent = "50", MaxDecreasePercent = "20.5", Severity = "warn", OnError = "alert" } },
    };

    [Theory, MemberData(nameof(Kinds))]
    public void A_form_of_every_kind_is_written_and_read_back_as_the_same_check(string kind, CheckForm form)
    {
        var (checks, diagnostics, yaml) = RoundTrip(form);

        Assert.True(diagnostics.Count == 0, $"{kind}: {string.Join("; ", diagnostics)}\n{yaml}");
        var back = CheckForms.Of(Assert.Single(checks!));
        // compared as the JSON a screen would see (the lists are compared by their items)
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(form), System.Text.Json.JsonSerializer.Serialize(back));
    }

    [Fact]
    public void Several_checks_keep_their_order_and_no_check_writes_no_section()
    {
        var (checks, _, _) = RoundTrip(new CheckForm { Id = "z_last", Kind = "unique", Dataset = "d", Column = "k" }, new CheckForm { Id = "a_first", Kind = "not_null", Dataset = "d", Column = "k" });

        Assert.Equal(["z_last", "a_first"], checks!.Select(c => c.Id));
        Assert.Equal("", CheckForms.ToYaml([]));
    }

    [Fact]
    public void Only_what_a_form_sets_is_written_and_the_defaults_are_the_readers()
    {
        var yaml = CheckForms.ToYaml([new CheckForm { Id = "a", Kind = "not_null", Dataset = "d", Column = "k", Severity = "", Description = " " }]);

        Assert.Equal("checks:\n  - id: a\n    kind: not_null\n    dataset: d\n    column: k\n", yaml);
    }

    [Fact]
    public void A_form_that_is_not_a_check_is_refused_by_the_reader_with_a_line_and_the_near_kind()
    {
        var yaml = CheckForms.ToYaml([new CheckForm { Id = "a", Kind = "not_null", Dataset = "d", Column = "k" }, new CheckForm { Id = "b", Kind = "freshnes", Dataset = "d", MaxAge = "6h" }]);
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, "task.yaml", diagnostics);

        var checks = CheckSection.Read(((YamlMapping)root!).GetEntry("checks")!.Value, "task.yaml", diagnostics);

        Assert.Null(checks);
        var bad = Assert.Single(diagnostics);
        Assert.Equal(CheckSection.UnknownKind, bad.Code);
        Assert.Equal(7, bad.Line);                        // the line of `kind: freshnes` in the text above
        Assert.Contains("Did you mean `freshness`?", bad.Message);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(90 * 60, "90m")]
    [InlineData(6 * 3600, "6h")]
    [InlineData(86400, "1d")]
    [InlineData(2 * 86400, "2d")]
    public void An_age_is_written_in_the_largest_unit_that_divides_it(int seconds, string expected) => Assert.Equal(expected, CheckForms.AgeText(TimeSpan.FromSeconds(seconds)));
}
