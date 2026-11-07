using DbDataConsole.Server;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.State.Cli;
using DbDataKit.State.Engines.Snapshots;
using Microsoft.Data.Sqlite;

namespace DbDataConsole.Tests;

/// <summary>
/// The snapshot verbs on the console's command line (plan 29b X04b, C04): <c>state export</c> then <c>state import</c> into a fresh database gives the console the same users, sessions and notifications on each
/// engine; <c>state verify</c> names the table of a changed row; <c>state move</c> switches the console from SQLite to a server engine and writes <c>settings.yaml</c> only after the copy is verified.
/// </summary>
public sealed class ConsoleStateSnapshotVerbTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ddk-console-snap").FullName;
    private readonly StateVerbDatabases databases = new();

    public void Dispose()
    {
        databases.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(root, name);

    private static async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(Func<string, string?>? environment, Func<MoveOptions, MoveOptions>? move, TextReader? input, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var context = new CliContext(o, e, input ?? TextReader.Null);
        var exit = await ConsoleCli.Router(environment, move).RunAsync(args, context);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    private static Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(Func<string, string?>? environment, params string[] args) => Run(environment, null, null, args);

    /// <summary>A console state with two people, a session and three notifications, on the engine and directory given.</summary>
    private static (string Admin, string Session) Seed(string directory, StateSettings? settings, Func<string, string?>? environment)
    {
        var database = ConsoleState.Open(directory, settings, environment);
        var users = new IdentityUserStore(database);
        var admin = users.CreateUser("Pat Admin", "pat@example.test", UserRole.Admin, emailVerified: true);
        users.CreateUser("Sam Viewer", "sam@example.test", UserRole.Viewer);
        var session = new IdentitySessionStore(database).Create(admin.Id);
        var feed = new NotificationFeedStore(database, ConsoleState.ServiceId);
        for (var i = 1; i <= 3; i++) feed.Raise("info", $"notification {i}");
        return (admin.Id, session.Id);
    }

    private static string UsersOf(StateDatabase database) =>
        string.Join("|", new IdentityUserStore(database).List().OrderBy(u => u.Id, StringComparer.Ordinal).Select(u => $"{u.Id}/{u.DisplayName}/{u.Email}/{u.Role}/{u.Enabled}"));

    [Theory]
    [MemberData(nameof(StateVerbDatabases.Engines), MemberType = typeof(StateVerbDatabases))]
    public async Task Export_then_import_into_a_fresh_database_starts_the_console_with_the_same_users_sessions_and_notifications(string engine)
    {
        var sourceConnection = engine == "sqlite" ? null : databases.Provision(engine);
        var targetConnection = engine == "sqlite" ? null : databases.Provision(engine);
        var sourceEnvironment = databases.EnvFor(engine == "sqlite" ? null : engine, sourceConnection);
        var targetEnvironment = databases.EnvFor(engine == "sqlite" ? null : engine, targetConnection);
        var (admin, session) = Seed(Dir("source"), engine == "sqlite" ? null : new StateSettings(engine, sourceConnection), sourceEnvironment);
        var expectedUsers = UsersOf(ConsoleState.Open(Dir("source"), engine == "sqlite" ? null : new StateSettings(engine, sourceConnection), sourceEnvironment));

        var export = await Run(sourceEnvironment, "state", "export", "--to", Dir("snapshot"), "--repo", Dir("source"), "--include-secrets-check");
        Assert.True(export.Exit == 0, export.Out + export.Err);
        Assert.Contains("identity.", export.Out);   // a progress line per table
        Assert.Contains("Secrets check", export.Out);
        Assert.True(File.Exists(Path.Combine(Dir("snapshot"), "manifest.json")));
        Assert.Equal(Dir("snapshot"), export.Data["directory"]);
        Assert.True(Convert.ToInt64(export.Data["rows"]) >= 6);

        var import = await Run(targetEnvironment, "state", "import", "--from", Dir("snapshot"), "--repo", Dir("target"));
        Assert.True(import.Exit == 0, import.Out + import.Err);
        Assert.Contains("Start the console as usual", import.Out);

        // the console opens the imported database and finds what the old one held
        var settings = engine == "sqlite" ? null : new StateSettings(engine, targetConnection);
        var database = ConsoleState.Open(Dir("target"), settings, targetEnvironment);
        Assert.Equal(expectedUsers, UsersOf(database));
        Assert.NotNull(new IdentitySessionStore(database).Resolve(session, new IdentityUserStore(database)));
        Assert.Equal(3, new NotificationFeedStore(database, ConsoleState.ServiceId).List().Count);
        Assert.NotNull(new IdentityUserStore(database).Get(admin));

        // every comparison says equal: the snapshot alone, against the target, and against the source database
        Assert.Equal(0, (await Run(null, "state", "verify", "--snapshot", Dir("snapshot"))).Exit);
        var against = await Run(targetEnvironment, "state", "verify", "--snapshot", Dir("snapshot"), "--db", "--rehash-payloads", "--repo", Dir("target"));
        Assert.True(against.Exit == 0, against.Out + against.Err);
        Assert.Equal(true, against.Data["equal"]);
        var other = engine == "sqlite"
            ? await Run(targetEnvironment, "state", "verify", "--other-engine", "sqlite", "--other-sqlite", Path.Combine(Dir("source"), "state.db"), "--repo", Dir("target"))
            : await Run(targetEnvironment, "state", "verify", "--other-engine", engine, "--other-connection", sourceConnection!, "--repo", Dir("target"));
        Assert.True(other.Exit == 0, other.Out + other.Err);

        // secrets never appear: not in any output, not in the snapshot
        var everything = string.Join("\n", export.Out, export.Err, import.Out, import.Err, against.Out, other.Out, StateVerbDatabases.EverythingUnder(Dir("snapshot")));
        if (databases.Password.Length > 0) Assert.DoesNotContain(databases.Password, everything);
    }

    [Fact]
    public async Task A_changed_row_is_found_and_its_table_is_named_and_a_damaged_snapshot_names_its_file()
    {
        Seed(Dir("source"), null, null);
        Assert.Equal(0, (await Run(null, "state", "export", "--to", Dir("snapshot"), "--repo", Dir("source"))).Exit);
        Assert.Equal(0, (await Run(null, "state", "verify", "--snapshot", Dir("snapshot"), "--db", "--repo", Dir("source"))).Exit);

        var database = ConsoleState.Open(Dir("source"));
        var users = new IdentityUserStore(database);
        users.SetEnabled(users.List().First(u => u.DisplayName == "Sam Viewer").Id, false);
        SqliteConnection.ClearAllPools();

        var changed = await Run(null, "state", "verify", "--snapshot", Dir("snapshot"), "--db", "--repo", Dir("source"));
        Assert.Equal(StateExit.VerifyDiffers, changed.Exit);
        Assert.Contains("identity", changed.Out);
        Assert.Contains("user", changed.Out);
        Assert.Equal(false, changed.Data["equal"]);
        Assert.NotEmpty((IEnumerable<string>)changed.Data["differences"]!);

        // damage a table file: the self-check names it, with no database and no settings
        var table = Directory.EnumerateFiles(Dir("snapshot"), "*", SearchOption.AllDirectories).First(f => !f.EndsWith("manifest.json", StringComparison.Ordinal));
        File.AppendAllText(table, "damage");
        var damaged = await Run(null, "state", "verify", "--snapshot", Dir("snapshot"));
        Assert.Equal(StateExit.VerifyDiffers, damaged.Exit);
        Assert.Contains(Path.GetFileName(table), damaged.Out);

        Assert.Equal(CliExit.Usage, (await Run(null, "state", "verify")).Exit);
        Assert.Equal(CliExit.Usage, (await Run(null, "state", "verify", "--db")).Exit);
        Assert.Equal(StateExit.SnapshotInvalid, (await Run(null, "state", "verify", "--snapshot", Dir("nothing-here"))).Exit);
    }

    [Fact]
    public async Task An_export_is_refused_while_another_process_holds_the_file_and_into_a_directory_that_is_not_empty()
    {
        Seed(Dir("source"), null, null);
        SqliteConnection.ClearAllPools();
        using (var holder = new SqliteConnection($"Data Source={Path.Combine(Dir("source"), "state.db")};Pooling=False"))
        {
            holder.Open();
            using var begin = holder.CreateCommand();
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
            var held = await Run(null, "state", "export", "--to", Dir("snapshot"), "--repo", Dir("source"));
            Assert.True(held.Exit is StateExit.SnapshotRefused or StateExit.Unreachable, held.Out + held.Err);
            Assert.Contains("lock", held.Err, StringComparison.OrdinalIgnoreCase);
        }

        Directory.CreateDirectory(Dir("full"));
        File.WriteAllText(Path.Combine(Dir("full"), "something.txt"), "x");
        var full = await Run(null, "state", "export", "--to", Dir("full"), "--repo", Dir("source"));
        Assert.Equal(StateExit.SnapshotRefused, full.Exit);
        Assert.Contains("something.txt", File.ReadAllText(Path.Combine(Dir("full"), "something.txt")) + "something.txt");
        Assert.Equal(CliExit.Usage, (await Run(null, "state", "export", "--repo", Dir("source"))).Exit);
    }

    [Fact]
    public async Task An_import_into_a_database_that_has_modules_is_refused_naming_one_and_changes_nothing()
    {
        Seed(Dir("source"), null, null);
        Assert.Equal(0, (await Run(null, "state", "export", "--to", Dir("snapshot"), "--repo", Dir("source"))).Exit);
        var before = new FileInfo(Path.Combine(Dir("source"), "state.db")).Length;
        SqliteConnection.ClearAllPools();

        var refused = await Run(null, "state", "import", "--from", Dir("snapshot"), "--repo", Dir("source"));

        Assert.Equal(StateExit.TargetNotEmpty, refused.Exit);
        Assert.Contains("identity", refused.Err);
        Assert.Equal(before, new FileInfo(Path.Combine(Dir("source"), "state.db")).Length);
        Assert.Equal(CliExit.Usage, (await Run(null, "state", "import", "--from", Dir("snapshot"), "--migrate-after", "maybe", "--repo", Dir("fresh"))).Exit);
    }

    [Theory]
    [MemberData(nameof(StateVerbDatabases.ServerEngines), MemberType = typeof(StateVerbDatabases))]
    public async Task Move_from_sqlite_to_a_server_engine_writes_the_settings_and_the_restarted_console_shows_the_same_data(string engine)
    {
        var connection = databases.Provision(engine);
        var (_, session) = Seed(Dir("console"), null, null);
        var expectedUsers = UsersOf(ConsoleState.Open(Dir("console")));
        SqliteConnection.ClearAllPools();
        var environment = databases.EnvFor(null, null);

        var plan = await Run(environment, "state", "move", "--to-engine", engine, "--to-connection", connection, "--repo", Dir("console"));
        Assert.Equal(StateExit.NotConfirmed, plan.Exit);   // the plan was printed, nothing was confirmed
        Assert.Contains("Move the state of the console", plan.Out);
        Assert.False(File.Exists(Path.Combine(Dir("console"), "settings.yaml")));

        var moved = await Run(environment, "state", "move", "--to-engine", engine, "--to-connection", connection, "--yes", "--repo", Dir("console"));
        Assert.True(moved.Exit == 0, moved.Out + moved.Err);
        Assert.Contains("Restart the service", moved.Out);
        Assert.Contains("Way back", moved.Out);
        Assert.Equal(true, moved.Data["verified_against_snapshot"]);
        Assert.Equal(true, moved.Data["verified_against_source"]);

        // the new settings name the target, with the password still a reference; the old database is as it was
        var settingsText = File.ReadAllText(Path.Combine(Dir("console"), "settings.yaml"));
        Assert.Contains($"engine: {engine}", settingsText);
        Assert.Contains($"Password=env:{StateVerbDatabases.PasswordVariable}", settingsText);
        Assert.DoesNotContain(databases.Password, settingsText);
        Assert.Equal(expectedUsers, UsersOf(ConsoleState.Open(Dir("console"))));
        SqliteConnection.ClearAllPools();

        // restart: the settings file is what the console now opens, and it shows the same people, session and notifications
        var read = ConsoleState.SettingsFrom(environment, null, null, Dir("console"))!;
        Assert.Equal(engine, read.Engine);
        var database = ConsoleState.Open(Dir("console"), read, environment);
        Assert.Equal(expectedUsers, UsersOf(database));
        Assert.NotNull(new IdentitySessionStore(database).Resolve(session, new IdentityUserStore(database)));
        Assert.Equal(3, new NotificationFeedStore(database, ConsoleState.ServiceId).List().Count);
        var shown = await Run(environment, "state", "show", "--repo", Dir("console"));
        Assert.Equal(0, shown.Exit);
        Assert.Equal(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, shown.Data["engine"]);

        Assert.False(Directory.EnumerateFiles(root, "state-move-*", SearchOption.AllDirectories).Any(), "the temporary snapshot is removed");
        Assert.DoesNotContain(databases.Password, string.Join("\n", plan.Out, plan.Err, moved.Out, moved.Err, shown.Out));
    }

    [Fact]
    public async Task A_move_confirmed_at_the_prompt_goes_ahead_and_a_failure_after_the_import_leaves_the_settings_unchanged()
    {
        var connection = databases.Provision("postgres");
        Seed(Dir("console"), null, null);
        SqliteConnection.ClearAllPools();
        var environment = databases.EnvFor(null, null);
        var kept = Dir("kept-snapshot");

        // a failure injected after the import: the copy no longer equals the source, so the move stops before the settings
        var failing = await Run(environment, options => options with
        {
            SnapshotDirectory = kept,
            AfterImport = (target, _) =>
            {
                var users = new IdentityUserStore(target);
                users.SetEnabled(users.List().First().Id, false);
                return Task.CompletedTask;
            },
        }, null, "state", "move", "--to-engine", "postgres", "--to-connection", connection, "--yes", "--repo", Dir("console"));
        Assert.Equal(StateExit.VerifyDiffers, failing.Exit);
        Assert.Contains("was not changed", failing.Err);
        Assert.False(File.Exists(Path.Combine(Dir("console"), "settings.yaml")), "the settings were written although the verification failed");
        Assert.Empty(Directory.EnumerateFiles(Dir("console"), "*.before-state-move"));
        Assert.True(File.Exists(Path.Combine(kept, "manifest.json")), "a failed move keeps its snapshot");

        // the target holds the injected copy; a fresh one, with the answer typed at the prompt
        var retry = databases.Provision("postgres");
        var answered = await Run(environment, null, new StringReader("yes\n"), "state", "move", "--to-engine", "postgres", "--to-connection", retry, "--keep-snapshot", "--repo", Dir("console"));
        Assert.True(answered.Exit == 0, answered.Out + answered.Err);
        Assert.Contains("Type yes", answered.Out);
        Assert.Equal("postgres", StateSettingsWriter.Load(Path.Combine(Dir("console"), "settings.yaml")).Engine);
        Assert.NotNull(answered.Data["snapshot_directory"]);   // --keep-snapshot
        Assert.True(Directory.Exists((string)answered.Data["snapshot_directory"]!));
    }

    [Fact]
    public async Task A_move_refuses_a_password_written_out_a_missing_target_and_the_same_database_and_changes_nothing()
    {
        Seed(Dir("console"), null, null);
        SqliteConnection.ClearAllPools();

        var literal = await Run(null, "state", "move", "--to-engine", "postgres", "--to-connection", "Host=h;Database=d;Username=u;Password=hunter2-plain", "--yes", "--repo", Dir("console"));
        Assert.Equal(CliExit.Usage, literal.Exit);
        Assert.Contains("secret reference", literal.Err);
        Assert.DoesNotContain("hunter2-plain", literal.Out + literal.Err);

        Assert.Equal(CliExit.Usage, (await Run(null, "state", "move", "--yes", "--repo", Dir("console"))).Exit);
        Assert.Equal(CliExit.Usage, (await Run(null, "state", "move", "--to-engine", "postgres", "--yes", "--repo", Dir("console"))).Exit);   // a server engine needs its connection
        Assert.Equal(CliExit.Usage, (await Run(null, "state", "move", "--to-engine", "sqlite", "--yes", "--repo", Dir("console"))).Exit);   // the same SQLite file
        Assert.False(File.Exists(Path.Combine(Dir("console"), "settings.yaml")));
    }
}
