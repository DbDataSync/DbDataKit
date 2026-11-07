using DbDataKit.Cli;
using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.State.Cli;
using DbDataKit.Web;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.State;

namespace DbDataProxy.Tests.Hosting;

/// <summary>
/// The snapshot verbs on the proxy's command line (plan 29b X04b): the state of a proxy (its group's notifications and pairing codes, among the modules of the serving layer) survives <c>state export</c> and
/// <c>state import</c> into a fresh database on SQLite, PostgreSQL and SQL Server. <c>state move</c> writes the <c>state:</c> section of the proxy's <c>server.yaml</c> (the shared section, plan 29b C07).
/// </summary>
public sealed class StateSnapshotVerbTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ddk-proxy-snap").FullName;
    private readonly StateVerbDatabases databases = new();

    public void Dispose()
    {
        databases.Dispose();
        Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, null);
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(root, name);

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>The proxy's data directory with the <c>state:</c> section of its <c>server.yaml</c> naming the database (none for SQLite).</summary>
    private StateSettings? Configure(string directory, string engine, string? connection)
    {
        if (engine == "sqlite") return null;
        Directory.CreateDirectory(ProxyConfigFile.DirectoryIn(directory));
        File.WriteAllText(ProxyConfigFile.PathIn(directory), $"state:\n  engine: {engine}\n  connection: \"{connection!.Replace("\"", "\\\"")}\"\n");
        return new StateSettings(engine, connection);
    }

    private StateDatabase OpenState(string directory, StateSettings? settings) =>
        ProxyState.Open(directory, settings, text => text.Replace($"env:{StateVerbDatabases.PasswordVariable}", databases.Password, StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(StateVerbDatabases.Engines), MemberType = typeof(StateVerbDatabases))]
    public async Task Export_then_import_into_a_fresh_database_starts_the_proxy_with_the_same_group_state(string engine)
    {
        var sourceConnection = engine == "sqlite" ? null : databases.Provision(engine);
        var targetConnection = engine == "sqlite" ? null : databases.Provision(engine);
        Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, databases.Password);

        var sourceSettings = Configure(Dir("source"), engine, sourceConnection);
        var source = OpenState(Dir("source"), sourceSettings);
        var feed = new NotificationFeedStore(source, ProxyState.ServiceId);
        feed.Raise(NotificationKinds.CertificateExpiring, "the certificate ends soon");
        feed.Raise(NotificationKinds.CertificateExpired, "the certificate ended");
        new PairingFiles(Path.Combine(Dir("source"), "server.yaml"), Group: source).Codes().Create(ServerRoles.Close(ServerRole.Admin), "console");
        var expected = feed.List().Select(n => n.Message).ToList();

        var export = await Run("state", "export", "--to", Dir("snapshot"), "--repo", Dir("source"), "--include-secrets-check");
        Assert.True(export.Exit == 0, export.Out + export.Error);
        Assert.Contains("trust.", export.Out);

        var targetSettings = Configure(Dir("target"), engine, targetConnection);
        var import = await Run("state", "import", "--from", Dir("snapshot"), "--repo", Dir("target"));
        Assert.True(import.Exit == 0, import.Out + import.Error);
        Assert.Contains("Start the proxy as usual", import.Out);

        var target = OpenState(Dir("target"), targetSettings);
        Assert.Equal(expected, new NotificationFeedStore(target, ProxyState.ServiceId).List().Select(n => n.Message).ToList());
        Assert.Equal(1, new PairingFiles(Path.Combine(Dir("target"), "server.yaml"), Group: target).Codes().Pending());

        Assert.Equal(0, (await Run("state", "verify", "--snapshot", Dir("snapshot"))).Exit);
        var against = await Run("state", "verify", "--snapshot", Dir("snapshot"), "--db", "--repo", Dir("target"));
        Assert.True(against.Exit == 0, against.Out + against.Error);
        var other = engine == "sqlite"
            ? await Run("state", "verify", "--other-engine", "sqlite", "--other-sqlite", ProxyState.PathIn(Dir("source")), "--repo", Dir("target"))
            : await Run("state", "verify", "--other-engine", engine, "--other-connection", sourceConnection!, "--repo", Dir("target"));
        Assert.True(other.Exit == 0, other.Out + other.Error);

        // the password is in no output and no file of the snapshot
        var everything = string.Join("\n", export.Out, export.Error, import.Out, import.Error, against.Out, other.Out, StateVerbDatabases.EverythingUnder(Dir("snapshot")));
        if (databases.Password.Length > 0) Assert.DoesNotContain(databases.Password, everything);
    }

    [Theory]
    [MemberData(nameof(StateVerbDatabases.ServerEngines), MemberType = typeof(StateVerbDatabases))]
    public async Task State_move_from_sqlite_to_a_server_engine_writes_the_state_section_of_the_proxys_server_yaml_and_the_proxy_then_reads_it(string engine)
    {
        var target = databases.Provision(engine);
        Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, databases.Password);
        OpenState(Dir("source"), null).OpenConnection().Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var moved = await Run("state", "move", "--to-engine", engine, "--to-connection", target, "--yes", "--repo", Dir("source"));

        Assert.True(moved.Exit == 0, moved.Out + moved.Error);
        var text = File.ReadAllText(ProxyConfigFile.PathIn(Dir("source")));
        Assert.Contains($"engine: {engine}", text);
        Assert.Contains($"Password=env:{StateVerbDatabases.PasswordVariable}", text);
        Assert.DoesNotContain(databases.Password, text);
        var read = ProxyConfiguration.LoadState(Dir("source"));
        Assert.Equal(engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, read.Settings.EngineId);
        Assert.Equal(StateSourceKind.File, read.Sources.Engine.Kind);
    }
}
