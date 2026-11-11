using System.IO.Compression;
using DbDataKit.State;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataConsole.Tests;

/// <summary>New empty databases on the engines of the shared test environment (<c>DBDATAKIT_TEST_POSTGRES</c>, <c>DBDATAKIT_TEST_SQLSERVER</c>), named the way a console's operator names them: the password is a reference to an environment variable, so no test text holds it.</summary>
internal sealed class StateVerbDatabases : IDisposable
{
    public const string PasswordVariable = "CONSOLE_VERB_TEST_PASSWORD";

    private readonly List<Action> _cleanups = [];

    /// <summary>The engines the verbs are tested on; a server engine whose environment variable is not set skips its row.</summary>
    public static TheoryData<string> Engines => ["sqlite", "postgres", "mssql"];

    public static TheoryData<string> ServerEngines => ["postgres", "mssql"];

    public string Password { get; private set; } = "";

    /// <summary>A new empty database: its connection string (the password as <c>env:</c> reference) and the password for the environment.</summary>
    public string Provision(string engine)
    {
        var server = engine == "mssql" ? Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER") : Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES");
        if (server is null) Assert.Skip($"set DBDATAKIT_TEST_{(engine == "mssql" ? "SQLSERVER" : "POSTGRES")} to run on {engine}");

        var name = $"console_verbs_{Guid.NewGuid():N}";
        if (engine == "mssql")
        {
            var builder = new SqlConnectionStringBuilder(server);
            Password = builder.Password;
            using (var admin = new SqlConnection(server))
            {
                admin.Open();
                using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{name}]";
                create.ExecuteNonQuery();
            }

            _cleanups.Add(() =>
            {
                SqlConnection.ClearAllPools();
                using var admin = new SqlConnection(server);
                admin.Open();
                using var drop = admin.CreateCommand();
                drop.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];";
                drop.ExecuteNonQuery();
            });
            builder.InitialCatalog = name;
            builder.Remove("Password");
            return builder.ConnectionString + $";Password=env:{PasswordVariable}";
        }
        else
        {
            var builder = new NpgsqlConnectionStringBuilder(server);
            Password = builder.Password!;
            using (var admin = new NpgsqlConnection(server))
            {
                admin.Open();
                using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE {name}";
                create.ExecuteNonQuery();
            }

            _cleanups.Add(() =>
            {
                NpgsqlConnection.ClearAllPools();
                using var admin = new NpgsqlConnection(server);
                admin.Open();
                using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE)";
                drop.ExecuteNonQuery();
            });
            builder.Database = name;
            builder.Password = null;
            return builder.ConnectionString + $";Password=env:{PasswordVariable}";
        }
    }

    public void Dispose()
    {
        foreach (var cleanup in _cleanups) { try { cleanup(); } catch (Exception) { /* a test database that cannot be dropped is left behind, not a failure */ } }
    }

    /// <summary>Every byte of every file under the directories (table files decompressed), as text, for a search for a secret.</summary>
    public static string EverythingUnder(params string[] directories)
    {
        var text = new System.Text.StringBuilder();
        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".db", StringComparison.Ordinal) || file.Contains("secrets", StringComparison.Ordinal) || file.EndsWith("-wal", StringComparison.Ordinal) || file.EndsWith("-shm", StringComparison.Ordinal)) continue;
                using var raw = File.OpenRead(file);
                var gzip = raw.Length > 2 && raw.ReadByte() == 0x1f && raw.ReadByte() == 0x8b;
                raw.Position = 0;
                using var stream = gzip ? new GZipStream(raw, CompressionMode.Decompress) : (Stream)raw;
                using var reader = new StreamReader(stream);
                text.Append(file).Append('\n').Append(reader.ReadToEnd()).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The settings a console opens its state with for a database made by <see cref="Provision"/>.</summary>
    public static StateSettings SettingsFor(string engine, string? connection) => new(engine, connection);

    public Func<string, string?> EnvFor(string? engine, string? connection) => key =>
        key == PasswordVariable ? Password
        : key == DbDataConsole.Server.ConsoleState.EngineVariable ? engine
        : key == DbDataConsole.Server.ConsoleState.ConnectionVariable ? connection
        : null;
}
