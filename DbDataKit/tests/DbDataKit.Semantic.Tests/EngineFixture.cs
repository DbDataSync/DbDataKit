using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// A scratch database on each shared engine (<c>testenv/testenv.sh up mssql postgres</c>), seeded with the conformance data and dropped afterwards.
/// <c>DBDATA_TEST_SQLSERVER</c> and <c>DBDATA_TEST_POSTGRES</c> name a server; an engine whose variable is not set skips its tests unless
/// <c>DBDATA_REQUIRE_SQLSERVER</c> / <c>DBDATA_REQUIRE_POSTGRES</c> is <c>true</c>, which fails them (the CI job that started the engines sets it).
/// </summary>
public sealed class EngineFixture : IAsyncLifetime
{
    public const string SqlServer = "mssql";
    public const string Postgres = "postgres";

    private readonly string _name = $"dbdatakit_semantic_{Guid.NewGuid():N}";
    private readonly Dictionary<string, string> _databases = [];

    public static TheoryData<string> Engines => [SqlServer, Postgres];

    private static string? Server(string engine) =>
        Environment.GetEnvironmentVariable(engine == SqlServer ? "DBDATA_TEST_SQLSERVER" : "DBDATA_TEST_POSTGRES");

    private static bool Required(string engine) => string.Equals(
        Environment.GetEnvironmentVariable(engine == SqlServer ? "DBDATA_REQUIRE_SQLSERVER" : "DBDATA_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase);

    public ValueTask InitializeAsync()
    {
        var root = ConformanceTests.Root;
        if (Server(SqlServer) is { } mssql)
        {
            using (var con = new SqlConnection(mssql)) { con.Open(); Execute(con, $"CREATE DATABASE [{_name}]"); }
            var db = new SqlConnectionStringBuilder(mssql) { InitialCatalog = _name }.ConnectionString;
            SqlServerFixtureExecutor.LoadSeed(db, File.ReadAllText(Path.Combine(root, "data", "seed.tsql.sql")));
            _databases[SqlServer] = db;
        }
        if (Server(Postgres) is { } postgres)
        {
            using (var con = new NpgsqlConnection(postgres)) { con.Open(); Execute(con, $"CREATE DATABASE {_name}"); }
            var db = new NpgsqlConnectionStringBuilder(postgres) { Database = _name }.ConnectionString;
            PostgresTwinExecutor.LoadSeed(db, ConformanceTests.Seed);
            _databases[Postgres] = db;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Server(SqlServer) is { } mssql && _databases.ContainsKey(SqlServer))
        {
            SqlConnection.ClearAllPools();
            using var con = new SqlConnection(mssql);
            con.Open();
            Execute(con, $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]");
        }
        if (Server(Postgres) is { } postgres && _databases.ContainsKey(Postgres))
        {
            NpgsqlConnection.ClearAllPools();
            using var con = new NpgsqlConnection(postgres);
            con.Open();
            Execute(con, $"DROP DATABASE {_name} WITH (FORCE)");
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>The connection string of the seeded scratch database on the engine; skips the test when the engine is not provided, fails it when it is required.</summary>
    public string Database(string engine)
    {
        if (_databases.TryGetValue(engine, out var db)) return db;
        Assert.False(Required(engine), $"{engine} is required but its DBDATA_TEST_* variable is not set");
        Assert.Skip($"set DBDATA_TEST_{(engine == SqlServer ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        return "";
    }

    private static void Execute(System.Data.Common.DbConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
