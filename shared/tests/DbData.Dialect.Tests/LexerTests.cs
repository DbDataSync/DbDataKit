using DbData.Dialect;
using Xunit;

namespace DbData.Dialect.Tests;

public class LexerTests
{
    public static IEnumerable<object[]> Engines() => DialectCatalog.Builtin.Ids.Select(i => new object[] { i });

    private static List<SqlToken> Lex(string engine, string sql) => SqlLexer.Tokenize(sql, DialectCatalog.Builtin.Get(engine).Syntax);

    private static string[] Sig(string engine, string sql)
        => Lex(engine, sql).Where(t => !t.IsTrivia).Select(t => $"{t.Kind}:{t.Text}").ToArray();

    [Fact]
    public void Postgres_E_string_backslash_quote_stays_inside_string()
    {
        var t = Sig("postgres", @"SELECT E'it\'s; DROP TABLE x' , 1");
        Assert.Equal([ "Word:SELECT", @"String:E'it\'s; DROP TABLE x'", "Punctuation:,", "Number:1" ], t);
    }

    [Fact]
    public void Postgres_plain_string_backslash_is_literal()
    {
        var t = Sig("postgres", @"SELECT 'a\' ; DROP TABLE x");
        Assert.Contains("Separator:;", t);
        Assert.Contains("Word:DROP", t);
    }

    [Fact]
    public void MySql_backslash_escape_and_hash_comment_and_backtick()
    {
        var t = Sig("mysql", "SELECT 'a\\'b' , `we``ird` # trailing ' comment\n, 2");
        Assert.Equal(["Word:SELECT", "String:'a\\'b'", "Punctuation:,", "QuotedIdentifier:`we``ird`", "Punctuation:,", "Number:2"], t);
    }

    [Fact]
    public void MySql_dashdash_needs_space_and_executable_comment_is_flagged()
    {
        Assert.Contains("Operator:-", Sig("mysql", "SELECT 1--1"));
        var toks = Lex("mysql", "SELECT 1 /*!50000 DROP TABLE x */");
        Assert.Contains(toks, x => x.Executable);
    }

    [Fact]
    public void Dollar_quoting_and_tags()
    {
        var t = Sig("postgres", "SELECT $$a'b$$, $tag$ $$ ' $tag$, $1");
        Assert.Equal(["Word:SELECT", "String:$$a'b$$", "Punctuation:,", "String:$tag$ $$ ' $tag$", "Punctuation:,", "Parameter:$1"], t);
    }

    [Fact]
    public void TSql_brackets_nested_comments_and_params()
    {
        var t = Sig("sqlserver", "SELECT [a]]b] /* x /* y */ z */ , @p1, N'é', @@rowcount");
        Assert.Equal(["Word:SELECT", "QuotedIdentifier:[a]]b]", "Punctuation:,", "Parameter:@p1", "Punctuation:,", "String:N'é'", "Punctuation:,", "Word:@@rowcount"], t);
    }

    [Fact]
    public void Oracle_q_quote_and_colon_params()
    {
        var t = Sig("oracle", "SELECT q'[it's]', :name, :1 FROM dual");
        Assert.Equal("String:q'[it's]'", t[1]);
        Assert.Equal("Parameter::name", t[3]);
    }

    [Fact]
    public void Question_markers_only_where_enabled()
    {
        Assert.Contains("Parameter:?", Sig("mysql", "SELECT ?"));
        Assert.DoesNotContain("Parameter:?", Sig("postgres", "SELECT a ? b"));
    }

    [Fact]
    public void Unterminated_constructs_are_flagged_not_thrown()
    {
        foreach (var sql in new[] { "SELECT 'abc", "SELECT /* x", "SELECT \"x", "SELECT $a$ x" })
            Assert.Contains(Lex("postgres", sql), t => !t.Terminated);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Fuzz_tokens_tile_input_and_never_throw(string engine)
    {
        var syntax = DialectCatalog.Builtin.Get(engine).Syntax;
        var rnd = new Random(1234);
        string[] atoms = ["'", "\"", "`", "[", "]", "$", "$a$", "--", "/*", "*/", "#", "\\", "?", "@", ":", "::", "E'", "N'", "q'[", ";", "\n", " ", "x", "1", "1.5e+3", "é", "😀", "\0", "(", ")", ".", "-", "/", "*", "!"];
        for (int n = 0; n < 3000; n++)
        {
            var sb = new System.Text.StringBuilder();
            int len = rnd.Next(0, 24);
            for (int k = 0; k < len; k++) sb.Append(atoms[rnd.Next(atoms.Length)]);
            string input = sb.ToString();
            var tokens = SqlLexer.Tokenize(input, syntax);
            Assert.Equal(input, SqlLexer.Join(tokens));
            int pos = 0;
            foreach (var t in tokens) { Assert.Equal(pos, t.Start); Assert.True(t.Length > 0); pos = t.End; }
            Assert.Equal(input.Length, pos);
        }
    }

    [Fact]
    public void Fuzz_random_chars_every_syntax_combination()
    {
        var rnd = new Random(99);
        for (int n = 0; n < 4000; n++)
        {
            var syntax = (SqlSyntax)rnd.Next(0, 1 << 20);
            var chars = new char[rnd.Next(0, 30)];
            for (int k = 0; k < chars.Length; k++) chars[k] = (char)rnd.Next(rnd.Next(2) == 0 ? 32 : 0, rnd.Next(2) == 0 ? 127 : 0x3000);
            string input = new(chars);
            Assert.Equal(input, SqlLexer.Join(SqlLexer.Tokenize(input, syntax)));
        }
    }

    [Fact]
    public void Parameter_rewriter_skips_strings_and_comments()
    {
        var pg = DialectCatalog.Builtin.Get("postgres");
        var r = SqlParameterRewriter.Rewrite("SELECT '?', ? /* ? */ , \"?\", ?", SqlSyntax.QuestionParameter | SqlSyntax.DoubleQuoteIdentifier, pg);
        Assert.Equal("SELECT '?', $1 /* ? */ , \"?\", $2", r.Sql);
        var ms = SqlParameterRewriter.Rewrite("select :a, :b, :a", SqlSyntax.ColonParameter, DialectCatalog.Builtin.Get("sqlserver"));
        Assert.Equal("select @a, @b, @a", ms.Sql);
        var pos = SqlParameterRewriter.Rewrite("select @a, @b, @a", SqlSyntax.AtParameter, DialectCatalog.Builtin.Get("mysql"));
        Assert.Equal("select ?, ?, ?", pos.Sql);
        Assert.Equal(["a", "b", "a"], pos.BindingOrder);
        Assert.Equal(["a", "b"], pos.DistinctNames);
    }
}
