using DbDataKit.Secrets;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State.Tests;

/// <summary>A product's state database opens on the engine its operator names (plan 13a, P11): SQLite from a file, and, when the shared engines are up, a scratch database on SQL Server or PostgreSQL through the same call.</summary>
public sealed class StateOpenerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("state-opener").FullName;
    private static readonly StateSchema Schema = new([NotificationSchema.Feed]);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(null, StateEngineIds.Sqlite)]
    [InlineData("", StateEngineIds.Sqlite)]
    [InlineData("SQLite", StateEngineIds.Sqlite)]
    [InlineData("mssql", StateEngineIds.MsSql)]
    [InlineData("SqlServer", StateEngineIds.MsSql)]
    [InlineData("Postgres", StateEngineIds.Postgres)]
    [InlineData("postgresql", StateEngineIds.Postgres)]
    public void An_engine_is_named_in_any_case_and_by_either_of_its_usual_names(string? name, string expected) =>
        Assert.Equal(expected, new StateSettings(name).EngineId);

    [Fact]
    public void An_unknown_engine_is_refused_with_the_names_that_are_accepted() =>
        Assert.Contains("sqlite, mssql or postgres", Assert.Throws<InvalidOperationException>(() => new StateSettings("oracle").EngineId).Message);

    [Fact]
    public void Nothing_named_is_a_file_in_the_products_own_directory_and_the_connection_string_is_ignored()
    {
        var file = Path.Combine(_directory, "state.db");
        var database = StateOpener.Open(new StateSettings(null, "Server=ignored"), file, Schema);

        Assert.Equal(StateEngineIds.Sqlite, database.Dialect.Engine);
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("mssql")]
    [InlineData("postgres")]
    public void A_server_engine_without_a_connection_string_stops_the_start_and_says_why(string engine) =>
        Assert.Contains("needs a connection string", Assert.Throws<InvalidOperationException>(() => StateOpener.Open(new StateSettings(engine), Path.Combine(_directory, "x.db"), Schema)).Message);

    [Fact]
    public void A_secret_the_providers_do_not_have_is_named_and_its_value_is_never_asked_for()
    {
        var resolver = new SecretResolverChain([]);
        var failure = Assert.Throws<InvalidOperationException>(() => StateOpener.Open(
            new StateSettings("postgres", "Host=db;Database=state;Username=u;Password=secret:state-db"), Path.Combine(_directory, "x.db"), Schema, resolver));

        Assert.Contains("state-db", failure.Message);
    }

    [Theory]
    [InlineData("mssql", "DBDATA_TEST_SQLSERVER")]
    [InlineData("postgres", "DBDATA_TEST_POSTGRES")]
    public void A_server_engine_opens_a_database_the_operator_names_and_keeps_state_in_it(string engine, string variable)
    {
        var server = Environment.GetEnvironmentVariable(variable);
        if (server is null) Assert.Skip($"set {variable} to run on {engine}");

        var name = $"dbdatakit_opener_{Guid.NewGuid():N}";
        var scratch = engine == "mssql" ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};";
        StateOpener.Open(new StateSettings(engine, server!), Path.Combine(_directory, "x.db"), Schema).Dialect.ToString();   // registers the provider
        using (var admin = StateDialect.For(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres).CreateConnection(server!))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = scratch;
            create.ExecuteNonQuery();
        }
        try
        {
            var connectionString = engine == "mssql" ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString : new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
            var database = StateOpener.Open(new StateSettings(engine, connectionString), Path.Combine(_directory, "x.db"), Schema);
            var store = new NotificationStore(database);
            Assert.NotNull(store);
            Assert.False(File.Exists(Path.Combine(_directory, "x.db")), "a server engine keeps no SQLite file");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            NpgsqlConnection.ClearAllPools();
            using var admin = StateDialect.For(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres).CreateConnection(server!);
            admin.Open();
            using var drop = admin.CreateCommand();
            drop.CommandText = engine == "mssql" ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE);";
            drop.ExecuteNonQuery();
        }
    }
}
