using DbDataConsole.Server;
using DbDataKit.Cli;
using DbDataKit.State;

namespace DbDataConsole.Tests;

/// <summary>Plan 29b C07: the console reads the <c>state:</c> section of <c>settings.yaml</c> in its directory, with the flag first, then the environment, then the file, then SQLite.</summary>
public sealed class ConsoleStateSettingsFileTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("ddk-console-c07").FullName;

    public void Dispose()
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SettingsFile => Path.Combine(directory, "settings.yaml");

    private void Write(string engine, string connection) => new StateSettingsWriter().Write(SettingsFile, new StateSettings(engine, connection));

    private async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(Func<string, string?> environment, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await ConsoleCli.Router(environment).RunAsync([.. args, "--repo", directory], context);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    /// <summary>Opens the state the way the console does and writes one record and reads it back.</summary>
    private static void WriteAndRead(StateDatabase database)
    {
        var users = new IdentityUserStore(database);
        var made = users.CreateUser("Pat", null, UserRole.Admin);
        Assert.Equal(made.Id, users.List().Single().Id);
    }

    [Fact]
    public void Nothing_named_is_sqlite_and_no_settings_file_is_created()
    {
        var resolved = ConsoleState.Resolve(directory, _ => null);

        Assert.True(resolved.Settings.IsSqlite);
        Assert.Equal(StateSources.Defaults, resolved.Sources);
        var database = ConsoleState.Open(directory, resolved.Settings);
        WriteAndRead(database);
        Assert.True(File.Exists(Path.Combine(directory, "state.db")));
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public void A_state_section_with_a_secret_reference_starts_the_console_on_that_engine(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        Write(engine, db.Connection);

        var resolved = ConsoleState.Resolve(directory, db.Environment);

        Assert.Equal(StateSourceKind.File, resolved.Sources.Engine.Kind);
        Assert.Equal(SettingsFile, resolved.Sources.Engine.Name);
        var database = ConsoleState.Open(directory, resolved.Settings, db.Environment);
        {
            Assert.Equal(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, database.Dialect.Engine);
            WriteAndRead(database);
        }

        Assert.False(File.Exists(Path.Combine(directory, "state.db")), "a server engine keeps no SQLite file");
        Assert.True(db.Tables("%") > 0);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task The_environment_beats_the_file_and_the_flag_beats_the_environment_and_state_show_names_each(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        Write(engine, db.Connection);

        // the file alone
        var fromFile = await Run(db.Environment, "state", "show", "--offline");
        Assert.Equal(0, fromFile.Exit);
        Assert.Contains($"engine       {(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres)}", fromFile.Out);
        Assert.Contains($"file {SettingsFile}", fromFile.Out);

        // the environment beats the file: SQLite, from the variable
        string? Env(string name) => name == ConsoleState.EngineVariable ? "sqlite" : db.Environment(name);
        var resolved = ConsoleState.Resolve(directory, Env);
        Assert.True(resolved.Settings.IsSqlite);
        Assert.Equal(new StateSource(StateSourceKind.Environment, ConsoleState.EngineVariable), resolved.Sources.Engine);
        var database = ConsoleState.Open(directory, resolved.Settings, Env);
        Assert.Equal(StateEngineIds.Sqlite, database.Dialect.Engine);
        var fromEnvironment = await Run(Env, "state", "show", "--offline");
        Assert.Contains($"engine       {StateEngineIds.Sqlite}", fromEnvironment.Out);
        Assert.Contains($"environment variable {ConsoleState.EngineVariable}", fromEnvironment.Out);
        var json = (IDictionary<string, object?>)((IDictionary<string, object?>)fromEnvironment.Data["sources"]!)["engine"]!;
        Assert.Equal(("environment", ConsoleState.EngineVariable), (json["kind"], json["name"]));

        // the flag beats the environment: the server engine again, the connection still the file's
        var flagged = ConsoleState.Resolve(directory, Env, engine, null, "--state-engine");
        Assert.False(flagged.Settings.IsSqlite);
        Assert.Equal(new StateSource(StateSourceKind.Flag, "--state-engine"), flagged.Sources.Engine);
        Assert.Equal(StateSourceKind.File, flagged.Sources.Connection.Kind);
        var fromFlag = await Run(Env, "state", "show", "--offline", "--engine", engine);
        Assert.Contains("flag --engine", fromFlag.Out);

        // a connection from the environment beats the file's
        var other = ConsoleState.Resolve(directory, name => name == ConsoleState.ConnectionVariable ? "Host=elsewhere;Database=x" : null);
        Assert.Equal("Host=elsewhere;Database=x", other.Settings.ConnectionString);
        Assert.Equal(new StateSource(StateSourceKind.Environment, ConsoleState.ConnectionVariable), other.Sources.Connection);
    }

    [Fact]
    public async Task A_typo_in_the_state_section_is_refused_with_a_suggestion_naming_the_file()
    {
        File.WriteAllText(SettingsFile, "state:\n  engin: postgres\n");

        var refused = Assert.Throws<InvalidOperationException>(() => ConsoleState.Resolve(directory, _ => null));
        Assert.Contains(SettingsFile, refused.Message);
        Assert.Contains("did you mean 'engine'", refused.Message);

        var shown = await Run(_ => null, "state", "show");
        Assert.Equal(10, shown.Exit);
        Assert.Contains(SettingsFile, shown.Err);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task State_init_for_a_group_writes_the_consoles_own_file_and_the_console_then_uses_it(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);

        var init = await Run(db.Environment, "state", "init", "--topology", "group", "--engine", engine, "--connection", db.Connection, "--admin-connection", db.Connection, "--apply");

        Assert.True(init.Exit == 0, init.Out + init.Err);
        Assert.True(File.Exists(SettingsFile), "init writes settings.yaml in the console directory with no --settings");
        var resolved = ConsoleState.Resolve(directory, db.Environment);
        Assert.Equal(StateSourceKind.File, resolved.Sources.Engine.Kind);
        Assert.False(resolved.Settings.IsSqlite);
        var database = ConsoleState.Open(directory, resolved.Settings, db.Environment);
        WriteAndRead(database);
        Assert.False(File.Exists(Path.Combine(directory, "state.db")));
    }

    [Fact]
    public async Task State_init_for_a_service_writes_nothing_and_the_console_stays_on_its_file()
    {
        var init = await Run(_ => null, "state", "init", "--topology", "service");

        Assert.Equal(0, init.Exit);
        var database = ConsoleState.Open(directory, ConsoleState.Resolve(directory, _ => null).Settings);
        Assert.Equal(StateEngineIds.Sqlite, database.Dialect.Engine);
    }
}
