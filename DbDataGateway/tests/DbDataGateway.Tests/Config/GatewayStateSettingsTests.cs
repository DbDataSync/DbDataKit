using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.State;
using Microsoft.AspNetCore.Builder;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 29b C07: the Gateway's <c>state:</c> section is the one every product shares (<c>engine</c>, <c>connection</c>, <c>namespaces</c>, <c>retention</c>). The Gateway's own layers decide the winner: the file, then the environment
/// (<c>DBGATEWAY_Gateway__State__Engine</c>), then the command line; an old-format file is refused naming the file and the key.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GatewayStateSettingsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbgateway-c07-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<WebApplication> _apps = [];

    private string ServerYaml => GatewayConfigFile.PathIn(_dir);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(_dir));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            await app.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        TestFolder.Delete(_dir);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void WriteFile(string state = "")
    {
        File.WriteAllText(ServerYaml, "schemaVersion: 2\ntls: { mode: selfSigned }\n" + state);
    }

    private void WriteState(string engine, string connection)
    {
        WriteFile();
        new StateSettingsWriter().Write(ServerYaml, new StateSettings(engine, connection));
    }

    private WebApplication Instance(params string[] more)
    {
        var app = GatewayHost.Build([$"--Gateway:DataDirectory={_dir}", "--Gateway:Listen:Address=127.0.0.1", $"--Gateway:Listen:Port={FreePort()}", .. more]);
        _apps.Add(app);
        return app;
    }

    private async Task<(int Exit, string Out, string Err, JsonNode Json)> Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await Cli.RunAsync([.. args, "--repo", _dir], new CliContext(o, e, TextReader.Null));
        var (j, je) = (new StringWriter(), new StringWriter());
        _ = await Cli.RunAsync([.. args, "--repo", _dir, "--format", "json"], new CliContext(j, je, TextReader.Null));
        return (exit, o.ToString(), e.ToString(), JsonNode.Parse(j.ToString()) ?? new JsonObject());
    }

    /// <summary>Writes one record to the instance's state and reads it back.</summary>
    private static void WriteAndRead(WebApplication app)
    {
        var database = app.Services.GetRequiredService<StateDatabase>();
        var log = new StateAuditLog(database);
        log.Append(new AuditEntry(DateTimeOffset.UtcNow, "console-1", "Console", "grace", "Grace Hopper", true, "data-source.write", "erp", "ok", "t1"));
        Assert.Contains(log.Read(), e => e.UserId == "grace");
    }

    private static string Id(string engine) => engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres;

    [Fact]
    public async Task Nothing_named_is_sqlite_and_state_show_says_it_is_the_default()
    {
        WriteFile();

        var shown = await Run("state", "show", "--offline");

        Assert.Equal(0, shown.Exit);
        Assert.Contains($"engine       {StateEngineIds.Sqlite}", shown.Out);
        Assert.Equal("default", shown.Json["data"]!["sources"]!["engine"]!["kind"]!.GetValue<string>());
        var app = Instance();
        await app.StartAsync();
        Assert.Equal(StateEngineIds.Sqlite, app.Services.GetRequiredService<StateDatabase>().Dialect.Engine);
        WriteAndRead(app);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_state_section_with_a_secret_reference_starts_the_gateway_on_that_engine(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        WriteState(engine, db.Connection);

        var app = Instance();
        await app.StartAsync();

        Assert.Equal(Id(engine), app.Services.GetRequiredService<StateDatabase>().Dialect.Engine);
        WriteAndRead(app);
        Assert.False(File.Exists(Path.Combine(_dir, "state.db")));
        var shown = await Run("state", "show", "--offline");
        Assert.Contains($"file {ServerYaml}", shown.Out);
        Assert.Equal(ServerYaml, shown.Json["data"]!["sources"]!["engine"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task The_environment_beats_the_file_and_the_flag_beats_the_environment_and_state_show_names_each(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        WriteState(engine, db.Connection);

        // the environment beats the file: SQLite, from the variable the gateway has always read
        using (new EnvScope(("DBGATEWAY_Gateway__State__Engine", "sqlite")))
        {
            var shown = await Run("state", "show", "--offline");
            Assert.Contains($"engine       {StateEngineIds.Sqlite}", shown.Out);
            Assert.Contains("environment variable DBGATEWAY_Gateway__State__Engine", shown.Out);
            Assert.Equal("environment", shown.Json["data"]!["sources"]!["engine"]!["kind"]!.GetValue<string>());

            var viaEnvironment = Instance();
            await viaEnvironment.StartAsync();
            Assert.Equal(StateEngineIds.Sqlite, viaEnvironment.Services.GetRequiredService<StateDatabase>().Dialect.Engine);
            await viaEnvironment.StopAsync();   // one instance at a time on a SQLite state

            // the command line beats the environment: the server engine again, the connection still the file's
            var viaFlag = Instance($"--Gateway:State:Engine={engine}");
            await viaFlag.StartAsync();
            Assert.Equal(Id(engine), viaFlag.Services.GetRequiredService<StateDatabase>().Dialect.Engine);
            WriteAndRead(viaFlag);
        }
    }

    [Fact]
    public async Task A_file_with_the_old_state_key_is_refused_naming_the_file_and_the_key_and_showing_the_new_shape()
    {
        foreach (var key in new[] { "connectionString", "connection_string" })
        {
            WriteFile($"state:\n  engine: postgres\n  {key}: Host=db;Database=x;Username=u;Password=secret:state-db\n");

            var refused = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={_dir}", "--Gateway:Listen:Address=127.0.0.1", $"--Gateway:Listen:Port={FreePort()}"]));
            Assert.Contains(ServerYaml, refused.Message);
            Assert.Contains($"state.{key}", refused.Message);
            Assert.Contains("  connection: ", refused.Message);   // the new shape

            var shown = await Run("state", "show");
            Assert.Equal(10, shown.Exit);
            Assert.Contains(ServerYaml, shown.Err);
            Assert.Contains($"state.{key}", shown.Err);
        }

        WriteFile("state:\n  definitionPollSeconds: 1\n");
        var moved = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={_dir}", "--Gateway:Listen:Address=127.0.0.1", $"--Gateway:Listen:Port={FreePort()}"]));
        Assert.Contains(ServerYaml, moved.Message);
        Assert.Contains("top level", moved.Message);
    }

    [Fact]
    public async Task A_typo_in_the_state_section_is_refused_with_a_suggestion_naming_the_file()
    {
        WriteFile("state:\n  engin: postgres\n");

        var refused = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={_dir}", "--Gateway:Listen:Address=127.0.0.1", $"--Gateway:Listen:Port={FreePort()}"]));
        Assert.Contains(ServerYaml, refused.Message);
        Assert.Contains("Did you mean `engine`", refused.Message, StringComparison.OrdinalIgnoreCase);

        var shown = await Run("state", "show");
        Assert.Equal(10, shown.Exit);
        Assert.Contains(ServerYaml, shown.Err);
        Assert.Contains("did you mean 'engine'", shown.Err);

        WriteFile("state:\n  namespaces:\n    identity: { shema: ident }\n");
        var inner = await Run("state", "show");
        Assert.Contains("shema", inner.Err);
        Assert.Contains("did you mean 'schema'", inner.Err);
    }

    [Fact]
    public void The_namespaces_and_retention_of_the_section_reach_the_settings()
    {
        WriteFile("state:\n  namespaces:\n    default: { schema: gateway }\n    identity: { schema: ident }\n  retention:\n    days: 30\n    tables: { audit.entry: forever }\n");

        var resolved = GatewayConfiguration.LoadState(_dir);

        Assert.Equal("gateway", resolved.Settings.Namespaces.Default.Schema);
        Assert.Equal("ident", resolved.Settings.Namespaces.Modules["identity"].Schema);
        Assert.Equal(30, resolved.Settings.Retention.Days);
        Assert.Equal(RetentionSettings.Forever, resolved.Settings.Retention.Tables["audit.entry"]);
        Assert.Equal(new StateSource(StateSourceKind.File, ServerYaml), resolved.Sources.Namespaces);
        Assert.Equal(new StateSource(StateSourceKind.File, ServerYaml), resolved.Sources.Retention);
        Assert.Equal(StateSourceKind.Default, resolved.Sources.Engine.Kind);

        // the options the host binds say the same through the same reader
        var options = GatewayConfiguration.LoadOptions(_dir).State.ToSettings();
        Assert.Equal("ident", options.Namespaces.Modules["identity"].Schema);
        Assert.Equal(30, options.Retention.Days);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task State_init_for_a_group_writes_the_gateways_own_file_and_the_gateway_then_uses_it(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);

        var init = await Run("state", "init", "--topology", "group", "--engine", engine, "--connection", db.Connection, "--admin-connection", db.Connection, "--apply");

        Assert.True(init.Exit == 0, init.Out + init.Err);
        Assert.True(File.Exists(ServerYaml), "init writes server.yaml in the config folder with no --settings");
        var app = Instance();
        await app.StartAsync();
        Assert.Equal(Id(engine), app.Services.GetRequiredService<StateDatabase>().Dialect.Engine);
        WriteAndRead(app);
        Assert.False(File.Exists(Path.Combine(_dir, "state.db")));
    }
}
