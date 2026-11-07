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

    /// <summary>A scratch database that can be opened more than once, with the old structure or with modules.</summary>
    public sealed class Location(string engine, string target)
    {
        public string Engine { get; } = engine;

        /// <summary>The file, for SQLite; a connection string for the others.</summary>
        public string Target { get; } = target;

        public StateDatabase Open(StateSchema schema) =>
            Engine == StateEngineIds.Sqlite ? new StateDatabase(Target, schema) : new StateDatabase(Engine, Target, schema);

        public StateDatabase Open(IEnumerable<StateModule> modules, StateScope? scope = null, StateNamespaceSettings? settings = null, TimeProvider? time = null, string? appliedBy = null)
        {
            var layout = StateLayout.Create(Engine, modules, scope, settings);
            return Engine == StateEngineIds.Sqlite ? new StateDatabase(Target, layout, appliedBy, time) : new StateDatabase(Engine, Target, layout, appliedBy, time);
        }

        /// <summary>Runs a statement on the scratch database outside the state layer, to look at or damage what it wrote.</summary>
        public object? Scalar(string sql)
        {
            using var connection = StateDialect.For(Engine).CreateConnection(Engine == StateEngineIds.Sqlite ? $"Data Source={Target};Pooling=False" : Target);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
    }

    public StateDatabase Open(string engine, StateSchema? schema = null) => Provision(engine).Open(schema ?? Schema);

    public Location Provision(string engine)
    {
        if (engine == StateEngineIds.Sqlite)
        {
            var dir = Directory.CreateTempSubdirectory("dbdatakit-state-");
            _cleanups.Add(() => Directory.Delete(dir.FullName, recursive: true));
            return new Location(engine, Path.Combine(dir.FullName, "state.db"));
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
        return new Location(engine, connectionString);
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

/// <summary>A clock a test moves: what the state layer reads as "now" (<see cref="StateDatabase.Time"/>).</summary>
public sealed class StateTestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
