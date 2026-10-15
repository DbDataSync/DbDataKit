using DbData.Hosting.Certificates;
using DbData.Hosting.Systemd;
using DbData.Hosting.WindowsService;

namespace DbData.Hosting.Cli.Tests;

public class ServiceCommandTests
{
    private sealed class Elevated(bool value) : IElevationChecker
    {
        public bool IsElevated => value;
    }

    private sealed class EmptyFs : IFileSystemAclReader
    {
        public bool DirectoryExists(string path) => false;

        public IEnumerable<string> EnumerateFiles(string directory) => [];

        public ObservedAcl? ReadAcl(string path) => null;
    }

    private sealed class RecordingAcl : IAclPolicy
    {
        public List<LayoutRole> Applied { get; } = [];

        public void Apply(LayoutEntry entry, IPrincipalResolver principals) => Applied.Add(entry.Role);
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
        return (new ServiceCommand(ctx, scm, new EmptyFs(), new Elevated(elevated), new InMemoryEventLogSourceRegistrar(), acl, _ => Task.CompletedTask), scm, acl);
    }

    [Fact]
    public void Install_defaults_to_virtual_account_and_applies_directory_acls()
    {
        var (cmd, scm, acl) = Make();
        var console = new BufferConsole();
        Assert.Equal(0, cmd.Execute(["install"], console));
        Assert.Equal(@"NT SERVICE\Prod", scm.GetConfig("Prod")!.AccountName);
        Assert.Contains(LayoutRole.Secrets, acl.Applied);
        Assert.DoesNotContain(LayoutRole.Current, acl.Applied);
        Assert.Contains("installed", console.OutWriter.ToString());
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
        Assert.Empty(acl.Applied);
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
        Assert.Contains("elevated", ElevationDetector.Message("x"), StringComparison.OrdinalIgnoreCase);
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
