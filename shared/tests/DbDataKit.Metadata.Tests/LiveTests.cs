using System.Data.Common;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata.Tests;

/// <summary>
/// Live-engine tests, skipped unless an environment variable names a server:
/// <c>DBDATA_METADATA_ODBC</c> (an ODBC connection string), <c>DBDATA_METADATA_SQLSERVER</c> and <c>DBDATA_METADATA_POSTGRES</c> (ADO.NET connection strings; the provider
/// factory must be registered in this process, which needs a provider package this repo does not reference, so these skip with a note until one is added to the test project).
/// </summary>
public class LiveTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [Fact]
    public async Task Odbc_crawl_and_describe_against_a_real_driver()
    {
        string? connectionString = Env("DBDATA_METADATA_ODBC");
        Assert.SkipUnless(connectionString is not null, "Set DBDATA_METADATA_ODBC to an ODBC connection string");

        using var probe = new System.Data.Odbc.OdbcConnection(connectionString);
        await using var reader = await OdbcCatalogReader.OpenAsync(connectionString!, probeConnection: probe);
        var facts = await reader.GetServerFactsAsync();
        Assert.False(string.IsNullOrEmpty(facts.ProductName));

        var shapes = new List<TableShape>();
        await foreach (var shape in ((ICatalogReader)reader).CrawlAsync()) { shapes.Add(shape); if (shapes.Count >= 25) break; }
        Assert.All(shapes, s => Assert.NotEmpty(s.Columns));

        var described = await reader.DescribeQueryAsync("SELECT 1 AS one");
        Assert.Single(described.Columns);
    }

    [Theory]
    [InlineData("DBDATA_METADATA_SQLSERVER", "DBDATA_METADATA_SQLSERVER_PROVIDER", "Microsoft.Data.SqlClient", "sqlserver")]
    [InlineData("DBDATA_METADATA_POSTGRES", "DBDATA_METADATA_POSTGRES_PROVIDER", "Npgsql", "postgres")]
    public async Task Provider_readers_against_a_real_server(string connectionVariable, string providerVariable, string defaultProvider, string engine)
    {
        string? connectionString = Env(connectionVariable);
        Assert.SkipUnless(connectionString is not null, $"Set {connectionVariable} to a connection string");
        DbProviderFactory? factory = null;
        try { factory = DbProviderFactories.GetFactory(Env(providerVariable) ?? defaultProvider); }
        catch (ArgumentException) { /* not registered */ }
        Assert.SkipUnless(factory is not null, "The ADO.NET provider factory is not registered in the test process (add the provider package and register it)");

        await using var connection = factory!.CreateConnection()!;
        connection.ConnectionString = connectionString;
        await connection.OpenAsync();
        ICatalogReader reader = engine == "sqlserver" ? new SqlServerCatalogReader(connection) : new PostgresCatalogReader(connection);

        var tables = new List<TableRef>();
        await foreach (var t in reader.ListTablesAsync()) { tables.Add(t); if (tables.Count >= 10) break; }
        foreach (var t in tables) Assert.NotEmpty((await reader.GetShapeAsync(t)).Columns);
        Assert.Single((await reader.DescribeQueryAsync("SELECT 1 AS one")).Columns);
        Assert.Equal(DialectProfiles.BuiltinFor((await reader.GetServerFactsAsync()).ProductName), engine);
    }
}
