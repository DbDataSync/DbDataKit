using System.Diagnostics;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// How long <see cref="Sandbox.Run"/> takes to load a result set into DuckDB and aggregate it (plan 19 J21). Not a pass/fail test: set <c>DBDATA_BENCH=1</c>
/// and read the output (<c>dotnet test --project tests/DbDataKit.Semantic.Tests --filter-class "*SandboxLoadBenchmark" -- --output Detailed</c>).
/// </summary>
public class SandboxLoadBenchmark(ITestOutputHelper output)
{
    internal static ResultSet Make(int rows)
    {
        var descriptor = JsonNode.Parse("""
            {"descriptor_version":"1.0","name":"d1","schema":{"fields":[
              {"name":"customers_region","type":"string","x_kind":"attribute"},
              {"name":"date_fiscal_month","type":"string","x_kind":"attribute"},
              {"name":"orders_units","type":"integer","x_kind":"measure"},
              {"name":"orders_net_revenue","type":"number","x_kind":"measure"},
              {"name":"orders_shipped","type":"boolean","x_kind":"measure"}]}}
            """)!.AsObject();
        var list = new List<Dictionary<string, object?>>(rows);
        for (int i = 0; i < rows; i++)
            list.Add(new()
            {
                ["customers_region"] = "R" + (i % 50), ["date_fiscal_month"] = "2026-" + (i % 12 + 1).ToString("00"),
                ["orders_units"] = (long)i, ["orders_net_revenue"] = i * 1.25m, ["orders_shipped"] = i % 3 == 0,
            });
        return new ResultSet("d1", "src", ["customers_region", "date_fiscal_month"], null, descriptor, null, list);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    [InlineData(300_000)]
    [InlineData(1_000_000)]
    public void Load(int rows)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DBDATA_BENCH") == "1", "set DBDATA_BENCH=1 to measure");
        int max = int.TryParse(Environment.GetEnvironmentVariable("DBDATA_BENCH_MAX_ROWS"), out var m) ? m : 100_000;
        Assert.SkipUnless(rows <= max, $"above DBDATA_BENCH_MAX_ROWS ({max})");
        var set = Make(rows);
        var sw = Stopwatch.StartNew();
        var result = Sandbox.Run("SELECT customers_region, sum(orders_net_revenue) AS s, count(*) AS n FROM \"d1\" GROUP BY ALL ORDER BY ALL", [set], int.MaxValue);
        var line = $"{rows,9:N0} rows: {sw.ElapsedMilliseconds,6} ms ({rows / Math.Max(1, sw.Elapsed.TotalSeconds):N0} rows/s), {result.Rows.Count} result rows";
        output.WriteLine(line);
        File.AppendAllText(Environment.GetEnvironmentVariable("DBDATA_BENCH_OUT") ?? "sandbox-load-benchmark.txt", line + "\n");
        Assert.Equal(50, result.Rows.Count);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void Read(int rows)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DBDATA_BENCH") == "1", "set DBDATA_BENCH=1 to measure");
        int max = int.TryParse(Environment.GetEnvironmentVariable("DBDATA_BENCH_MAX_ROWS"), out var m) ? m : 100_000;
        Assert.SkipUnless(rows <= max, $"above DBDATA_BENCH_MAX_ROWS ({max})");
        using var con = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT 'R' || (i % 50) AS region, i AS units, i * 1.25 AS revenue, DATE '2026-01-01' + CAST(i % 300 AS INTEGER) AS day, i % 3 = 0 AS shipped FROM range({rows}) t(i)";
        var sw = Stopwatch.StartNew();
        var result = QueryRowReader.Read(cmd, int.MaxValue);
        var line = $"read {rows,9:N0} rows: {sw.ElapsedMilliseconds,6} ms ({rows / Math.Max(1, sw.Elapsed.TotalSeconds):N0} rows/s)";
        output.WriteLine(line);
        File.AppendAllText(Environment.GetEnvironmentVariable("DBDATA_BENCH_OUT") ?? "sandbox-load-benchmark.txt", line + "\n");
        Assert.Equal(rows, result.Rows.Count);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void ReadColumnar(int rows)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DBDATA_BENCH") == "1", "set DBDATA_BENCH=1 to measure");
        int max = int.TryParse(Environment.GetEnvironmentVariable("DBDATA_BENCH_MAX_ROWS"), out var m) ? m : 100_000;
        Assert.SkipUnless(rows <= max, $"above DBDATA_BENCH_MAX_ROWS ({max})");
        using var con = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT 'R' || (i % 50) AS region, i AS units, CAST(i * 1.25 AS DOUBLE) AS revenue, DATE '2026-01-01' + CAST(i % 300 AS INTEGER) AS day, i % 3 = 0 AS shipped FROM range({rows}) t(i)";
        var sw = Stopwatch.StartNew();
        var outcome = QueryRowReader.ReadColumnar(cmd, int.MaxValue);
        var line = $"read columnar {rows,9:N0} rows: {sw.ElapsedMilliseconds,6} ms ({rows / Math.Max(1, sw.Elapsed.TotalSeconds):N0} rows/s), {(outcome.Columnar is null ? "fell back to rows" : "arrow")}";
        output.WriteLine(line);
        File.AppendAllText(Environment.GetEnvironmentVariable("DBDATA_BENCH_OUT") ?? "sandbox-load-benchmark.txt", line + "\n");
        Assert.Equal(rows, outcome.AsRows().Rows.Count);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void LoadColumnar(int rows)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DBDATA_BENCH") == "1", "set DBDATA_BENCH=1 to measure");
        int max = int.TryParse(Environment.GetEnvironmentVariable("DBDATA_BENCH_MAX_ROWS"), out var m) ? m : 100_000;
        Assert.SkipUnless(rows <= max, $"above DBDATA_BENCH_MAX_ROWS ({max})");
        var set = Make(rows);
        var columnar = new QueryRowReaderFromSet(set).Columnar();
        var sw = Stopwatch.StartNew();
        var result = Sandbox.RunColumnar("SELECT customers_region, sum(orders_net_revenue) AS s, count(*) AS n FROM \"d1\" GROUP BY ALL ORDER BY ALL", [new(set.Id, set.Descriptor, columnar)], int.MaxValue);
        var line = $"load columnar {rows,9:N0} rows: {sw.ElapsedMilliseconds,6} ms ({rows / Math.Max(1, sw.Elapsed.TotalSeconds):N0} rows/s), {result.Rows.Count} result rows";
        output.WriteLine(line);
        File.AppendAllText(Environment.GetEnvironmentVariable("DBDATA_BENCH_OUT") ?? "sandbox-load-benchmark.txt", line + "\n");
        Assert.Equal(50, result.Rows.Count);
    }
}

/// <summary>Builds the Arrow form of a result set the way a source read would (through a DuckDB table), so the load can be timed apart from the read.</summary>
internal sealed class QueryRowReaderFromSet(ResultSet set)
{
    public ColumnarRows Columnar()
    {
        using var con = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        con.Open();
        using (var create = con.CreateCommand())
        {
            create.CommandText = "CREATE TABLE s (customers_region VARCHAR, date_fiscal_month VARCHAR, orders_units BIGINT, orders_net_revenue DOUBLE, orders_shipped BOOLEAN)";
            create.ExecuteNonQuery();
        }
        using (var appender = con.CreateAppender("s"))
            foreach (var r in set.Rows)
                appender.CreateRow().AppendValue((string?)r["customers_region"]).AppendValue((string?)r["date_fiscal_month"]).AppendValue((long)r["orders_units"]!)
                    .AppendValue((double)(decimal)r["orders_net_revenue"]!).AppendValue((bool)r["orders_shipped"]!).EndRow();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT * FROM s";
        return QueryRowReader.ReadColumnar(cmd, int.MaxValue).Columnar!;
    }
}
