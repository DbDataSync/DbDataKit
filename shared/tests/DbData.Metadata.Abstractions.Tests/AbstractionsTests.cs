using DbData.Dialect;

namespace DbData.Metadata.Tests;

public class LikePatternTests
{
    [Theory]
    [InlineData("a%", "abc", true)]
    [InlineData("a%", "ABC", true)]
    [InlineData("a_c", "abc", true)]
    [InlineData("a_c", "ac", false)]
    [InlineData("%b%", "abc", true)]
    [InlineData("%b", "abc", false)]
    [InlineData("a\\%c", "a%c", true)]
    [InlineData("a\\%c", "abc", false)]
    [InlineData("a\\_c", "a_c", true)]
    [InlineData("a\\_c", "abc", false)]
    [InlineData("a\\\\c", "a\\c", true)]
    [InlineData("%", "", true)]
    [InlineData("", "anything", true)]
    [InlineData("a.c", "abc", false)]
    [InlineData("[ab]", "a", false)]
    public void Matches_like_semantics(string pattern, string text, bool expected) =>
        Assert.Equal(expected, LikePattern.Compile(pattern)(text));

    [Fact]
    public void Escaped_literal_matches_only_itself()
    {
        string pattern = LikePattern.Escape("100%_done\\");
        Assert.True(LikePattern.Compile(pattern)("100%_done\\"));
        Assert.False(LikePattern.Compile(pattern)("100x_done\\"));
        Assert.Equal("100%_done\\", LikePattern.AsLiteral(pattern));
    }

    [Fact]
    public void Hostile_patterns_run_in_linear_time()
    {
        string pattern = string.Concat(Enumerable.Repeat("%a", 2000)) + "b";
        var match = LikePattern.Compile(pattern);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(match(new string('a', 5000)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Wildcard_detection_respects_the_escape()
    {
        Assert.True(LikePattern.HasWildcards("a%"));
        Assert.False(LikePattern.HasWildcards("a\\%"));
        Assert.Null(LikePattern.AsLiteral("a%"));
    }
}

public class TableFilterTests
{
    private static readonly TableRef[] Tables =
    [
        new("db", "dbo", "Orders"), new("db", "dbo", "Order_Lines"), new("db", "sales", "OrdersView", TableKind.View),
        new("db", "sys", "objects", TableKind.SystemTable), new("other", "dbo", "Orders"),
    ];

    [Fact]
    public void Default_filter_keeps_tables_and_views_but_not_system_objects() =>
        Assert.Equal(4, Tables.Count(TableFilter.All.Compile()));

    [Fact]
    public void Catalog_is_exact_and_schema_and_name_are_patterns() =>
        Assert.Equal(["Orders", "Order_Lines"], Tables.Where(new TableFilter("db", "d%", "Order%").Compile()).Select(t => t.Name));

    [Fact]
    public void Exactly_escapes_wildcards_in_names() =>
        Assert.Equal(["Order_Lines"], Tables.Where(TableFilter.Exactly("db", "dbo", "Order_Lines").Compile()).Select(t => t.Name));

    [Fact]
    public void Underscore_in_a_pattern_is_a_wildcard_unless_escaped()
    {
        Assert.Equal(4, Tables.Count(new TableFilter(NamePattern: "Order_%").Compile()));
        Assert.Equal(1, Tables.Count(new TableFilter(NamePattern: "Order\\_%").Compile()));
    }

    [Fact]
    public void System_objects_are_included_only_on_request() =>
        Assert.Contains(Tables, t => new TableFilter(IncludeSystemObjects: true).Compile()(t) && t.Kind == TableKind.SystemTable);
}

public class CatalogNamesTests
{
    [Theory]
    [InlineData("ok name", true, null)]
    [InlineData("a\u0000b", true, "control character")]
    [InlineData("a\nb", false, "control character")]
    [InlineData("", true, "empty name")]
    [InlineData("it's", false, "unterminated ' quote")]
    [InlineData("a\"b", false, "unterminated \" quote")]
    [InlineData("a[b", false, "unterminated [ bracket")]
    [InlineData("x; DROP", false, "statement separator or comment in an unquotable name")]
    [InlineData("it's", true, null)]
    [InlineData("a\"b", true, null)]
    public void Validate(string name, bool quoteChar, string? reason) => Assert.Equal(reason, CatalogNames.Validate(name, quoteChar));

    [Fact]
    public void Quote_goes_through_the_dialect_and_doubles_the_closer()
    {
        var d = DialectCatalog.For(EngineId.SqlServer);
        Assert.Equal("[a]]b]", CatalogNames.Quote(d, "a]b"));
        Assert.Equal("[db].[dbo].[T]", CatalogNames.QuoteTable(d, new TableRef("db", "dbo", "T")));
        Assert.Equal("[dbo].[T]", CatalogNames.QuoteTable(d, new TableRef(null, "dbo", "T")));
    }

    [Fact]
    public void Control_characters_are_refused_even_with_a_quote_character()
    {
        var d = DialectCatalog.For(EngineId.Postgres);
        Assert.Throws<CatalogNameException>(() => CatalogNames.Quote(d, "a\u0001b"));
    }

    [Fact]
    public void A_driver_without_quotes_refuses_unterminated_quote_sequences()
    {
        var d = OdbcDialect.FromFacts(new OdbcFacts("nolang", IdentifierQuoteChar: " "));
        var ex = Assert.Throws<CatalogNameException>(() => CatalogNames.QuoteTable(d, new TableRef(null, "s", "bad'name"), driverReportsQuote: false));
        Assert.Contains("quote", ex.Message);
    }
}

public class FingerprintTests
{
    [Fact]
    public void Secrets_and_key_order_do_not_change_the_fingerprint()
    {
        string a = ConnectionFingerprint.Of("Driver={SQL Server};Server=s;Database=d;UID=u;PWD=one");
        string b = ConnectionFingerprint.Of("database=d; server=s; Driver={SQL Server}; uid=u; pwd=two");
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void Different_databases_differ() =>
        Assert.NotEqual(ConnectionFingerprint.Of("Server=s;Database=a"), ConnectionFingerprint.Of("Server=s;Database=b"));

    [Fact]
    public void A_semicolon_inside_braces_does_not_split()
    {
        string a = ConnectionFingerprint.Of("Driver={A;B};Server=s");
        Assert.NotEqual(a, ConnectionFingerprint.Of("Driver={A;C};Server=s"));
    }
}

public class TableRefTests
{
    [Fact]
    public void Parts_skip_missing_catalog_and_schema()
    {
        Assert.Equal(["s", "t"], new TableRef(null, "s", "t").Parts);
        Assert.Equal(["t"], new TableRef(null, null, "t").Parts);
        Assert.Equal("c.s.t", new TableRef("c", "s", "t").ToString());
    }

    [Fact]
    public void Key_is_unambiguous_for_names_containing_separators() =>
        Assert.NotEqual(new TableRef("a|b", null, "c").Key, new TableRef("a", "b", "c").Key);
}
