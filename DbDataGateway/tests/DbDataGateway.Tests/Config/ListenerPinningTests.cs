using System.Net;
using System.Net.Sockets;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;
using DbDataKit.Hosting.Startup;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// The Gateway's three default listeners (public, Flight SQL, management) take free ports of its block at the first start and keep them; a written address is never moved;
/// an old `port` or `address` is refused naming the file and the setting; a list of addresses is read for every listener.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ListenerPinningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-listen-").FullName;

    public void Dispose()
    {
        TempTree.Delete(_dir);
    }

    private string Settings => GatewayConfigFile.PathIn(_dir);

    private static TcpListener Hold(int port)
    {
        // ExclusiveAddressUse is set on Windows only: set to false on Linux it asks for SO_REUSEPORT, and the gateway could then share the port
        var listener = new TcpListener(IPAddress.Any, port);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        listener.Start();
        return listener;
    }

    /// <summary>Runs <c>server run</c> on the folder, waits until the chosen addresses are written, runs <paramref name="during"/> and stops it. Returns the addresses of the three listeners as the file holds them.</summary>
    private async Task<string[]> StartAsync(string[] extra, Func<string[], Task>? during = null)
    {
        using var stop = new CancellationTokenSource();
        var error = new StringWriter();
        var run = Cli.RunAsync(["server", "run", $"--Gateway:DataDirectory={_dir}", "--Gateway:Tls:Mode=SelfSigned", .. extra], new CliContext(TextWriter.Null, TextWriter.Synchronized(error), TextReader.Null, stop.Token));
        var deadline = DateTime.UtcNow.AddSeconds(90);
        string[] found = [];
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            found = Read();
            if (found.All(f => f.Length > 0)) break;
            await Task.Delay(100);
        }
        Assert.False(run.IsCompleted, "the gateway stopped instead of serving: " + error);
        if (during is not null) await during(found);
        stop.Cancel();
        Assert.Equal(0, await run);
        return found;
    }

    private string[] Read()
    {
        if (!File.Exists(Settings)) return ["", "", ""];
        var text = File.ReadAllText(Settings);
        string Get(string path)
        {
            // the file is edited in place; the reader of the same strict parser finds the value
            var diags = new List<ConfigDiagnostic>();
            var root = StrictYamlReader.Read(text, Settings, diags, GatewayConfig.Catalog) as YamlMapping;
            var node = path.Split('.').Aggregate((YamlNode?)root, (n, key) => (n as YamlMapping)?.Get(key));
            return (node as YamlScalar)?.Value ?? "";
        }
        return [Get("public.listen"), Get("listen"), Get("management.listen")];
    }

    private static int PortOf(string address) => ListenUrl.Parse(address).Port;

    [Fact]
    public async Task A_gateway_started_with_no_settings_listens_on_three_ports_of_its_block_and_on_the_same_three_after_a_restart()
    {
        var block = SuiteServices.Gateway.Ports;
        Assert.SkipWhen(Enumerable.Range(block.Start, 3).Any(p => !ListenPorts.CanBind(IPAddress.Any, p)), "a port of the first three of the block is taken on this machine");
        var first = await StartAsync([], async found =>
        {
            Assert.Equal(3, found.Select(PortOf).Distinct().Count());
            Assert.All(found.Select(PortOf), p => Assert.True(block.Contains(p), $"{p} is outside {block}"));
            foreach (var address in found)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, PortOf(address));
                Assert.True(client.Connected);
            }
        });
        Assert.StartsWith("https://", first[0]);
        Assert.StartsWith("https://127.0.0.1:", first[2]);   // the management API is on the loopback address
        Assert.Equal(first, await StartAsync([]));
    }

    [Fact]
    public async Task With_the_first_port_of_the_block_held_the_listeners_take_other_free_ports_of_it()
    {
        var block = SuiteServices.Gateway.Ports;
        TcpListener held;
        try { held = Hold(block.Start); }
        catch (SocketException) { Assert.Skip("the first port of the block is taken on this machine"); return; }
        using (held)
        {
            var found = await StartAsync([]);
            Assert.DoesNotContain(block.Start, found.Select(PortOf));
            Assert.All(found.Select(PortOf), p => Assert.True(block.Contains(p)));
        }
    }

    [Fact]
    public async Task A_written_address_is_not_moved_and_only_the_listeners_without_one_are_chosen()
    {
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(_dir));
        File.WriteAllText(Settings, "# mine\nlisten: https://127.0.0.1:0\n");
        var found = await StartAsync(["--Gateway:Management:Enabled=false"], null);
        Assert.Equal("https://127.0.0.1:0", found[1]);
        Assert.Contains("# mine", File.ReadAllText(Settings));
    }

    [Fact]
    public async Task A_configured_address_whose_port_is_held_fails_the_ports_step_naming_the_port_for_each_listener()
    {
        foreach (var setting in new[] { "--Gateway:Public:Listen", "--Gateway:Listen", "--Gateway:Management:Listen" })
        {
            using var held = new TcpListener(IPAddress.Loopback, 0);
            if (OperatingSystem.IsWindows()) held.ExclusiveAddressUse = true;
            held.Start();
            var port = ((IPEndPoint)held.LocalEndpoint).Port;
            var dir = Directory.CreateTempSubdirectory("dbgateway-held-").FullName;
            try
            {
                var error = new StringWriter();
                var exit = await Cli.RunAsync(["server", "run", $"--Gateway:DataDirectory={dir}", $"{setting}=https://127.0.0.1:{port}"], new CliContext(TextWriter.Null, error, TextReader.Null));
                Assert.NotEqual(0, exit);
                var text = File.ReadAllText(SuiteServices.Gateway.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Gateway, dir)));
                Assert.Contains("failed at the ports step", text);
                Assert.Contains($"Port {port} cannot be bound", text);
                Assert.Contains("Excluded port ranges: ", text);
            }
            finally
            {
                TempTree.Delete(dir);
            }
        }
    }

    [Theory]
    [InlineData("listen: { address: 127.0.0.1, port: 9000 }\n", "Listen:Port")]
    [InlineData("management: { port: 8816 }\n", "Management:Port")]
    [InlineData("public: { address: 0.0.0.0 }\n", "Public:Address")]
    [InlineData("modules: [apps]\napps: { port: 8818 }\n", "Apps:Port")]
    [InlineData("rest: { port: 8821 }\n", "Rest:Port")]
    public void A_file_with_port_or_address_is_refused_naming_the_file_and_the_setting(string text, string setting)
    {
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(_dir));
        File.WriteAllText(Settings, text);
        var error = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={_dir}"]));
        Assert.Contains(setting, error.Message);
        Assert.Contains("server.yaml", error.Message);
        Assert.Contains("`listen`", error.Message);
    }

    [Fact]
    public void An_old_environment_variable_or_flag_is_refused_naming_where_it_came_from()
    {
        using var env = new EnvScope(("DBDATAKIT_Gateway__Management__Port", "8816"));
        var fromEnvironment = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={_dir}"]));
        Assert.Contains("the environment variables", fromEnvironment.Message);
        Assert.Contains("Management:Port", fromEnvironment.Message);
    }

    [Fact]
    public void A_listen_setting_as_one_address_and_as_a_list_is_read_by_every_listener_of_the_gateway()
    {
        const string one = "https://127.0.0.1:9101";
        string[] two = ["https://127.0.0.1:9102", "https://127.0.0.1:9103"];
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(_dir));
        File.WriteAllText(Settings,
            $"listen: {one}\npublic: {{ listen: [{two[0]}, {two[1]}] }}\nmanagement: {{ listen: https://127.0.0.1:9104 }}\nmodules: [flightsql, identity, apps, store, views, feeds, mcp]\n"
            + "identity: { listen: https://127.0.0.1:9105 }\napps: { listen: [https://127.0.0.1:9106, https://127.0.0.1:9107], home: { clientId: test } }\nstore: { listen: https://127.0.0.1:9108 }\n"
            + "views: { listen: https://127.0.0.1:9109 }\nmcp: { listen: https://127.0.0.1:9110 }\nrest: { listen: https://127.0.0.1:9111 }\n");
        var o = GatewayConfiguration.LoadOptions(_dir);
        Assert.Equal([one], o.Listen);
        Assert.Equal(two, o.Public.Listen);
        Assert.Equal(["https://127.0.0.1:9104"], o.Management.Listen);
        Assert.Equal(["https://127.0.0.1:9105"], o.Identity.Listen);
        Assert.Equal(["https://127.0.0.1:9106", "https://127.0.0.1:9107"], o.Apps.Listen);
        Assert.Equal(["https://127.0.0.1:9108"], o.Store.Listen);
        Assert.Equal(["https://127.0.0.1:9109"], o.Views.Listen);
        Assert.Equal(["https://127.0.0.1:9110"], o.Mcp.Listen);
        Assert.Equal(["https://127.0.0.1:9111"], o.Rest.Listen);
        // the strict loader (the server's own) reads the same, as a scalar and as a list
        var strict = GatewayConfig.Load(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Add(new GatewayConfigDirectorySource(GatewayConfigFile.DirectoryIn(_dir))).Build());
        Assert.Equal([one], strict.Value!.Listen);
        Assert.Equal(two, strict.Value.Public.Listen);
        Assert.DoesNotContain(strict.Diagnostics, d => d.Severity == ConfigSeverity.Error);
    }

    [Theory]
    [InlineData("https://0.0.0.0", "Listen[0]")]
    [InlineData("0.0.0.0:7821", "Listen[0]")]
    [InlineData("http://127.0.0.1:7821", "Listen[0]")]   // Flight takes http only without TLS
    public void A_listen_address_that_is_not_one_is_an_error_on_the_setting(string address, string key)
    {
        using var file = TempConfig.Create(("server.yaml", $"listen: {address}\nmanagement: {{ enabled: false }}\n"));
        var load = GatewayConfig.Load(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Add(new GatewayConfigDirectorySource(file.Directory)).Build());
        Assert.Contains(load.Diagnostics, d => d.KeyPath == key && d.Severity == ConfigSeverity.Error);
    }

    [Fact]
    public void Two_listeners_on_one_port_are_refused_naming_the_later_setting()
    {
        using var file = TempConfig.Create(("server.yaml", "listen: https://127.0.0.1:9200\npublic: { listen: [https://127.0.0.1:9201, https://127.0.0.1:9200] }\nmanagement: { enabled: false }\n"));
        var load = GatewayConfig.Load(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Add(new GatewayConfigDirectorySource(file.Directory)).Build());
        Assert.Contains(load.Diagnostics, d => d.KeyPath == "Public.Listen" && d.Message.Contains("9200", StringComparison.Ordinal) || d.KeyPath == "Public.Listen" && d.Fix!.Contains("9200", StringComparison.Ordinal));
    }

    [Fact]
    public void The_apps_quickstart_gives_identity_and_apps_a_listen_each_on_the_ports_it_publishes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "dist", "compose", "quickstart-apps.yml");
        var root = FindRepository();
        var text = File.ReadAllText(Path.Combine(root, "dist", "compose", "quickstart-apps.yml"));
        Assert.Contains("DBDATAKIT_Gateway__Identity__Listen: https://0.0.0.0:7823", text);
        Assert.Contains("DBDATAKIT_Gateway__Apps__Listen: https://0.0.0.0:7824", text);
        Assert.DoesNotContain("__Port", text);
        Assert.DoesNotContain("__Address", text);
        Assert.Contains("- \"7823:7823\"", text);
        Assert.Contains("- \"7824:7824\"", text);
        _ = path;
    }

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DbDataKit.slnx"))) return dir.FullName;
        throw new InvalidOperationException("The repository root (DbDataKit.slnx) is not above the test output.");
    }
}
