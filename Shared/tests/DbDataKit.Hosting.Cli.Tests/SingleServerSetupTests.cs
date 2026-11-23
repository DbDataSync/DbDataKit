using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Cli.Tests;

/// <summary>
/// <c>setup single-server</c> on a machine made of fakes: a service manager whose services "start" by writing what a real server writes (the
/// <c>listen:</c> line, the deleted join request, or a startup-failure file), a console that lists what joined, and a probe that answers for a running
/// service. The real programs are joined by the same code in the console's Servers suite (<c>SingleServerSetupTests</c> there).
/// </summary>
public sealed class SingleServerSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ddk-single-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly SingleServerSetupOptions Fast = new() { Wait = TimeSpan.FromSeconds(10), Poll = TimeSpan.FromMilliseconds(20), StoppedFor = TimeSpan.FromSeconds(2) };

    private static int PortOf(SuiteService service) => service.Ports.Start + (service == SuiteServices.Gateway ? 2 : 0);

    /// <summary>The console's half: a token is a file in the member's folder; a member is joined once its fake start consumed it.</summary>
    private sealed class FakeConsole : ISingleServerConsole
    {
        public HashSet<string> Joined { get; } = [];
        public List<string> Requests { get; } = [];
        public bool Certificate { get; set; }

        public bool CanMakeTokens => Certificate;

        public string WriteJoinRequest(SetupMember member)
        {
            var path = Path.Combine(member.DataFolder, "join-request.yaml");
            Directory.CreateDirectory(member.DataFolder);
            File.WriteAllText(path, "token: " + Guid.NewGuid().ToString("N"));
            Requests.Add(member.Service.Program);
            return path;
        }

        public bool IsJoined(string certificateFingerprint) => Joined.Contains(certificateFingerprint);

        public string FirstAdministrator() => "Open this link once to create the first administrator: https://localhost:7800/invite#k7d2";
    }

    /// <summary>A service manager whose services are fakes that do at their start what the real server does.</summary>
    private sealed class FakeHost(FakeConsole console, IReadOnlyList<SetupMember> members) : ISuiteServiceHost
    {
        public bool Elevated { get; set; } = true;
        public Dictionary<string, SuiteRegistration> Registered { get; } = [];
        public Dictionary<string, SuiteServiceState> States { get; } = [];
        public List<string> Calls { get; } = [];
        public HashSet<string> Failing { get; } = [];

        public bool IsElevated => Elevated;

        public RegistrationCheck Check(SuiteRegistration registration) =>
            !Registered.TryGetValue(registration.Service.Program, out var had) ? new(RegistrationState.NotRegistered)
            : had.Spec.CommandLine == registration.Spec.CommandLine ? new(RegistrationState.Same, had.Spec.CommandLine)
            : new(RegistrationState.Different, had.Spec.CommandLine);

        public void Register(SuiteRegistration registration)
        {
            Calls.Add("register " + registration.Service.Program);
            Registered[registration.Service.Program] = registration;
            States.TryAdd(registration.Service.Program, SuiteServiceState.Stopped);
        }

        public void PrepareFolders(SuiteRegistration registration)
        {
            Calls.Add("folders " + registration.Service.Program);
            Directory.CreateDirectory(registration.Member.DataFolder);
        }

        public void GiveToService(SuiteRegistration registration, string file) => Calls.Add("give " + registration.Service.Program);

        public SuiteServiceState State(SuiteService service) => States.GetValueOrDefault(service.Program, SuiteServiceState.NotRegistered);

        public void Start(SuiteService service)
        {
            Calls.Add("start " + service.Program);
            var member = members.Single(m => m.Service == service);
            var logs = SuiteServices.LogsRootFor(service, member.DataFolder);
            var failure = service.FailureFileIn(logs);
            if (Failing.Contains(service.Program))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(failure)!);
                File.WriteAllText(failure, $"The start failed at the ports step: Port {PortOf(service)} cannot be bound: another program holds it.\n");
                States[service.Program] = SuiteServiceState.Stopped;
                return;
            }
            if (File.Exists(failure)) File.Delete(failure);
            // the listen line the server pins at its first start
            var settings = Path.Combine(member.DataFolder, service.SettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, service == SuiteServices.Gateway
                ? $"management:\n  listen: https://127.0.0.1:{PortOf(service)}\n"
                : $"listen: https://127.0.0.1:{PortOf(service)}\n");
            if (service == SuiteServices.Console) console.Certificate = true;
            var request = Path.Combine(member.DataFolder, "join-request.yaml");
            if (File.Exists(request))
            {
                File.Delete(request);
                console.Joined.Add("fp-" + service.Program);
            }
            States[service.Program] = SuiteServiceState.Running;
        }

        public Task StopAsync(SuiteService service, TimeSpan timeout)
        {
            Calls.Add("stop " + service.Program);
            States[service.Program] = SuiteServiceState.Stopped;
            return Task.CompletedTask;
        }

        public Task<string?> ProbeAsync(Uri listen, CancellationToken ct)
        {
            var service = SuiteServices.All.Single(s => PortOf(s) == listen.Port);
            return Task.FromResult(States.GetValueOrDefault(service.Program) == SuiteServiceState.Running ? "fp-" + service.Program : null);
        }
    }

    private (FakeHost Host, FakeConsole Console, IReadOnlyList<SetupMember> Members, SingleServerSetup Setup) Machine()
    {
        var members = SuiteServices.All.Select(s => new SetupMember(s, Path.Combine(_root, "programs", s.DataFolder, s.Program), Path.Combine(_root, "data", s.DataFolder))).ToList();
        var console = new FakeConsole();
        var host = new FakeHost(console, members);
        return (host, console, members, new SingleServerSetup(host, console, host.ProbeAsync));
    }

    private static async Task<(int Exit, string Output)> RunAsync(SingleServerSetup setup, IReadOnlyList<SetupMember> members, SingleServerSetupOptions? options = null)
    {
        var output = new StringWriter();
        var exit = await setup.RunAsync(members, options ?? Fast, output);
        return (exit, output.ToString());
    }

    [Fact]
    public async Task A_first_run_registers_starts_and_joins_every_service_and_ends_with_the_console_and_the_setup_link()
    {
        var (host, console, members, setup) = Machine();

        var (exit, output) = await RunAsync(setup, members);

        Assert.True(exit == 0, output);
        Assert.Contains("dbdatakit        registered · started · listening\n", output.ReplaceLineEndings("\n"));
        foreach (var program in new[] { "dbdatagateway", "dbdatarunner", "dbdataworkspace" })
            Assert.Contains($"{program.PadRight(15)}  registered · started · listening · joined\n", output.ReplaceLineEndings("\n"));
        Assert.Equal(["dbdatagateway", "dbdatarunner", "dbdataworkspace"], console.Requests);
        Assert.Contains("Ready: dbdatakit, dbdatagateway, dbdatarunner, dbdataworkspace.", output);
        Assert.Contains("The console is on https://127.0.0.1:7800.", output);
        Assert.Contains("https://localhost:7800/invite#k7d2", output);
        // the console first, since the others join it; each registered and its folders set before it starts
        Assert.Equal(["register dbdatakit", "folders dbdatakit", "start dbdatakit", "register dbdatagateway", "folders dbdatagateway", "give dbdatagateway", "start dbdatagateway"], host.Calls.Take(7));
        Assert.All(members.Skip(1), m => Assert.False(File.Exists(Path.Combine(m.DataFolder, "join-request.yaml"))));
    }

    [Fact]
    public async Task A_second_run_says_there_is_nothing_to_do_and_touches_nothing()
    {
        var (host, _, members, setup) = Machine();
        Assert.Equal(0, (await RunAsync(setup, members)).Exit);
        host.Calls.Clear();

        var (exit, output) = await RunAsync(setup, members);

        Assert.True(exit == 0, output);
        Assert.Contains("Nothing to do: every service is registered, running and joined.", output);
        Assert.Contains("registered · started · listening · joined (already)", output);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_service_that_fails_prints_its_failure_file_and_is_named_and_a_run_after_the_fix_finishes_it_alone()
    {
        var (host, _, members, setup) = Machine();
        host.Failing.Add("dbdatarunner");

        var (exit, output) = await RunAsync(setup, members);

        Assert.True(exit == 1, output);
        var lines = output.ReplaceLineEndings("\n");
        Assert.Contains("dbdatarunner     registered · started\n", lines);
        Assert.Contains("(the last start failed):", output);
        Assert.Contains("Port 7840 cannot be bound", output);
        Assert.Contains("Not ready: dbdatarunner (its start failed: its startup-failure file is above).", output);
        Assert.Contains("dbdataworkspace  registered · started · listening · joined\n", lines);

        host.Failing.Clear();
        host.Calls.Clear();
        var (again, after) = await RunAsync(setup, members);

        Assert.True(again == 0, after);
        Assert.Contains("dbdatarunner     registered · started · listening · joined\n", after.ReplaceLineEndings("\n"));
        Assert.Contains("dbdataworkspace  registered · started · listening · joined (already)", after);
        Assert.DoesNotContain(host.Calls, c => c.EndsWith("dbdataworkspace", StringComparison.Ordinal) || c.EndsWith("dbdatakit", StringComparison.Ordinal));
        Assert.Contains("start dbdatarunner", host.Calls);
    }

    [Fact]
    public async Task A_service_the_service_manager_keeps_restarting_after_a_failed_start_is_stopped_and_started_by_the_run_after_the_fix()
    {
        var (host, _, members, setup) = Machine();
        host.Failing.Add("dbdatarunner");
        Assert.Equal(1, (await RunAsync(setup, members)).Exit);
        // the port is freed; meanwhile the service manager's retry (systemd's Restart=, Windows' failure actions) is under way and has just failed again
        host.Failing.Clear();
        host.States["dbdatarunner"] = SuiteServiceState.Starting;
        var runner = members.Single(m => m.Service == SuiteServices.Runner);
        await File.WriteAllTextAsync(SuiteServices.Runner.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Runner, runner.DataFolder)),
            "The start failed at the ports step: Port 7840 cannot be bound: address already in use.\n");
        host.Calls.Clear();

        var (exit, output) = await RunAsync(setup, members);

        Assert.True(exit == 0, output);
        Assert.Equal(["stop dbdatarunner", "start dbdatarunner"], host.Calls.Where(c => c.StartsWith("st", StringComparison.Ordinal)));
        Assert.Contains("dbdatarunner     registered · started · listening · joined\n", output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_stopped_service_the_console_lists_is_started_with_no_new_token_and_one_it_does_not_list_joins_again()
    {
        var (host, console, members, setup) = Machine();
        Assert.Equal(0, (await RunAsync(setup, members)).Exit);
        host.States["dbdatarunner"] = SuiteServiceState.Stopped;
        console.Joined.Remove("fp-dbdataworkspace");
        console.Requests.Clear();
        host.Calls.Clear();

        var (exit, output) = await RunAsync(setup, members);

        Assert.True(exit == 0, output);
        Assert.Equal(["dbdataworkspace"], console.Requests);
        Assert.Contains("start dbdatarunner", host.Calls);
        // the Workspace ran and listened, so it was given a request and started again to read it
        Assert.Equal(["folders dbdataworkspace", "give dbdataworkspace", "stop dbdataworkspace", "start dbdataworkspace"], host.Calls.Where(c => c.EndsWith("dbdataworkspace", StringComparison.Ordinal)));
        Assert.Contains("dbdataworkspace  registered · started · listening · joined\n", output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_skip_switch_is_put_on_each_registered_command_line_and_a_run_without_it_takes_it_off()
    {
        var (host, _, members, setup) = Machine();

        var (exit, output) = await RunAsync(setup, members, new SingleServerSetupOptions { SkipFolderPermissionsCheck = true, Wait = Fast.Wait, Poll = Fast.Poll, StoppedFor = Fast.StoppedFor });

        Assert.True(exit == 0, output);
        foreach (var registration in host.Registered.Values)
        {
            Assert.Contains(FolderPermissions.Flag, registration.Spec.Arguments);
            Assert.EndsWith(" " + FolderPermissions.Flag, registration.Unit.ExecStart);
        }

        var (_, without) = await RunAsync(setup, members);
        Assert.Contains("registered DbDataRunner again", without);
        Assert.All(host.Registered.Values, r => Assert.DoesNotContain(FolderPermissions.Flag, r.Spec.Arguments));
    }

    [Fact]
    public async Task A_run_that_is_not_elevated_changes_nothing()
    {
        var (host, _, members, setup) = Machine();
        host.Elevated = false;

        var (exit, output) = await RunAsync(setup, members);

        Assert.Equal(ExitCodes.NotElevated, exit);
        Assert.Contains("setup single-server", output);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Stop_stops_every_registered_service_the_last_in_start_order_first()
    {
        var (host, _, members, setup) = Machine();
        Assert.Equal(0, (await RunAsync(setup, members)).Exit);
        host.States["dbdatagateway"] = SuiteServiceState.Stopped;
        host.Calls.Clear();

        var output = new StringWriter();
        var exit = await setup.StopAllAsync(Fast, output);

        Assert.Equal(0, exit);
        Assert.Equal(["stop dbdataworkspace", "stop dbdatarunner", "stop dbdatakit"], host.Calls);
        Assert.Contains("dbdatagateway    stopped already", output.ToString());
        Assert.All(host.States.Values, s => Assert.Equal(SuiteServiceState.Stopped, s));
    }

    [Fact]
    public async Task A_program_not_installed_is_named_with_where_it_was_looked_for()
    {
        var (_, _, members, setup) = Machine();
        var output = new StringWriter();

        var exit = await setup.RunAsync([.. members.Where(m => m.Service != SuiteServices.Workspace)], Fast, output, missing: [(SuiteServices.Workspace, ["/opt/workspace/dbdataworkspace"])]);

        Assert.Equal(0, exit);
        Assert.Contains("dbdataworkspace  not installed beside dbdatakit (looked for /opt/workspace/dbdataworkspace)", output.ToString());
    }

    [Fact]
    public void Discovery_finds_the_programs_of_the_zips_the_Scoop_package_and_install_sh()
    {
        var file = (SuiteService s) => SuiteDiscovery.ProgramFile(s);
        var console = Path.Combine(_root, "suite", "console", file(SuiteServices.Console));
        var installed = new HashSet<string>
        {
            Path.Combine(_root, "suite", "gateway", file(SuiteServices.Gateway)),      // the zips and the Scoop package: a folder named for the service
            Path.Combine(_root, "suite", "dbdatarunner", file(SuiteServices.Runner)),  // install.sh: a folder named for the program
        };

        var (found, missing) = SuiteDiscovery.Find(console, s => Path.Combine(_root, "data", s.DataFolder), installed.Contains);

        Assert.Equal(["dbdatakit", "dbdatagateway", "dbdatarunner"], found.Select(m => m.Service.Program));
        Assert.Equal(installed.Order(), found.Skip(1).Select(m => m.Binary).Order());
        Assert.Equal(Path.Combine(_root, "data", "runner"), found[2].DataFolder);
        var (service, lookedIn) = Assert.Single(missing);
        Assert.Equal(SuiteServices.Workspace, service);
        Assert.Equal([Path.Combine(_root, "suite", "workspace", file(service)), Path.Combine(_root, "suite", "dbdataworkspace", file(service)), Path.Combine(_root, "suite", "console", file(service))], lookedIn);
    }

    [Fact]
    public void The_address_a_service_reports_is_read_from_its_own_settings()
    {
        var gateway = Path.Combine(_root, "gateway");
        Directory.CreateDirectory(Path.Combine(gateway, "config"));
        File.WriteAllText(Path.Combine(gateway, "config", "server.yaml"), "schemaVersion: 2\npublic:\n  listen: https://0.0.0.0:7820\nManagement:\n  Listen: [https://127.0.0.1:7822]\n");
        var runner = Path.Combine(_root, "runner");
        Directory.CreateDirectory(runner);
        File.WriteAllText(Path.Combine(runner, "server.yaml"), "# chosen at the first start\nlisten: https://0.0.0.0:7841\n");

        Assert.Equal(new Uri("https://127.0.0.1:7822"), SuiteListen.Address(SuiteServices.Gateway, gateway));
        Assert.Equal(new Uri("https://0.0.0.0:7841"), SuiteListen.Address(SuiteServices.Runner, runner));
        Assert.Equal(new Uri("https://127.0.0.1:7841"), SuiteListen.Local(new Uri("https://0.0.0.0:7841")));
        Assert.Null(SuiteListen.Address(SuiteServices.Workspace, Path.Combine(_root, "workspace")));
    }

    private sealed class RecordingAcl : IAclService
    {
        public List<(string Path, AclSpecification Spec)> Applied { get; } = [];
        public bool DirectoryExists(string path) => true;
        public IEnumerable<string> EnumerateFiles(string directory) => [];
        public ObservedAcl? Read(string path) => null;
        public void Apply(string directory, AclSpecification spec) => Applied.Add((directory, spec));
    }

    private sealed class Elevated : IElevationChecker
    {
        public bool IsElevated => true;
    }

    [Fact]
    public void On_Windows_a_service_is_registered_as_its_own_install_registers_it_and_its_program_folder_is_closed_to_writes()
    {
        var scm = new InMemoryServiceControlManager();
        var acl = new RecordingAcl();
        var logsWriters = new List<(string Folder, string Writer)>();
        var host = new WindowsSuiteServiceHost(scm, acl, new Elevated(), (folder, writer) => logsWriters.Add((folder, writer)));
        var member = new SetupMember(SuiteServices.Runner, @"C:\Program Files\DbDataKit\runner\dbdatarunner.exe", @"C:\ProgramData\DbDataKit\runner");
        var registration = SuiteRegistration.For(member, skipFolderPermissionsCheck: true, _ => new WellKnownPrincipalResolver("S-1-5-80-7"));

        Assert.Equal(RegistrationState.NotRegistered, host.Check(registration).State);
        host.Register(registration);
        host.PrepareFolders(registration);

        var config = scm.GetConfig("DbDataRunner")!;
        Assert.Equal(@"NT SERVICE\DbDataRunner", config.AccountName);
        Assert.Equal(SuiteServices.Runner.Description, config.Description);
        Assert.Equal(registration.Spec.CommandLine, config.BinaryPathName);
        Assert.Equal(["server", "run", "--repo", @"C:\ProgramData\DbDataKit\runner", FolderPermissions.Flag], registration.Spec.Arguments);
        Assert.Equal(RegistrationState.Same, host.Check(registration).State);
        Assert.Equal(RegistrationState.Different, host.Check(SuiteRegistration.For(member, false, _ => new WellKnownPrincipalResolver("S-1-5-80-7"))).State);

        // the top folder: owned by the Administrators group, the service may change what is in it
        var data = Assert.Single(acl.Applied, a => a.Path == member.DataFolder).Spec;
        Assert.Equal("S-1-5-32-544", data.OwnerSid);
        Assert.Contains(data.Entries, e => e.Sid == "S-1-5-80-7" && e.Rights == AccessRights.Modify);
        // the program folder: administrators and SYSTEM change it, everyone else reads and runs; nothing is inherited from the parent
        var programs = Assert.Single(acl.Applied, a => a.Path == member.ProgramFolder).Spec;
        Assert.True(programs.CutInheritance);
        Assert.Equal(AccessRights.Full, Assert.Single(programs.Entries, e => e.Sid == "S-1-5-32-544").Rights);
        Assert.Equal(AccessRights.Full, Assert.Single(programs.Entries, e => e.Sid == "S-1-5-18").Rights);
        Assert.Equal(AccessRights.ReadExecute, Assert.Single(programs.Entries, e => e.Sid == "S-1-5-32-545").Rights);
        Assert.All(programs.Entries.Where(e => e.Sid is not ("S-1-5-32-544" or "S-1-5-18")), e => Assert.Equal(AccessRights.ReadExecute, e.Rights));
        // the shared logs folder lets the service make its own folder there
        Assert.Equal([(SuiteServices.LogsRootFor(SuiteServices.Runner, member.DataFolder), "S-1-5-80-7")], logsWriters);
    }

    [Fact]
    public void With_systemd_a_service_gets_its_user_its_unit_and_its_folders()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "systemd's paths and modes are Linux's");
        var units = Path.Combine(_root, "units");
        var calls = new List<string>();
        var users = new HashSet<string>();
        CommandResult Run(string program, IReadOnlyList<string> arguments)
        {
            var line = program + " " + string.Join(' ', arguments);
            calls.Add(line);
            return line switch
            {
                _ when line.StartsWith("id -u ", StringComparison.Ordinal) => new(users.Contains(arguments[^1]) ? 0 : 1, ""),
                _ when line.StartsWith("useradd ", StringComparison.Ordinal) => Added(arguments[^1]),
                "getent group dbdatakit" => new(2, ""),
                _ when line.StartsWith("systemctl show", StringComparison.Ordinal) => new(0, "activating\n"),
                _ => new(0, ""),
            };
        }
        CommandResult Added(string user) { users.Add(user); return new(0, ""); }
        var host = new SystemdSuiteServiceHost(Run, units, () => true);
        var member = new SetupMember(SuiteServices.Gateway, "/opt/dbdatagateway/dbdatagateway", Path.Combine(_root, "var", "gateway"));
        var registration = SuiteRegistration.For(member, skipFolderPermissionsCheck: false);

        Assert.Equal(RegistrationState.NotRegistered, host.Check(registration).State);
        host.Register(registration);
        host.PrepareFolders(registration);

        // the unit the Gateway's own `service unit` prints, which install.sh --system writes
        Assert.Equal(SystemdUnitGenerator.Generate(SuiteServiceRegistration.UnitOptions(SuiteServices.Gateway, "/opt/dbdatagateway/dbdatagateway")), File.ReadAllText(host.UnitPath(SuiteServices.Gateway)));
        Assert.Contains("groupadd --system dbdatakit", calls);
        Assert.Contains(calls, c => c.StartsWith("useradd --system --gid dbdatakit --no-create-home --home-dir /nonexistent --shell ", StringComparison.Ordinal) && c.EndsWith(" dbdatagateway", StringComparison.Ordinal));
        Assert.Contains("systemctl daemon-reload", calls);
        Assert.Contains("systemctl enable --quiet dbdatagateway", calls);
        Assert.Contains($"chown dbdatagateway:dbdatakit {member.DataFolder}", calls);
        Assert.Contains("chown -R root:root /opt/dbdatagateway", calls);
        Assert.Contains("chmod -R go-w /opt/dbdatagateway", calls);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(member.DataFolder));
        Assert.Equal(RegistrationState.Same, host.Check(registration).State);
        Assert.Equal(SuiteServiceState.Starting, host.State(SuiteServices.Gateway));

        calls.Clear();
        host.Register(registration);
        Assert.DoesNotContain(calls, c => c.StartsWith("useradd", StringComparison.Ordinal));
    }
}
