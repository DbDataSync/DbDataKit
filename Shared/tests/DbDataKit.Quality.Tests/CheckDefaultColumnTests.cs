using DbDataKit.Config.Yaml;

namespace DbDataKit.Quality.Tests;

/// <summary>Plan 23a FQ02: the checks written in a column's own declaration name neither the dataset nor the column; the host gives both.</summary>
public sealed class CheckDefaultColumnTests
{
    private static (IReadOnlyList<CheckDefinition>? Checks, List<ConfigDiagnostic> Diagnostics) Read(string yaml, string? column = "customer_id")
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var node = StrictYamlReader.Read(yaml, "orders.yml", diagnostics);
        Assert.NotNull(node);
        Assert.Empty(diagnostics);
        var checks = CheckSection.Read(node, "orders.yml", diagnostics, defaultDataset: "marts.orders", defaultColumn: column);
        return (checks, diagnostics);
    }

    [Fact]
    public void A_check_that_names_no_column_is_about_the_default_column()
    {
        var (checks, diagnostics) = Read("- { id: a, kind: not_null }\n- { id: b, kind: accepted_values, values: [x, y] }\n- { id: c, kind: range, min: 1 }\n- { id: d, kind: freshness, max_age: 1d }");
        Assert.Empty(diagnostics);
        Assert.All(checks!, c => Assert.Equal(["customer_id"], c.Attachment.Columns));
        Assert.All(checks!, c => Assert.Equal("marts.orders", c.Attachment.Dataset));
    }

    [Fact]
    public void A_referential_check_pairs_the_default_column_with_the_target_column()
    {
        var (checks, diagnostics) = Read("- { id: r, kind: referential, references: { dataset: marts.customers, column: id } }");
        Assert.Empty(diagnostics);
        Assert.Equal(["customer_id"], checks!.Single().Attachment.Columns);
    }

    [Fact]
    public void Row_count_change_is_about_the_whole_dataset_whatever_the_default_column()
    {
        var (checks, diagnostics) = Read("- { id: n, kind: row_count_change, max_decrease_percent: 20 }");
        Assert.Empty(diagnostics);
        Assert.Empty(checks!.Single().Attachment.Columns);
    }

    [Fact]
    public void Without_a_default_column_a_check_still_needs_its_column()
    {
        var (checks, diagnostics) = Read("- { id: a, kind: not_null }", column: null);
        Assert.Null(checks);
        Assert.Contains(diagnostics, d => d.Message.Contains("needs the column"));
    }

    [Fact]
    public void A_column_the_check_names_wins_over_the_default()
    {
        var (checks, _) = Read("- { id: a, kind: not_null, column: other }");
        Assert.Equal(["other"], checks!.Single().Attachment.Columns);
    }
}
