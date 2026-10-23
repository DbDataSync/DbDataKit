using DbDataKit.Hosting.WindowsService;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>service install|uninstall|status|start|stop|restart|doctor</c> on Windows, run on any OS against an in-memory Service Control
/// Manager: what gets registered (the same account, start mode, display name and command as before the move onto the shared
/// provisioner), the refusals that happen before anything is touched, and the new verbs. The real Service Control Manager is
/// exercised by the Windows CI job.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class ServiceCommandInstallTests : IDisposable
{
    private const string Executable = @"C:\Tools\dbdatasync.exe";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-service-install-tests-").FullName;
    private readonly InMemoryServiceControlManager _scm = new();
    private readonly List<(string Root, string? Account)> _grants = [];
    private int _eventSourceRegistrations;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private ServiceEnvironment Environment => new()
    {
        Scm = _scm,
        GrantDataDirectoryAccess = (root, account) => _grants.Add((root, account)),
        EnsureEventSource = () => _eventSourceRegistrations++,
    };

    private (int ExitCode, string Output) Install(params string[] extra) =>
        Capture(() => ServiceCommand.Install(["install", "--repo", _root, .. extra], elevatedOverride: true, Environment, Executable));

    [Fact]
    public void Install_RegistersTheServiceTheWayItAlwaysHas()
    {
        var (exitCode, output) = Install();

        Assert.Equal(0, exitCode);
        var config = _scm.GetConfig("DbDataSync")!;
        Assert.Equal("DbDataSync", config.DisplayName);
        Assert.Equal("LocalSystem", config.AccountName);
        Assert.Equal(ServiceStartMode.Automatic, config.StartMode);
        Assert.Equal(0, config.FailureRestartActions);
        Assert.Equal(ServiceSidType.None, config.SidType);
        Assert.StartsWith($"\"{Executable}\" server run --repo ", config.BinaryPathName);
        Assert.Contains(_root, config.BinaryPathName);

        Assert.Contains("Registering the 'DbDataSync' service:", output);
        Assert.Contains("account            LocalSystem", output);
        Assert.Contains("Connections using integrated authentication will connect as this account.", output);
        Assert.Contains("Service 'DbDataSync' installed.", output);
        Assert.Contains("Next: `dbdatasync config check`.", output);

        Assert.Equal([(_root, (string?)null)], _grants);
        Assert.Equal(1, _eventSourceRegistrations);
        var record = ServiceRegistration.Read(_root)!;
        Assert.Equal("LocalSystem", record.Account);
        Assert.Equal("windows", record.Platform);
    }

    [Fact]
    public void Install_Again_IsHarmlessAndSaysNothingChanged()
    {
        Install();

        var (exitCode, output) = Install();

        Assert.Equal(0, exitCode);
        Assert.Contains("already up to date", output);
        Assert.Single(_scm.Operations, o => o.StartsWith("create:"));
    }

    [Fact]
    public void Install_WithADifferentAccount_UpdatesTheRegistration()
    {
        Install();

        var (exitCode, output) = Install("--account", @"NT SERVICE\DbDataSync");

        Assert.Equal(0, exitCode);
        Assert.Contains("updated", output);
        Assert.Equal(@"NT SERVICE\DbDataSync", _scm.GetConfig("DbDataSync")!.AccountName);
        Assert.Equal(@"NT SERVICE\DbDataSync", ServiceRegistration.Read(_root)!.Account);
        Assert.Equal(@"NT SERVICE\DbDataSync", _grants.Last().Account);
    }

    [Fact]
    public void Install_NamedUserWithoutAPassword_RefusesBeforeTouchingTheMachine()
    {
        var (exitCode, output) = Install("--account", @".\svc-dbdatasync");

        Assert.Equal(1, exitCode);
        Assert.Contains("password-stdin", output);
        Assert.Contains("Nothing has been changed.", output);
        Assert.Empty(_scm.Operations);
        Assert.Empty(_grants);
        Assert.Equal(0, _eventSourceRegistrations);
        Assert.Null(ServiceRegistration.Read(_root));
    }

    [Fact]
    public void Install_NamedUserWithPasswordOnStandardInput_IsRegisteredWithIt()
    {
        var originalIn = Console.In;
        Console.SetIn(new StringReader("correct horse\n"));
        try
        {
            var (exitCode, _) = Install("--account", @".\svc-dbdatasync", "--password-stdin");

            Assert.Equal(0, exitCode);
            Assert.True(_scm.LastPasswordSupplied);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void Install_EmptyPasswordInput_RefusesBeforeTouchingTheMachine()
    {
        var originalIn = Console.In;
        Console.SetIn(new StringReader(""));
        try
        {
            var (exitCode, output) = Install("--account", @".\svc-dbdatasync", "--password-stdin");

            Assert.Equal(1, exitCode);
            Assert.Contains("standard input was empty", output);
            Assert.Empty(_scm.Operations);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void Install_NotElevated_RefusesBeforeTouchingAnything()
    {
        var (exitCode, output) = Capture(() => ServiceCommand.Install(["install", "--repo", _root], elevatedOverride: false, Environment, Executable));

        Assert.Equal(1, exitCode);
        Assert.Contains("Needs Administrator", output);
        Assert.Contains("Nothing has been changed.", output);
        Assert.Empty(_scm.Operations);
        Assert.Empty(_grants);
        Assert.Null(ServiceRegistration.Read(_root));
    }

    [Fact]
    public void Uninstall_RemovesTheServiceAndTheRegistrationRecord()
    {
        Install();
        _scm.Start("DbDataSync");

        var (exitCode, output) = Capture(() => ServiceCommand.Uninstall(["uninstall", "--repo", _root], elevatedOverride: true, Environment));

        Assert.Equal(0, exitCode);
        Assert.Contains("Removed the 'DbDataSync' service", output);
        Assert.Null(_scm.GetConfig("DbDataSync"));
        Assert.Null(ServiceRegistration.Read(_root));
        Assert.Contains("stop:DbDataSync", _scm.Operations);
    }

    [Fact]
    public void Uninstall_NotInstalled_FailsAndLeavesTheRecordAlone()
    {
        ServiceRegistration.Write(_root, "LocalSystem", "windows");

        var (exitCode, output) = Capture(() => ServiceCommand.Uninstall(["uninstall", "--repo", _root], elevatedOverride: true, Environment));

        Assert.Equal(1, exitCode);
        Assert.Contains("is not installed", output);
        Assert.NotNull(ServiceRegistration.Read(_root));
    }

    [Fact]
    public void Uninstall_NotElevated_RefusesAndKeepsTheRegistrationRecord()
    {
        Install();

        var (exitCode, output) = Capture(() => ServiceCommand.Uninstall(["uninstall", "--repo", _root], elevatedOverride: false, Environment));

        Assert.Equal(1, exitCode);
        Assert.Contains("Administrator", output);
        Assert.NotNull(_scm.GetConfig("DbDataSync"));
        Assert.NotNull(ServiceRegistration.Read(_root));
    }

    [Fact]
    public void StartStopRestart_DriveTheServiceAndWaitForIt()
    {
        Install();

        Assert.Equal(0, Capture(() => ServiceCommand.Control("start", elevatedOverride: true, Environment)).ExitCode);
        Assert.Equal(ServiceRunState.Running, _scm.QueryStatus("DbDataSync").State);

        var (restartExit, restartOutput) = Capture(() => ServiceCommand.Control("restart", elevatedOverride: true, Environment));
        Assert.Equal(0, restartExit);
        Assert.Contains("restarted", restartOutput);
        Assert.Equal(ServiceRunState.Running, _scm.QueryStatus("DbDataSync").State);

        Assert.Equal(0, Capture(() => ServiceCommand.Control("stop", elevatedOverride: true, Environment)).ExitCode);
        Assert.Equal(ServiceRunState.Stopped, _scm.QueryStatus("DbDataSync").State);
    }

    [Fact]
    public void Control_ServiceNotInstalled_FailsWithTheManagersMessage()
    {
        var (exitCode, output) = Capture(() => ServiceCommand.Control("start", elevatedOverride: true, Environment));

        Assert.Equal(1, exitCode);
        Assert.Contains("DbDataSync", output);
    }

    [Fact]
    public void Control_NotElevated_Refuses()
    {
        Install();

        var (exitCode, output) = Capture(() => ServiceCommand.Control("start", elevatedOverride: false, Environment));

        Assert.Equal(1, exitCode);
        Assert.Contains("Needs Administrator", output);
        Assert.DoesNotContain("start:DbDataSync", _scm.Operations);
    }

    [Fact]
    public void Status_ReportsStateAccountStartModeAndCommand()
    {
        var (notInstalledExit, notInstalled) = Capture(() => ServiceCommand.Status(Environment));
        Assert.Equal(1, notInstalledExit);
        Assert.Contains("DbDataSync: not installed", notInstalled);

        Install();
        _scm.Start("DbDataSync");
        var (exitCode, output) = Capture(() => ServiceCommand.Status(Environment));

        Assert.Equal(0, exitCode);
        Assert.Contains("DbDataSync: Running (pid 4242)", output);
        Assert.Contains("account: LocalSystem", output);
        Assert.Contains("start:   Automatic", output);
    }

    [Fact]
    public void Doctor_InstalledService_PassesWithTheRecoveryWarning()
    {
        Install();

        var (exitCode, output) = Capture(() => ServiceCommand.Doctor(["--repo", _root], Environment));

        Assert.Equal(0, exitCode);
        Assert.Contains("[ok  ] service.installed", output);
        Assert.Contains("[ok  ] data.directory", output);
        Assert.Contains("[warn] service.recovery", output);
        Assert.Contains("Doctor: OK", output);
    }

    [Fact]
    public void Run_Doctor_ChecksTheRepositoryNamedByDashDashRepo()
    {
        if (!OperatingSystem.IsWindows())
            return; // Run dispatches to the Windows verbs only on Windows; the Windows CI job runs this one for real

        var (exitCode, output) = Capture(() => ServiceCommand.Run(["doctor", "--repo", _root]));

        Assert.Contains(_root, output);
        Assert.DoesNotContain("C:\\ProgramData\\DbDataSync does not exist", output);
        _ = exitCode;
    }

    [Fact]
    public void Doctor_NotInstalled_Fails()
    {
        var (exitCode, output) = Capture(() => ServiceCommand.Doctor(["--repo", _root], Environment));

        Assert.Equal(1, exitCode);
        Assert.Contains("[FAIL] service.installed", output);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\DbDataSync\\dbdatasync.exe\" server run --repo C:\\data", "C:\\Program Files\\DbDataSync\\dbdatasync.exe")]
    [InlineData("C:\\tools\\dbdatasync.exe serve", "C:\\tools\\dbdatasync.exe")]
    public void ExecutableOf_ReadsTheProgramOutOfACommandLine(string commandLine, string expected)
    {
        Assert.Equal(expected, ServiceCommand.ExecutableOf(commandLine));
    }

    private static (int ExitCode, string Output) Capture(Func<int> action)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return (action(), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
