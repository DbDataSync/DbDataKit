using DbDataConsole.Server;
using DbDataKit.State;
using Npgsql;

namespace DbDataConsole.Tests;

/// <summary>The console keeps its state on the engine its operator names (plan 13a, P11): SQLite unless flags or the environment say otherwise, and a PostgreSQL database when one is named, with the password a secret reference.</summary>
public sealed class ConsoleStateEngineTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("ddk-console-state").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void Nothing_named_is_sqlite_in_the_console_directory_and_the_flags_win_over_the_environment()
    {
        Assert.Null(ConsoleState.SettingsFrom(_ => null));

        var fromEnvironment = ConsoleState.SettingsFrom(name => name switch { ConsoleState.EngineVariable => "mssql", ConsoleState.ConnectionVariable => "Server=a", _ => null });
        Assert.Equal("mssql", fromEnvironment!.Engine);

        var flagged = ConsoleState.SettingsFrom(name => name == ConsoleState.EngineVariable ? "mssql" : null, "postgres", "Host=b");
        Assert.Equal(("postgres", "Host=b"), (flagged!.Engine, flagged.ConnectionString));

        var database = ConsoleState.Open(directory);
        Assert.Equal(StateEngineIds.Sqlite, database.Dialect.Engine);
        Assert.True(File.Exists(Path.Combine(directory, "state.db")));
    }

    [Fact]
    public void A_server_engine_without_a_connection_string_stops_the_open_and_says_why() =>
        Assert.Contains("needs a connection string", Assert.Throws<InvalidOperationException>(() => ConsoleState.Open(directory, new StateSettings("postgres"))).Message);

    [Fact]
    public void Users_are_kept_in_a_postgresql_database_the_operator_names_with_the_password_from_the_environment()
    {
        var server = Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES");
        if (server is null) Assert.Skip("set DBDATA_TEST_POSTGRES to run on PostgreSQL");

        var name = $"console_state_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(server);
        var password = builder.Password;
        using (var admin = new NpgsqlConnection(server))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {name}";
            create.ExecuteNonQuery();
        }
        try
        {
            builder.Database = name;
            builder.Password = null;
            var settings = new StateSettings("postgres", builder.ConnectionString + ";Password=env:CONSOLE_STATE_PASSWORD");
            var database = ConsoleState.Open(directory, settings, key => key == "CONSOLE_STATE_PASSWORD" ? password : null);

            var users = new UserStore(database);
            var made = users.CreateUser("Pat", null, UserRole.Admin);
            Assert.Equal(made.Id, users.List().Single().Id);
            Assert.False(File.Exists(Path.Combine(directory, "state.db")), "a server engine keeps no SQLite file");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            using var admin = new NpgsqlConnection(server);
            admin.Open();
            using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE)";
            drop.ExecuteNonQuery();
        }
    }
}
