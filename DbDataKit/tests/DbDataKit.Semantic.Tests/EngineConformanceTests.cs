using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The conformance fixtures on the shared engines. SQL Server runs the T-SQL the engine emits (and the level 0 agent-written queries);
/// PostgreSQL runs the DuckDB-dialect twin until the engine has its own emitter (J05). The same checks as the stand-in run:
/// the expected rows, the result schema, replay from the run record, and the other subtotal modes.
/// </summary>
public class EngineConformanceTests(EngineFixture engines) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// What the DuckDB-dialect twin cannot say on PostgreSQL: DuckDB's <c>strftime</c> for the time buckets (nine of these are the datetime fixtures of
    /// level 1), and a NUL character PostgreSQL does not store in text. The PostgreSQL emitter (J05) writes these natively and empties this list.
    /// </summary>
    private static readonly Dictionary<string, string> PostgresGaps = new()
    {
        ["level1/56_month_in_the_catalog_zone.json"] = "strftime",
        ["level1/57_month_in_the_requests_zone.json"] = "strftime",
        ["level1/59_between_dates_on_a_datetime.json"] = "strftime",
        ["level1/60_relative_range_on_a_datetime.json"] = "strftime",
        ["level1/62_quarter_buckets.json"] = "strftime",
        ["level1/63_hour_buckets.json"] = "strftime",
        ["level1/64_week_buckets.json"] = "strftime",
        ["level4/45_allocate_weights_from_a_later_period_missing.json"] = "a NUL character in a parameter",
        ["level4/84_window_first_member_is_blank.json"] = "a NUL character in a parameter",
    };

    public static TheoryData<string, string> Fixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var engine in new[] { EngineFixture.SqlServer, EngineFixture.Postgres })
            foreach (var f in ConformanceTests.Fixtures()) data.Add(engine, (string)f[0]);
        return data;
    }

    public static TheoryData<string, string> TotalsFixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var engine in new[] { EngineFixture.SqlServer, EngineFixture.Postgres })
            foreach (var f in ConformanceTests.TotalsFixtures()) data.Add(engine, (string)f[0]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture(string engine, string relativePath) => Run(engine, relativePath, "native");

    [Theory]
    [MemberData(nameof(TotalsFixtures))]
    public void FixtureWithEngineSubtotals(string engine, string relativePath) => Run(engine, relativePath, "engine");

    [Theory]
    [MemberData(nameof(TotalsFixtures))]
    public void FixtureWithAutoSubtotals(string engine, string relativePath) => Run(engine, relativePath, "auto");

    private void Run(string engine, string relativePath, string totalsCompute)
    {
        var database = engines.Database(engine);
        if (engine == EngineFixture.Postgres && PostgresGaps.TryGetValue(relativePath.Replace('\\', '/'), out var gap))
            Assert.Skip($"needs the PostgreSQL emitter (J05): the DuckDB twin uses {gap}");
        using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
        ISourceExecutor executor = engine == EngineFixture.SqlServer
            ? new SqlServerFixtureExecutor(database, standIn)
            : new PostgresTwinExecutor(database, standIn);
        ConformanceTests.RunFixture(relativePath, totalsCompute, executor);
    }
}
