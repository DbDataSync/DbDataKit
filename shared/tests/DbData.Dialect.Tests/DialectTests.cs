using DbData.Dialect;
using Xunit;

namespace DbData.Dialect.Tests;

public class DialectTests
{
    private static ISqlDialect D(string id) => DialectCatalog.Builtin.Get(id);

    [Fact]
    public void Catalog_contains_all_builtins()
        => Assert.Equal(["ansi", "duckdb", "fabric", "mysql", "oracle", "postgres", "sqlserver"], DialectCatalog.Builtin.Ids);

    [Theory]
    [InlineData("sqlserver", "a]b", "[a]]b]")]
    [InlineData("postgres", "a\"b", "\"a\"\"b\"")]
    [InlineData("mysql", "a`b", "`a``b`")]
    [InlineData("oracle", "ab", "\"ab\"")]
    [InlineData("duckdb", "x y", "\"x y\"")]
    public void Quote_doubles_the_closer(string engine, string name, string expected)
        => Assert.Equal(expected, D(engine).Quote(name));

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("duckdb")]
    [InlineData("fabric")]
    public void Hostile_names_round_trip_through_quote_and_split(string engine)
    {
        var d = D(engine);
        foreach (var name in new[] { "a.b", "x]y", "x\"y", "x`y", "]]", "\"\"", "``", " lead", "sp ace", "[a]", "a;DROP TABLE t;--", "é.ü", "'; --" })
        {
            var q = d.QuoteQualified(["sch", name]);
            Assert.Equal(["sch", name], d.SplitQualifiedName(q));
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public void Control_characters_are_refused(string engine)
    {
        var d = D(engine);
        Assert.Throws<ArgumentException>(() => d.Quote("a\0b"));
        Assert.Throws<ArgumentException>(() => d.Quote("a\nb"));
        Assert.Throws<ArgumentException>(() => d.Quote(""));
        Assert.NotNull(d.ValidateName("a\tb"));
        Assert.Null(d.ValidateName("fine"));
    }

    [Fact]
    public void Oracle_cannot_quote_a_double_quote_and_mysql_trailing_space()
    {
        Assert.Throws<ArgumentException>(() => D("oracle").Quote("a\"b"));
        Assert.Throws<ArgumentException>(() => D("mysql").Quote("a "));
    }

    [Theory]
    [InlineData("sqlserver", "a.[b")]
    [InlineData("sqlserver", "a..b")]
    [InlineData("sqlserver", "a.")]
    [InlineData("postgres", "\"a")]
    [InlineData("postgres", "\"a\"b")]
    public void Split_rejects_malformed(string engine, string text)
        => Assert.Throws<FormatException>(() => D(engine).SplitQualifiedName(text));

    [Fact]
    public void Split_handles_plain_and_mixed()
    {
        Assert.Equal(["db", "dbo", "T"], D("sqlserver").SplitQualifiedName("db.[dbo] . T"));
        Assert.Equal(["a.b", "c"], D("postgres").SplitQualifiedName("\"a.b\".c"));
    }

    [Fact]
    public void IsSafeUnquoted_considers_fold_reserved_and_temp()
    {
        Assert.True(D("postgres").IsSafeUnquoted("orders"));
        Assert.False(D("postgres").IsSafeUnquoted("Orders"));
        Assert.False(D("postgres").IsSafeUnquoted("select"));
        Assert.False(D("postgres").IsSafeUnquoted("a b"));
        Assert.True(D("oracle").IsSafeUnquoted("ORDERS"));
        Assert.False(D("oracle").IsSafeUnquoted("orders"));
        Assert.True(D("sqlserver").IsSafeUnquoted("#tmp"));
        Assert.False(D("postgres").IsSafeUnquoted("#tmp"));
    }

    [Theory]
    [InlineData("sqlserver", "@p0", "@x")]
    [InlineData("oracle", ":p0", ":x")]
    [InlineData("postgres", "$1", "$1")]
    [InlineData("mysql", "?", "?")]
    [InlineData("duckdb", "$1", "$1")]
    public void Parameter_names(string engine, string byIndex, string byName)
    {
        Assert.Equal(byIndex, D(engine).ParameterName(0));
        Assert.Equal(byName, D(engine).ParameterName(0, "x"));
    }

    [Fact]
    public void Row_limits()
    {
        var ms = D("sqlserver");
        Assert.Equal("TOP (10) ", ms.RowLimit(RowLimitStyle.TopN, 10).SelectPrefix);
        var ties = ms.RowLimit(RowLimitStyle.TopN, 10, tieSafe: true);
        Assert.Equal("TOP (10) WITH TIES ", ties.SelectPrefix);
        Assert.True(ties.TieSafe && ties.RequiresOrderBy);
        Assert.Equal(" OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY", ms.RowLimit(RowLimitStyle.OffsetFetch, 10, 5).Suffix);
        Assert.Throws<ArgumentException>(() => ms.RowLimit(RowLimitStyle.TopN, 10, 5));
        Assert.Throws<ArgumentException>(() => ms.RowLimit(RowLimitStyle.LimitOffset, 10));

        var pg = D("postgres");
        Assert.Equal(" LIMIT 10 OFFSET 5", pg.RowLimit(RowLimitStyle.LimitOffset, 10, 5).Suffix);
        Assert.Equal(" LIMIT 10", pg.RowLimit(RowLimitStyle.LimitOffset, 10).Suffix);
        var pgTies = pg.RowLimit(RowLimitStyle.OffsetFetch, 3, tieSafe: true);
        Assert.EndsWith("WITH TIES", pgTies.Suffix);
        var my = D("mysql").RowLimit(RowLimitStyle.LimitOffset, 3, tieSafe: true);
        Assert.False(my.TieSafe);
        Assert.NotNull(my.Note);
    }

    [Theory]
    [InlineData("sqlserver", "it's", "'it''s'")]
    [InlineData("sqlserver", "é", "N'é'")]
    [InlineData("postgres", "a\\b'c", "'a\\b''c'")]
    [InlineData("mysql", "a\\b'c", "'a\\\\b''c'")]
    [InlineData("mysql", "trail\\", "'trail\\\\'")]
    [InlineData("oracle", "x", "'x'")]
    public void String_literals_escape(string engine, string value, string expected)
        => Assert.Equal(expected, D(engine).Literal(value, CanonicalType.String()));

    [Fact]
    public void Other_literals()
    {
        var ms = D("sqlserver"); var pg = D("postgres");
        Assert.Equal("NULL", pg.Literal(null, CanonicalType.Of(CanonicalKind.Int32)));
        Assert.Equal("1", ms.Literal(true, CanonicalType.Of(CanonicalKind.Boolean)));
        Assert.Equal("TRUE", pg.Literal(true, CanonicalType.Of(CanonicalKind.Boolean)));
        Assert.Equal("0xDEAD", ms.Literal(new byte[] { 0xDE, 0xAD }, CanonicalType.Binary()));
        Assert.Equal("'\\xDEAD'::bytea", pg.Literal(new byte[] { 0xDE, 0xAD }, CanonicalType.Binary()));
        Assert.Equal("12.50", pg.Literal(12.50m, CanonicalType.Decimal(10, 2)));
        Assert.Equal("1.5", pg.Literal(1.5, CanonicalType.Of(CanonicalKind.Double)));
        Assert.Throws<NotSupportedException>(() => pg.Literal(double.NaN, CanonicalType.Of(CanonicalKind.Double)));
        Assert.Throws<ArgumentException>(() => pg.Literal("a\0b", CanonicalType.String()));
        Assert.Equal("TIMESTAMP '2024-03-05 01:02:03.500000'", pg.Literal(new DateTime(2024, 3, 5, 1, 2, 3, 500), CanonicalType.Timestamp()));
        Assert.Equal("CAST('2024-03-05 01:02:03.5000000' AS datetime2(7))", ms.Literal(new DateTime(2024, 3, 5, 1, 2, 3, 500), CanonicalType.Timestamp()));
        Assert.Equal("TIMESTAMPTZ '2024-03-05 01:02:03.000000+02:00'", pg.Literal(new DateTimeOffset(2024, 3, 5, 1, 2, 3, TimeSpan.FromHours(2)), CanonicalType.Timestamp(tz: true)));
        Assert.Equal("'00000000-0000-0000-0000-000000000000'::uuid", pg.Literal(Guid.Empty, CanonicalType.Of(CanonicalKind.Guid)));
        Assert.Equal("'{\"a\":\"it''s\"}'::jsonb", pg.Literal("{\"a\":\"it's\"}", CanonicalType.Of(CanonicalKind.Json)));
        Assert.Equal("18446744073709551615", pg.Literal(ulong.MaxValue, new CanonicalType(CanonicalKind.Int64, Unsigned: true)));
    }

    [Fact]
    public void Capabilities_reflect_data()
    {
        Assert.True(D("sqlserver").Caps.HasFlag(EngineCaps.Merge));
        Assert.False(D("postgres").Caps.HasFlag(EngineCaps.Merge));
        Assert.True(D("postgres").Caps.HasFlag(EngineCaps.UpsertOnConflict));
        Assert.False(D("fabric").Caps.HasFlag(EngineCaps.IdentityInsert));
        Assert.True(D("sqlserver").Caps.HasFlag(EngineCaps.IdentityInsert));
        Assert.IsType<FabricDialect>(D("fabric"));
        Assert.IsType<SqlServerDialect>(D("sqlserver"));
    }

    [Fact]
    public void Odbc_dialect_from_facts()
    {
        var d = OdbcDialect.FromFacts(new OdbcFacts("Acme", IdentifierQuoteChar: "`", SupportsLimit: true, SupportsOffsetFetch: false));
        Assert.Equal("odbc:acme", d.Id.Value);
        Assert.Equal("`a``b`", d.Quote("a`b"));
        Assert.Equal(ParameterStyle.Positional, d.ParameterStyle);
        Assert.Equal(RowLimitStyle.LimitOffset, d.DefaultRowLimitStyle);
        var none = OdbcDialect.FromFacts(new OdbcFacts("Bare", IdentifierQuoteChar: " "));
        Assert.Equal("ok_name", none.Quote("ok_name"));
        Assert.Throws<NotSupportedException>(() => none.Quote("not ok"));
        Assert.Equal(["a", "b"], none.SplitQualifiedName("a.b"));
    }

    // ---- yaml loader -----------------------------------------------------------------------------------------

    private const string Min = "schema: dialect/1\nid: x\nextends: ansi\n";

    [Fact]
    public void Project_dialect_extends_builtin_and_keeps_compiled_class()
    {
        var c = DialectCatalog.Builtin.With(Min.Replace("ansi", "sqlserver") + "identifier: { max_length: 30 }\n");
        var d = c.Get("x");
        Assert.IsType<SqlServerDialect>(d);
        Assert.Equal(30, d.Identifiers.MaxLength);
        Assert.Equal("[a]", d.Quote("a"));
        Assert.DoesNotContain("x", DialectCatalog.Builtin.Ids);
    }

    [Theory]
    [InlineData("schema: dialect/2\nid: x\n", "unsupported schema")]
    [InlineData("id: x\n", "missing 'schema'")]
    [InlineData(Min + "bogus: 1\n", "unknown key 'bogus'")]
    [InlineData(Min + "id: y\n", "duplicate key")]
    [InlineData(Min + "parameters: { style: wat }\n", "unknown value")]
    [InlineData(Min + "capabilities: [nope]\n", "unknown capability")]
    [InlineData(Min + "a: &x 1\n", "anchors")]
    [InlineData(Min + "types:\n  - { native: int, canonical: int32, fidelity: lossy }\n", "needs a 'note'")]
    [InlineData(Min + "types:\n  - { native: int, canonical: blob }\n", "unknown canonical kind")]
    [InlineData(Min + "types:\n  - { native: \"x({p})\", canonical: int32, bogus: 1 }\n", "unknown key")]
    [InlineData("schema: dialect/1\nid: x\nextends: nothere\n", "unknown dialect")]
    [InlineData("schema: dialect/1\nid: x\nquote: { open: \"[\" }\n", "missing 'close'")]
    [InlineData("schema: dialect/1\nid: Upper\n", "lower-case")]
    [InlineData("schema: dialect/1\nid: x\nextends: x\n", "cycle")]
    [InlineData("a: [1\n", "")]
    public void Loader_is_strict(string yaml, string expectedFragment)
    {
        var ex = Assert.Throws<DialectException>(() => DialectCatalog.Builtin.With(yaml, "test.yaml"));
        Assert.Contains(expectedFragment, ex.Message);
        Assert.StartsWith("test.yaml", ex.Message);
    }

    [Fact]
    public void Loader_reports_line_numbers()
    {
        var ex = Assert.Throws<DialectException>(() => DialectCatalog.Builtin.With(Min + "\n\nbogus: 1\n", "t.yaml"));
        Assert.Equal(6, ex.Line);
    }
}
