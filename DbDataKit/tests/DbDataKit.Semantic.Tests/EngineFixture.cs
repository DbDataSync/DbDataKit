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
    /// <summary>DuckDB needs no server: the fixtures run on a seeded file, opened read-only through <c>DbDataKit.Connections.DuckDb</c>.</summary>
    public const string DuckDb = "duckdb";
    /// <summary>SQL Server Analysis Services (Tabular), Windows only: <c>DBDATA_TEST_SSAS</c> is the connection string of the instance without a Catalog (<c>Data Source=localhost:2383</c>); the sample model is deployed once as database <c>qis_budget</c>.</summary>
    public const string Ssas = "ssas";

    private readonly string _name = $"dbdatakit_semantic_{Guid.NewGuid():N}";
    private readonly Dictionary<string, string> _databases = [];
    private DirectoryInfo? _directory;

    public static TheoryData<string> Engines => [SqlServer, DuckDb, Postgres, Ssas];

    private static string Suffix(string engine) => engine switch { SqlServer => "SQLSERVER", Ssas => "SSAS", _ => "POSTGRES" };

    private static string? Server(string engine) => Environment.GetEnvironmentVariable("DBDATA_TEST_" + Suffix(engine));

    private static bool Required(string engine) => string.Equals(Environment.GetEnvironmentVariable("DBDATA_REQUIRE_" + Suffix(engine)), "true", StringComparison.OrdinalIgnoreCase);

    public ValueTask InitializeAsync()
    {
        var root = ConformanceTests.Root;
        _directory = Directory.CreateTempSubdirectory("dbdatakit-semantic-");
        var file = Path.Combine(_directory.FullName, "seed.duckdb");
        using (var duck = new DuckDB.NET.Data.DuckDBConnection($"DataSource={file}"))
        {
            duck.Open();
            using var cmd = duck.CreateCommand();
            cmd.CommandText = ConformanceTests.Seed;
            cmd.ExecuteNonQuery();
        }
        _databases[DuckDb] = file;
        if (Server(SqlServer) is { } mssql)
        {
            using (var con = new SqlConnection(mssql)) { con.Open(); Execute(con, $"CREATE DATABASE [{_name}]"); }
            var db = new SqlConnectionStringBuilder(mssql) { InitialCatalog = _name }.ConnectionString;
            NativeTextTestExecutor.LoadSeed(db, File.ReadAllText(Path.Combine(root, "data", "seed.tsql.sql")));
            _databases[SqlServer] = db;
        }
        if (Server(Ssas) is { } ssas)
        {
            DbDataKit.Semantic.Ssas.SsasExecutor.DeployModel(ssas, File.ReadAllText(Path.Combine(root, "ssas", "model.tmsl.json")));
            _databases[Ssas] = ssas + ";Catalog=qis_budget";
        }
        if (Server(Postgres) is { } postgres)
        {
            using (var con = new NpgsqlConnection(postgres)) { con.Open(); Execute(con, $"CREATE DATABASE {_name}"); }
            var db = new NpgsqlConnectionStringBuilder(postgres) { Database = _name }.ConnectionString;
            PostgresSeed.Load(db, ConformanceTests.Seed);
            _databases[Postgres] = db;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _directory?.Delete(recursive: true); } catch (IOException) { /* a file DuckDB still holds is the temp folder's to clean */ }
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

    /// <summary>The connection string of the seeded scratch database on the engine (the file, for DuckDB); skips the test when the engine is not provided, fails it when it is required.</summary>
    public string Database(string engine)
    {
        if (_databases.TryGetValue(engine, out var db)) return db;
        Assert.False(Required(engine), $"{engine} is required but its DBDATA_TEST_* variable is not set");
        Assert.Skip($"set DBDATA_TEST_{Suffix(engine)} to run on {engine}");
        return "";
    }

    private static void Execute(System.Data.Common.DbConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
