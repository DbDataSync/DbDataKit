using DbDataBuild.Sql;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Canaries for the polyglot behavior recorded in spike/RESULTS.md, at the pinned commit. Each asserts what is *emitted*,
/// including known-wrong output. A failure after bumping the pin means upstream changed: re-run the spike, then update
/// the matrix rows and RESULTS.md. These are not endorsements of the output.
/// </summary>
public class PolyglotKnownGapsTests
{
    public PolyglotKnownGapsTests() =>
        Assert.True(Polyglot.IsAvailable(), "Native polyglot library not found. Run scripts/build-polyglot.sh.");

    private static string To(string target, string sql, string options = "{}")
    {
        var (o, text) = Polyglot.TranspileOne(sql, Dialects.Canonical, Dialects.ForTarget(target), options);
        Assert.True(o.Ok, o.Error);
        return text!;
    }

    [Fact]
    public void Division_is_cast_to_float_for_sqlserver_and_postgres_but_not_fabric()
    {
        Assert.Equal("SELECT CAST(a AS FLOAT) / NULLIF(b, 0) AS x FROM t", To("sqlserver", "SELECT a / b AS x FROM t"));
        Assert.Equal("SELECT CAST(a AS DOUBLE PRECISION) / NULLIF(b, 0) AS x FROM t", To("postgres", "SELECT a / b AS x FROM t"));
        Assert.Equal("SELECT a / b AS x FROM t", To("fabric", "SELECT a / b AS x FROM t")); // differs from tsql; integer division if Fabric follows T-SQL (not tested on Fabric)
    }

    [Fact]
    public void Qualify_is_rewritten_for_sqlserver_but_left_in_place_for_fabric()
    {
        const string sql = "SELECT a FROM t QUALIFY ROW_NUMBER() OVER (PARTITION BY a ORDER BY b) = 1";
        Assert.DoesNotContain("QUALIFY", To("sqlserver", sql));
        Assert.Contains("QUALIFY", To("fabric", sql)); // left in place; Fabric documents QUALIFY as supported
    }

    [Fact]
    public void Varchar_length_is_lost_and_unicode_literals_get_no_n_prefix()
    {
        Assert.Equal("SELECT CAST(s AS VARCHAR(MAX)) AS x FROM t", To("sqlserver", "SELECT CAST(s AS VARCHAR(20)) AS x FROM t"));
        Assert.Equal("SELECT 'héllo ☃' AS x", To("sqlserver", "SELECT 'héllo ☃' AS x"));
    }

    [Fact]
    public void Try_cast_loses_its_semantics_on_postgres()
    {
        Assert.StartsWith("SELECT TRY_CAST(", To("sqlserver", "SELECT TRY_CAST(s AS INTEGER) AS x FROM t"));
        Assert.Equal("SELECT CAST(s AS INT) AS x FROM t", To("postgres", "SELECT TRY_CAST(s AS INTEGER) AS x FROM t"));
    }

    [Theory]
    [InlineData("SELECT a, COUNT(*) FROM t GROUP BY 1")]
    [InlineData("SELECT a, COUNT(*) FROM t GROUP BY ALL")]
    [InlineData("SELECT [1, 2, 3] AS l")]
    [InlineData("SELECT {'a': 1} AS s")]
    public void Unsupported_constructs_pass_through_tsql_silently_even_with_unsupported_level_raise(string sql)
    {
        const string raise = """{"unsupportedLevel":"raise"}""";
        Assert.True(Polyglot.Transpile(sql, Dialects.Canonical, "tsql", raise).Ok);
        Assert.True(Polyglot.Transpile(sql, Dialects.Canonical, "fabric", raise).Ok);
    }

    [Theory]
    [InlineData("SELECT UNNEST([1, 2, 3]) AS u")]
    [InlineData("SELECT * FROM t WHERE REGEXP_MATCHES(s, 'a')")]
    [InlineData("SELECT * FROM t LEFT JOIN u USING (a)")]
    [InlineData("SELECT * FROM t NATURAL JOIN u")]
    public void Unsupported_level_raise_catches_unnest_regex_and_join_using_natural(string sql)
    {
        const string raise = """{"unsupportedLevel":"raise"}""";
        Assert.False(Polyglot.Transpile(sql, Dialects.Canonical, "tsql", raise).Ok);
        Assert.True(Polyglot.Transpile(sql, Dialects.Canonical, "tsql").Ok); // default mode passes them through
    }
}
