using System.Net;
using System.Net.Sockets;
using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// <c>dbdatakit setup single-server</c> with the real programs: the console, a Gateway, a Runner and a Workspace started from their built output by a
/// service manager that runs each registered command line as a process of this test (the Windows service manager and systemd legs are the
/// <c>suite-release.yml</c> dry run's). One run registers, starts and joins the four, and the console lists the three servers; a second run has nothing
/// to do; a Runner whose configured port another program holds stops at its start, its startup-failure file is printed and the summary names it; a run
/// after the port is freed finishes it and leaves the others alone; and a run with <c>--skip-folder-permissions-check</c> puts the switch on every
/// registered command line.
/// </summary>
public sealed class SingleServerSetupTests
{
    private static string Built(string project, string dll)
    {
        var configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;
        var path = Path.Combine(Programs.RepositoryRoot, project, "bin", configuration, "net10.0", dll);
        Assert.True(File.Exists(path), $"{path} is not built. Build it first: dotnet build {project} -c {configuration}");
        return path;
    }

    /// <summary>A service manager whose services are processes of this test, each started with the command line it was registered with.</summary>
    private sealed class ProcessHost : ISuiteServiceHost, IAsyncDisposable
    {
        public Dictionary<string, SuiteRegistration> Registered { get; } = [];
        public Dictionary<string, RealProcess> Running { get; } = [];
        public List<string> Calls { get; } = [];

        public bool IsElevated => true;

        public RegistrationCheck Check(SuiteRegistration registration) =>
            !Registered.TryGetValue(registration.Service.Program, out var had) ? new(RegistrationState.NotRegistered)
            : had.Spec.CommandLine == registration.Spec.CommandLine ? new(RegistrationState.Same, had.Spec.CommandLine)
            : new(RegistrationState.Different, had.Spec.CommandLine);

        public void Register(SuiteRegistration registration)
        {
            Calls.Add("register " + registration.Service.Program);
            Registered[registration.Service.Program] = registration;
        }

        public void PrepareFolders(SuiteRegistration registration) => Directory.CreateDirectory(registration.Member.DataFolder);

        public void GiveToService(SuiteRegistration registration, string file) { }

        public SuiteServiceState State(SuiteService service) =>
            !Registered.ContainsKey(service.Program) ? SuiteServiceState.NotRegistered
            : Running.TryGetValue(service.Program, out var process) && !process.HasExited ? SuiteServiceState.Running
            : SuiteServiceState.Stopped;

        public void Start(SuiteService service)
        {
            Calls.Add("start " + service.Program);
            var registration = Registered[service.Program];
            Running[service.Program] = RealProcess.Start(registration.Member.Binary, registration.Spec.Arguments);
        }

        public async Task StopAsync(SuiteService service, TimeSpan timeout)
        {
            Calls.Add("stop " + service.Program);
            if (!Running.Remove(service.Program, out var process)) return;
            await process.KillAsync(timeout);
            await process.DisposeAsync();
        }

        public string OutputOf(SuiteService service) => Running.TryGetValue(service.Program, out var p) ? p.Output : "(not running)";

        public async ValueTask DisposeAsync()
        {
            foreach (var process in Running.Values) await process.DisposeAsync();
        }
    }

    private static async Task<(int Exit, string Output)> SetUpAsync(SingleServerSetup setup, IReadOnlyList<SetupMember> members, bool skip = false)
    {
        var output = new StringWriter();
        var exit = await setup.RunAsync(members, new SingleServerSetupOptions { SkipFolderPermissionsCheck = skip, Wait = TimeSpan.FromSeconds(120) }, output);
        TestContext.Current.TestOutputHelper?.WriteLine($"setup single-server{(skip ? " --skip-folder-permissions-check" : "")} exited {exit}:\n{output}");
        return (exit, output.ToString());
    }

    [Fact]
    public async Task One_run_registers_starts_and_joins_the_four_services_a_second_has_nothing_to_do_and_a_failed_start_is_named_and_finished_later()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-single-server-" + Guid.NewGuid().ToString("N")[..8]);
        var console = Built("DbDataKit/src/DbDataKit.Server", "dbdatakit.dll");
        var programs = new Dictionary<SuiteService, string>
        {
            [SuiteServices.Console] = console,
            [SuiteServices.Gateway] = Programs.Gateway,
            [SuiteServices.Runner] = Programs.Runner,
            [SuiteServices.Workspace] = Built("DbDataWorkspace/src/DbDataWorkspace.Server", "dbdataworkspace.dll"),
        };
        var members = SuiteServices.All.Select(s => new SetupMember(s, programs[s], Path.Combine(root, s.DataFolder), NamedDataFolder: true)).ToList();

        // each service's address configured in its own settings, as a person or a compose file sets it: a port of this test's own, never the suite's
        // ports another test or program on this machine may hold
        var ports = SuiteServices.All.ToDictionary(s => s, _ => Programs.FreePort());
        foreach (var member in members)
        {
            var settings = Path.Combine(member.DataFolder, member.Service.SettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            await File.WriteAllTextAsync(settings, member.Service == SuiteServices.Gateway
                ? $"schemaVersion: 2\nlisten: https://127.0.0.1:{Programs.FreePort()}\npublic: {{ listen: https://127.0.0.1:{Programs.FreePort()} }}\nmanagement: {{ enabled: true, listen: https://127.0.0.1:{ports[member.Service]}, localChannel: false }}\n"
                : $"listen: https://127.0.0.1:{ports[member.Service]}\n");
        }

        await using var host = new ProcessHost();
        var consoleFolder = members[0].DataFolder;
        var setup = new SingleServerSetup(host, new ConsoleSingleServer(consoleFolder));
        try
        {
            // one run: the four reach their last stage, the console lists the three servers, the summary has the console's address and the setup link
            var (exit, output) = await SetUpAsync(setup, members);
            Assert.True(exit == 0, Diagnose(output, host));
            var lines = output.ReplaceLineEndings("\n");
            Assert.Contains("dbdatakit        registered · started · listening\n", lines);
            foreach (var program in new[] { "dbdatagateway", "dbdatarunner", "dbdataworkspace" })
                Assert.Contains($"{program.PadRight(15)}  registered · started · listening · joined\n", lines);
            Assert.Contains($"The console is on https://127.0.0.1:{ports[SuiteServices.Console]}.", output);
            Assert.Matches(@"https://\S+/invite#\S+", output);
            var registry = new InstanceRegistry(Path.Combine(consoleFolder, "instances.yaml"));
            Assert.Equal(
                new[] { SuiteServices.Gateway, SuiteServices.Runner, SuiteServices.Workspace }.Select(s => $"https://127.0.0.1:{ports[s]}/").Order(),
                registry.All().Select(i => i.Url.AbsoluteUri).Order());
            Assert.All(members.Skip(1), m => Assert.False(File.Exists(ServerJoin.RequestPath(m.DataFolder)), $"{m.Service.Program} consumed its join request"));

            // a second run: nothing to do, nothing restarted
            host.Calls.Clear();
            (exit, output) = await SetUpAsync(setup, members);
            Assert.True(exit == 0, Diagnose(output, host));
            Assert.Contains("Nothing to do: every service is registered, running and joined.", output);
            Assert.Empty(host.Calls);

            // the Runner's configured port held by another program: its start fails at the ports step, the file is printed, the summary names it
            await host.StopAsync(SuiteServices.Runner, TimeSpan.FromSeconds(30));
            // the stopped Runner's connections may still be closing: the port is taken as soon as the system lets it go
            var holder = new TcpListener(IPAddress.Loopback, ports[SuiteServices.Runner]);
            holder.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            holder.Start();
            try
            {
                (exit, output) = await SetUpAsync(setup, members);
                Assert.True(exit == 1, Diagnose(output, host));
                Assert.Contains("dbdatarunner     registered · started\n", output.ReplaceLineEndings("\n"));
                Assert.Contains("(the last start failed):", output);
                Assert.Contains("ports step", output);
                Assert.Contains(ports[SuiteServices.Runner].ToString(System.Globalization.CultureInfo.InvariantCulture), output);
                Assert.Contains("Not ready: dbdatarunner (its start failed", output);
                Assert.Contains("dbdataworkspace  registered · started · listening · joined (already)", output);
            }
            finally
            {
                holder.Stop();
            }

            // a run after the fix finishes the Runner, which is still joined, and leaves the others alone
            host.Calls.Clear();
            (exit, output) = await SetUpAsync(setup, members);
            Assert.True(exit == 0, Diagnose(output, host));
            Assert.Contains("dbdatarunner     registered · started · listening · joined\n", output.ReplaceLineEndings("\n"));
            Assert.Equal(["start dbdatarunner"], host.Calls);
            Assert.Equal(3, registry.All().Count);

            // the switch: every service registered again with it on its command line, started again and still joined
            (exit, output) = await SetUpAsync(setup, members, skip: true);
            Assert.True(exit == 0, Diagnose(output, host));
            Assert.All(host.Registered.Values, r => Assert.Contains(FolderPermissions.Flag, r.Spec.Arguments));
            Assert.Contains("registered DbDataRunner again", output);
            Assert.Equal(3, registry.All().Count);
        }
        finally
        {
            await host.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Diagnose(string output, ProcessHost host) =>
        output + "\n" + string.Join("\n", SuiteServices.All.Select(s => $"--- {s.Program}\n{host.OutputOf(s)}"));
}
