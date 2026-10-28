using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Connections;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The conformance fixtures on the shared engines. SQL Server and DuckDB run through <c>DbDataKit.Semantic.Connections</c> (the shared connections:
/// the T-SQL the engine emits on SQL Server, its DuckDB twin on a read-only DuckDB file; the level 0 agent-written queries run on SQL Server in test code);
/// SSAS (Windows, <c>DBDATA_TEST_SSAS</c>) runs the DAX of the catalog's SSAS source on a real Tabular server; PostgreSQL runs the text the engine emits for it (the catalog's relational source declared a `postgres` one) through the same connections executor. The same checks as the stand-in run:
/// the expected rows, the result schema, replay from the run record, and the other subtotal modes.
/// </summary>
public class EngineConformanceTests(EngineFixture engines) : IClassFixture<EngineFixture>
{
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

    /// <summary>The conformance catalog with its relational source declared as PostgreSQL's (the seed data has the same tables there).</summary>
    internal static Catalog PostgresCatalog()
    {
        var cat = Catalog.Load(Path.Combine(ConformanceTests.Root, "catalog", "catalog.yaml"));   // a fresh load: Clone() shares the sources with the shared catalog
        cat.Doc.Sources["sqlserver_sales"].Kind = "postgres";
        return cat;
    }

    private static string DatabaseName(string connectionString) => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;

    private void Run(string engine, string relativePath, string totalsCompute)
    {
        var database = engines.Database(engine);
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
            // PostgreSQL: the catalog's relational source is declared a PostgreSQL one, so the engine writes PostgreSQL's own text for it
            _ => new ConnectionsSourceExecutor(new Dictionary<string, SemanticSource> { ["sqlserver_sales"] = ConnectionContexts.Postgres(database) }, standIn),
        };
        ConformanceTests.RunFixture(relativePath, totalsCompute, executor, catalog: engine == EngineFixture.Postgres ? PostgresCatalog : null);
    }
}
