using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>Plan 29b C07: a DbDataStore server reads the <c>state:</c> section of its <c>server.yaml</c>, with the flag first, then the environment, then the file, then SQLite.</summary>
public sealed class StoreStateSettingsFileTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("dds-c07").FullName;

    public void Dispose()
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SettingsFile => Path.Combine(directory, "server.yaml");

    private void Write(string engine, string connection) => new StateSettingsWriter().Write(SettingsFile, new StateSettings(engine, connection));

    private async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(Func<string, string?> environment, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await StoreCli.Router(environment).RunAsync([.. args, "--repo", directory], context);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    /// <summary>The instance id is made the first time and read back after: one record written, one read.</summary>
    private static void WriteAndRead(StateDatabase database)
    {
        var settings = new GroupSettings(database);
        var made = GroupIdentity.InstanceId(settings, "store");
        Assert.Equal(made, GroupIdentity.InstanceId(new GroupSettings(database), "store"));
    }

    [Fact]
    public void Nothing_named_is_sqlite_and_the_file_is_not_created()
    {
        var resolved = StoreState.Resolve(directory, _ => null);

        Assert.True(resolved.Settings.IsSqlite);
        Assert.Equal(StateSources.Defaults, resolved.Sources);
        WriteAndRead(StoreState.Open(directory));
        Assert.True(File.Exists(Path.Combine(directory, "state.db")));
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public void A_state_section_with_a_secret_reference_starts_the_server_on_that_engine(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        Write(engine, db.Connection);

        var resolved = StoreState.Resolve(directory, db.Environment);

        Assert.Equal(new StateSource(StateSourceKind.File, SettingsFile), resolved.Sources.Engine);
        var database = StoreState.Open(directory, resolved.Settings, db.Environment);
        Assert.Equal(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, database.Dialect.Engine);
        WriteAndRead(database);
        Assert.False(File.Exists(Path.Combine(directory, "state.db")));
        Assert.True(db.Tables("%") > 0);

        // and the pairing verbs, which open the same state through the same file
        Assert.NotNull(StoreState.Pairing(directory, db.Environment).Codes().Create(ServerRoles.Close(ServerRole.Viewer)).Code);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_server_built_with_the_state_section_serves_from_that_engine(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        Write(engine, db.Connection);   // the password variable is set for the process, which is where a host reads it

        var settings = new StoreSettings(directory, new Uri("https://127.0.0.1:0")) { State = StoreState.Resolve(directory, db.Environment).Settings };
        var app = StoreServer.Build(settings, StoreServer.Certificate(directory), _ => { });
        await app.StartAsync();
        try { Assert.True(db.Tables("%") > 0); }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
        Assert.False(File.Exists(Path.Combine(directory, "state.db")));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task The_environment_beats_the_file_and_the_flag_beats_the_environment_and_state_show_names_each(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        Write(engine, db.Connection);

        var fromFile = await Run(db.Environment, "state", "show", "--offline");
        Assert.Equal(0, fromFile.Exit);
        Assert.Contains($"engine       {(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres)}", fromFile.Out);
        Assert.Contains($"file {SettingsFile}", fromFile.Out);

        string? Env(string name) => name == StoreState.EngineVariable ? "sqlite" : db.Environment(name);
        var resolved = StoreState.Resolve(directory, Env);
        Assert.True(resolved.Settings.IsSqlite);
        Assert.Equal(new StateSource(StateSourceKind.Environment, StoreState.EngineVariable), resolved.Sources.Engine);
        Assert.Equal(StateEngineIds.Sqlite, StoreState.Open(directory, resolved.Settings, Env).Dialect.Engine);
        var fromEnvironment = await Run(Env, "state", "show", "--offline");
        Assert.Contains($"environment variable {StoreState.EngineVariable}", fromEnvironment.Out);
        Assert.Contains($"engine       {StateEngineIds.Sqlite}", fromEnvironment.Out);

        var flagged = StoreState.Resolve(directory, Env, engine, null, "--state-engine");
        Assert.False(flagged.Settings.IsSqlite);
        Assert.Equal(new StateSource(StateSourceKind.Flag, "--state-engine"), flagged.Sources.Engine);
        var fromFlag = await Run(Env, "state", "show", "--offline", "--engine", engine);
        Assert.Contains("flag --engine", fromFlag.Out);

        var other = StoreState.Resolve(directory, name => name == StoreState.ConnectionVariable ? "Host=elsewhere;Database=x" : null);
        Assert.Equal(new StateSource(StateSourceKind.Environment, StoreState.ConnectionVariable), other.Sources.Connection);
        Assert.Equal("Host=elsewhere;Database=x", other.Settings.ConnectionString);
    }

    [Fact]
    public async Task A_typo_in_the_state_section_is_refused_with_a_suggestion_naming_the_file()
    {
        File.WriteAllText(SettingsFile, "instance: x\nstate:\n  conection: Host=db\n");

        var refused = Assert.Throws<InvalidOperationException>(() => StoreState.Resolve(directory, _ => null));
        Assert.Contains(SettingsFile, refused.Message);
        Assert.Contains("did you mean 'connection'", refused.Message);

        var shown = await Run(_ => null, "state", "show");
        Assert.Equal(10, shown.Exit);
        Assert.Contains(SettingsFile, shown.Err);
    }

    [Fact]
    public void The_namespaces_and_retention_of_the_section_reach_the_settings()
    {
        File.WriteAllText(SettingsFile, "state:\n  namespaces:\n    trust: { file: trust.db }\n  retention:\n    days: 30\n");

        var resolved = StoreState.Resolve(directory, _ => null);

        Assert.Equal("trust.db", resolved.Settings.Namespaces.Modules["trust"].File);
        Assert.Equal(30, resolved.Settings.Retention.Days);
        Assert.Equal(new StateSource(StateSourceKind.File, SettingsFile), resolved.Sources.Namespaces);
        Assert.Equal(new StateSource(StateSourceKind.File, SettingsFile), resolved.Sources.Retention);
        Assert.Equal(StateSourceKind.Default, resolved.Sources.Engine.Kind);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task State_init_for_a_group_writes_the_servers_own_file_and_the_server_then_uses_it(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);

        var init = await Run(db.Environment, "state", "init", "--topology", "group", "--engine", engine, "--connection", db.Connection, "--admin-connection", db.Connection, "--apply");

        Assert.True(init.Exit == 0, init.Out + init.Err);
        Assert.True(File.Exists(SettingsFile));
        var resolved = StoreState.Resolve(directory, db.Environment);
        Assert.Equal(StateSourceKind.File, resolved.Sources.Engine.Kind);
        WriteAndRead(StoreState.Open(directory, resolved.Settings, db.Environment));
        Assert.False(File.Exists(Path.Combine(directory, "state.db")));
    }
}
