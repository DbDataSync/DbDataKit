using DbDataBuild.Core;
using DbDataBuild.Sql;
using DbDataBuild.Sql.Analysis;

namespace DbDataBuild.Tests.Unit;

public class SqlParseHintsTests
{
    [Theory]
    [InlineData("SELECT a // b FROM t", "SELECT a  / b FROM t")]
    [InlineData("SELECT 'a // b', \"c // d\" FROM t -- e // f\n", "SELECT 'a // b', \"c // d\" FROM t -- e // f\n")]
    [InlineData("SELECT /* x // y */ a // 2 FROM t", "SELECT /* x // y */ a  / 2 FROM t")]
    [InlineData("SELECT a / b FROM t", "SELECT a / b FROM t")]
    public void The_parser_is_given_integer_division_as_a_division_and_nothing_else_changes(string sql, string expected)
    {
        Assert.Equal(expected, SqlParseHints.ForParser(sql));
        Assert.Equal(sql.Length, SqlParseHints.ForParser(sql).Length);
    }

    [Fact]
    public void A_query_with_integer_division_parses_for_lineage_and_for_the_hash()
    {
        const string sql = "SELECT a // 60 AS minutes, b FROM s.t";
        Assert.True(Polyglot.Parse(sql, Dialects.Canonical).Ok);
        var (facts, error) = QueryAnalyzer.Analyze(sql);
        Assert.True(facts != null, error);
        Assert.Contains(facts!.BaseTables, t => t.QualifiedName == "s.t");
        Assert.NotNull(AstHasher.Hash(sql).Hash);
    }
}
