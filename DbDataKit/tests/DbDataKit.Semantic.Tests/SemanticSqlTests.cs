using System.Text.Json.Nodes;
using DbDataKit.Semantic.Sql;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Semantic SQL (plan 19, J17 to J19; docs/semantic/semantic-sql.md): every query fixture has a SQL form, checked in under <c>golden/semantic-sql</c>
/// (<c>UPDATE_GOLDEN=1</c> writes them), and the SQL reads back to the fixture's request.
/// </summary>
public class SemanticSqlTests
{
    private static readonly string Model = ConformanceTests.Cat.Name;

    public static TheoryData<string> QueryFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var f in ConformanceTests.Fixtures())
        {
            var relative = (string)f[0];
            var request = JsonNode.Parse(File.ReadAllText(Path.Combine(ConformanceTests.Root, "fixtures", relative)))!["request"]!;
            if (request["kind"]?.GetValue<string>() == "query") data.Add(relative);
        }
        return data;
    }

    private static JsonObject Request(string relative) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ConformanceTests.Root, "fixtures", relative)))!["request"]!.AsObject();

    [Theory]
    [MemberData(nameof(QueryFixtures))]
    public void EveryFixtureHasItsSqlFormChecked(string relative)
    {
        var sql = SemanticSqlRenderer.Render(Request(relative), Model);
        Golden.Assert("semantic-sql/" + relative.Replace(".json", ".sql"), sql);
    }

    [Theory]
    [MemberData(nameof(QueryFixtures))]
    public void TheSqlFormReadsBackToTheRequest(string relative)
    {
        var request = Request(relative);
        var sql = SemanticSqlRenderer.Render(request, Model);
        var translation = SemanticSqlTranslator.Translate(sql, Model);
        Assert.True(translation.Ok, string.Join("; ", translation.Errors.Select(e => $"{e.Code}: {e.Message}")) + "\n" + sql);
        var expected = RequestNormalizer.Normalize(request).ToJsonString();
        var actual = RequestNormalizer.Normalize(translation.Request!).ToJsonString();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"{sql}\nexpected {expected}\nactual   {actual}");
    }

    [Theory]
    [MemberData(nameof(QueryFixtures))]
    public void TheEngineGivesTheFixturesAnswerToTheRequestThatCameBackFromSql(string relative)
    {
        using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
        ConformanceTests.RunFixture(relative, "native", standIn, request =>
        {
            var translation = SemanticSqlTranslator.Translate(SemanticSqlRenderer.Render(request, Model), Model);
            Assert.True(translation.Ok, string.Join("; ", translation.Errors.Select(e => e.Message)));
            return translation.Request!;
        });
    }

    // ---------- what is refused, and what a person may write beyond the canonical form ----------

    private static SqlTranslation Translate(string sql) => SemanticSqlTranslator.Translate(sql, Model);

    [Theory]
    [InlineData("SELECT * FROM sales_catalog LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT DISTINCT customers.region FROM sales_catalog LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL LIMIT 5 OFFSET 2", "sql_unsupported")]
    [InlineData("SELECT MEASURE(orders.net_revenue) FROM sales_catalog JOIN customers ON 1 = 1 LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT MEASURE(orders.net_revenue) FROM (SELECT 1) AS x LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT MEASURE(orders.net_revenue) FROM sales_catalog UNION SELECT MEASURE(orders.net_revenue) FROM sales_catalog", "sql_unsupported")]
    [InlineData("WITH x AS (SELECT 1) SELECT MEASURE(orders.net_revenue) FROM sales_catalog LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT MEASURE(orders.net_revenue) FROM sales_catalog WHERE customers.region LIKE 'W%' LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT MEASURE(orders.net_revenue) FROM sales_catalog WHERE upper(customers.region) = 'WEST' LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT MEASURE(orders.net_revenue) + 1 FROM sales_catalog LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT customers.region AS r, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL ORDER BY 2 LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL ORDER BY customers.region NULLS LAST LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT rank() OVER (ORDER BY 1) FROM sales_catalog LIMIT 5", "sql_unsupported")]
    [InlineData("SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY products.product LIMIT 5", "sql_group_by_mismatch")]
    [InlineData("SELECT customers.region, products.product, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY GROUPING SETS ((customers.region), (products.product))", "sql_unsupported")]
    [InlineData("SELECT MEASURE(orders.net_revenue) FROM some_other_model LIMIT 5", "sql_wrong_model")]
    [InlineData("SELEC MEASURE(orders.net_revenue)", "sql_syntax")]
    [InlineData("SELECT MEASURE(orders.net_revenue) FROM sales_catalog; SELECT 1", "sql_syntax")]
    public void AnythingOutsideTheSubsetIsRefusedWithANamedCode(string sql, string code)
    {
        var translation = Translate(sql);
        Assert.False(translation.Ok);
        Assert.Equal(code, Assert.Single(translation.Errors).Code);
    }

    [Fact]
    public void AHandWrittenQueryNeedNotBeCanonical()
    {
        var translation = Translate("""
            select customers.region,
                   measure(orders.net_revenue) as revenue,
                   measure(orders.net_revenue, spread := 'blank') filter (where orders.status = 'Completed' and customers.region <> 'North') as done,
                   avg(orders.unit_price) as avg_unit_price,
                   count(distinct orders.order_id) as customers_served
            from SALES_CATALOG(timezone := 'America/Chicago', segments := ['big'])
            where date.fiscal_quarter in ('FY27-Q1', 'FY27-Q2') and (customers.region = 'West' or products.product starts_with_not_a_thing is null)
            group by customers.region
            having measure(orders.net_revenue) > 100.5
            qualify revenue between 1 and 5
            order by revenue desc, customers.region
            limit 10
            """);
        Assert.False(translation.Ok);   // starts_with_not_a_thing is a column: a value is expected after IS NULL's subject, so the typo is caught
        translation = Translate("""
            select customers.region, measure(orders.net_revenue) as revenue,
                   measure(orders.net_revenue, spread := 'blank') filter (where orders.status = 'Completed' and customers.region <> 'North') as done,
                   avg(orders.unit_price) as avg_unit_price, count(distinct orders.order_id) as served
            from SALES_CATALOG(timezone := 'America/Chicago', segments := ['big'])
            where date.fiscal_quarter in ('FY27-Q1', 'FY27-Q2') and (customers.region = 'West' or products.product is null)
            group by customers.region
            having measure(orders.net_revenue) > 100.5
            qualify revenue between 1 and 5
            order by revenue desc, customers.region
            limit 10
            """);
        Assert.True(translation.Ok, string.Join("; ", translation.Errors.Select(e => e.Message)));
        var q = translation.Request!["query"]!;
        Assert.Equal("America/Chicago", q["timezone"]!.GetValue<string>());
        Assert.Equal(["big"], q["segments"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("count_distinct", q["measures"]![3]!["agg"]!.GetValue<string>());
        Assert.Equal("blank", q["measures"]![1]!["spread"]!.GetValue<string>());
        Assert.Equal(2, q["measures"]![1]!["filters"]!.AsArray().Count);
        Assert.Equal("measure", q["filters"]![2]!.AsObject().First().Key);   // the HAVING condition, after the WHERE ones
        Assert.Equal(100.5m, q["filters"]![2]!["values"]![0]!.GetValue<decimal>());
        Assert.Equal("revenue", q["post_filters"]![0]!["measure"]!.GetValue<string>());
        Assert.Equal([["revenue", "desc"], ["customers.region", "asc"]], q["order"]!.AsArray().Select(o => o!.AsArray().Select(x => x!.GetValue<string>()).ToArray()));
        Assert.Equal(10, q["limit"]!.GetValue<int>());
    }

    [Fact]
    public void TheGeneratedQueryValidatesAgainstTheRequestSchema()
    {
        var cat = ConformanceTests.Cat.Clone();
        var validator = new Validator(cat);
        foreach (var f in new[] { "level1/01_single_source_by_region.json", "level3/34_cube_totals.json", "level4/100_chain_running_total_over_a_window.json" })
        {
            var request = Request(f);
            var back = SemanticSqlTranslator.Translate(SemanticSqlRenderer.Render(request, Model), Model).Request!;
            Assert.Empty(validator.SchemaErrors(back, "semantic_request", "native_query"));
        }
    }

    [Fact]
    public void ARequestThatIsNotAQueryHasNoSqlForm()
    {
        var e = Assert.Throws<SemanticSqlException>(() => SemanticSqlRenderer.Render(JsonNode.Parse("""{"spec_version":"0.3","kind":"clarify"}""")!.AsObject(), Model));
        Assert.Contains("kind query", e.Message);
    }

    [Fact]
    public void ANameThatIsAReservedWordIsQuoted()
    {
        var request = JsonNode.Parse("""{"spec_version":"0.3","kind":"query","query":{"measures":["orders.net_revenue"],"attributes":["select.from"],"limit":5}}""")!.AsObject();
        var sql = SemanticSqlRenderer.Render(request, Model);
        Assert.Contains("\"select\".\"from\"", sql);
        Assert.Equal("select.from", SemanticSqlTranslator.Translate(sql, Model).Request!["query"]!["attributes"]![0]!.GetValue<string>());
    }
}
