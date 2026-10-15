namespace DbData.Odbc.Metadata.Tests;

/// <summary>SQLPrepare + SQLDescribeCol through the native layer and the typed catalog, against scripted fakes.</summary>
public class DescribeQueryTests
{
    [Fact]
    public async Task Native_describe_prepares_describes_each_column_and_frees_the_statement()
    {
        var api = new FakeOdbcApi
        {
            DescribedColumns = [("id", OdbcSqlType.Integer, 10, 0, 0), ("name", OdbcSqlType.WVarchar, 50, 0, 1), ("x", OdbcSqlType.Decimal, 0, 2, 2)],
        };
        await using var functions = new OdbcNativeFunctions("Driver={Fake};Server=x", null, api);

        var columns = await functions.DescribeQueryAsync("SELECT id, name, x FROM t", CancellationToken.None);

        Assert.Equal(["SELECT id, name, x FROM t"], api.Prepared);
        Assert.Equal(3, columns.Count);
        Assert.Equal(new OdbcResultColumn(1, "id", OdbcSqlType.Integer, 10, 0, false), columns[0]);
        Assert.Equal(new OdbcResultColumn(2, "name", OdbcSqlType.WVarchar, 50, 0, true), columns[1]);
        Assert.Equal(new OdbcResultColumn(3, "x", OdbcSqlType.Decimal, null, 2, null), columns[2]);
        Assert.Empty(api.OpenStatements);
        Assert.Equal(0, api.FetchCalls);
    }

    [Fact]
    public async Task A_failed_prepare_reports_the_sqlstate_and_frees_the_statement()
    {
        var api = new FakeOdbcApi { PrepareResult = -1, State = "42S02" };
        await using var functions = new OdbcNativeFunctions("Driver={Fake};Server=x", null, api);

        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => functions.DescribeQueryAsync("SELECT * FROM nope", CancellationToken.None));

        Assert.Equal("SQLPrepare", ex.Function);
        Assert.Equal("42S02", ex.SqlState);
        Assert.Empty(api.OpenStatements);
    }

    [Fact]
    public async Task Functions_that_do_not_implement_describe_report_not_supported()
    {
        var catalog = new OdbcCatalog(new FakeFunctions());
        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => catalog.DescribeQueryAsync("SELECT 1"));
        Assert.True(ex.NotSupported);
    }

    [Fact]
    public async Task Raw_info_returns_null_when_the_driver_does_not_answer()
    {
        var fake = new FakeFunctions { Strings = { [89] = "SELECT,FROM" }, Numbers = { [10005] = 128 } };
        var catalog = new OdbcCatalog(fake);
        Assert.Equal("SELECT,FROM", await catalog.GetInfoTextAsync(89));
        Assert.Equal(128u, await catalog.GetInfoNumberAsync(10005));
        Assert.Null(await catalog.GetInfoTextAsync(1));
        Assert.Null(await catalog.GetInfoNumberAsync(1));
    }
}
