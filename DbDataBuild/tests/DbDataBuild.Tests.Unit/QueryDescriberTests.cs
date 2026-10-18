using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Tests.Unit;

public class QueryDescriberTests
{
    private static readonly DuckTable Orders = new("staging", "orders",
    [
        new("order_id", "BIGINT", false),
        new("customer_id", "BIGINT", true),
        new("amount", "DECIMAL(14, 2)", true),
        new("code", "VARCHAR(20)", false),
        new("order_date", "DATE", false),
        new("created_at", "TIMESTAMP", true),
    ]);

    private static DescribeResult Describe(string sql, params DuckTable[] tables) => QueryDescriber.Describe(tables.Length == 0 ? [Orders] : tables, sql);

    [Fact]
    public void Describes_output_names_and_duckdb_types_against_an_empty_schema()
    {
        var r = Describe("SELECT o.order_id, o.amount, o.code AS discount_code, o.order_date, COUNT(*) AS n FROM staging.orders AS o GROUP BY ALL");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(
            [("order_id", "BIGINT"), ("amount", "DECIMAL(14,2)"), ("discount_code", "VARCHAR"), ("order_date", "DATE"), ("n", "BIGINT")],
            r.Columns!.Select(c => (c.Name, c.DuckDbType)));
    }

    [Fact]
    public void Star_expands_and_trailing_semicolons_are_tolerated()
    {
        var r = Describe("SELECT * FROM staging.orders;\n");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(["order_id", "customer_id", "amount", "code", "order_date", "created_at"], r.Columns!.Select(c => c.Name));
    }

    [Fact]
    public void Expression_types_come_from_duckdb()
    {
        var r = Describe("SELECT amount * 2 AS d, order_id + 1 AS i, order_date + INTERVAL 3 DAY AS shifted, CAST(code AS INTEGER) AS c, a.x FROM staging.orders, (SELECT 1.5 AS x) AS a");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(["DECIMAL(18,2)", "BIGINT", "TIMESTAMP", "INTEGER", "DECIMAL(2,1)"], r.Columns!.Select(c => c.DuckDbType));
    }

    [Fact]
    public void A_query_that_does_not_bind_is_an_error_with_duckdbs_first_line()
    {
        var r = Describe("SELECT nope FROM staging.orders");
        Assert.False(r.Ok);
        Assert.Contains("nope", r.Error);
        Assert.DoesNotContain("\n", r.Error);

        var missing = Describe("SELECT * FROM staging.missing_table");
        Assert.False(missing.Ok);
        Assert.Contains("missing_table", missing.Error);
    }

    [Fact]
    public void The_query_is_described_not_run_and_no_data_exists()
    {
        // a query that would fail at run time on data is still describable, and the schema holds no rows
        var r = Describe("SELECT 1 / 0 AS boom, (SELECT COUNT(*) FROM staging.orders) AS n");
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public void External_access_is_disabled_while_binding()
    {
        var file = Describe("SELECT * FROM read_csv('/etc/passwd')");
        Assert.False(file.Ok);
        Assert.Contains("access", file.Error, StringComparison.OrdinalIgnoreCase);

        var web = Describe("SELECT * FROM read_csv('https://example.invalid/x.csv')");
        Assert.False(web.Ok);

        var attach = Describe("ATTACH '/tmp/ddb-should-not-exist.db' AS x");
        Assert.False(attach.Ok);
        Assert.False(File.Exists("/tmp/ddb-should-not-exist.db"));
    }

    [Theory]
    [InlineData("BIGINT", true)]
    [InlineData("DECIMAL(14, 2)", true)]
    [InlineData("VARCHAR(20)", true)]
    [InlineData("TIMESTAMP WITH TIME ZONE", true)]
    [InlineData("BIGINT[]", true)]
    [InlineData("INT); DROP TABLE x; --", false)]
    [InlineData("INT, b INT", false)]
    [InlineData("", false)]
    [InlineData("VARCHAR(", false)]
    public void Only_plain_type_names_reach_duckdb(string type, bool plain) => Assert.Equal(plain, QueryDescriber.IsPlainType(type));

    [Fact]
    public void An_unusable_declared_type_is_reported_instead_of_executed()
    {
        var r = QueryDescriber.Describe([new DuckTable("s", "t", [new DuckColumn("a", "INT); DROP TABLE x; --", true)])], "SELECT * FROM s.t");
        Assert.False(r.Ok);
        Assert.Contains("not a plain SQL type name", r.Error);
    }

    [Fact]
    public void Identifiers_with_quotes_and_spaces_are_quoted()
    {
        var t = new DuckTable("my schema", "my\"table", [new DuckColumn("Order Id", "INTEGER", true)]);
        var r = QueryDescriber.Describe([t], "SELECT \"Order Id\" FROM \"my schema\".\"my\"\"table\"");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Order Id", r.Columns![0].Name);
    }
}
