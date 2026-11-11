using System.Data.Common;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Secrets;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State.Cli.Tests;

/// <summary>What one run of a verb printed.</summary>
public sealed record Ran(int Exit, string Out, string Error, JsonNode Json)
{
    public string All => Out + Error + Json.ToJsonString();

    public JsonNode Data => Json["data"]!;
}

/// <summary>
/// A scratch state database per test on SQLite and, when the shared engines are up (<c>DBDATAKIT_TEST_SQLSERVER</c>, <c>DBDATAKIT_TEST_POSTGRES</c>), on SQL Server and PostgreSQL, with the
/// helpers to run the <c>state</c> verbs against it as a product would register them.
/// </summary>
public sealed class StateCliHarness : IDisposable
{
    public const string Password = "Pl@nted_Secret_Pw9";

    private static readonly object Registration = new();
    private readonly List<Action> _cleanups = [];

    public static TheoryData<string> Engines => [StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres];

    public static TheoryData<string> ServerEngines => [StateEngineIds.MsSql, StateEngineIds.Postgres];

    public static StateModule Module(string name, int migrations = 2) => new(name, StateScopeKind.Install,
        [.. new[] { "CREATE TABLE {table:item} (id {{key}} PRIMARY KEY, label {{text}} NULL);", "CREATE TABLE {table:extra} (id {{key}} PRIMARY KEY);" }.Take(migrations)]);

    private static string? Server(string engine) => engine switch
    {
        StateEngineIds.MsSql => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER"),
        StateEngineIds.Postgres => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES"),
        _ => null,
    };

    /// <summary>A scratch database: <see cref="Target"/> is the SQLite file (its folder does not exist yet) or the admin's connection string for a fresh database on the server.</summary>
    public sealed class Db(string engine, string target)
    {
        public string Engine { get; } = engine;

        public string Target { get; } = target;

        public StateDatabase Open(IEnumerable<StateModule> modules, StateNamespaceSettings? settings = null)
        {
            StateOpener.RegisterProviders();
            var layout = StateLayout.Create(Engine, modules, StateScope.Install, settings);
            return Engine == StateEngineIds.Sqlite ? new StateDatabase(Target, layout) : new StateDatabase(Engine, Target, layout);
        }

        public object? Scalar(string sql)
        {
            StateOpener.RegisterProviders();
            using var connection = StateDialect.For(Engine).CreateConnection(Engine == StateEngineIds.Sqlite ? $"Data Source={Target};Pooling=False" : Target);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        /// <summary>The same connection with another login.</summary>
        public string As(string login, string password) => Engine == StateEngineIds.MsSql
            ? new SqlConnectionStringBuilder(Target) { UserID = login, Password = password }.ConnectionString
            : new NpgsqlConnectionStringBuilder(Target) { Username = login, Password = password }.ConnectionString;

        public string WithDatabase(string database) => Engine == StateEngineIds.MsSql
            ? new SqlConnectionStringBuilder(Target) { InitialCatalog = database }.ConnectionString
            : new NpgsqlConnectionStringBuilder(Target) { Database = database }.ConnectionString;

        public string CreateLogin(StateHarnessCleanup cleanup)
        {
            var login = "cli_" + Guid.NewGuid().ToString("N")[..12];
            if (Engine == StateEngineIds.MsSql)
            {
                Scalar($"CREATE LOGIN [{login}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF;");
                Scalar($"CREATE USER [{login}] FOR LOGIN [{login}];");
            }
            else Scalar($"CREATE ROLE {login} LOGIN PASSWORD '{Password}';");
            cleanup(() =>
            {
                try
                {
                    if (Engine == StateEngineIds.MsSql) { SqlConnection.ClearAllPools(); Scalar($"DROP USER [{login}]; DROP LOGIN [{login}];"); }
                    else { NpgsqlConnection.ClearAllPools(); Scalar($"DROP OWNED BY {login}; DROP ROLE {login};"); }
                }
                catch (Exception) { /* a login left behind is untidy; its name carries a GUID */ }
            });
            return login;
        }

        public void Grant(IEnumerable<StateModule> modules, string login, GrantLevel level)
        {
            foreach (var statement in StateGrants.Statements(StateLayout.Create(Engine, modules, StateScope.Install), login, level)) Scalar(statement);
        }
    }

    public delegate void StateHarnessCleanup(Action action);

    public Db Provision(string engine)
    {
        if (engine == StateEngineIds.Sqlite)
        {
            var dir = Directory.CreateTempSubdirectory("dbdatakit-statecli-");
            _cleanups.Add(() => Directory.Delete(dir.FullName, recursive: true));
            return new Db(engine, Path.Combine(dir.FullName, "not-yet", "state.db"));
        }

        var server = Server(engine);
        if (server is null)
        {
            Assert.False(Environment.GetEnvironmentVariable(engine == StateEngineIds.MsSql ? "DBDATAKIT_TEST_REQUIRE_SQLSERVER" : "DBDATAKIT_TEST_REQUIRE_POSTGRES") is "true", $"{engine} is required but its DBDATAKIT_TEST_* variable is not set");
            Assert.Skip($"set DBDATAKIT_TEST_{(engine == StateEngineIds.MsSql ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        }

        StateOpener.RegisterProviders();
        var name = $"dbdatakit_statecli_{Guid.NewGuid():N}";
        Exec(engine, server!, engine == StateEngineIds.MsSql ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        _cleanups.Add(() =>
        {
            try
            {
                if (engine == StateEngineIds.MsSql) { SqlConnection.ClearAllPools(); Exec(engine, server!, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];"); }
                else { NpgsqlConnection.ClearAllPools(); Exec(engine, server!, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);"); }
            }
            catch (Exception) { /* a scratch database left behind is untidy; the name carries a GUID */ }
        });
        return new Db(engine, engine == StateEngineIds.MsSql
            ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString
            : new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
    }

    public void OnDispose(Action action) => _cleanups.Add(action);

    private static void Exec(string engine, string connectionString, string sql)
    {
        using var connection = StateDialect.For(engine).CreateConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The engine name settings use.</summary>
    public static string EngineName(string engine) => engine == StateEngineIds.MsSql ? "mssql" : engine == StateEngineIds.Postgres ? "postgres" : "sqlite";

    /// <summary>A product's context over a scratch database: its settings name the engine and <paramref name="connection"/> (the database's own unless another is given).</summary>
    public static StateVerbContext Context(Db db, IReadOnlyList<StateModule>? modules, string? connection = null, StateNamespaceSettings? namespaces = null, Func<ISecretResolver?>? secrets = null, Func<DbConnection?, string?>? version = null) =>
        new("tool", "the test tool",
            _ => new StateTarget(new StateSettings(EngineName(db.Engine), db.Engine == StateEngineIds.Sqlite ? null : connection ?? db.Target) { Namespaces = namespaces ?? StateNamespaceSettings.Defaults }, db.Engine == StateEngineIds.Sqlite ? db.Target : Path.Combine(Path.GetTempPath(), "unused.db"), secrets),
            modules, StateScope.Install, ["repo"], "[--repo <directory>]")
        { EngineVersion = version };

    /// <summary>Runs <c>state &lt;verb&gt;</c> with <c>--format json</c>, as the shared router does.</summary>
    public static async Task<Ran> RunAsync(StateVerbContext context, params string[] args)
    {
        var router = new NounRouter("tool", [StateVerbs.Create(context)], "1.0");
        var output = new StringWriter();
        var error = new StringWriter();
        var text = new StringWriter();
        var textError = new StringWriter();
        var exit = await router.RunAsync(["state", .. args], new CliContext(text, textError, TextReader.Null));
        _ = await router.RunAsync(["state", .. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        return new Ran(exit, text.ToString(), textError.ToString(), JsonNode.Parse(output.ToString())!);
    }

    public void Dispose()
    {
        foreach (var cleanup in _cleanups) cleanup();
    }
}
