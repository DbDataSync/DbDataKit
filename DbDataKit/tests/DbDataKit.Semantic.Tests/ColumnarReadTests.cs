using DbDataKit.Semantic.Abstractions;
using DuckDB.NET.Data;

namespace DbDataKit.Semantic.Tests;

/// <summary>The Arrow read of a source (plan 19 J24) gives the engine the same rows as the row read does.</summary>
public class ColumnarReadTests
{
    private static DuckDBConnection Open()
    {
        var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        return con;
    }

    private static DuckDBCommand Command(DuckDBConnection con, string sql)
    {
        var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private const string Mixed = """
        SELECT * FROM (VALUES
          (1, 'a', 1.5::DOUBLE, true,  DATE '2026-01-31', 7::SMALLINT),
          (NULL, NULL, NULL, NULL, NULL, NULL),
          (3, 'c', -2.25::DOUBLE, false, DATE '2026-02-01', 9::SMALLINT)) t(i, s, d, b, day, small)
        """;

    [Fact]
    public void TheArrowRowsAreTheRowsOfTheRowRead()
    {
        using var con = Open();
        var expected = QueryRowReader.Read(Command(con, Mixed), 100);
        var outcome = QueryRowReader.ReadColumnar(Command(con, Mixed), 100);
        Assert.NotNull(outcome.Columnar);
        Assert.Equal(3, outcome.Columnar.Count);
        var actual = outcome.AsRows();
        Assert.Equal(expected.Columns, actual.Columns);
        for (int r = 0; r < 3; r++)
            foreach (var c in expected.Columns)
            {
                var (e, a) = (expected.Rows[r][c], actual.Rows[r][c]);
                // whole numbers of any width arrive as long, the one place the row read keeps the driver's own type
                Assert.Equal(e is null ? null : Convert.ChangeType(e, a!.GetType(), System.Globalization.CultureInfo.InvariantCulture), a);
            }
        Assert.Equal("2026-01-31", actual.Rows[0]["day"]);
    }

    [Fact]
    public void ARowCapStopsTheReadWhereTheRowReadStops()
    {
        using var con = Open();
        var outcome = QueryRowReader.ReadColumnar(Command(con, "SELECT i, 'x' || i AS s FROM range(50) t(i)"), 7);
        Assert.Equal(7, outcome.Columnar!.Count);
        Assert.Equal(6L, outcome.AsRows().Rows[6]["i"]);
    }

    [Fact]
    public void ADecimalTheProviderGivesNoPrecisionForFallsBackToRows()
    {
        // DuckDB.NET reports no precision or scale, and a guessed scale could round: the row read keeps the exact value
        using var con = Open();
        var outcome = QueryRowReader.ReadColumnar(Command(con, "SELECT 12.50::DECIMAL(18,2) AS dec"), 100);
        Assert.Null(outcome.Columnar);
        Assert.Equal(12.50m, outcome.Rows!.Rows[0]["dec"]);
    }

    [Fact]
    public void AColumnWithoutAnArrowFormFallsBackToRowsOnTheSameReader()
    {
        using var con = Open();
        var outcome = QueryRowReader.ReadColumnar(Command(con, "SELECT i, TIMESTAMP '2026-01-01 10:00:00' + to_hours(CAST(i AS INTEGER)) AS at FROM range(3) t(i)"), 100);
        Assert.Null(outcome.Columnar);
        Assert.Equal(3, outcome.Rows!.Rows.Count);
        Assert.IsType<DateTime>(outcome.Rows.Rows[2]["at"]);
    }

    [Fact]
    public void ARunOverArrowTablesAnswersLikeTheRunOverRows()
    {
        const string sql = "SELECT region, sum(revenue) AS s, count(*) AS n, bool_or(shipped) AS any_shipped FROM \"d1\" GROUP BY ALL ORDER BY ALL";
        var set = SandboxLoadBenchmark.Make(5_000);
        var expected = Sandbox.Run(sql.Replace("region", "customers_region").Replace("sum(revenue)", "sum(orders_net_revenue)").Replace("shipped", "orders_shipped"), [set], int.MaxValue);
        var columnar = new QueryRowReaderFromSet(set).Columnar();
        var actual = Sandbox.RunColumnar(sql.Replace("region", "customers_region").Replace("sum(revenue)", "sum(orders_net_revenue)").Replace("shipped", "orders_shipped"),
            [new(set.Id, set.Descriptor, columnar)], int.MaxValue);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int r = 0; r < expected.Rows.Count; r++)
            foreach (var c in expected.Columns) Assert.Equal(expected.Rows[r][c], actual.Rows[r][c]);
    }
}
