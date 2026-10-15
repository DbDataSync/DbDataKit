using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;
using DbDataProxy.Server.Hosting;

namespace DbDataProxy.Tests.Hosting;

/// <summary>
/// The service verbs against an in-memory Service Control Manager: planning, arguments, passwords, accounts,
/// failure actions. The real SCM is exercised by <see cref="ServiceAdminTests"/> (needs elevation).
/// </summary>
public sealed class ServiceCliTests
{
    private const string Exe = @"C:\Program Files\DbDataProxy\DbDataProxy.exe";
    private const string DataDir = @"C:\ProgramData\DbDataProxy";

    private sealed class Elevated(bool value) : IElevationChecker
    {
        public bool IsElevated => value;
    }

    private sealed class RecordingAcl : IAclService
    {
        public List<(string Path, AclSpecification Spec)> Applied { get; } = [];

        public bool DirectoryExists(string path) => false;

        public IEnumerable<string> EnumerateFiles(string directory) => [];

        public ObservedAcl? Read(string path) => null;

        public void Apply(string directory, AclSpecification spec) => Applied.Add((directory, spec));
    }

    private sealed class Rig
    {
        public InMemoryServiceControlManager Scm { get; } = new();
        public RecordingAcl Acl { get; } = new();
        public BufferConsole Console { get; } = new();
        public Dictionary<string, string> Env { get; } = [];
        public string? Prompted { get; private set; }
        public string PromptAnswer { get; set; } = "typed-secret";
        public bool Elevated { get; set; } = true;
        public bool Windows { get; set; } = true;
        public bool CanPrompt { get; set; }

        public int Run(params string[] args) => ServiceCli.Run(args, new ServiceCliEnvironment
        {
            Scm = Scm,
            Acl = Acl,
            Elevation = new Elevated(Elevated),
            Console = Console,
            BinaryPath = Exe,
            DefaultDataDirectory = () => DataDir,
            IsWindows = Windows,
            GetEnvironmentVariable = name => Env.GetValueOrDefault(name),
            PrincipalResolver = account => new WellKnownPrincipalResolver(account.Kind == ServiceAccountKind.VirtualAccount ? ServiceSid.FromName(account.Name[(account.Name.LastIndexOf((char)92) + 1)..]) : "S-1-5-21-1-2-3-1001"),
            PromptPassword = CanPrompt ? p => { Prompted = p; return PromptAnswer; } : null,
        });

        public string Out => Console.OutWriter.ToString();
        public string Err => Console.ErrorWriter.ToString();
    }

    [Fact]
    public void Install_registers_a_virtual_account_service_with_the_previous_settings()
    {
        var rig = new Rig();
        Assert.Equal(0, rig.Run("install"));

        var cfg = rig.Scm.GetConfig("DbDataProxy")!;
        Assert.Equal(@"NT SERVICE\DbDataProxy", cfg.AccountName);
        Assert.Equal(ServiceStartMode.Automatic, cfg.StartMode); // "start= auto" before, not delayed
        Assert.Equal(3, cfg.FailureRestartActions); // restart 5s / 5s / 60s
        Assert.Equal(ServiceCli.DisplayName, cfg.DisplayName);
        Assert.Equal(ServiceCli.Description, cfg.Description);
        Assert.Equal($"\"{Exe}\"", cfg.BinaryPathName);
        Assert.False(rig.Scm.LastPasswordSupplied);
    }

    [Fact]
    public void Install_locks_the_data_directory_to_the_service_and_administrators()
    {
        var rig = new Rig();
        Assert.Equal(0, rig.Run("install"));

        var data = Assert.Single(rig.Acl.Applied, a => a.Path == DataDir).Spec;
        Assert.True(data.CutInheritance);
        string serviceSid = ServiceSid.FromName("DbDataProxy");
        Assert.Contains(data.Entries, e => e.Sid == serviceSid && e.Rights == AccessRights.Modify);
        Assert.Contains(data.Entries, e => e.Sid == WellKnownPrincipalResolver.AdministratorsSid && e.Rights == AccessRights.Full);
        Assert.Contains(data.Entries, e => e.Sid == WellKnownPrincipalResolver.SystemSid);
        Assert.DoesNotContain(data.Entries, e => WellKnownPrincipalResolver.IsBroadGroup(e.Sid));

        // Binaries: the service reads and runs them but cannot change them.
        var install = Assert.Single(rig.Acl.Applied, a => a.Path == @"C:\Program Files\DbDataProxy").Spec;
        Assert.Contains(install.Entries, e => e.Sid == serviceSid && e.Rights == AccessRights.ReadExecute);
    }

    [Fact]
    public void Data_dir_option_reaches_the_service_as_configuration_and_the_acl_target()
    {
        var rig = new Rig();
        Assert.Equal(0, rig.Run("install", "--name", "ProxyB", "--data-dir", @"D:\proxy data"));

        var cfg = rig.Scm.GetConfig("ProxyB")!;
        Assert.Equal(@"NT SERVICE\ProxyB", cfg.AccountName);
        Assert.Contains("\"--Proxy:DataDirectory=D:\\proxy data\"", cfg.BinaryPathName);
        Assert.Contains(rig.Acl.Applied, a => a.Path == @"D:\proxy data");
        Assert.DoesNotContain("--data-dir", cfg.BinaryPathName);
    }

    [Fact]
    public void Password_on_the_command_line_still_works_but_warns_and_never_reaches_the_service_definition()
    {
        var rig = new Rig();
        Assert.Equal(0, rig.Run("install", "--account", @"CORP\svc-proxy", "--password", "hunter2"));

        Assert.Contains("Warning: --password", rig.Err);
        Assert.Contains("--password-secret", rig.Err);
        Assert.True(rig.Scm.LastPasswordSupplied);
        Assert.DoesNotContain("hunter2", rig.Out + rig.Err);
        Assert.DoesNotContain("hunter2", rig.Scm.GetConfig("DbDataProxy")!.BinaryPathName);
        Assert.DoesNotContain(rig.Scm.Operations, o => o.Contains("hunter2"));
    }

    [Fact]
    public void Password_secret_reads_an_environment_variable_or_a_file_without_a_warning()
    {
        var rig = new Rig();
        rig.Env["PROXY_SVC_PW"] = "from-env";
        Assert.Equal(0, rig.Run("install", "--account", @"CORP\svc", "--password-secret", "env:PROXY_SVC_PW"));
        Assert.True(rig.Scm.LastPasswordSupplied);
        Assert.DoesNotContain("Warning", rig.Err);
        Assert.DoesNotContain("from-env", rig.Out + rig.Err);

        string file = Path.Combine(Path.GetTempPath(), "dbproxy-pw-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "from-file\r\nsecond line ignored\r\n");
        try
        {
            var fileRig = new Rig();
            Assert.Equal(0, fileRig.Run("install", "--account", @"CORP\svc", "--password-secret", "file:" + file));
            Assert.True(fileRig.Scm.LastPasswordSupplied);
            Assert.DoesNotContain("from-file", fileRig.Out + fileRig.Err);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("env:NOT_SET", "empty or not set")]
    [InlineData("vault:thing", "Unknown --password-secret scheme")]
    [InlineData("file:C:\\definitely\\not\\here.txt", "Cannot read the file")]
    public void Bad_secret_references_fail_with_a_usage_error_and_change_nothing(string reference, string message)
    {
        var rig = new Rig();
        Assert.Equal(ExitCodes.Usage, rig.Run("install", "--account", @"CORP\svc", "--password-secret", reference));
        Assert.Contains(message, rig.Err);
        Assert.Empty(rig.Scm.Operations);
    }

    [Fact]
    public void An_account_that_needs_a_password_is_prompted_for_it_when_a_terminal_is_available()
    {
        var rig = new Rig { CanPrompt = true };
        Assert.Equal(0, rig.Run("install", "--account", @"CORP\svc"));
        Assert.Equal(@"Password for CORP\svc: ", rig.Prompted);
        Assert.True(rig.Scm.LastPasswordSupplied);
        Assert.DoesNotContain("typed-secret", rig.Out + rig.Err);

        var noTerminal = new Rig();
        Assert.Equal(ExitCodes.Failure, noTerminal.Run("install", "--account", @"CORP\svc"));
        Assert.Contains("password", noTerminal.Err);
    }

    [Fact]
    public void A_virtual_or_managed_account_is_never_prompted()
    {
        var rig = new Rig { CanPrompt = true };
        Assert.Equal(0, rig.Run("install", "--account", @"CORP\gmsa-proxy$"));
        Assert.Null(rig.Prompted);
        Assert.False(rig.Scm.LastPasswordSupplied);
    }

    [Fact]
    public void An_account_that_does_not_exist_is_reported_after_registration_not_thrown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Account lookup is Windows-only.");
        var rig = new Rig();
        rig.Env["PW"] = "x";
        int rc = ServiceCli.Run(["install", "--account", @"NO_SUCH_DOMAIN_9\nobody", "--password-secret", "env:PW"], new ServiceCliEnvironment
        {
            Scm = rig.Scm,
            Acl = rig.Acl,
            Elevation = new Elevated(true),
            Console = rig.Console,
            BinaryPath = Exe,
            DefaultDataDirectory = () => DataDir,
            GetEnvironmentVariable = name => rig.Env.GetValueOrDefault(name),
        });
        Assert.Equal(ExitCodes.Failure, rc);
        Assert.Contains("directory permissions could not be applied", rig.Err);
        Assert.Contains("again", rig.Err);
    }

    [Fact]
    public void LocalSystem_needs_an_explicit_allow()
    {
        var rig = new Rig();
        Assert.Equal(ExitCodes.Failure, rig.Run("install", "--account", "LocalSystem"));
        Assert.Contains("LocalSystem", rig.Err);
        Assert.Null(rig.Scm.GetConfig("DbDataProxy"));

        Assert.Equal(0, rig.Run("install", "--account", "LocalSystem", "--allow-localsystem"));
        Assert.NotNull(rig.Scm.GetConfig("DbDataProxy"));
    }

    [Fact]
    public void Install_needs_elevation_but_a_dry_run_does_not_and_changes_nothing()
    {
        var rig = new Rig { Elevated = false };
        Assert.Equal(ExitCodes.NotElevated, rig.Run("install"));
        Assert.Empty(rig.Scm.Operations);

        Assert.Equal(0, rig.Run("install", "--dry-run"));
        Assert.Empty(rig.Scm.Operations);
        Assert.Empty(rig.Acl.Applied);
        Assert.Contains("Create service 'DbDataProxy'", rig.Out);
    }

    [Fact]
    public void Install_twice_is_idempotent_and_uninstall_removes_it()
    {
        var rig = new Rig();
        Assert.Equal(0, rig.Run("install"));
        Assert.Equal(0, rig.Run("install"));
        Assert.Equal(1, rig.Scm.Operations.Count(o => o.StartsWith("create:")));

        Assert.Equal(0, rig.Run("uninstall"));
        Assert.Null(rig.Scm.GetConfig("DbDataProxy"));
        Assert.Equal(0, rig.Run("uninstall")); // already gone is not an error
        Assert.Contains("not installed", rig.Out);
    }

    [Fact]
    public void Lifecycle_verbs_and_status_work_through_the_shared_command()
    {
        var rig = new Rig();
        rig.Run("install");
        Assert.Equal(0, rig.Run("start"));
        Assert.Equal(0, rig.Run("status"));
        Assert.Contains("Running", rig.Out);
        Assert.Equal(0, rig.Run("stop"));
        Assert.Equal(0, rig.Run("restart"));
    }

    [Fact]
    public void Doctor_reports_missing_directories_after_an_install_against_a_fake_file_system()
    {
        var rig = new Rig();
        rig.Run("install");
        Assert.Equal(ExitCodes.Failure, rig.Run("doctor")); // the recording ACL service cannot read anything back
        Assert.Contains("FAIL", rig.Out);
    }

    [Fact]
    public void Service_management_is_refused_off_windows_but_unit_generation_is_not()
    {
        var rig = new Rig { Windows = false };
        Assert.Equal(ExitCodes.Failure, rig.Run("install"));
        Assert.Contains("only available on Windows", rig.Err);
        Assert.Equal(0, rig.Run("unit"));
        Assert.Contains("ExecStart=/opt/dbdataproxy/DbDataProxy", rig.Out);
    }

    [Fact]
    public void Unknown_verbs_and_missing_option_values_show_usage()
    {
        var rig = new Rig();
        Assert.Equal(ExitCodes.Usage, rig.Run("frobnicate"));
        Assert.Contains("Usage:", rig.Err);
        Assert.Equal(ExitCodes.Usage, rig.Run("install", "--name"));
    }

    [Fact]
    public void Running_under_dotnet_exe_is_refused_because_the_service_would_register_dotnet()
    {
        var rig = new Rig();
        int rc = ServiceCli.Run(["install"], new ServiceCliEnvironment
        {
            Scm = rig.Scm,
            Acl = rig.Acl,
            Elevation = new Elevated(true),
            Console = rig.Console,
            BinaryPath = @"C:\Program Files\dotnet\dotnet.exe",
            DefaultDataDirectory = () => DataDir,
        });
        Assert.Equal(ExitCodes.Failure, rc);
        Assert.Empty(rig.Scm.Operations);
    }

    [Fact]
    public void Systemd_unit_matches_the_golden_file()
    {
        var rig = new Rig { Windows = false };
        Assert.Equal(0, rig.Run("--systemd", "--exec", "/opt/dbdataproxy/DbDataProxy"));
        string actual = rig.Out.Replace("\r\n", "\n");

        string golden = Path.Combine(AppContext.BaseDirectory, "Hosting", "Golden", "dbdataproxy.service");
        string source = Path.Combine(Support.WorkerBuild.RepositoryRoot(), "tests", "DbDataProxy.Tests", "Hosting", "Golden", "dbdataproxy.service");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") File.WriteAllText(source, actual);
        Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n"), actual);
    }

    [Fact]
    public void Systemd_unit_is_hardened_and_runs_as_a_dynamic_user()
    {
        var rig = new Rig { Windows = false };
        rig.Run("unit", "--name", "ProxyB", "--exec", "/srv/proxy/DbDataProxy");
        Assert.Contains("DynamicUser=yes", rig.Out);
        Assert.Contains("NoNewPrivileges=yes", rig.Out);
        Assert.Contains("StateDirectory=proxyb", rig.Out);
        Assert.Contains("DBPROXY_Proxy__DataDirectory=/var/lib/proxyb", rig.Out);
        Assert.DoesNotContain("User=root", rig.Out);

        var relative = new Rig { Windows = false };
        Assert.Equal(ExitCodes.Usage, relative.Run("unit", "--exec", "relative/path"));
        Assert.Contains("absolute", relative.Err);
    }
}
