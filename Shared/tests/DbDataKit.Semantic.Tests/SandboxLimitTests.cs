using DbDataKit.Semantic;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Tests;

public sealed class SandboxLimitTests
{
    private static ResultSet Many(int rows) => SandboxLoadBenchmark.Make(rows);

    private const string Sql = "SELECT count(*) AS n FROM \"{0}\"";

    [Fact]
    public void A_combine_over_more_rows_than_it_may_load_is_stopped_before_loading()
    {
        var set = Many(2_000);
        using var _ = Sandbox.Scope(new SandboxLimits(512L << 20, TimeSpan.FromSeconds(30), 1_000));
        var stopped = Assert.Throws<SandboxLimitException>(() => Sandbox.Run(string.Format(Sql, set.Id), [set], 10));
        Assert.Equal("rows", stopped.Limit);
    }

    [Fact]
    public void A_combine_that_runs_too_long_is_interrupted()
    {
        var set = Many(1_000);
        using var _ = Sandbox.Scope(new SandboxLimits(512L << 20, TimeSpan.FromMilliseconds(200), 1_000_000));
        // a cross join of the table with itself several times over runs far longer than the limit
        var sql = $"SELECT count(*) AS n FROM \"{set.Id}\" a, \"{set.Id}\" b, \"{set.Id}\" c, \"{set.Id}\" d";
        var stopped = Assert.Throws<SandboxLimitException>(() => Sandbox.Run(sql, [set], 10));
        Assert.Equal("time", stopped.Limit);
    }

    [Fact]
    public void A_combine_within_its_limits_runs_as_before()
    {
        var set = Many(500);
        using var _ = Sandbox.Scope(new SandboxLimits(512L << 20, TimeSpan.FromSeconds(30), 1_000));
        Assert.Single(Sandbox.Run(string.Format(Sql, set.Id), [set], 10).Rows);
    }
}
