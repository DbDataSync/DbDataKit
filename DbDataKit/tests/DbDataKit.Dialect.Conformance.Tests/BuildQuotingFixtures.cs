using Xunit;

namespace DbDataKit.Dialect.Conformance.Tests;

/// <summary>
/// The quoting cases DbDataBuild's DDL generators, tracking DDL and loaders rely on (plan 13, B01), pinned here against the shared dialects they quote through.
/// Build's own `DdlGeneratorTests` check the statement text; these check the rule underneath, so a change to a dialect fails in the shared suite first.
/// </summary>
public sealed class BuildQuotingFixtures
{
    [Theory]
    [InlineData("sqlserver", "orders", "[orders]")]
    [InlineData("sqlserver", "we]ird", "[we]]ird]")]
    [InlineData("sqlserver", "with space", "[with space]")]
    [InlineData("sqlserver", "select", "[select]")]
    [InlineData("sqlserver", "é.ü", "[é.ü]")]
    [InlineData("fabric", "orders", "[orders]")]
    [InlineData("fabric", "we]ird", "[we]]ird]")]
    [InlineData("postgres", "orders", "\"orders\"")]
    [InlineData("postgres", "we\"ird", "\"we\"\"ird\"")]
    [InlineData("postgres", "Mixed Case", "\"Mixed Case\"")]
    [InlineData("postgres", "select", "\"select\"")]
    public void Identifiers_are_quoted_as_Build_writes_them(string dialect, string name, string expected)
    {
        var d = DialectCatalog.Builtin.Get(dialect);
        Assert.Equal(expected, d.Quote(name));
        Assert.Equal([name], d.SplitQualifiedName(expected));
    }

    [Theory]
    [InlineData("sqlserver", "[marts].[fct]")]
    [InlineData("fabric", "[marts].[fct]")]
    [InlineData("postgres", "\"marts\".\"fct\"")]
    public void A_schema_and_table_are_quoted_part_by_part(string dialect, string expected) =>
        Assert.Equal(expected, DialectCatalog.Builtin.Get(dialect).QuoteQualified(["marts", "fct"]));

    [Theory, InlineData("sqlserver"), InlineData("fabric"), InlineData("postgres")]
    public void An_empty_name_and_a_name_with_a_control_character_are_refused(string dialect)
    {
        var d = DialectCatalog.Builtin.Get(dialect);
        Assert.ThrowsAny<ArgumentException>(() => d.Quote(""));
        Assert.ThrowsAny<ArgumentException>(() => d.Quote("a\u0001b"));
    }
}
