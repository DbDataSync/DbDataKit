using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.State.Cli;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State.Api.Tests;

/// <summary>An audit that keeps what it was given.</summary>
public sealed class MemoryAudit : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries { get { lock (_entries) return [.. _entries]; } }

    public void Append(AuditEntry entry) { lock (_entries) _entries.Add(entry); }

    public IReadOnlyList<AuditEntry> Read(int limit = 100) => Entries;
}

/// <summary>A scratch state database per test: SQLite, and SQL Server and PostgreSQL when the shared engines are up (<c>DBDATAKIT_TEST_SQLSERVER</c>, <c>DBDATAKIT_TEST_POSTGRES</c>).</summary>
public sealed class ScratchDatabases : IDisposable
{
    public const string Password = "Pl@nted_Secret_Pw9";

    private readonly List<Action> _cleanups = [];

    public static TheoryData<string> Engines => [StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres];

    public static StateModule Module(string name, int migrations = 2) => new(name, StateScopeKind.Install,
        [.. new[] { "CREATE TABLE {table:item} (id {{key}} PRIMARY KEY, label {{text}} NULL);", "CREATE TABLE {table:extra} (id {{key}} PRIMARY KEY);" }.Take(migrations)]);

    private static string? Server(string engine) => engine switch
    {
        StateEngineIds.MsSql => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER"),
        StateEngineIds.Postgres => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES"),
        _ => null,
    };

    public sealed class Db(string engine, string target)
    {
        public string Engine { get; } = engine;

        /// <summary>The SQLite file, or the connection string of the scratch database.</summary>
        public string Target { get; } = target;

        public StateDatabase Open(IEnumerable<StateModule> modules)
        {
            StateOpener.RegisterProviders();
            var layout = StateLayout.Create(Engine, modules, StateScope.Install);
            if (Engine == StateEngineIds.Sqlite) Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            return Engine == StateEngineIds.Sqlite ? new StateDatabase(Target, layout) : new StateDatabase(Engine, Target, layout);
        }

        public string? PasswordOf() => Engine switch
        {
            StateEngineIds.MsSql => new SqlConnectionStringBuilder(Target).Password,
            StateEngineIds.Postgres => new NpgsqlConnectionStringBuilder(Target).Password,
            _ => null,
        };

        /// <summary>The connection string with the password written as the reference <c>env:STATE_TEST_PASSWORD</c>.</summary>
        public string WithPasswordReference() => Engine == StateEngineIds.MsSql
            ? new SqlConnectionStringBuilder(Target) { Password = "env:STATE_TEST_PASSWORD" }.ConnectionString
            : new NpgsqlConnectionStringBuilder(Target) { Password = "env:STATE_TEST_PASSWORD" }.ConnectionString;
    }

    public Db Provision(string engine)
    {
        if (engine == StateEngineIds.Sqlite)
        {
            var dir = Directory.CreateTempSubdirectory("dbdatakit-stateapi-");
            _cleanups.Add(() => { SqliteClear(); Directory.Delete(dir.FullName, recursive: true); });
            return new Db(engine, Path.Combine(dir.FullName, "state.db"));
        }

        var server = Server(engine);
        if (server is null)
        {
            Assert.False(Environment.GetEnvironmentVariable(engine == StateEngineIds.MsSql ? "DBDATAKIT_TEST_REQUIRE_SQLSERVER" : "DBDATAKIT_TEST_REQUIRE_POSTGRES") is "true", $"{engine} is required but its DBDATAKIT_TEST_* variable is not set");
            Assert.Skip($"set DBDATAKIT_TEST_{(engine == StateEngineIds.MsSql ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        }

        StateOpener.RegisterProviders();
        var name = $"dbdatakit_stateapi_{Guid.NewGuid():N}";
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

    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static void Exec(string engine, string connectionString, string sql)
    {
        using var connection = StateDialect.For(engine).CreateConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static string EngineName(string engine) => engine == StateEngineIds.MsSql ? "mssql" : engine == StateEngineIds.Postgres ? "postgres" : "sqlite";

    public void Dispose()
    {
        foreach (var cleanup in _cleanups) cleanup();
    }
}

/// <summary>
/// A host with the shared state endpoints and maintenance middleware in front of two stand-in endpoints (<c>/work</c>, and <c>/slow</c> which waits until the test lets it go). Who is calling is the <c>X-Role</c> header
/// (<c>viewer</c>, <c>admin</c> or none) and <c>X-User</c>, in place of a console's token.
/// </summary>
public sealed class StateApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StateApiHost(StateManagementOptions options, IMaintenanceGate gate)
    {
        Audit = (MemoryAudit)options.Audit;
        Gate = gate;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseMaintenance(gate, context => RoleOf(context) == "admin");
        _app.MapState(new StateManagement(options), context =>
        {
            var role = RoleOf(context);
            var user = context.Request.Headers["X-User"].FirstOrDefault() ?? "anonymous";
            return StateCaller.FromSession(user, user, needed => role switch
            {
                "admin" => true,
                "viewer" => needed == ServerRole.Viewer,
                _ => false,
            });
        });
        _app.MapGet("/work", () => "done");
        _app.MapGet("/slow", async () =>
        {
            _slowStarted.TrySetResult();
            await _release.Task;
            return "slow done";
        });
        _app.MapGet("/health", () => "ok");
        _app.StartAsync().GetAwaiter().GetResult();
        Client = _app.GetTestClient();
    }

    public MemoryAudit Audit { get; }

    public IMaintenanceGate Gate { get; }

    public HttpClient Client { get; }

    public Task SlowStarted => _slowStarted.Task;

    public void ReleaseSlow() => _release.TrySetResult();

    private static string? RoleOf(HttpContext context) => context.Request.Headers["X-Role"].FirstOrDefault();

    public HttpRequestMessage Request(HttpMethod method, string path, string? role, string user = "alice", string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (role is not null) request.Headers.Add("X-Role", role);
        request.Headers.Add("X-User", user);
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    public async Task<(HttpResponseMessage Response, string Text)> SendAsync(HttpMethod method, string path, string? role, string user = "alice", string? json = null)
    {
        var response = await Client.SendAsync(Request(method, path, role, user, json));
        return (response, await response.Content.ReadAsStringAsync());
    }

    public static JsonNode Parse(string text) => JsonNode.Parse(text)!;

    public async ValueTask DisposeAsync()
    {
        _release.TrySetResult();
        Client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>The options of a product over <paramref name="db"/>: the settings name its engine and connection as written; <paramref name="resolver"/> looks up the references in it.</summary>
    public static StateManagementOptions Options(ScratchDatabases.Db db, IReadOnlyList<StateModule> modules, string? connection = null, Func<ISecretResolver?>? resolver = null, IMaintenanceGate? gate = null, IInstanceDirectory? instances = null, MemoryAudit? audit = null, string settingsFile = "settings.yaml", bool move = false, Func<DbDataKit.State.Engines.Snapshots.MoveOptions, DbDataKit.State.Engines.Snapshots.MoveOptions>? tuneMove = null) =>
        new("testproduct", "testtool", "the test product",
            () => new StateTarget(
                new StateSettings(ScratchDatabases.EngineName(db.Engine), db.Engine == StateEngineIds.Sqlite ? null : connection ?? db.Target) { Namespaces = StateNamespaceSettings.Defaults },
                db.Engine == StateEngineIds.Sqlite ? db.Target : Path.Combine(Path.GetTempPath(), "unused.db"), resolver, settingsFile, StateSources.Defaults),
            modules, StateScope.Install, gate ?? new MaintenanceGate(), audit ?? new MemoryAudit(), InstanceId: "instance-1", Version: "1.2.3", Instances: instances, Move: move, TuneMove: tuneMove);
}
