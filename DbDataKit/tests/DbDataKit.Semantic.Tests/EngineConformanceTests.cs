using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Connections;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The conformance fixtures on the shared engines. SQL Server and DuckDB run through <c>DbDataKit.Semantic.Connections</c> (the shared connections:
/// the T-SQL the engine emits on SQL Server, its DuckDB twin on a read-only DuckDB file; the level 0 agent-written queries run on SQL Server in test code);
/// SSAS (Windows, <c>DBDATA_TEST_SSAS</c>) runs the DAX of the catalog's SSAS source on a real Tabular server; PostgreSQL runs the DuckDB-dialect twin until the engine has its own emitter (J05). The same checks as the stand-in run:
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
        foreach (var engine in new[] { EngineFixture.SqlServer, EngineFixture.DuckDb, EngineFixture.Postgres, EngineFixture.Ssas })
            foreach (var f in ConformanceTests.Fixtures()) data.Add(engine, (string)f[0]);
        return data;
    }

    public static TheoryData<string, string> TotalsFixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var engine in new[] { EngineFixture.SqlServer, EngineFixture.DuckDb, EngineFixture.Postgres, EngineFixture.Ssas })
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

    private static string DatabaseName(string connectionString) => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;

    private void Run(string engine, string relativePath, string totalsCompute)
    {
        var database = engines.Database(engine);
        if (engine == EngineFixture.Postgres && PostgresGaps.TryGetValue(relativePath.Replace('\\', '/'), out var gap))
            Assert.Skip($"needs the PostgreSQL emitter (J05): the DuckDB twin uses {gap}");
        using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
        // the catalog's SQL Server source runs through the shared connections; its SSAS source stays on the stand-in
        ISourceExecutor executor = engine switch
        {
            EngineFixture.SqlServer => new NativeTextTestExecutor(
                new ConnectionsSourceExecutor(new Dictionary<string, SemanticSource> { ["sqlserver_sales"] = ConnectionContexts.SqlServer(DatabaseName(database)) }, standIn), database),
            EngineFixture.DuckDb => new ConnectionsSourceExecutor(
                new Dictionary<string, SemanticSource> { ["sqlserver_sales"] = ConnectionContexts.DuckDb(database) }, standIn),
            // the SSAS source runs as real DAX; the SQL Server source stays on the stand-in (the Windows runner has no SQL Server)
            EngineFixture.Ssas => new DbDataKit.Semantic.Ssas.SsasExecutor(database, standIn),
            _ => new PostgresTwinExecutor(database, standIn),
        };
        ConformanceTests.RunFixture(relativePath, totalsCompute, executor);
    }
}
