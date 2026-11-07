using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using System.Data.Common;
using DbDataKit.State;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataRunner.Tests;

/// <summary>
/// A throwaway state per test on SQLite and, when the shared engines are up (<c>testenv/testenv.sh up mssql postgres</c>, then <c>eval "$(testenv/testenv.sh env dbdatakit)"</c>), on SQL Server and PostgreSQL
/// (<c>DBDATA_TEST_SQLSERVER</c> and <c>DBDATA_TEST_POSTGRES</c>): the arrangement of the state suite. An engine whose variable is not set skips its rows, unless <c>DBDATA_REQUIRE_SQLSERVER</c> / <c>DBDATA_REQUIRE_POSTGRES</c> is true.
/// </summary>
public sealed class StateEngines : IDisposable
{
    private static readonly object Registration = new();
    private readonly List<Action> _cleanups = [];

    public static TheoryData<string> Engines => [StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres];

    private static string? Server(string engine) => engine switch
    {
        StateEngineIds.MsSql => Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER"),
        StateEngineIds.Postgres => Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES"),
        _ => null,
    };

    private static bool Required(string engine) => string.Equals(
        Environment.GetEnvironmentVariable(engine == StateEngineIds.MsSql ? "DBDATA_REQUIRE_SQLSERVER" : "DBDATA_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase);

    private static DbProviderFactory Factory(string libraryId) => libraryId switch
    {
        MsSqlStateDialect.LibraryId => SqlClientFactory.Instance,
        PostgresStateDialect.LibraryId => NpgsqlFactory.Instance,
        _ => throw new InvalidOperationException($"no provider for '{libraryId}'"),
    };

    /// <summary>The settings of a new, empty state: no setting at all for SQLite (the file in the server's directory), a new database on the server for the others.</summary>
    public StateSettings Provision(string engine)
    {
        if (engine == StateEngineIds.Sqlite) return new StateSettings();
        var server = Server(engine);
        if (server is null)
        {
            Assert.False(Required(engine), $"{engine} is required but its DBDATA_TEST_* variable is not set");
            Assert.Skip($"set DBDATA_TEST_{(engine == StateEngineIds.MsSql ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        }

        var name = $"dbdatarunner_{Guid.NewGuid():N}";
        Execute(engine, server!, engine == StateEngineIds.MsSql ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        _cleanups.Add(() =>
        {
            if (engine == StateEngineIds.MsSql)
            {
                SqlConnection.ClearAllPools();
                Execute(engine, server!, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            }
            else
            {
                NpgsqlConnection.ClearAllPools();
                Execute(engine, server!, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
            }
        });
        var connectionString = engine == StateEngineIds.MsSql
            ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString
            : new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        return new StateSettings(engine, connectionString);
    }

    private static void Execute(string engine, string connectionString, string sql)
    {
        lock (Registration) StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
        using var connection = StateDialect.For(engine).CreateConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var cleanup in _cleanups)
        {
            try { cleanup(); }
            catch (Exception) { /* a scratch database left behind is untidy; its name carries a GUID */ }
        }
    }
}
