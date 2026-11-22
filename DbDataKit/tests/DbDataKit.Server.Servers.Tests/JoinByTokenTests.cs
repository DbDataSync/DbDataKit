using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Servers join the console by the command it prints, with the real programs: a console in this process (its state on SQLite, SQL Server or PostgreSQL,
/// where the join tokens are rows of the <c>trust</c> module), and a Gateway, a Runner and a Workspace started from their built output. Each runs
/// <c>pair</c> with the command <c>dbdatakit setup invite</c> printed and then starts; it joins at its start, the console lists it at the address and with
/// the certificate it reported, and the console's calls to it succeed through that pin. The same token used again is refused, and the server's start
/// fails at the join step saying so.
/// </summary>
public sealed class JoinByTokenTests
{
    public static TheoryData<string> Engines => ["sqlite", "mssql", "postgres"];

    private static string Workspace
    {
        get
        {
            var path = Path.Combine(Programs.RepositoryRoot, "DbDataWorkspace/src/DbDataWorkspace.Server", "bin", new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name, "net10.0", "dbdataworkspace.dll");
            Assert.True(File.Exists(path), $"{path} is not built. Build it first: dotnet build DbDataWorkspace/src/DbDataWorkspace.Server");
            return path;
        }
    }

    /// <summary>What <c>setup invite</c> printed: the arguments of the <c>pair</c> command after the program's name.</summary>
    private static async Task<string[]> InviteAsync(NounRouter console, string directory, Uri consoleUrl, string product)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await console.RunAsync(["setup", "invite", product, "--url", consoleUrl.GetLeftPart(UriPartial.Authority), "--repo", directory], new CliContext(output, error, TextReader.Null));
        Assert.True(exit == 0, $"setup invite {product} exited {exit}:\n{output}\n{error}");
        var line = output.ToString().Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith(ConsoleJoins.Products[product] + " pair ", StringComparison.Ordinal));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
    }

    private static async Task<RegisteredInstance> ListedAsync(InstanceRegistry registry, Func<RegisteredInstance, bool> which, RealProcess process)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < until)
        {
            if (registry.All().FirstOrDefault(which) is { } found) return found;
            await Task.Delay(250);
        }
        Assert.Fail($"The server was not listed by the console within 90 s:\n{process.Output}");
        return null!;
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_Gateway_a_Runner_and_a_Workspace_join_by_their_printed_commands_and_a_reused_token_is_refused(string engine)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-joins-" + Guid.NewGuid().ToString("N")[..8]);
        Func<Task> drop = () => Task.CompletedTask;
        StateDatabase? stateOnEngine = null;
        var environment = new Dictionary<string, string>();
        if (engine != "sqlite")
        {
            (var connection, drop) = await ConsoleJoinsRunnerGroupTests.ProvisionAsync(engine);
            environment["DBDATAKIT_STATE_SERVER_ENGINE"] = engine;
            environment["DBDATAKIT_STATE_SERVER_CONNECTION"] = connection;
        }

        var started = new List<RealProcess>();
        WebApplication? console = null;
        try
        {
            var consoleDirectory = Path.Combine(root, "console");
            var state = engine == "sqlite" ? null : new StateSettings(engine, environment["DBDATAKIT_STATE_SERVER_CONNECTION"]);
            console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0"), State: state), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
            await console.StartAsync();
            var consoleUrl = new Uri(new Uri(console.Urls.First()).GetLeftPart(UriPartial.Authority).Replace("localhost", "127.0.0.1", StringComparison.Ordinal));
            stateOnEngine = console.Services.GetRequiredService<StateDatabase>();
            Assert.Equal(engine == "sqlite" ? StateEngineIds.Sqlite : engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, stateOnEngine.Dialect.Engine);
            var cli = ConsoleCli.Router(name => environment.GetValueOrDefault(name) ?? Environment.GetEnvironmentVariable(name));
            var registry = console.Services.GetRequiredService<InstanceRegistry>();
            var instances = console.Services.GetRequiredService<ConsoleInstances>();

            // the Runner: pair, then start; it joins at its start
            var runnerDirectory = Path.Combine(root, "runner");
            Directory.CreateDirectory(runnerDirectory);
            var runnerPair = await InviteAsync(cli, consoleDirectory, consoleUrl, "runner");
            var said = await RealProcess.RunAsync(Programs.Runner, [.. runnerPair, "--repo", runnerDirectory]);
            Assert.Contains("Start the server to join: dbdatarunner server run --repo", said);
            var runnerPort = Programs.FreePort();
            var runner = RealProcess.Start(Programs.Runner, ["server", "run", "--repo", runnerDirectory, "--listen", $"https://127.0.0.1:{runnerPort}"]);
            started.Add(runner);
            var runnerListed = await ListedAsync(registry, i => i.Url.Port == runnerPort, runner);
            // the console lists the server before it answers the join; the server deletes its request once it has the answer
            for (var wait = 0; wait < 120 && File.Exists(Path.Combine(runnerDirectory, JoinRequestFile.FileName)); wait++) await Task.Delay(250);
            Assert.False(File.Exists(Path.Combine(runnerDirectory, JoinRequestFile.FileName)));

            // the Workspace
            var workspaceDirectory = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceDirectory);
            await RealProcess.RunAsync(Workspace, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "workspace"), "--repo", workspaceDirectory]);
            var workspacePort = Programs.FreePort();
            var workspace = RealProcess.Start(Workspace, ["server", "run", "--repo", workspaceDirectory, "--listen", $"https://127.0.0.1:{workspacePort}"]);
            started.Add(workspace);
            var workspaceListed = await ListedAsync(registry, i => i.Url.Port == workspacePort, workspace);

            // the Gateway: the console calls it back on its management listener
            var gatewayDirectory = Path.Combine(root, "gateway");
            Directory.CreateDirectory(Path.Combine(gatewayDirectory, "config", "gateway"));
            File.WriteAllText(Path.Combine(gatewayDirectory, "config", "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
            File.WriteAllText(Path.Combine(gatewayDirectory, "config", "gateway", "auth.yaml"), "jwt:\n  signingKey: servers-e2e-signing-key-0123456789abcdef\n  audience: api://servers-e2e\n");
            await RealProcess.RunAsync(Programs.Gateway, [.. await InviteAsync(cli, consoleDirectory, consoleUrl, "gateway"), "--repo", gatewayDirectory]);
            var management = Programs.FreePort();
            var gateway = RealProcess.Start(Programs.Gateway, ["server", "run", $"--Gateway:DataDirectory={gatewayDirectory}", $"--Gateway:Listen=https://127.0.0.1:{Programs.FreePort()}",
                "--Gateway:Management:Enabled=true", $"--Gateway:Management:Listen=https://127.0.0.1:{management}", "--Gateway:Management:LocalChannel=false"]);
            started.Add(gateway);
            var gatewayListed = await ListedAsync(registry, i => i.Url.Port == management, gateway);

            // each listed with the address and certificate it reported, and the console's calls succeed through that pin
            foreach (var (listed, product) in new[] { (runnerListed, "dbdatarunner"), (workspaceListed, "dbdataworkspace"), (gatewayListed, "dbdatagateway") })
            {
                Assert.Equal("127.0.0.1", listed.Url.Host);
                Assert.Matches("^[0-9a-f]{64}$", listed.CertificateFingerprint);
                Assert.True(ServerRoles.Allows(listed.Ceiling, ServerRole.Admin));
                var status = await instances.StatusAsync(listed);
                Assert.True(status.Reachable, $"{product}: {status.Problem}");
                Assert.True(status.Ready, product);
                Assert.Equal(product, status.Product);
            }
            Assert.Equal(PairingProof.CertificateFingerprint(DbDataKit.Hosting.Certificates.DirectoryCertificate.Load(runnerDirectory)), runnerListed.CertificateFingerprint);

            // the Runner's command again, on another machine's folder: the token was used, so that start fails at the join step and says why
            var secondDirectory = Path.Combine(root, "runner-2");
            Directory.CreateDirectory(secondDirectory);
            await RealProcess.RunAsync(Programs.Runner, [.. runnerPair, "--repo", secondDirectory]);
            var second = RealProcess.Start(Programs.Runner, ["server", "run", "--repo", secondDirectory, "--listen", $"https://127.0.0.1:{Programs.FreePort()}"]);
            started.Add(second);
            var exited = await second.WaitForExitAsync(TimeSpan.FromSeconds(90));
            Assert.True(exited == 1, $"the second Runner exited {exited}:\n{second.Output}");
            Assert.Contains("join step", second.Output);
            Assert.Contains("already used", second.Output);
            var failure = File.ReadAllText(Path.Combine(secondDirectory, "logs", "startup-failure-dbdatarunner.txt"));
            Assert.Contains("The join token was already used", failure);
            Assert.True(File.Exists(Path.Combine(secondDirectory, JoinRequestFile.FileName)));
            Assert.Equal(3, registry.All().Count);

            // the joins and the refusal are audited with the token's id, never its value
            var token = runnerPair[Array.IndexOf(runnerPair, "--token") + 1];
            var audit = console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(VerbPrefix: "server."));
            Assert.Equal(3, audit.Count(e => e.Entry.Verb == JoinService.AuditCommand && e.Entry.Outcome == AuditEvent.Ok));
            Assert.Contains(audit, e => e.Entry.Verb == JoinService.AuditCommand && e.Entry.Outcome == AuditEvent.Refused);
            Assert.Equal(3, audit.Count(e => e.Entry.Verb == ConsoleJoins.InviteVerb));
            Assert.DoesNotContain(token, string.Join('\n', audit.Select(e => e.Entry.Detail)));
            Assert.All(console.Services.GetRequiredService<ConsoleJoins>().Tokens.All(), t => Assert.NotNull(t.SpentAt));
        }
        finally
        {
            if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
            foreach (var process in started) await process.DisposeAsync();
            await drop();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
