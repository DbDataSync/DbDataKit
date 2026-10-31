using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State.Tests;

/// <summary>
/// A throwaway state database per test on SQLite, and, when the shared engines are up (<c>testenv/testenv.sh up mssql postgres</c>), on SQL Server and PostgreSQL:
/// <c>DBDATA_TEST_SQLSERVER</c> and <c>DBDATA_TEST_POSTGRES</c> name a server, and each test gets its own scratch database on it. An engine whose variable is not set
/// skips its theory rows, unless <c>DBDATA_REQUIRE_SQLSERVER</c> / <c>DBDATA_REQUIRE_POSTGRES</c> is <c>true</c>, which fails them instead.
/// </summary>
public sealed class StateEngines : IDisposable
{
    private static readonly object Registration = new();
    private readonly List<Action> _cleanups = [];

    public static string? Server(string engine) => engine switch
    {
        StateEngineIds.MsSql => Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER"),
        StateEngineIds.Postgres => Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES"),
        _ => null,
    };

    private static bool Required(string engine) => string.Equals(
        Environment.GetEnvironmentVariable(engine == StateEngineIds.MsSql ? "DBDATA_REQUIRE_SQLSERVER" : "DBDATA_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase);

    public static TheoryData<string> Engines => [StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres];

    /// <summary>The provider factories the server dialects ask for, by library id: here the packages are referenced directly.</summary>
    private static DbProviderFactory Factory(string libraryId) => libraryId switch
    {
        MsSqlStateDialect.LibraryId => SqlClientFactory.Instance,
        PostgresStateDialect.LibraryId => NpgsqlFactory.Instance,
        _ => throw new InvalidOperationException($"no provider for '{libraryId}'"),
    };

    /// <summary>The standard schema every shared store needs.</summary>
    public static StateSchema Schema { get; } = new([IdentitySchema.Accounts, IdentitySchema.Invites, NotificationSchema.Feed, IdentitySchema.AccountKeys]);

    public StateDatabase Open(string engine, StateSchema? schema = null)
    {
        schema ??= Schema;
        if (engine == StateEngineIds.Sqlite)
        {
            var dir = Directory.CreateTempSubdirectory("dbdatakit-state-");
            _cleanups.Add(() => Directory.Delete(dir.FullName, recursive: true));
            return new StateDatabase(Path.Combine(dir.FullName, "state.db"), schema);
        }

        var server = Server(engine);
        if (server is null)
        {
            Assert.False(Required(engine), $"{engine} is required but its DBDATA_TEST_* variable is not set");
            Assert.Skip($"set DBDATA_TEST_{(engine == StateEngineIds.MsSql ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        }

        lock (Registration) StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
        var name = $"dbdatakit_state_{Guid.NewGuid():N}";
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
        return new StateDatabase(engine, connectionString, schema);
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
            catch (Exception) { /* a scratch database left behind is untidy; the name carries a GUID, so nothing collides */ }
        }
    }
}
