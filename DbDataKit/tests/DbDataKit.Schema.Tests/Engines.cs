using System.Data.Common;
using DbDataKit.Metadata;
using DbDataKit.Schema.Reading;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Schema.Tests;

/// <summary>
/// A throwaway database per test on the shared engines (<c>testenv/testenv.sh up mssql postgres</c>, then <c>eval "$(testenv/testenv.sh env dbdatakit)"</c>): <c>DBDATA_TEST_SQLSERVER</c> and <c>DBDATA_TEST_POSTGRES</c> name a server. An engine
/// whose variable is not set skips its rows, unless <c>DBDATA_REQUIRE_SQLSERVER</c> / <c>DBDATA_REQUIRE_POSTGRES</c> is <c>true</c>, which fails them instead.
/// </summary>
public sealed class Engines : IAsyncDisposable
{
    public const string SqlServer = "sqlserver";
    public const string Postgres = "postgres";

    public static TheoryData<string> Both => [SqlServer, Postgres];

    private readonly List<Func<Task>> _cleanups = [];

    public required string Engine { get; init; }
    public required DbConnection Connection { get; init; }
    public ShapeEngine Shape => Engine == SqlServer ? ShapeEngine.SqlServer : ShapeEngine.Postgres;

    private static string? Server(string engine) => Environment.GetEnvironmentVariable(engine == SqlServer ? "DBDATA_TEST_SQLSERVER" : "DBDATA_TEST_POSTGRES");

    private static bool Required(string engine) =>
        string.Equals(Environment.GetEnvironmentVariable(engine == SqlServer ? "DBDATA_REQUIRE_SQLSERVER" : "DBDATA_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase);

    public static async Task<Engines> OpenAsync(string engine)
    {
        var server = Server(engine);
        if (server is null)
        {
            Assert.False(Required(engine), $"{engine} is required but its DBDATA_TEST_* variable is not set");
            Assert.Skip($"set DBDATA_TEST_{(engine == SqlServer ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        }

        var name = $"dbdatakit_schema_{Guid.NewGuid():N}";
        await using (var admin = Create(engine, server!))
        {
            await admin.OpenAsync();
            await Run(admin, engine == SqlServer ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        }
        var connectionString = engine == SqlServer
            ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString
            : new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        var connection = Create(engine, connectionString);
        await connection.OpenAsync();
        var engines = new Engines { Engine = engine, Connection = connection };
        engines._cleanups.Add(async () =>
        {
            await connection.DisposeAsync();
            if (engine == SqlServer) SqlConnection.ClearAllPools(); else NpgsqlConnection.ClearAllPools();
            await using var admin = Create(engine, server!);
            await admin.OpenAsync();
            await Run(admin, engine == SqlServer ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        });
        return engines;
    }

    private static DbConnection Create(string engine, string connectionString) => engine == SqlServer ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);

    private static async Task Run(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public Task ExecuteAsync(string sql) => Run(Connection, sql);

    /// <summary>The reader the shared libraries give this engine, with the indexes behind constraints included (a primary key is a thing the structure has).</summary>
    public ICatalogReader Reader() => Engine == SqlServer
        ? new SqlServerCatalogReader(Connection, options: new CatalogReaderOptions { IncludeConstraintIndexes = true })
        : new PostgresCatalogReader(Connection, options: new CatalogReaderOptions { IncludeConstraintIndexes = true });

    public async ValueTask DisposeAsync()
    {
        foreach (var cleanup in _cleanups) await cleanup();
    }
}
