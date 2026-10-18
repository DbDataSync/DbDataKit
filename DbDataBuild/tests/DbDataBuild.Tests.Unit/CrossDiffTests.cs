using DbDataBuild.Execution;
using DbDataBuild.State;
using Xunit;

namespace DbDataBuild.Tests.Unit;

public class CrossDiffTests
{
    private static ColumnShape Col(string name, string type, int? precision = null, int? scale = null, int? length = null) =>
        new(name, type, length, precision, scale, true, null);

    private static DiffTable Table(params ColumnShape[] columns) => new("s", "t", columns);

    [Fact]
    public void Native_names_that_mean_the_same_kind_of_value_are_comparable_and_are_no_type_difference()
    {
        var (plan, problem) = CrossDiffer.Plan("sqlserver", "postgres",
            Table(Col("id", "bigint"), Col("n", "int"), Col("price", "decimal", 18, 3), Col("seen", "datetime2"), Col("flag", "bit")),
            Table(Col("id", "bigint"), Col("n", "integer"), Col("price", "numeric", 12, 2), Col("seen", "timestamp without time zone"), Col("flag", "boolean")), ["id"]);
        Assert.Null(problem);
        Assert.Equal(5, plan!.Compared.Count);
        Assert.Empty(plan.Skipped);
        Assert.Equal(["price"], plan.Compared.Where(c => !CrossDiffer.SameType(plan, "postgres", c)).Select(c => c.Name));
    }

    [Theory]
    [InlineData("float", "double precision", "floating point")]
    [InlineData("int", "text", "different kinds")]
    [InlineData("date", "timestamp without time zone", "different kinds")]
    [InlineData("xml", "text", "no canonical text form")]
    public void A_column_without_an_engine_independent_form_is_left_out_with_the_reason(string left, string right, string reason)
    {
        var (plan, _) = CrossDiffer.Plan("sqlserver", "postgres", Table(Col("id", "bigint"), Col("x", left)), Table(Col("id", "bigint"), Col("x", right)), ["id"]);
        Assert.Equal(["id"], plan!.Compared.Select(c => c.Name));
        Assert.Contains(reason, plan.Skipped.Single().Reason);
    }

    [Fact]
    public void A_key_that_cannot_be_compared_is_refused_and_a_numeric_without_precision_is_left_out()
    {
        var (plan, problem) = CrossDiffer.Plan("sqlserver", "postgres", Table(Col("id", "float")), Table(Col("id", "double precision")), ["id"]);
        Assert.Null(plan);
        Assert.Contains("cannot be compared", problem);
        var (p2, _) = CrossDiffer.Plan("sqlserver", "postgres", Table(Col("id", "bigint"), Col("v", "decimal", 10, 2)), Table(Col("id", "bigint"), Col("v", "numeric")), ["id"]);
        Assert.Contains("without a declared precision", p2!.Skipped.Single().Reason);
    }

    [Fact]
    public void The_digest_queries_never_select_a_value_without_the_request_for_values()
    {
        var (plan, _) = CrossDiffer.Plan("sqlserver", "postgres", Table(Col("id", "bigint"), Col("code", "nvarchar", length: 40)), Table(Col("id", "bigint"), Col("code", "varchar", length: 40)), ["id"]);
        var buckets = CrossDiffer.BucketSql(plan!, "postgres", true);
        Assert.Contains("HASHBYTES('SHA2_256'", buckets);
        Assert.Contains("GROUP BY LEFT(k, 2)", buckets);
        Assert.DoesNotMatch(@"SELECT [^(]*\[code\] AS", buckets);
        var rows = CrossDiffer.RowsSql(plan!, "postgres", false, ["ab", "cd"]);
        Assert.Contains("sha256(convert_to(", rows);
        Assert.Contains("IN ('ab', 'cd')", rows);
    }

    [Fact]
    public void Text_is_hashed_as_utf8_and_a_fixed_length_char_is_trimmed()
    {
        var k = CrossDiffer.KindOf("sqlserver", Col("c", "char", length: 8));
        Assert.Contains("RTRIM(", CrossDiffer.Canonical("sqlserver", "[c]", k, 0));
        Assert.Contains("UTF8", CrossDiffer.Canonical("sqlserver", "[c]", k, 0));
        Assert.DoesNotContain("RTRIM(", CrossDiffer.Canonical("sqlserver", "[c]", CrossDiffer.KindOf("sqlserver", Col("c", "nvarchar", length: 8)), 0));
        Assert.Equal("CAST(CAST(\"p\" AS NUMERIC(38,3)) AS TEXT)", CrossDiffer.Canonical("postgres", "\"p\"", CrossDiffer.KindOf("postgres", Col("p", "numeric", 12, 2)), 3));
    }
}
