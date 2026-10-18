using DbDataBuild.Sql.Analysis;

namespace DbDataBuild.Tests.Unit;

public class QueryAnalyzerTests
{
    private static readonly SchemaTableSpec[] Schema =
    [
        new("staging", "orders",
        [
            new("order_id", "BIGINT", false), new("customer_id", "BIGINT", true), new("amount", "DECIMAL(14, 2)", true),
            new("code", "VARCHAR(20)", false), new("order_date", "DATE", false),
        ]),
        new("staging", "customers", [new("customer_id", "BIGINT", false), new("name", "VARCHAR(50)", true)]),
    ];

    private static QueryFacts Analyze(string sql)
    {
        var (facts, error) = QueryAnalyzer.Analyze(sql, Schema);
        Assert.NotNull(facts);
        Assert.Null(error);
        return facts!;
    }

    [Fact]
    public void Projections_carry_lineage_nullability_and_transform_kind()
    {
        var f = Analyze("SELECT o.order_id, o.customer_id, o.code AS discount_code, o.amount * 2 AS double_amt, COUNT(*) AS n FROM staging.orders AS o GROUP BY o.order_id, o.customer_id, o.code, o.amount");
        var byName = f.Projections.ToDictionary(p => p.Name!);
        Assert.Equal(("direct", "non_null"), (byName["order_id"].TransformKind, byName["order_id"].Nullability));
        Assert.Equal("nullable", byName["customer_id"].Nullability);          // declared nullable: true
        Assert.Equal([new ColumnRef("staging.orders", "code")], byName["discount_code"].Upstream);
        Assert.Equal("non_null", byName["discount_code"].Nullability);
        Assert.Equal("expression", byName["double_amt"].TransformKind);
        Assert.Equal("aggregation", byName["n"].TransformKind);
        Assert.Equal("non_null", byName["n"].Nullability);
        Assert.Equal([0, 1, 2, 3, 4], f.Projections.Select(p => p.Index));
    }

    [Fact]
    public void A_left_join_makes_the_right_side_nullable()
    {
        var f = Analyze("SELECT o.order_id, c.customer_id AS cid FROM staging.orders o LEFT JOIN staging.customers c ON o.customer_id = c.customer_id");
        Assert.Equal("non_null", f.Projections[0].Nullability);
        Assert.Equal("nullable", f.Projections[1].Nullability);               // declared NOT NULL, but outer-joined
        Assert.Equal(1, f.JoinCount);
    }

    [Fact]
    public void Star_projections_expand_to_one_projection_per_column()
    {
        var f = Analyze("SELECT * FROM staging.orders");
        Assert.Equal(["order_id", "customer_id", "amount", "code", "order_date"], f.Projections.Select(p => p.Name));
        Assert.Equal([new BaseTable("staging", "orders")], f.BaseTables);
    }

    [Fact]
    public void A_sized_cast_is_reported_as_a_cast_with_its_type()
    {
        var p = Analyze("SELECT CAST(o.code AS VARCHAR(5)) AS c5 FROM staging.orders o").Projections[0];
        Assert.Equal("cast", p.TransformKind);
        Assert.Equal("TEXT(5)", p.CastType);                                    // polyglot spells VARCHAR(n) as TEXT(n)
    }

    [Fact]
    public void Base_tables_include_every_physical_dependency_and_not_ctes()
    {
        var f = Analyze("WITH c AS (SELECT customer_id FROM staging.customers) SELECT o.order_id, c.customer_id FROM staging.orders o JOIN c ON o.customer_id = c.customer_id");
        Assert.Equal(["staging.customers", "staging.orders"], f.BaseTables.Select(t => t.QualifiedName).Order());
    }

    [Fact]
    public void Group_by_columns_are_reported_by_lineage()
    {
        var f = Analyze("SELECT o.customer_id, COUNT(*) AS n FROM staging.orders o GROUP BY o.customer_id");
        Assert.Equal([new ColumnRef("staging.orders", "customer_id")], f.GroupedColumns);
        Assert.False(f.IsDistinct);
    }

    [Fact]
    public void Distinct_and_set_operations_are_recognized()
    {
        Assert.True(Analyze("SELECT DISTINCT o.customer_id FROM staging.orders o").IsDistinct);
        Assert.False(Analyze("SELECT DISTINCT ON (o.customer_id) o.customer_id, o.amount FROM staging.orders o").IsDistinct);
        Assert.True(Analyze("SELECT order_id FROM staging.orders UNION ALL SELECT customer_id FROM staging.customers").IsSetOperation);
        Assert.False(Analyze("SELECT order_id FROM staging.orders").IsSetOperation);
    }

    [Fact]
    public void Without_a_schema_the_analysis_still_names_columns_and_tables_but_knows_less()
    {
        var (facts, error) = QueryAnalyzer.Analyze("SELECT a, b + 1 AS c FROM s.t");
        Assert.Null(error);
        Assert.Equal(["a", "c"], facts!.Projections.Select(p => p.Name));
        Assert.Equal("s.t", Assert.Single(facts.BaseTables).QualifiedName);
        Assert.Equal("unknown", facts.Projections[0].Nullability);
    }

    [Fact]
    public void Unparseable_sql_is_an_error_not_an_exception()
    {
        var (facts, error) = QueryAnalyzer.Analyze("SELEC FROM FROM (", Schema);
        Assert.Null(facts);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
