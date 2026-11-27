using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using DbDataGateway.Client;
using DbDataKit.Cli;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Groups through the join token, with the real programs on one state database (SQL Server or PostgreSQL) and a console in this process. Two Gateways joined to
/// group <c>sales</c> get the group's key from the console (none is copied): a pass-through session one makes is served by the other, credentials and all, and the
/// second listens on exactly the first's pinned ports, on its own address; a third whose machine holds one of those ports fails at the ports step naming it and
/// listens on nothing. Two Runners joined to group <c>night</c> hold the key the console keeps for the group, and a login sent to either is the group's. A token
/// for <c>night</c> used by a Gateway, and by a Runner of another state database, is refused.
/// </summary>
public sealed class GroupJoinByTokenTests
{
    public static TheoryData<string> Engines => ["mssql", "postgres"];

    private const string GatewayKey = "dbdatakit:gateway:state:session-key";
    private const string RunnerKey = "dbdatakit:runner:state:group-key";

    /// <summary>What <c>setup invite</c> printed for a server of a group: the arguments of the <c>pair</c> command after the program's name.</summary>
    private static async Task<string[]> InviteAsync(NounRouter console, string directory, Uri consoleUrl, string product, string group)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await console.RunAsync(["setup", "invite", product, "--group", group, "--url", consoleUrl.GetLeftPart(UriPartial.Authority), "--repo", directory], new CliContext(output, error, TextReader.Null));
        Assert.True(exit == 0, $"setup invite {product} exited {exit}:\n{output}\n{error}");
        var line = output.ToString().Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith(ConsoleJoins.Products[product] + " pair ", StringComparison.Ordinal));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
    }

    /// <summary>The group's key as an instance keeps it in its own store (with no DPAPI on Linux).</summary>
    private static async Task<string?> KeyOnInstanceAsync(string directory, string secret)
    {
        var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), null, null));
        using var held = await store.TryResolveAsync(SecretRef.Parse("secret:" + secret));
        return held?.RevealString();
    }

    /// <summary>The SHA-256 of the certificate a listener presents.</summary>
    private static async Task<string> CertificateAtAsync(string host, int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        string? seen = null;
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => { seen = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(certificate!.GetRawCertData())); return true; });
        await tls.AuthenticateAsClientAsync("localhost");
        return seen!;
    }

    private static GatewayFlightClient Flight(string host, int port) => new($"https://{host}:{port}", new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } },
    });

    private static async Task<long> FirstValueAsync(GatewayFlightClient client)
    {
        var result = await client.QueryAsync("SELECT 1 AS one");
        await foreach (var batch in result)
            if (batch.Length > 0)
                return batch.Column(0) switch
                {
                    Apache.Arrow.Int32Array i => i.GetValue(0) ?? -1,
                    Apache.Arrow.Int64Array l => l.GetValue(0) ?? -1,
                    var other => throw new InvalidOperationException($"SELECT 1 came back as {other.GetType().Name}."),
                };
        return -1;
    }

    /// <summary>
    /// A Gateway's data folder: its listeners on <paramref name="host"/> with the ports given, a pass-through data source on the engine's server, and the
    /// settings the join step needs.
    /// </summary>
    private static string GatewayFolder(string root, string name, string host, (int Public, int Flight, int Management) ports, string engine, string server)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(directory, "config", "gateway"));
        Directory.CreateDirectory(Path.Combine(directory, "config", "connections"));
        File.WriteAllText(Path.Combine(directory, "config", "server.yaml"),
            $"schemaVersion: 2\ntls: {{ mode: selfSigned }}\npublic:\n  listen: https://{host}:{ports.Public}\nlisten: https://{host}:{ports.Flight}\nmanagement:\n  listen: https://{host}:{ports.Management}\n");
        File.WriteAllText(Path.Combine(directory, "config", "gateway", "auth.yaml"), "jwt:\n  signingKey: servers-e2e-signing-key-0123456789abcdef\n  audience: api://servers-e2e\n");
        // the server's address only: a pass-through session connects with the caller's login
        var source = engine == "mssql"
            ? new SqlConnectionStringBuilder { DataSource = new SqlConnectionStringBuilder(server).DataSource, InitialCatalog = "master", TrustServerCertificate = true }.ConnectionString
            : new NpgsqlConnectionStringBuilder { Host = new NpgsqlConnectionStringBuilder(server).Host, Port = new NpgsqlConnectionStringBuilder(server).Port, Database = "postgres" }.ConnectionString;
        File.WriteAllText(Path.Combine(directory, "config", "connections", "engine.yaml"),
            $"schema: connection/1\nname: engine\nengine: {(engine == "mssql" ? "sqlserver" : "postgres")}\nconnection_string: '{source.Replace("'", "''")}'\nextensions:\n  gateway:\n    pass_through: true\n");
        return directory;
    }

    private static string[] GatewayRun(string directory, string engine, string connection) =>
        ["server", "run", $"--Gateway:DataDirectory={directory}", $"--Gateway:State:Engine={engine}", $"--Gateway:State:Connection={connection}",
            "--Gateway:Management:Enabled=true", "--Gateway:Management:LocalChannel=false"];

    private static (int, int, int) ThreePorts() => (Programs.FreePort(), Programs.FreePort(), Programs.FreePort());

    [Theory, MemberData(nameof(Engines))]
    public async Task Gateways_of_one_group_share_the_key_and_the_ports_and_Runners_of_one_group_share_the_key(string engine)
    {
        // the instances' stores are read here without DPAPI, which a server on Windows protects its key ring with
        if (OperatingSystem.IsWindows()) Assert.Skip("reads the servers' key rings as Linux keeps them");
        var server = Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");
        if (string.IsNullOrEmpty(server)) Assert.Skip($"set {(engine == "mssql" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES")} to run on {engine}");
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-groups-" + Guid.NewGuid().ToString("N")[..8]);
        var (gatewayState, dropGateways) = await ConsoleJoinsRunnerGroupTests.ProvisionAsync(engine);
        var (runnerState, dropRunners) = await ConsoleJoinsRunnerGroupTests.ProvisionAsync(engine);
        var started = new List<RealProcess>();
        WebApplication? console = null;
        TcpListener? held = null;
        try
        {
            var consoleDirectory = Path.Combine(root, "console");
            console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
            await console.StartAsync();
            var consoleUrl = new Uri(new Uri(console.Urls.First()).GetLeftPart(UriPartial.Authority).Replace("localhost", "127.0.0.1", StringComparison.Ordinal));
            var cli = ConsoleCli.Router(name => Environment.GetEnvironmentVariable(name));
            var registry = console.Services.GetRequiredService<InstanceRegistry>();
            var consoleStore = console.Services.GetRequiredService<EncryptedFileSecretStore>();
            async Task<string?> ConsoleKeyAsync(string instanceId)
            {
                using var key = await consoleStore.TryResolveAsync(ConsoleGroupKeys.NameFor(instanceId).ToRef());
                return key?.RevealString();
            }

            // the first Gateway of group sales: its join reports its pinned ports, and the console makes the group's key and hands it over
            var portsA = ThreePorts();
            var dirA = GatewayFolder(root, "gateway-a", "127.0.0.1", portsA, engine, server);
            await RealProcess.RunAsync(Programs.Gateway, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "gateway", "sales"), "--repo", dirA]);
            var a = RealProcess.Start(Programs.Gateway, GatewayRun(dirA, engine, gatewayState));
            started.Add(a);
            await a.WaitUntilAsync(new Uri($"https://127.0.0.1:{portsA.Item3}/health"), TimeSpan.FromSeconds(120));
            for (var wait = 0; wait < 240 && File.Exists(Path.Combine(dirA, JoinRequestFile.FileName)); wait++) await Task.Delay(250);
            var gateway = Assert.Single(registry.All());
            Assert.Equal(portsA.Item3, gateway.Url.Port);
            var key = await ConsoleKeyAsync(gateway.InstanceId);
            Assert.NotNull(key);
            Assert.Equal(key, await KeyOnInstanceAsync(dirA, GatewayKey));
            // the first instance starts again once it holds the key, so it keeps its certificate in the state as the group's
            for (var wait = 0; wait < 240 && !a.Output.Contains("starting again", StringComparison.Ordinal); wait++) await Task.Delay(250);
            await a.WaitUntilAsync(new Uri($"https://127.0.0.1:{portsA.Item3}/health"), TimeSpan.FromSeconds(120));

            // the second, on its own address with ports of its own in its settings: the join hands it the group's key and the first one's ports, and it starts
            // again on them, serving the group's certificate; the console keeps calling the group at the first one's address
            var dirB = GatewayFolder(root, "gateway-b", "127.0.0.2", ThreePorts(), engine, server);
            await RealProcess.RunAsync(Programs.Gateway, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "gateway", "sales"), "--repo", dirB]);
            var b = RealProcess.Start(Programs.Gateway, GatewayRun(dirB, engine, gatewayState));
            started.Add(b);
            await b.WaitUntilAsync(new Uri($"https://127.0.0.2:{portsA.Item3}/health"), TimeSpan.FromSeconds(180));
            Assert.Contains("starting again on the group's ports", b.Output);
            Assert.False(File.Exists(Path.Combine(dirB, JoinRequestFile.FileName)));
            var settingsB = File.ReadAllText(Path.Combine(dirB, "config", "server.yaml"));
            Assert.Contains($"https://127.0.0.2:{portsA.Item1}", settingsB);
            Assert.Contains($"https://127.0.0.2:{portsA.Item2}", settingsB);
            Assert.Contains($"https://127.0.0.2:{portsA.Item3}", settingsB);
            Assert.Equal(key, await KeyOnInstanceAsync(dirB, GatewayKey));
            Assert.Equal(gateway, Assert.Single(registry.All()));
            Assert.Equal(gateway.CertificateFingerprint, await CertificateAtAsync("127.0.0.2", portsA.Item3));
            Assert.Equal(gateway.CertificateFingerprint, await CertificateAtAsync("127.0.0.1", portsA.Item3));

            // a pass-through session made on the first is served by the second, the database login with it
            var (user, password) = engine == "mssql"
                ? (new SqlConnectionStringBuilder(server).UserID, new SqlConnectionStringBuilder(server).Password)
                : (new NpgsqlConnectionStringBuilder(server).Username!, new NpgsqlConnectionStringBuilder(server).Password!);
            using (var onA = Flight("127.0.0.1", portsA.Item2))
            using (var onB = Flight("127.0.0.2", portsA.Item2))
            {
                await onA.AuthenticateAsync(user, password);
                Assert.Equal(1, await FirstValueAsync(onA));
                onB.Authorization = onA.Authorization;
                Assert.Equal(1, await FirstValueAsync(onB));
                onB.Authorization = "Bearer not-a-session-of-the-group";
                await Assert.ThrowsAnyAsync<Grpc.Core.RpcException>(() => FirstValueAsync(onB));
            }

            // a third whose machine holds one of the group's ports: it fails at the ports step naming the port, and listens on nothing
            var dirC = GatewayFolder(root, "gateway-c", "127.0.0.3", ThreePorts(), engine, server);
            held = new TcpListener(IPAddress.Parse("127.0.0.3"), portsA.Item2);
            held.Start();
            await RealProcess.RunAsync(Programs.Gateway, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "gateway", "sales"), "--repo", dirC]);
            var c = RealProcess.Start(Programs.Gateway, GatewayRun(dirC, engine, gatewayState));
            started.Add(c);
            var exited = await c.WaitForExitAsync(TimeSpan.FromSeconds(180));
            Assert.True(exited == 1, $"the third Gateway exited {exited}:\n{c.Output}");
            var failure = File.ReadAllText(DbDataKit.Hosting.Startup.SuiteServices.Gateway.FailureFileIn(Path.Combine(dirC, "logs")));
            Assert.Contains("ports", failure);
            Assert.Contains(portsA.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture), failure);
            Assert.Contains($"https://127.0.0.3:{portsA.Item2}", File.ReadAllText(Path.Combine(dirC, "config", "server.yaml")));
            foreach (var port in new[] { portsA.Item1, portsA.Item3 })
            {
                using var probe = new TcpClient();
                await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync("127.0.0.3", port));
            }
            held.Stop();
            held = null;

            // the Runners of group night share one state database: each joins by a token for night and gets the key the console keeps for the group
            var runnerEnvironment = new Dictionary<string, string> { ["DBDATAKIT_STATE_RUNNER_ENGINE"] = engine, ["DBDATAKIT_STATE_RUNNER_CONNECTION"] = runnerState };
            var runnerUrls = new List<Uri>();
            var runnerDirectories = new List<string>();
            foreach (var name in new[] { "runner-a", "runner-b" })
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                await RealProcess.RunWithEnvironmentAsync(Programs.Runner, null, runnerEnvironment, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "runner", "night"), "--repo", directory]);
                var url = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
                var runner = RealProcess.Start(Programs.Runner, ["server", "run", "--repo", directory, "--listen", url.AbsoluteUri.TrimEnd('/')], environment: runnerEnvironment);
                started.Add(runner);
                await runner.WaitUntilAsync(new Uri(url, "/health"), TimeSpan.FromSeconds(120));
                for (var wait = 0; wait < 240 && File.Exists(Path.Combine(directory, JoinRequestFile.FileName)); wait++) await Task.Delay(250);
                Assert.False(File.Exists(Path.Combine(directory, JoinRequestFile.FileName)), runner.Output);
                runnerUrls.Add(url);
                runnerDirectories.Add(directory);
            }
            var runners = registry.All().Where(i => i.InstanceId != gateway.InstanceId).ToList();
            var group = Assert.Single(runners);
            Assert.Equal(runnerUrls[0].Port, group.Url.Port);
            var runnerKey = await ConsoleKeyAsync(group.InstanceId);
            Assert.NotNull(runnerKey);
            Assert.NotEqual(key, runnerKey);
            foreach (var directory in runnerDirectories) Assert.Equal(runnerKey, await KeyOnInstanceAsync(directory, RunnerKey));

            // a login sent to the group's registered instance and one sent to the second are both the group's: kept in the state, listed by the first
            var relay = console.Services.GetRequiredService<ConsoleRelay>();
            var second = group with { Url = runnerUrls[1], CertificateFingerprint = PairingProof.CertificateFingerprint(DbDataKit.Hosting.Certificates.DirectoryCertificate.Load(runnerDirectories[1])) };
            foreach (var (instance, name) in new[] { (group, "dbdatakit:sync:connection:erp:run"), (second, "dbdatakit:sync:connection:crm:run") })
            {
                var (status, body) = await relay.CallAsConsoleAsync(instance, ServerRole.Admin, HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(name)}", new { value = "s3nt-" + name }, default);
                Assert.True(status == 200, $"{status} {body?.ToJsonString()}");
            }
            var (listedStatus, listed) = await relay.CallAsConsoleAsync(group, ServerRole.Admin, HttpMethod.Get, "/api/v1/secrets", null, default);
            Assert.True(listedStatus == 200, listed?.ToJsonString());
            Assert.Contains("dbdatakit:sync:connection:crm:run", listed!.ToJsonString());
            Assert.Contains("dbdatakit:sync:connection:erp:run", listed.ToJsonString());
            foreach (var instance in new[] { group, second })
            {
                var (keyStatus, keyBody) = await relay.CallAsConsoleAsync(instance, ServerRole.Viewer, HttpMethod.Get, "/api/v1/group/key", null, default);
                Assert.True(keyStatus == 200 && (bool)keyBody!["held"]!, keyBody?.ToJsonString());
            }

            // a token for night used by a Gateway, and by a Runner of another state database, is refused at the join step
            var gatewayD = GatewayFolder(root, "gateway-d", "127.0.0.4", ThreePorts(), engine, server);
            await RealProcess.RunAsync(Programs.Gateway, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "runner", "night"), "--repo", gatewayD]);
            var d = RealProcess.Start(Programs.Gateway, ["server", "run", $"--Gateway:DataDirectory={gatewayD}", "--Gateway:Management:Enabled=true", "--Gateway:Management:LocalChannel=false"]);
            started.Add(d);
            Assert.Equal(1, await d.WaitForExitAsync(TimeSpan.FromSeconds(180)));
            Assert.Contains("made for a runner", d.Output);
            var otherRunner = Path.Combine(root, "runner-other");
            Directory.CreateDirectory(otherRunner);
            await RealProcess.RunAsync(Programs.Runner, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "runner", "night"), "--repo", otherRunner]);
            var other = RealProcess.Start(Programs.Runner, ["server", "run", "--repo", otherRunner, "--listen", $"https://127.0.0.1:{Programs.FreePort()}"]);
            started.Add(other);
            Assert.Equal(1, await other.WaitForExitAsync(TimeSpan.FromSeconds(120)));
            Assert.Contains("not an instance of it", other.Output);
            Assert.Null(await KeyOnInstanceAsync(otherRunner, RunnerKey));
            Assert.Equal(2, registry.All().Count);

            // the keys are in no audit entry and in no output of the servers
            var audit = console.Services.GetRequiredService<DbDataKit.State.AuditLog>().Read(new DbDataKit.State.AuditQuery(VerbPrefix: "server."));
            var said = string.Join('\n', audit.Select(e => e.Entry.Detail)) + string.Join('\n', started.Select(p => p.Output));
            Assert.DoesNotContain(key!, said);
            Assert.DoesNotContain(runnerKey!, said);
        }
        finally
        {
            held?.Stop();
            if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
            foreach (var process in started) await process.DisposeAsync();
            await dropGateways();
            await dropRunners();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
