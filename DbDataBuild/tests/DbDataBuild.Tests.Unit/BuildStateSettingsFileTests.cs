using DbDataBuild.Cli.Server;
using DbDataKit.Scheduling;
using DbDataKit.State;
using Xunit;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 29b C07: the Build server reads the <c>state:</c> section of its <c>server.yaml</c>, with the flag first, then the environment, then the file, then SQLite. The tests on SQL Server and PostgreSQL do nothing
/// when <c>DBDATA_TEST_SQLSERVER</c> or <c>DBDATA_TEST_POSTGRES</c> is not set (xunit 2 cannot skip at run time).
/// </summary>
public sealed class BuildStateSettingsFileTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ddb-c07").FullName;

    public void Dispose()
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string SettingsFile => Path.Combine(dir, "server.yaml");

    private void Write(string engine, string connection) => new StateSettingsWriter().Write(SettingsFile, new StateSettings(engine, connection));

    private async Task<(int Exit, string Out, string Err)> Run(Func<string, string?> environment, params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await ServerCli.RunAsync([.. args, "--repo", dir], o, e, TextReader.Null, environment);
        return (exit, o.ToString(), e.ToString());
    }

    /// <summary>One record written and read back, through the scheduler's own store.</summary>
    private static void WriteAndRead(StateDatabase database)
    {
        var pauses = new ModuleJobPauseStore(database, BuildSchedulerSchema.Service);
        Assert.False(pauses.IsPaused("sales", "nightly"));
        pauses.Set("sales", "nightly", "pat", DateTimeOffset.UtcNow);
        Assert.True(pauses.IsPaused("sales", "nightly"));
    }

    private static string Id(string engine) => engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres;

    [Fact]
    public void Nothing_named_is_sqlite_and_no_settings_file_is_created()
    {
        var resolved = BuildState.Resolve(dir, _ => null);

        Assert.True(resolved.Settings.IsSqlite);
        Assert.Equal(StateSources.Defaults, resolved.Sources);
        WriteAndRead(BuildSchedulerSchema.Open(dir));
        Assert.True(File.Exists(Path.Combine(dir, "state.db")));
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public void A_state_section_with_a_secret_reference_starts_the_scheduler_on_that_engine(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        if (db is null) return;
        Write(engine, db.Connection);

        var resolved = BuildState.Resolve(dir, db.Environment);

        Assert.Equal(new StateSource(StateSourceKind.File, SettingsFile), resolved.Sources.Engine);
        var database = BuildState.Open(dir, settings: resolved.Settings, environment: db.Environment);
        Assert.Equal(Id(engine), database.Dialect.Engine);
        WriteAndRead(database);
        Assert.False(File.Exists(Path.Combine(dir, "state.db")));
        Assert.True(db.Tables("%") > 0);

        // BuildSchedulerSchema.Open, which the server calls, reads the same file (the password variable is set for the process)
        Assert.Equal(Id(engine), BuildSchedulerSchema.Open(dir).Dialect.Engine);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task The_environment_beats_the_file_and_the_flag_beats_the_environment_and_state_show_names_each(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        if (db is null) return;
        Write(engine, db.Connection);

        var fromFile = await Run(db.Environment, "state", "show", "--offline");
        Assert.Equal(0, fromFile.Exit);
        Assert.Contains($"engine       {Id(engine)}", fromFile.Out);
        Assert.Contains($"file {SettingsFile}", fromFile.Out);

        string? Env(string name) => name == BuildState.EngineVariable ? "sqlite" : db.Environment(name);
        var resolved = BuildState.Resolve(dir, Env);
        Assert.True(resolved.Settings.IsSqlite);
        Assert.Equal(new StateSource(StateSourceKind.Environment, BuildState.EngineVariable), resolved.Sources.Engine);
        Assert.Equal(StateEngineIds.Sqlite, BuildState.Open(dir, settings: resolved.Settings, environment: Env).Dialect.Engine);
        var fromEnvironment = await Run(Env, "state", "show", "--offline");
        Assert.Contains($"environment variable {BuildState.EngineVariable}", fromEnvironment.Out);
        Assert.Contains($"engine       {StateEngineIds.Sqlite}", fromEnvironment.Out);

        var flagged = BuildState.Resolve(dir, Env, engine, null, "--state-engine");
        Assert.False(flagged.Settings.IsSqlite);
        Assert.Equal(new StateSource(StateSourceKind.Flag, "--state-engine"), flagged.Sources.Engine);
        var fromFlag = await Run(Env, "state", "show", "--offline", "--engine", engine);
        Assert.Contains("flag --engine", fromFlag.Out);

        var other = BuildState.Resolve(dir, name => name == BuildState.ConnectionVariable ? "Host=elsewhere;Database=x" : null);
        Assert.Equal(new StateSource(StateSourceKind.Environment, BuildState.ConnectionVariable), other.Sources.Connection);
    }

    [Fact]
    public async Task A_typo_in_the_state_section_is_refused_with_a_suggestion_naming_the_file()
    {
        File.WriteAllText(SettingsFile, "state:\n  retenton: { days: 3 }\n");

        var refused = Assert.Throws<InvalidOperationException>(() => BuildState.Resolve(dir, _ => null));
        Assert.Contains(SettingsFile, refused.Message);
        Assert.Contains("did you mean 'retention'", refused.Message);

        var shown = await Run(_ => null, "state", "show");
        Assert.Equal(10, shown.Exit);
        Assert.Contains(SettingsFile, shown.Err);
    }

    [Fact]
    public void The_namespaces_and_retention_of_the_section_reach_the_settings()
    {
        File.WriteAllText(SettingsFile, "state:\n  namespaces:\n    scheduling: { file: scheduling.db }\n  retention:\n    days: 14\n");

        var resolved = BuildState.Resolve(dir, _ => null);

        Assert.Equal("scheduling.db", resolved.Settings.Namespaces.Modules["scheduling"].File);
        Assert.Equal(14, resolved.Settings.Retention.Days);
        Assert.Equal(new StateSource(StateSourceKind.File, SettingsFile), resolved.Sources.Namespaces);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task State_init_for_an_install_writes_the_servers_own_file_and_the_server_then_uses_it(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        if (db is null) return;

        var init = await Run(db.Environment, "state", "init", "--topology", "install", "--engine", engine, "--connection", db.Connection, "--admin-connection", db.Connection, "--apply");

        Assert.True(init.Exit == 0, init.Out + init.Err);
        Assert.True(File.Exists(SettingsFile));
        var resolved = BuildState.Resolve(dir, db.Environment);
        Assert.Equal(StateSourceKind.File, resolved.Sources.Engine.Kind);
        WriteAndRead(BuildState.Open(dir, settings: resolved.Settings, environment: db.Environment));
        Assert.False(File.Exists(Path.Combine(dir, "state.db")));
    }
}
