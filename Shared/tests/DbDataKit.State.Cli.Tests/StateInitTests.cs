using System.Data.Common;
using DbDataKit.Secrets;
using Microsoft.Data.SqlClient;
using Npgsql;
using static DbDataKit.State.Cli.Tests.StateCliHarness;

namespace DbDataKit.State.Cli.Tests;

/// <summary>Plan 29b C03: <c>state init --topology service|group|install</c> on SQLite, PostgreSQL and SQL Server.</summary>
public sealed class StateInitTests : IDisposable
{
    private const string Yaml = "# the operator's own file\nlisten: 0.0.0.0:8470   # keep this\nlogging:\n  level: debug\n";

    private readonly StateCliHarness _h = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dbdatakit-stateinit-");

    public void Dispose()
    {
        _h.Dispose();
        _dir.Delete(true);
    }

    private string SettingsFile => Path.Combine(_dir.FullName, "server.yaml");

    /// <summary>A product's context: its settings are whatever the file says (the defaults without one), its secrets come from two variables.</summary>
    private StateVerbContext Product(Db db, IReadOnlyList<StateModule> modules, StateScope? scope = null, string? service = null, string? settingsFile = null)
    {
        var file = settingsFile ?? SettingsFile;
        var sqlite = db.Engine == StateEngineIds.Sqlite ? db.Target : Path.Combine(_dir.FullName, "state.db");
        return new StateVerbContext("tool", "the test tool",
            _ => new StateTarget(StateSettingsWriter.Load(file), sqlite, () => new SecretResolverChain([new EnvSecretProvider(name => name switch { "INIT_PW" => Password, "INIT_ADMIN_PW" => AdminPassword(db), _ => null })]), file),
            modules, scope ?? new StateScope(Service: service), ["repo"], "[--repo <directory>]");
    }

    private static string AdminPassword(Db db) => db.Engine == StateEngineIds.MsSql ? new SqlConnectionStringBuilder(db.Target).Password : new NpgsqlConnectionStringBuilder(db.Target).Password!;

    /// <summary>The database's connection string logging in as <paramref name="login"/>, its password a reference to INIT_PW.</summary>
    private static string Referenced(Db db, string login, string variable = "INIT_PW") => db.Engine == StateEngineIds.MsSql
        ? new SqlConnectionStringBuilder(db.Target) { UserID = login, Password = "env:" + variable }.ConnectionString
        : new NpgsqlConnectionStringBuilder(db.Target) { Username = login, Password = "env:" + variable }.ConnectionString;

    private static string AdminReference(Db db) => db.Engine == StateEngineIds.MsSql
        ? new SqlConnectionStringBuilder(db.Target) { Password = "env:INIT_ADMIN_PW" }.ConnectionString
        : new NpgsqlConnectionStringBuilder(db.Target) { Password = "env:INIT_ADMIN_PW" }.ConnectionString;

    private static string AdminLogin(Db db) => db.Engine == StateEngineIds.MsSql ? new SqlConnectionStringBuilder(db.Target).UserID : new NpgsqlConnectionStringBuilder(db.Target).Username!;

    private static string EngineWord(Db db) => EngineName(db.Engine);

    private static long Schemas(Db db) => Convert.ToInt64(db.Scalar(db.Engine == StateEngineIds.MsSql ? "SELECT COUNT(*) FROM sys.schemas WHERE name LIKE 'dbdatakit%';" : "SELECT COUNT(*) FROM pg_namespace WHERE nspname LIKE 'dbdatakit%';"));

    private static IReadOnlyList<StateModule> Service(string name) => [new StateModule("shared", StateScopeKind.Install, ["CREATE TABLE {table:item} (id {{key}} PRIMARY KEY);"]), new StateModule(name, StateScopeKind.Service, ["CREATE TABLE {table:item} (id {{key}} PRIMARY KEY);"])];

    // ---- service

    [Fact]
    public async Task Service_topology_on_a_fresh_machine_writes_nothing_and_the_test_passes()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);

        var ran = await RunAsync(Product(db, [Module("alpha")]), "init", "--topology", "service");

        Assert.Equal(0, ran.Exit);
        Assert.False(File.Exists(SettingsFile), "the defaults are the service topology: nothing to write");
        Assert.DoesNotContain("CHANGED", ran.Out);
        Assert.True((bool)ran.Data["ok"]!);
        Assert.Contains("OK      test:settings", ran.Out);
        Assert.False(File.Exists(db.Target), "init creates no database file; the first start does");
    }

    [Fact]
    public async Task Service_topology_returns_an_unpopulated_server_setting_to_a_file_and_keeps_the_rest_of_the_settings_file()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);
        File.WriteAllText(SettingsFile, Yaml + "state:\n  engine: postgres\n  connection: Host=nowhere;Database=x;Password=env:INIT_PW\n  retention:\n    days: 7\n");

        var ran = await RunAsync(Product(db, [Module("alpha")]), "init", "--topology", "service");

        Assert.Equal(0, ran.Exit);
        Assert.Contains("CHANGED settings", ran.Out);   // the harness runs the verb twice (text, then JSON): the document is the second run's
        var text = File.ReadAllText(SettingsFile);
        Assert.StartsWith(Yaml, text);   // everything before the section is byte for byte
        var written = StateSettingsWriter.Load(SettingsFile);
        Assert.Equal("sqlite", written.Engine);
        Assert.Null(written.ConnectionString);
        Assert.Equal(7, written.Retention.Days);

        var again = await RunAsync(Product(db, [Module("alpha")]), "init", "--topology", "service");
        Assert.Equal(0, again.Exit);
        Assert.DoesNotContain("CHANGED", again.Out);
        Assert.Equal(text, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public async Task A_setting_an_environment_variable_still_decides_is_named_after_init_wrote_the_file()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);
        File.WriteAllText(SettingsFile, Yaml + "state:\n  engine: postgres\n  connection: Host=nowhere;Database=x;Password=env:INIT_PW\n");
        var plain = Product(db, [Module("alpha")]);
        var overridden = plain with
        {
            Locate = args => plain.Locate(args) with { Sources = new StateSources(new StateSource(StateSourceKind.Environment, "TOOL_STATE_ENGINE"), StateSource.Default, StateSource.Default, StateSource.Default) },
        };

        var ran = await RunAsync(overridden, "init", "--topology", "service");

        Assert.Equal(0, ran.Exit);
        Assert.Contains("NOTE    override", ran.Out);
        Assert.Contains("the engine still comes from environment variable TOOL_STATE_ENGINE", ran.Out);
        Assert.Equal(["engine"], ran.Data["overridden_by_environment"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());

        var quiet = await RunAsync(plain, "init", "--topology", "service");
        Assert.DoesNotContain("override", quiet.Out);
    }

    // ---- group

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task Group_print_only_writes_the_settings_prints_the_statements_and_runs_none(string engine)
    {
        var db = _h.Provision(engine);
        var login = db.CreateLogin(_h.OnDispose);
        File.WriteAllText(SettingsFile, Yaml);

        var ran = await RunAsync(Product(db, [Module("alpha")], StateScope.Standalone), "init", "--topology", "group", "--engine", EngineWord(db), "--connection", Referenced(db, AdminLogin(db)), "--login", login, "--group", "g1");

        Assert.Equal(0, ran.Exit);
        Assert.Contains(engine == StateEngineIds.MsSql ? "CREATE SCHEMA [dbdatakit_alpha]" : "CREATE SCHEMA IF NOT EXISTS dbdatakit_alpha;", ran.Out);
        Assert.Contains(login, ran.Out);
        Assert.False((bool)ran.Data["applied"]!);
        Assert.Equal(0, Schemas(db));   // nothing was executed
        var written = StateSettingsWriter.Load(SettingsFile);
        Assert.Equal(EngineWord(db), written.Engine);
        Assert.Contains("env:INIT_PW", written.ConnectionString);
        Assert.StartsWith(Yaml, File.ReadAllText(SettingsFile));
        Assert.Contains("Not usable yet", ran.Out);
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task Group_apply_makes_a_working_database_and_a_second_run_changes_nothing(string engine)
    {
        var db = _h.Provision(engine);
        var login = db.CreateLogin(_h.OnDispose);
        File.WriteAllText(SettingsFile, Yaml);
        var context = Product(db, [Module("alpha")], StateScope.Standalone);
        string[] args = ["init", "--topology", "group", "--engine", EngineWord(db), "--connection", Referenced(db, login), "--login", login, "--admin-connection", AdminReference(db), "--apply"];

        var first = await RunAsync(context, args);

        Assert.True(first.Exit == 0, first.All);
        Assert.True((bool)first.Data["applied"]!);
        Assert.Contains("CHANGED settings", first.Out);
        Assert.Contains("OK      test:rights", first.Out);
        Assert.StartsWith(Yaml, File.ReadAllText(SettingsFile));
        // the settings open the database as the service's login
        var settings = StateSettingsWriter.Load(SettingsFile);
        Assert.Equal(0, (await RunAsync(Product(db, [Module("alpha")], StateScope.Standalone), "test")).Exit);
        _ = StateOpener.Open(settings, "unused.db", [Module("alpha")], StateScope.Standalone, new SecretResolverChain([new EnvSecretProvider(n => n == "INIT_PW" ? Password : null)]));
        var text = File.ReadAllText(SettingsFile);
        var registry = Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM dbdatakit.modules;"));

        var second = await RunAsync(context, args);

        Assert.True(second.Exit == 0, second.All);
        Assert.DoesNotContain("CHANGED", second.Out);
        Assert.Equal(text, File.ReadAllText(SettingsFile));
        Assert.Equal(registry, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM dbdatakit.modules;")));
    }

    // ---- install

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task Install_topology_gives_each_service_a_login_that_cannot_read_the_other_services_module(string engine)
    {
        var db = _h.Provision(engine);
        var loginA = db.CreateLogin(_h.OnDispose);
        var loginB = db.CreateLogin(_h.OnDispose);
        var modulesA = Service("work");
        var modulesB = Service("work");
        var fileA = Path.Combine(_dir.FullName, "a.yaml");
        var fileB = Path.Combine(_dir.FullName, "b.yaml");

        async Task<Ran> Init(string service, string login, string file, IReadOnlyList<StateModule> modules) =>
            await RunAsync(Product(db, modules, service: service, settingsFile: file), "init", "--topology", "install", "--engine", EngineWord(db), "--connection", Referenced(db, login), "--login", login, "--admin-connection", AdminReference(db), "--apply");

        var a = await Init("svca", loginA, fileA, modulesA);
        var b = await Init("svcb", loginB, fileB, modulesB);

        Assert.True(a.Exit == 0, a.All);
        Assert.True(b.Exit == 0, b.All);
        var layoutA = StateLayout.Create(db.Engine, modulesA, new StateScope("main", "svca"), StateSettingsWriter.Load(fileA).Namespaces);
        var layoutB = StateLayout.Create(db.Engine, modulesB, new StateScope("main", "svcb"), StateSettingsWriter.Load(fileB).Namespaces);
        var tableA = layoutA.Table("work", "item");
        var tableB = layoutB.Table("work", "item");
        Assert.NotEqual(tableA, tableB);
        // the module kept per install is one copy per service: the same name, a table of each login's own
        Assert.NotEqual(layoutA.Table("shared", "item"), layoutB.Table("shared", "item"));
        var asA = new Db(db.Engine, db.As(loginA, Password));
        var asB = new Db(db.Engine, db.As(loginB, Password));
        Assert.Equal(0L, Convert.ToInt64(asA.Scalar($"SELECT COUNT(*) FROM {tableA};")));
        Assert.Equal(0L, Convert.ToInt64(asA.Scalar($"SELECT COUNT(*) FROM {layoutA.Table("shared", "item")};")));
        foreach (var (login, other) in new[] { (asA, layoutB), (asB, layoutA) })
            foreach (var table in new[] { other.Table("work", "item"), other.Table("shared", "item") })
            {
                var refused = Assert.ThrowsAny<DbException>(() => login.Scalar($"SELECT COUNT(*) FROM {table};"));
                Assert.True(StateDatabaseErrors.Classify(refused) == StateErrorKind.PermissionDenied, refused.Message);
            }

        // the login of the other service's settings opens its own modules
        Assert.Equal(0, (await RunAsync(Product(db, modulesB, service: "svcb", settingsFile: fileB), "test")).Exit);
    }

    [Fact]
    public void A_login_is_named_after_its_service_and_group()
    {
        Assert.Equal("dbdatakit_gateway_sales", StateLogins.Name("dbdatagateway", "sales", "dbdatagateway"));
        Assert.Equal("dbdatakit_runner_main", StateLogins.Name("dbdatarunner", "main", "dbdatarunner"));
        Assert.Equal("dbdatakit_workspace_main", StateLogins.Name("dbdataworkspace", "main", "dbdataworkspace"));
        Assert.Equal("dbdatakit_console", StateLogins.Name(null, null, "dbdatakit"));
        Assert.Equal("dbdatakit_gateway_north_east", StateLogins.Name("dbdatagateway", "North East", "x"));
        Assert.NotEqual(StateLogins.Name("dbdatagateway", "sales", "g"), StateLogins.Name("dbdatagateway", "finance", "g"));
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task Grants_without_a_login_prints_the_services_own_login_and_the_schemas_of_that_login_only(string engine)
    {
        var db = _h.Provision(engine);
        var file = Path.Combine(_dir.FullName, "g.yaml");
        var gateway = Product(db, Service("work"), new StateScope("sales", "dbdatagateway"), settingsFile: file);
        var runnerFile = Path.Combine(_dir.FullName, "r.yaml");
        var runner = Product(db, Service("work"), new StateScope("main", "dbdatarunner"), settingsFile: runnerFile);
        var login = db.CreateLogin(_h.OnDispose);
        foreach (var (context, settingsFile) in new[] { (gateway, file), (runner, runnerFile) })
            Assert.Equal(0, (await RunAsync(context, "init", "--topology", "install", "--engine", EngineWord(db), "--connection", Referenced(db, login))).Exit);

        var g = await RunAsync(gateway, "grants");
        var r = await RunAsync(runner, "grants");

        Assert.Equal(0, g.Exit);
        Assert.Equal("dbdatakit_gateway_sales", (string)g.Data["login"]!);
        Assert.Equal("dbdatakit_runner_main", (string)r.Data["login"]!);
        var gateStatements = ((System.Text.Json.Nodes.JsonArray)g.Data["statements"]!).Select(n => (string)n!).ToList();
        var runStatements = ((System.Text.Json.Nodes.JsonArray)r.Data["statements"]!).Select(n => (string)n!).ToList();
        Assert.All(gateStatements, s => Assert.DoesNotContain("dbdatakit_runner_main", s));
        Assert.All(runStatements, s => Assert.DoesNotContain("dbdatakit_gateway_sales", s));
        Assert.Contains(gateStatements, s => s.Contains("dbdatakit_gateway_sales_shared") && s.Contains("dbdatakit_gateway_sales"));
        Assert.Contains("-- login dbdatakit_gateway_sales", g.Out);
        Assert.Equal(0, (await RunAsync(gateway, "grants", "--login", "svc_x")).Exit);
        Assert.Contains("TO " + (engine == StateEngineIds.MsSql ? "[svc_x]" : "svc_x"), (await RunAsync(gateway, "grants", "--login", "svc_x")).Out);
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task A_gateways_login_is_refused_on_a_runners_tables_and_the_reverse_when_each_runs_state_grants(string engine)
    {
        var db = _h.Provision(engine);
        var gatewayLogin = db.CreateLogin(_h.OnDispose);
        var runnerLogin = db.CreateLogin(_h.OnDispose);
        var gatewayFile = Path.Combine(_dir.FullName, "gw.yaml");
        var runnerFile = Path.Combine(_dir.FullName, "rn.yaml");
        // both services keep the same modules, one of them per install: in one database that module used to be one set of tables
        var modules = Service("work");
        var gateway = Product(db, modules, new StateScope("main", "dbdatagateway"), settingsFile: gatewayFile);
        var runner = Product(db, modules, new StateScope("main", "dbdatarunner"), settingsFile: runnerFile);
        foreach (var (context, login) in new[] { (gateway, gatewayLogin), (runner, runnerLogin) })
        {
            var ran = await RunAsync(context, "init", "--topology", "install", "--engine", EngineWord(db), "--connection", Referenced(db, login), "--login", login, "--admin-connection", AdminReference(db), "--apply");
            Assert.True(ran.Exit == 0, ran.All);
        }

        var gatewayLayout = StateLayout.Create(db.Engine, modules, new StateScope("main", "dbdatagateway"), StateSettingsWriter.Load(gatewayFile).Namespaces);
        var runnerLayout = StateLayout.Create(db.Engine, modules, new StateScope("main", "dbdatarunner"), StateSettingsWriter.Load(runnerFile).Namespaces);
        var asGateway = new Db(db.Engine, db.As(gatewayLogin, Password));
        var asRunner = new Db(db.Engine, db.As(runnerLogin, Password));
        foreach (var module in new[] { "work", "shared" })
        {
            Assert.Equal(0L, Convert.ToInt64(asGateway.Scalar($"SELECT COUNT(*) FROM {gatewayLayout.Table(module, "item")};")));
            Assert.Equal(0L, Convert.ToInt64(asRunner.Scalar($"SELECT COUNT(*) FROM {runnerLayout.Table(module, "item")};")));
            var gatewayOnRunner = Assert.ThrowsAny<DbException>(() => asGateway.Scalar($"SELECT COUNT(*) FROM {runnerLayout.Table(module, "item")};"));
            var runnerOnGateway = Assert.ThrowsAny<DbException>(() => asRunner.Scalar($"SELECT COUNT(*) FROM {gatewayLayout.Table(module, "item")};"));
            Assert.Equal(StateErrorKind.PermissionDenied, StateDatabaseErrors.Classify(gatewayOnRunner));
            Assert.Equal(StateErrorKind.PermissionDenied, StateDatabaseErrors.Classify(runnerOnGateway));
        }

        // and the registries are two: neither login reads the other's
        var runnerRegistry = runnerLayout.Registry;
        var onRegistry = Assert.ThrowsAny<DbException>(() => asGateway.Scalar($"SELECT COUNT(*) FROM {runnerRegistry};"));
        Assert.Equal(StateErrorKind.PermissionDenied, StateDatabaseErrors.Classify(onRegistry));
        Assert.Equal(0, (await RunAsync(gateway, "test")).Exit);
        Assert.Equal(0, (await RunAsync(runner, "test")).Exit);
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task Init_again_changes_nothing_and_keeps_namespaces_an_operator_wrote(string engine)
    {
        var db = _h.Provision(engine);
        var login = db.CreateLogin(_h.OnDispose);
        var file = Path.Combine(_dir.FullName, "k.yaml");
        var context = Product(db, Service("work"), new StateScope("main", "dbdatarunner"), settingsFile: file);
        string[] args = ["init", "--topology", "install", "--engine", EngineWord(db), "--connection", Referenced(db, login)];

        Assert.Equal(0, (await RunAsync(context, args)).Exit);
        var first = File.ReadAllText(file);
        Assert.Contains("dbdatakit_runner_main_{module}", first);
        Assert.Equal(0, (await RunAsync(context, args)).Exit);
        Assert.Equal(first, File.ReadAllText(file));

        File.WriteAllText(file, "state:\n  engine: " + EngineWord(db) + "\n  connection: \"" + Referenced(db, login) + "\"\n  namespaces:\n    default: { schema: \"mine_{module}\" }\n");
        var kept = await RunAsync(context, args);
        Assert.True(kept.Exit == 0, kept.All + File.ReadAllText(file));
        Assert.Contains("mine_{module}", File.ReadAllText(file));
    }

    // ---- refusals

    [Theory]
    [InlineData("group")]
    [InlineData("install")]
    public async Task A_plain_password_in_the_connection_is_refused_naming_the_fix_and_nothing_is_written(string topology)
    {
        var db = _h.Provision(StateEngineIds.Sqlite);

        var ran = await RunAsync(Product(db, [Module("alpha")]), "init", "--topology", topology, "--engine", "postgres", "--connection", "Host=db;Database=d;Username=svc;Password=hunter2plain");

        Assert.Equal(2, ran.Exit);
        Assert.Contains("--connection env:", ran.Error);
        Assert.Contains("secret:", ran.Error);
        Assert.Contains("tool secret set state-db", ran.Error);
        Assert.DoesNotContain("hunter2plain", ran.All);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_plain_password_in_the_admin_connection_is_refused_too()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);

        var ran = await RunAsync(Product(db, [Module("alpha")]), "init", "--topology", "group", "--engine", "postgres", "--connection", "env:STATE_CONNECTION", "--admin-connection", "Host=db;Username=sa;Password=adminplain1", "--apply");

        Assert.Equal(2, ran.Exit);
        Assert.Contains("--admin-connection", ran.Error);
        Assert.DoesNotContain("adminplain1", ran.All);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Whole_connection_references_are_accepted()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);

        var ran = await RunAsync(Product(db, [Module("alpha")]), "init", "--topology", "group", "--engine", "postgres", "--connection", "env:STATE_CONNECTION");

        Assert.NotEqual(2, ran.Exit);   // the variable is not set, which is a different failure
        Assert.Equal("env:STATE_CONNECTION", StateSettingsWriter.Load(SettingsFile).ConnectionString);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("init --topology everywhere")]
    [InlineData("init --topology group")]
    [InlineData("init --topology group --engine sqlite --connection x")]
    [InlineData("init --topology group --engine postgres")]
    [InlineData("init --topology service --apply")]
    [InlineData("init --topology service --migrate")]
    public async Task A_wrong_use_is_exit_2_and_changes_nothing(string line)
    {
        var db = _h.Provision(StateEngineIds.Sqlite);

        var ran = await RunAsync(Product(db, [Module("alpha")]), line.Split(' '));

        Assert.Equal(2, ran.Exit);
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task A_state_with_data_is_not_pointed_elsewhere_and_the_refusal_names_state_move(string engine)
    {
        var db = _h.Provision(engine);
        var modules = new[] { Module("alpha") };
        _ = db.Open(modules);   // populated: the registry has a row
        File.WriteAllText(SettingsFile, engine == StateEngineIds.Sqlite ? Yaml : $"{Yaml}state:\n  engine: {EngineWord(db)}\n  connection: \"{db.Target}\"\n");
        var before = File.ReadAllText(SettingsFile);
        var context = Product(db, modules);
        var args = engine == StateEngineIds.Sqlite
            ? new[] { "init", "--topology", "group", "--engine", "postgres", "--connection", "Host=other;Database=d;Username=svc;Password=env:INIT_PW" }
            : ["init", "--topology", "service"];

        var ran = await RunAsync(context, args);

        Assert.Equal(StateExit.Populated, ran.Exit);
        Assert.Contains("state move", ran.Error);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
    }
}
