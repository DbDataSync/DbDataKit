using System.Data.Common;
using DbDataKit.Secrets;
using Microsoft.Data.SqlClient;
using Npgsql;
using static DbDataKit.State.Cli.Tests.StateCliHarness;

namespace DbDataKit.State.Cli.Tests;

/// <summary>Plan 29b C01: <c>state show</c> and <c>state test</c> on SQLite, PostgreSQL and SQL Server.</summary>
public sealed class StateVerbsTests : IDisposable
{
    private readonly StateCliHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- show

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task Show_on_an_empty_database_says_no_modules_are_registered_and_creates_nothing(string engine)
    {
        var db = _h.Provision(engine);
        var context = Context(db, [Module("alpha")]);

        var shown = await RunAsync(context, "show");

        Assert.Equal(0, shown.Exit);
        Assert.Contains("alpha", shown.Out);
        Assert.Contains(engine == StateEngineIds.Sqlite ? "does not exist" : "no modules registered", shown.Out);
        Assert.Equal(2, (int)shown.Data["pending_migrations"]!);
        if (engine == StateEngineIds.Sqlite)
        {
            Assert.False(File.Exists(db.Target));
            Assert.False(Directory.Exists(Path.GetDirectoryName(db.Target)), "show made the folder");
        }
        else
        {
            Assert.Equal(0, Convert.ToInt32(db.Scalar(engine == StateEngineIds.MsSql ? "SELECT COUNT(*) FROM sys.schemas WHERE name LIKE 'dbdatakit%';" : "SELECT COUNT(*) FROM pg_namespace WHERE nspname LIKE 'dbdatakit%';")));
        }
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task Show_lists_each_module_with_its_registered_version_its_place_and_the_registry(string engine)
    {
        var db = _h.Provision(engine);
        db.Open([Module("alpha"), Module("beta", 1)]);

        var shown = await RunAsync(Context(db, [Module("alpha"), Module("beta", 1)]), "show");

        Assert.Equal(0, shown.Exit);
        Assert.Matches(@"alpha\s+.*binary v2\s+registered v2\s+current", shown.Out);
        Assert.Matches(@"beta\s+.*binary v1\s+registered v1\s+current", shown.Out);
        Assert.Contains(engine == StateEngineIds.Sqlite ? "dbdatakit_modules" : "dbdatakit.modules", shown.Out);
        Assert.Equal(0, (int)shown.Data["pending_migrations"]!);
        var modules = shown.Data["modules"]!.AsArray();
        Assert.Equal(2, modules.Count);
        Assert.Equal(2, (int)modules[0]!["registered_version"]!);
        Assert.Equal(engine, (string)shown.Data["engine"]!);
        if (engine == StateEngineIds.Sqlite)
        {
            Assert.Contains(db.Target, shown.Out);
            Assert.True((long)shown.Data["file_size"]! > 0);
        }
        else
        {
            Assert.Equal("dbdatakit_alpha", (string)modules[0]!["schema"]!);
            Assert.NotNull(shown.Data["engine_version"]);
            Assert.NotNull(shown.Data["login"]);
        }
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task Pending_migrations_are_reported_and_show_does_not_apply_them(string engine)
    {
        var db = _h.Provision(engine);
        db.Open([Module("alpha", 1)]);   // the database as an older binary made it

        var shown = await RunAsync(Context(db, [Module("alpha")]), "show");   // this binary has two migrations

        Assert.Equal(0, shown.Exit);
        Assert.Contains("binary v2", shown.Out);
        Assert.Contains("registered v1", shown.Out);
        Assert.Contains("1 migration(s) pending", shown.Out);
        Assert.Equal(1, (int)shown.Data["pending_migrations"]!);

        var layout = StateLayout.Create(engine, [Module("alpha")], StateScope.Install);
        Assert.Equal(1, Convert.ToInt32(db.Scalar($"SELECT version FROM {layout.Registry} WHERE module = 'alpha';")));
        Assert.False(TableExists(db, layout.Table("alpha", "extra")), "show applied the migration");

        var tested = await RunAsync(Context(db, [Module("alpha")]), "test");
        Assert.Equal(0, tested.Exit);   // the admin login may apply it
        Assert.Contains("1 migration(s) pending", tested.Out);
    }

    private static bool TableExists(StateCliHarness.Db db, string table)
    {
        try { db.Scalar($"SELECT COUNT(*) FROM {table};"); return true; }
        catch (Exception ex) when (ex is DbException) { return false; }
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task A_password_is_never_printed_and_a_secret_reference_is_shown_as_the_reference(string engine)
    {
        var db = _h.Provision(engine);
        var login = db.CreateLogin(_h.OnDispose);
        db.Open([Module("alpha")]);
        db.Grant([Module("alpha")], login, GrantLevel.Runtime);

        // the password written in the settings
        var literal = Context(db, [Module("alpha")], db.As(login, Password));
        foreach (var verb in new[] { "show", "test" })
        {
            var ran = await RunAsync(literal, verb);
            Assert.Equal(0, ran.Exit);
            Assert.DoesNotContain(Password, ran.All);
            Assert.Contains("***", ran.All);
        }

        // the password a reference: shown as the reference, resolved only to connect
        var reference = engine == StateEngineIds.MsSql ? new SqlConnectionStringBuilder(db.Target) { UserID = login, Password = "secret:state-db" }.ConnectionString : new NpgsqlConnectionStringBuilder(db.Target) { Username = login, Password = "secret:state-db" }.ConnectionString;
        var resolver = new SecretResolverChain([new EnvSecretProvider(name => name == "DBDATA_SECRET_STATE_DB" ? Password : null)]);
        var viaReference = Context(db, [Module("alpha")], reference, secrets: () => resolver);
        foreach (var verb in new[] { "show", "test" })
        {
            var ran = await RunAsync(viaReference, verb);
            Assert.Equal(0, ran.Exit);
            Assert.DoesNotContain(Password, ran.All);
            Assert.Contains("Password=secret:state-db", ran.Out);
        }
    }

    [Fact]
    public async Task A_product_with_no_module_list_says_so()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);
        db.Open([Module("alpha")]);
        var context = Context(db, null);

        var shown = await RunAsync(context, "show");
        Assert.Equal(0, shown.Exit);
        Assert.Contains("no module list yet", shown.Out);

        var tested = await RunAsync(context, "test");
        Assert.Equal(0, tested.Exit);
        Assert.Contains(tested.Json["data"]!["steps"]!.AsArray(), s => (string)s!["name"]! == "migrations" && (string)s["status"]! == "skipped");
    }

    // ---- test: the failures

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task Test_passes_on_a_working_database_and_lists_its_steps_in_order(string engine)
    {
        var db = _h.Provision(engine);
        db.Open([Module("alpha")]);

        var ran = await RunAsync(Context(db, [Module("alpha")]), "test");

        Assert.Equal(0, ran.Exit);
        Assert.True((bool)ran.Data["ok"]!);
        Assert.Equal(["settings", "connection", "registry", "rights", "engine", "clash", "migrations"], ran.Data["steps"]!.AsArray().Select(s => (string)s!["name"]!).ToArray());
        Assert.DoesNotContain("FAILED", ran.Out);
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task A_bad_password_is_refused_with_its_own_exit_code_and_the_fix(string engine)
    {
        var db = _h.Provision(engine);
        var login = db.CreateLogin(_h.OnDispose);

        var ran = await RunAsync(Context(db, [Module("alpha")], db.As(login, "Wrong_Pw_" + Password)), "test");

        Assert.Equal(StateExit.LoginRefused, ran.Exit);
        Assert.Contains($"The login '{login}' was refused", ran.Error);
        Assert.Contains("correct the password", ran.Error);
        Assert.DoesNotContain(Password, ran.All);
        Assert.Single(ran.Error.Trim().Split('\n'));
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task A_database_that_is_not_there_and_a_server_that_cannot_be_reached_are_told_apart_from_a_bad_login(string engine)
    {
        var db = _h.Provision(engine);

        var missing = await RunAsync(Context(db, [Module("alpha")], db.WithDatabase("nodb_" + Guid.NewGuid().ToString("N"))), "test");
        Assert.Equal(StateExit.Unreachable, missing.Exit);
        Assert.Contains("does not exist", missing.Error);

        var unreachable = engine == StateEngineIds.MsSql
            ? "Server=127.0.0.1,1;User Id=sa;Password=x;Connect Timeout=3;TrustServerCertificate=True;Encrypt=False"
            : "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=3";
        var down = await RunAsync(Context(db, [Module("alpha")], unreachable), "test");
        Assert.Equal(StateExit.Unreachable, down.Exit);
        Assert.Contains("Cannot reach", down.Error);
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task A_login_with_no_rights_cannot_read_the_registry_and_one_with_too_few_lacks_named_rights(string engine)
    {
        var db = _h.Provision(engine);
        db.Open([Module("alpha")]);
        var login = db.CreateLogin(_h.OnDispose);
        var context = Context(db, [Module("alpha")], db.As(login, Password));

        var none = await RunAsync(context, "test");
        Assert.Equal(StateExit.Registry, none.Exit);
        Assert.Contains($"'{login}'", none.Error);
        Assert.Contains("state grants --login " + login, none.Error);
        Assert.DoesNotContain(Password, none.All);

        // the registry, and nothing else: it reads, and then misses what the module's tables need
        db.Grant([], login, GrantLevel.Runtime);
        var few = await RunAsync(context, "test");
        Assert.Equal(StateExit.Rights, few.Exit);
        Assert.Contains("lacks", few.Error);
        Assert.Contains("state grants --login " + login, few.Error);

        // what the grants verb prints for the layout is enough
        db.Grant([Module("alpha")], login, GrantLevel.Runtime);
        var enough = await RunAsync(context, "test");
        Assert.Equal(0, enough.Exit);
    }

    [Theory]
    [MemberData(nameof(ServerEngines), MemberType = typeof(StateCliHarness))]
    public async Task Pending_migrations_a_login_may_not_apply_are_a_failure_of_their_own(string engine)
    {
        var db = _h.Provision(engine);
        db.Open([Module("alpha", 1)]);
        var login = db.CreateLogin(_h.OnDispose);
        db.Grant([Module("alpha", 1)], login, GrantLevel.Runtime);
        var context = Context(db, [Module("alpha")], db.As(login, Password));

        var blocked = await RunAsync(context, "test");
        Assert.Equal(StateExit.PendingBlocked, blocked.Exit);
        Assert.Contains("1 migration(s) are pending", blocked.Error);
        Assert.Contains("--migrate", blocked.Error);

        db.Grant([Module("alpha")], login, GrantLevel.Migrate);
        Assert.Equal(0, (await RunAsync(context, "test")).Exit);
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task An_engine_older_than_the_oldest_supported_is_refused_naming_the_oldest(string engine)
    {
        var db = _h.Provision(engine);
        db.Open([Module("alpha")]);
        var old = engine switch { StateEngineIds.MsSql => "15.0.4000.1", StateEngineIds.Postgres => "15.4", _ => "3.34.1" };

        var ran = await RunAsync(Context(db, [Module("alpha")], version: _ => old), "test");

        Assert.Equal(StateExit.EngineVersion, ran.Exit);
        Assert.Contains(engine switch { StateEngineIds.MsSql => "SQL Server 2022", StateEngineIds.Postgres => "PostgreSQL 16", _ => "3.35" }, ran.Error);
        Assert.Contains(ran.Data["steps"]!.AsArray(), s => (string)s!["name"]! == "engine" && (string)s["status"]! == "failed");
    }

    [Theory]
    [InlineData(StateEngineIds.MsSql, "16.0.1000", true, true)]
    [InlineData(StateEngineIds.MsSql, "17.0.1000.7", true, true)]
    [InlineData(StateEngineIds.MsSql, "18.0.1", true, false)]
    [InlineData(StateEngineIds.MsSql, "15.0.2000", false, true)]
    [InlineData(StateEngineIds.Postgres, "16.4", true, true)]
    [InlineData(StateEngineIds.Postgres, "17.2", true, true)]
    [InlineData(StateEngineIds.Postgres, "18.0", true, false)]
    [InlineData(StateEngineIds.Postgres, "15.9", false, true)]
    [InlineData(StateEngineIds.Sqlite, "3.35.0", true, true)]
    [InlineData(StateEngineIds.Sqlite, "3.45.1", true, true)]
    [InlineData(StateEngineIds.Sqlite, "3.34.1", false, true)]
    public void The_support_table_knows_the_supported_versions(string engine, string version, bool supported, bool tested)
    {
        var check = EngineSupport.Check(engine, version);
        Assert.Equal(supported, check.Supported);
        Assert.Equal(tested, check.Tested);
        Assert.Contains(version, check.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task A_table_of_the_layout_that_something_else_made_is_a_schema_clash(string engine)
    {
        var db = _h.Provision(engine);
        var layout = StateLayout.Create(engine, [Module("alpha")], StateScope.Install);
        if (engine == StateEngineIds.Sqlite) Directory.CreateDirectory(Path.GetDirectoryName(db.Target)!);
        else db.Scalar(StateDialect.For(engine).CreateSchema(layout.Namespace("alpha").Schema!)!);
        db.Scalar($"CREATE TABLE {layout.Table("alpha", "item")} (somebody_elses_column {(engine == StateEngineIds.Sqlite ? "TEXT" : "int")});");

        var ran = await RunAsync(Context(db, [Module("alpha")]), "test");

        Assert.Equal(StateExit.SchemaClash, ran.Exit);
        Assert.Contains(layout.Table("alpha", "item"), ran.Error);
        Assert.Contains("state.namespaces.alpha", ran.Error);
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task A_database_the_service_would_refuse_is_said_so_with_what_to_do(string engine)
    {
        var db = _h.Provision(engine);
        db.Open([Module("alpha")]);

        // the settings moved the namespace
        var moved = new StateNamespaceSettings { Modules = new Dictionary<string, NamespaceSetting> { ["alpha"] = new(Schema: engine == StateEngineIds.Sqlite ? null : "elsewhere", Prefix: "other_") } };
        var ran = await RunAsync(Context(db, [Module("alpha")], namespaces: moved), "test");
        Assert.Equal(StateExit.Refused, ran.Exit);
        Assert.Contains("put the settings back", ran.Error);

        // a module newer than this binary
        var older = await RunAsync(Context(db, [Module("alpha", 1)]), "test");
        Assert.Equal(StateExit.Refused, older.Exit);
        Assert.Contains("upgrade this service", older.Error);
    }

    [Fact]
    public async Task A_database_of_the_old_structure_is_refused_naming_it()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);
        _ = new StateDatabase(db.Target, new StateSchema(["CREATE TABLE legacy_thing (id TEXT PRIMARY KEY);"]));

        var ran = await RunAsync(Context(db, [Module("alpha")]), "test");

        Assert.Equal(StateExit.Refused, ran.Exit);
        Assert.Contains("old structure", ran.Error);
    }

    [Fact]
    public async Task Settings_that_cannot_be_used_are_exit_10_naming_the_setting()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);

        var unknown = new StateVerbContext("tool", "the test tool", _ => new StateTarget(new StateSettings("oracle"), db.Target), [Module("alpha")], StateScope.Install, ["repo"], "[--repo <directory>]");
        var ran = await RunAsync(unknown, "test");
        Assert.Equal(StateExit.Settings, ran.Exit);
        Assert.Contains("sqlite, mssql or postgres", ran.Error);

        var noConnection = new StateVerbContext("tool", "the test tool", _ => new StateTarget(new StateSettings("postgres"), db.Target), [Module("alpha")], StateScope.Install, ["repo"], "[--repo <directory>]");
        var none = await RunAsync(noConnection, "show");
        Assert.Equal(StateExit.Settings, none.Exit);
        Assert.Contains("state.connection", none.Error);

        var resolver = new SecretResolverChain([new EnvSecretProvider(_ => null)]);
        var missingSecret = new StateVerbContext("tool", "the test tool", _ => new StateTarget(new StateSettings("postgres", "Host=127.0.0.1;Port=1;Username=u;Password=secret:nobody-has-this;Database=d"), db.Target, () => resolver), [Module("alpha")], StateScope.Install, ["repo"], "[--repo <directory>]");
        var unresolved = await RunAsync(missingSecret, "test");
        Assert.Equal(StateExit.Settings, unresolved.Exit);
        Assert.Contains("nobody-has-this", unresolved.Error);
    }

    [Fact]
    public async Task Wrong_use_is_exit_2()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);
        var context = Context(db, [Module("alpha")]);
        Assert.Equal(2, (await RunAsync(context, "show", "--bogus")).Exit);
        Assert.Equal(2, (await RunAsync(context, "test", "extra")).Exit);
        Assert.Equal(2, (await RunAsync(context, "grants")).Exit);
    }

    [Fact]
    public void The_connection_mask_hides_literals_and_keeps_references()
    {
        Assert.Equal("Host=db;Username=svc;Password=***;Database=d", ConnectionStringReferences.Mask("Host=db;Username=svc;Password=hunter2;Database=d"));
        Assert.Equal("Server=s;User Id=u;Pwd=***;", ConnectionStringReferences.Mask("Server=s;User Id=u;Pwd={sec;ret};"));
        Assert.Equal("Host=db;Password=secret:state-db", ConnectionStringReferences.Mask("Host=db;Password=secret:state-db"));
        Assert.Equal("Host=db;Password=env:DB_PW", ConnectionStringReferences.Mask("Host=db;Password=env:DB_PW"));
        Assert.Equal("secret:whole", ConnectionStringReferences.Mask("secret:whole"));
        Assert.Equal("postgres://svc:***@db/x", ConnectionStringReferences.Mask("postgres://svc:hunter2@db/x"));
        Assert.Equal("", ConnectionStringReferences.Mask(null));
    }

    [Fact]
    public async Task Grants_prints_the_statements_for_the_products_modules()
    {
        var db = _h.Provision(StateEngineIds.Sqlite);
        var context = new StateVerbContext("tool", "the test tool", args => new StateTarget(new StateSettings(args.Get("engine")), db.Target), [Module("alpha")], StateScope.Install, ["engine"], "[--engine mssql|postgres]");

        var ran = await RunAsync(context, "grants", "--login", "svc", "--engine", "postgres");

        Assert.Equal(0, ran.Exit);
        Assert.Contains("GRANT USAGE ON SCHEMA dbdatakit_alpha TO svc;", ran.Out);
        Assert.Equal(2, (await RunAsync(context, "grants", "--login", "svc")).Exit);   // SQLite: nothing to grant
    }
}
