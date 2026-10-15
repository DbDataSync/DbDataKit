using Xunit;

namespace DbData.Dialect.Conformance;

/// <summary>
/// Offline conformance suite. Derive one class per dialect and implement <see cref="CreateDialect"/>; adding an engine
/// means adding its dialect.yaml and a two-line subclass here. Everything runs in-process, with the lexer as the oracle
/// for what a quoted identifier or literal looks like.
/// </summary>
public abstract class DialectConformanceTests
{
    protected abstract ISqlDialect CreateDialect();

    public static readonly string[] HostileNames =
    [
        "plain", "with space", "dot.ted", "a;b", "x]y", "x[y", "x\"y", "x`y", "x'y", "]]", "\"\"", "``", "''",
        "--", "/*", "*/", "'; DROP TABLE t; --", "\\", "tail\\", "é.ü", "😀", "select", "Mixed Case", "-- x", "#tmp", "@v", ":p", "$1", "?",
    ];

    public static readonly string[] HostileStrings =
    [
        "", "plain", "it's", "''", "\\", "a\\", "\\'", "'; DROP TABLE t; --", "/* c */", "-- c", "$$", "$a$ x $a$", "é", "😀", "line1\nline2", "tab\t", "%_",
    ];

    private SqlSyntax S => CreateDialect().Syntax;

    [Fact]
    public void Identity_and_parameter_style_are_consistent()
    {
        var d = CreateDialect();
        Assert.False(string.IsNullOrEmpty(d.Id.Value));
        Assert.Contains(d.DefaultRowLimitStyle, d.SupportedRowLimitStyles);
        Assert.NotEmpty(d.SupportedRowLimitStyles);
        Assert.StartsWith(d.ParameterStyle switch { ParameterStyle.NamedAt => "@", ParameterStyle.NamedColon => ":", ParameterStyle.Numbered => "$", _ => "?" }, d.ParameterName(0));
    }

    [Fact]
    public void Quote_round_trips_hostile_names_through_split()
    {
        var d = CreateDialect();
        foreach (var name in HostileNames)
        {
            if (d.ValidateName(name) is not null) { Assert.ThrowsAny<ArgumentException>(() => d.Quote(name)); continue; }
            string q;
            try { q = d.Quote(name); }
            catch (NotSupportedException) { continue; }          // engine without quoting and not a plain name
            Assert.Equal([name], d.SplitQualifiedName(q));
            Assert.Equal(["s", name, "t"], d.SplitQualifiedName(d.QuoteQualified(["s", name, "t"])));
        }
    }

    [Fact]
    public void Quote_always_doubles_the_closer_and_lexes_as_one_identifier()
    {
        var d = CreateDialect();
        foreach (var name in HostileNames)
        {
            if (d.ValidateName(name) is not null) continue;
            string q;
            try { q = d.Quote(name); } catch (NotSupportedException) { continue; }
            if (q == name) continue;                              // unquoted plain name
            var tokens = SqlLexer.Tokenize(q, d.Syntax).ToList();
            Assert.True(tokens.Count == 1 && tokens[0].Kind == SqlTokenKind.QuotedIdentifier && tokens[0].Terminated,
                $"{d.Id}: Quote({name}) = {q} did not lex as a single quoted identifier: {string.Join(" | ", tokens.Select(t => t.Kind + ":" + t.Text))}");
        }
    }

    [Fact]
    public void Quoted_names_stay_inert_inside_a_statement()
    {
        var d = CreateDialect();
        foreach (var name in HostileNames)
        {
            if (d.ValidateName(name) is not null) continue;
            string q;
            try { q = d.Quote(name); } catch (NotSupportedException) { continue; }
            var c = SqlStatementClassifier.Classify($"SELECT {q} FROM {q}", d);
            Assert.Single(c.Statements);
            Assert.Equal(SqlStatementKind.Select, c.Statements[0].Kind);
            Assert.DoesNotContain(c.Findings, f => f.Code != "deny-word" && f.Code != "deny-prefix");
        }
    }

    [Fact]
    public void Split_rejects_malformed_input()
    {
        var d = CreateDialect();
        Assert.Throws<FormatException>(() => d.SplitQualifiedName("a."));
        Assert.Throws<FormatException>(() => d.SplitQualifiedName(".a"));
        Assert.Throws<FormatException>(() => d.SplitQualifiedName(""));
        if (d.Syntax != SqlSyntax.None && d is DataDrivenDialect { CanQuote: true })
        {
            string open = d.Quote("x")[..1];
            Assert.Throws<FormatException>(() => d.SplitQualifiedName(open + "unterminated"));
        }
    }

    [Fact]
    public void Control_characters_and_empty_names_are_refused()
    {
        var d = CreateDialect();
        foreach (var bad in new[] { "", "a\0b", "a\nb", "a\tb", "a\u0007" })
        {
            Assert.NotNull(d.ValidateName(bad));
            Assert.ThrowsAny<ArgumentException>(() => d.Quote(bad));
        }
        Assert.Null(d.ValidateName("fine_name"));
        if (d.Identifiers.MaxLength > 0) Assert.NotNull(d.ValidateName(new string('x', d.Identifiers.MaxLength + 1)));
    }

    [Fact]
    public void IsSafeUnquoted_implies_lexes_as_one_word()
    {
        var d = CreateDialect();
        foreach (var name in HostileNames.Concat(["orders", "ORDERS", "Orders", "a1", "_x"]))
        {
            if (!d.IsSafeUnquoted(name)) continue;
            var tokens = SqlLexer.Tokenize(name, d.Syntax);
            Assert.True(tokens.Count == 1 && tokens[0].Kind == SqlTokenKind.Word, $"{d.Id}: '{name}' claimed safe but lexes as {string.Join("|", tokens.Select(t => t.Kind))}");
        }
        Assert.False(d.IsSafeUnquoted("select"));
        Assert.False(d.IsSafeUnquoted("a b"));
        Assert.False(d.IsSafeUnquoted("a;b"));
    }

    [Fact]
    public void String_literals_lex_as_exactly_one_string()
    {
        var d = CreateDialect();
        var type = CanonicalType.String();
        foreach (var s in HostileStrings)
        {
            var lit = d.Literal(s, type);
            var tokens = SqlLexer.Tokenize(lit, d.Syntax).ToList();
            Assert.True(tokens.Count == 1 && tokens[0].Kind == SqlTokenKind.String && tokens[0].Terminated,
                $"{d.Id}: Literal({s}) = {lit} lexed as {string.Join(" | ", tokens.Select(t => t.Kind + ":" + t.Text))}");
        }
        Assert.Throws<ArgumentException>(() => d.Literal("a\0b", type));
    }

    [Fact]
    public void Scalar_literals()
    {
        var d = CreateDialect();
        Assert.Equal("NULL", d.Literal(null, CanonicalType.Of(CanonicalKind.Int32)));
        Assert.Equal("42", d.Literal(42, CanonicalType.Of(CanonicalKind.Int32)));
        Assert.Equal("-9223372036854775808", d.Literal(long.MinValue, CanonicalType.Of(CanonicalKind.Int64)));
        Assert.Equal("12.50", d.Literal(12.50m, CanonicalType.Decimal(10, 2)));
        Assert.Equal("0.1", d.Literal(0.1, CanonicalType.Of(CanonicalKind.Double)));
        Assert.Throws<NotSupportedException>(() => d.Literal(double.PositiveInfinity, CanonicalType.Of(CanonicalKind.Double)));
        Assert.NotEqual(d.Literal(true, CanonicalType.Of(CanonicalKind.Boolean)), d.Literal(false, CanonicalType.Of(CanonicalKind.Boolean)));
        foreach (var lit in new[]
        {
            d.Literal(new byte[] { 0, 1, 0xFF }, CanonicalType.Binary()),
            d.Literal(new DateOnly(2024, 2, 29), CanonicalType.Of(CanonicalKind.Date)),
            d.Literal(new DateTime(2024, 2, 29, 13, 5, 9, 120), CanonicalType.Timestamp(3)),
            d.Literal(new DateTimeOffset(2024, 2, 29, 13, 5, 9, TimeSpan.FromHours(-5)), CanonicalType.Timestamp(tz: true)),
            d.Literal(new TimeOnly(13, 5, 9), CanonicalType.Time()),
            d.Literal(Guid.NewGuid(), CanonicalType.Of(CanonicalKind.Guid)),
        })
        {
            Assert.DoesNotContain(';', lit);
            // every quote opened in a literal is closed: the lexer must see no unterminated token
            Assert.All(SqlLexer.Tokenize(lit, d.Syntax), t => Assert.True(t.Terminated, $"{d.Id}: {lit}"));
        }
    }

    [Fact]
    public void Row_limits_for_every_supported_style()
    {
        var d = CreateDialect();
        foreach (var style in d.SupportedRowLimitStyles)
        {
            var plain = d.RowLimit(style, 10);
            Assert.Contains("10", plain.SelectPrefix + plain.Suffix);
            Assert.False(plain.TieSafe);
            if (style != RowLimitStyle.TopN)
            {
                var paged = d.RowLimit(style, 10, 20);
                Assert.Contains("20", paged.Suffix);
                Assert.True(paged.RequiresOrderBy || style == RowLimitStyle.LimitOffset);
            }
            else Assert.Throws<ArgumentException>(() => d.RowLimit(style, 10, 5));
            var ties = d.RowLimit(style, 10, tieSafe: true);
            Assert.Equal(ties.TieSafe, (ties.SelectPrefix + ties.Suffix).Contains("WITH TIES"));
            if (!ties.TieSafe) Assert.False(string.IsNullOrEmpty(ties.Note));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.RowLimit(style, -1));
        }
        foreach (var style in Enum.GetValues<RowLimitStyle>().Except(d.SupportedRowLimitStyles))
            Assert.Throws<ArgumentException>(() => d.RowLimit(style, 1));
    }

    [Fact]
    public void Parameter_markers_are_recognised_by_the_lexer_and_rewritten()
    {
        var d = CreateDialect();
        var r = SqlParameterRewriter.Rewrite("SELECT ?, '?', ?", SqlSyntax.QuestionParameter | SqlSyntax.DoubleQuoteIdentifier, d);
        Assert.Equal(["p1", "p2"], r.DistinctNames);
        var found = SqlParameterRewriter.Find(r.Sql, d.Syntax);
        Assert.Equal(2, found.Count);
        Assert.Contains("'?'", r.Sql);
        Assert.Equal(d.ParameterName(0, "p1"), found[0].Text);
        Assert.Equal(d.ParameterName(1, "p2"), found[1].Text);
    }

    [Fact]
    public void Type_table_is_total_and_honest()
    {
        var d = CreateDialect();
        string[] canon =
        [
            "boolean", "int8", "int16", "int32", "int64", "int32(unsigned)", "int64(unsigned)", "decimal(18,2)", "decimal(38,10)", "decimal(60,2)",
            "float", "double", "string(50)", "string(unicode,50)", "string(unicode)", "binary(16)", "binary", "date", "time", "timestamp", "timestamp(tz)",
            "guid", "json", "xml", "interval", "list",
        ];
        foreach (var c in canon)
        {
            var m = d.ColumnType(CanonicalType.Parse(c));
            if (!m.IsMapped) { Assert.Null(m.Result); Assert.False(string.IsNullOrEmpty(m.Note), $"{d.Id} {c}"); continue; }
            Assert.False(string.IsNullOrWhiteSpace(m.Result));
            if (m.Fidelity != Fidelity.Exact) Assert.False(string.IsNullOrEmpty(m.Note), $"{d.Id} {c} is {m.Fidelity} without a note");
            var back = d.ToCanonical(m.Result!);
            Assert.True(back.IsMapped, $"{d.Id}: rendered '{m.Result}' for {c} but cannot read it back");
        }
        var unknown = d.ToCanonical("definitely_not_a_type");
        Assert.Equal(Fidelity.Unmappable, unknown.Fidelity);
        Assert.Equal(CanonicalKind.Unmappable, unknown.Result!.Kind);
    }

    [Fact]
    public void Read_only_policy_blocks_the_hostile_corpus()
    {
        var d = CreateDialect();
        Assert.True(ReadOnlyPolicy.Check("SELECT 1", d).Allowed);
        foreach (var sql in new[] { "", "SELECT 1; SELECT 2", "DROP TABLE t", "INSERT INTO t VALUES (1)", "UPDATE t SET a = 1", "SELECT * INTO x FROM t", "SELECT 'unterminated", "DELETE FROM t" })
            Assert.False(ReadOnlyPolicy.Check(sql, d).Allowed, $"{d.Id}: {sql}");
        // a hostile identifier that is quoted must not trigger the keyword scan
        if (d is DataDrivenDialect { CanQuote: true })
            Assert.True(ReadOnlyPolicy.Check($"SELECT {d.Quote("drop")} FROM {d.Quote("delete")}", d).Allowed);
    }

    protected SqlSyntax Syntax => S;
}
