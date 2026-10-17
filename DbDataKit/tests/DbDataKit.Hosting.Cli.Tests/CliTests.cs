using DbDataKit.Security.Acl;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;

namespace DbDataKit.Hosting.Cli.Tests;

public class ServiceCommandTests
{
    private sealed class Elevated(bool value) : IElevationChecker
    {
        public bool IsElevated => value;
    }

    private sealed class RecordingAcl : IAclService
    {
        public List<string> AppliedPaths { get; } = [];

        public bool DirectoryExists(string path) => false;

        public IEnumerable<string> EnumerateFiles(string directory) => [];

        public ObservedAcl? Read(string path) => null;

        public void Apply(string directory, AclSpecification spec) => AppliedPaths.Add(directory);
    }

    private static (ServiceCommand Cmd, InMemoryServiceControlManager Scm, RecordingAcl Acl) Make(bool elevated = true)
    {
        var scm = new InMemoryServiceControlManager();
        var acl = new RecordingAcl();
        var ctx = new ServiceCommandContext
        {
            ServiceName = "Prod",
            BinaryPath = @"C:\Program Files\Prod\current\Prod.exe",
            Layout = DirectoryLayout.ForProduct("Prod", @"C:\PF", @"C:\PD"),
            PrincipalResolver = _ => new WellKnownPrincipalResolver("S-1-5-80-1"),
            SystemdUnit = () => new SystemdUnitOptions { Name = "prod", ExecStart = "/opt/prod/prod" },
            EventLogSource = "Prod",
            Version = "2.0.0",
        };
        return (new ServiceCommand(ctx, scm, acl, new Elevated(elevated), new InMemoryEventLogSourceRegistrar(), true, _ => Task.CompletedTask), scm, acl);
    }

    [Fact]
    public void Install_defaults_to_virtual_account_and_applies_directory_acls()
    {
        var (cmd, scm, acl) = Make();
        var console = new BufferConsole();
        Assert.Equal(0, cmd.Execute(["install"], console));
        Assert.Equal(@"NT SERVICE\Prod", scm.GetConfig("Prod")!.AccountName);
        var layout = DirectoryLayout.ForProduct("Prod", @"C:\PF", @"C:\PD");
        Assert.Contains(layout[LayoutRole.Secrets].Path, acl.AppliedPaths);
        Assert.DoesNotContain(layout[LayoutRole.Current].Path, acl.AppliedPaths);
        Assert.Contains("installed", console.OutWriter.ToString());
    }

    [Fact]
    public void Context_start_mode_failure_policy_and_data_dir_mapping_are_applied()
    {
        var scm = new InMemoryServiceControlManager();
        var ctx = new ServiceCommandContext
        {
            ServiceName = "Prod",
            BinaryPath = @"C:\Program Files\Prod\Prod.exe",
            Layout = DirectoryLayout.ForProduct("Prod", @"C:\PF", @"C:\PD"),
            PrincipalResolver = _ => new WellKnownPrincipalResolver("S-1-5-80-1"),
            StartMode = ServiceStartMode.Automatic,
            Failure = new FailurePolicy([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], TimeSpan.FromDays(1)),
            DataDirectoryArgument = path => [$"--Prod:DataDirectory={path}"],
        };
        var cmd = new ServiceCommand(ctx, scm, new RecordingAcl(), new Elevated(true), null, false, _ => Task.CompletedTask);
        Assert.Equal(0, cmd.Execute(["install", "--data-dir", @"D:\data"], new BufferConsole()));
        var cfg = scm.GetConfig("Prod")!;
        Assert.Equal(ServiceStartMode.Automatic, cfg.StartMode);
        Assert.Equal(2, cfg.FailureRestartActions);
        Assert.Contains(@"--Prod:DataDirectory=D:\data", cfg.BinaryPathName);
        Assert.DoesNotContain("--data-dir", cfg.BinaryPathName);
    }

    [Fact]
    public void Failing_to_apply_directory_permissions_is_reported_not_thrown()
    {
        var scm = new InMemoryServiceControlManager();
        var ctx = new ServiceCommandContext
        {
            ServiceName = "Prod",
            BinaryPath = @"C:\Prod\Prod.exe",
            Layout = DirectoryLayout.ForProduct("Prod", @"C:\PF", @"C:\PD"),
            PrincipalResolver = _ => throw new ArgumentException("no such account"),
        };
        var cmd = new ServiceCommand(ctx, scm, new RecordingAcl(), new Elevated(true), null, true, _ => Task.CompletedTask);
        var console = new BufferConsole();
        Assert.Equal(ExitCodes.Failure, cmd.Execute(["install"], console));
        Assert.NotNull(scm.GetConfig("Prod")); // registered
        Assert.Contains("run 'service install' again", console.ErrorWriter.ToString());
    }

    [Fact]
    public void Install_without_elevation_is_refused_but_dry_run_is_fine()
    {
        var (cmd, scm, _) = Make(elevated: false);
        var console = new BufferConsole();
        Assert.Equal(ExitCodes.NotElevated, cmd.Execute(["install"], console));
        Assert.Empty(scm.Operations);
        Assert.Equal(0, cmd.Execute(["install", "--dry-run"], new BufferConsole()));
        Assert.Empty(scm.Operations);
    }

    [Fact]
    public void Dry_run_prints_plan_and_changes_nothing()
    {
        var (cmd, scm, acl) = Make();
        var console = new BufferConsole();
        Assert.Equal(0, cmd.Execute(["install", "--dry-run"], console));
        Assert.Empty(scm.Operations);
        Assert.Empty(acl.AppliedPaths);
        Assert.Contains("create", console.OutWriter.ToString());
    }

    [Fact]
    public void Password_on_the_command_line_is_rejected()
    {
        var (cmd, scm, _) = Make();
        var console = new BufferConsole();
        Assert.Equal(ExitCodes.Usage, cmd.Execute(["install", "--account", @"CORP\alice", "--password", "hunter2"], console));
        Assert.Empty(scm.Operations);
        Assert.DoesNotContain("hunter2", console.OutWriter + console.ErrorWriter.ToString());
    }

    [Fact]
    public void Password_from_stdin_is_used_and_never_echoed()
    {
        var (cmd, scm, _) = Make();
        var console = new BufferConsole("hunter2");
        Assert.Equal(0, cmd.Execute(["install", "--account", @"CORP\alice", "--password-stdin"], console));
        Assert.True(scm.LastPasswordSupplied);
        Assert.DoesNotContain("hunter2", console.OutWriter.ToString() + console.ErrorWriter);
    }

    [Fact]
    public void Explicit_account_without_password_fails()
    {
        var (cmd, _, _) = Make();
        var console = new BufferConsole();
        Assert.Equal(ExitCodes.Failure, cmd.Execute(["install", "--account", @"CORP\alice"], console));
        Assert.Contains("password", console.ErrorWriter.ToString());
    }

    [Fact]
    public void LocalSystem_needs_the_explicit_flag()
    {
        var (cmd, scm, _) = Make();
        Assert.Equal(ExitCodes.Failure, cmd.Execute(["install", "--account", "LocalSystem"], new BufferConsole()));
        Assert.Equal(0, cmd.Execute(["install", "--account", "LocalSystem", "--allow-localsystem"], new BufferConsole()));
        Assert.NotNull(scm.GetConfig("Prod"));
    }

    [Fact]
    public void Start_stop_restart_status_uninstall_lifecycle()
    {
        var (cmd, scm, _) = Make();
        cmd.Execute(["install"], new BufferConsole());
        Assert.Equal(0, cmd.Execute(["start"], new BufferConsole()));
        var status = new BufferConsole();
        Assert.Equal(0, cmd.Execute(["status"], status));
        Assert.Contains("Running", status.OutWriter.ToString());
        Assert.Equal(0, cmd.Execute(["restart"], new BufferConsole()));
        Assert.Equal(0, cmd.Execute(["uninstall"], new BufferConsole()));
        Assert.Null(scm.GetConfig("Prod"));
        Assert.Equal(ExitCodes.Failure, cmd.Execute(["status"], new BufferConsole()));
    }

    [Fact]
    public void Doctor_reports_failures_when_directories_are_missing()
    {
        var (cmd, _, _) = Make();
        cmd.Execute(["install"], new BufferConsole());
        var console = new BufferConsole();
        Assert.Equal(ExitCodes.Failure, cmd.Execute(["doctor"], console));
        Assert.Contains("FAIL", console.OutWriter.ToString());
    }

    [Fact]
    public void Unit_prints_systemd_unit_and_unknown_verbs_show_usage()
    {
        var (cmd, _, _) = Make();
        var console = new BufferConsole();
        Assert.Equal(0, cmd.Execute(["unit"], console));
        Assert.Contains("NoNewPrivileges=yes", console.OutWriter.ToString());
        Assert.Equal(ExitCodes.Usage, cmd.Execute(["bogus"], new BufferConsole()));
        Assert.Equal(ExitCodes.Usage, cmd.Execute([], new BufferConsole()));
    }

    [Fact]
    public void Elevation_detector_reports_without_throwing() =>
        // Windows asks for an elevated prompt; elsewhere the advice is to use sudo.
        Assert.Contains(OperatingSystem.IsWindows() ? "elevated" : "root", ElevationDetector.Message("x"), StringComparison.OrdinalIgnoreCase);
}

public class CertCommandTests
{
    private static (CertCommand Cmd, string Dir, CertificateOptions Options) Make(TimeProvider? time = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbdata-cli-" + Guid.NewGuid().ToString("N"));
        var options = new CertificateOptions { Mode = CertificateMode.SelfSigned, Path = Path.Combine(dir, "s.pfx") };
        var secrets = new InMemorySecretSource();
        var provider = new CertificateProvider(secrets, time: time, fqdnLookup: _ => null);
        return (new CertCommand(options, secrets, provider, time), dir, options);
    }

    [Fact]
    public void Status_does_not_create_and_ensure_does()
    {
        var (cmd, dir, options) = Make();
        try
        {
            var c = new BufferConsole();
            Assert.Equal(ExitCodes.Failure, cmd.Execute(["status"], c));
            Assert.False(File.Exists(options.Path));

            Assert.Equal(0, cmd.Execute(["ensure"], c));
            Assert.True(File.Exists(options.Path));
            Assert.True(File.Exists(Path.ChangeExtension(options.Path!, ".cer")));

            var status = new BufferConsole();
            Assert.Equal(0, cmd.Execute(["status"], status));
            Assert.Contains("Thumbprint", status.OutWriter.ToString());
            Assert.Equal(0, cmd.Execute(["check"], new BufferConsole()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Check_fails_when_rotation_is_due()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbdata-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secrets = new InMemorySecretSource();
            var options = new CertificateOptions { Mode = CertificateMode.SelfSigned, Path = Path.Combine(dir, "s.pfx") };
            var now = new CertCommand(options, secrets, new CertificateProvider(secrets, fqdnLookup: _ => null));
            Assert.Equal(0, now.Execute(["ensure"], new BufferConsole()));

            var late = new FixedTime(DateTimeOffset.UtcNow.AddDays(350));
            var later = new CertCommand(options, secrets, new CertificateProvider(secrets, time: late), late);
            var console = new BufferConsole();
            Assert.Equal(ExitCodes.Failure, later.Execute(["check"], console));
            Assert.Contains("ExpiringSoon", console.OutWriter.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Show_is_status_and_renew_replaces_the_certificate_now()
    {
        var (cmd, dir, options) = Make();
        try
        {
            Assert.Equal(0, cmd.Execute(["ensure"], new BufferConsole()));
            var shown = new BufferConsole();
            Assert.Equal(0, cmd.Execute(["show"], shown));
            var before = shown.OutWriter.ToString();

            var renewed = new BufferConsole();
            Assert.Equal(0, cmd.Execute(["renew"], renewed));
            Assert.Contains("renewed", renewed.OutWriter.ToString());

            var after = new BufferConsole();
            Assert.Equal(0, cmd.Execute(["show"], after));
            Assert.NotEqual(before, after.OutWriter.ToString()); // new thumbprint and expiry
            Assert.Contains("Thumbprint", after.OutWriter.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Renew_is_refused_for_non_self_signed_modes()
    {
        var secrets = new InMemorySecretSource();
        var cmd = new CertCommand(new CertificateOptions { Mode = CertificateMode.Store, Thumbprint = "AB" }, secrets, new CertificateProvider(secrets));
        Assert.Equal(ExitCodes.Usage, cmd.Execute(["renew"], new BufferConsole()));
    }

    [Fact]
    public void Status_reports_a_certificate_that_cannot_be_opened_instead_of_crashing()
    {
        var (_, dir, options) = Make();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(options.Path!, "not a pfx");
            var secrets = new InMemorySecretSource();
            var cmd = new CertCommand(options, secrets, new CertificateProvider(secrets, fqdnLookup: _ => null));
            var console = new BufferConsole();
            Assert.Equal(ExitCodes.Failure, cmd.Execute(["show"], console));
            Assert.Contains("cannot be opened", console.OutWriter.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Unknown_verb_shows_usage()
    {
        var (cmd, _, _) = Make();
        Assert.Equal(ExitCodes.Usage, cmd.Execute(["nope"], new BufferConsole()));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
