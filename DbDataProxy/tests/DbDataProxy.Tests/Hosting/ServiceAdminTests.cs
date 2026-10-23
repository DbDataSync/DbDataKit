using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;
using DbDataProxy.Server.Hosting;

namespace DbDataProxy.Tests.Hosting;

/// <summary>
/// Installs and removes a real, throwaway Windows service through the proxy's own CLI. Needs an elevated prompt and
/// the Service Control Manager, so it only runs when DBPROXY_ADMIN_TESTS=1 (CI on windows-latest runs as admin).
/// The service is never started, is named uniquely, and is removed even when an assertion fails.
/// </summary>
public sealed class ServiceAdminTests : IDisposable
{
    private readonly string _name = "DbDataProxyTest-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dbproxy-svc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("DBPROXY_ADMIN_TESTS") == "1" && Environment.IsPrivilegedProcess)
        {
            try { new WindowsServiceControlManager().Delete(_name); } catch (ServiceControlException) { /* not created */ }
        }

        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Install_configure_and_uninstall_a_real_service()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows service control");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DBPROXY_ADMIN_TESTS") == "1", "Set DBPROXY_ADMIN_TESTS=1 (elevated prompt) to install a throwaway service.");
        Assert.SkipUnless(Environment.IsPrivilegedProcess, "Needs an elevated prompt.");
        if (!OperatingSystem.IsWindows()) return;

        string exe = Path.Combine(AppContext.BaseDirectory, "dbdataproxy.exe");
        Assert.True(File.Exists(exe), exe);
        var console = new BufferConsole();
        ServiceCliEnvironment Env() => new()
        {
            Scm = new WindowsServiceControlManager(),
            Acl = new FileSystemAclService(),
            Elevation = new ElevationDetector(),
            Console = console,
            BinaryPath = exe,
            DefaultDataDirectory = () => _dataDir,
        };

        Assert.Equal(0, ServiceCli.Run(["install", "--name", _name, "--data-dir", _dataDir], Env()));

        var scm = new WindowsServiceControlManager();
        var cfg = scm.GetConfig(_name)!;
        Assert.Equal($@"NT SERVICE\{_name}", cfg.AccountName);
        Assert.Equal(ServiceStartMode.Automatic, cfg.StartMode);
        Assert.Equal(3, cfg.FailureRestartActions);
        Assert.Equal(ServiceCli.DisplayName, cfg.DisplayName);
        Assert.Contains("--Proxy:DataDirectory=", cfg.BinaryPathName);

        var acl = new FileSystemAclService().Read(_dataDir)!;
        Assert.True(acl.InheritanceProtected);
        Assert.Contains(acl.Aces, a => a.Allow && a.Sid == ServiceSid.FromName(_name));
        Assert.DoesNotContain(acl.Aces, a => a.Allow && WellKnownPrincipalResolver.IsBroadGroup(a.Sid));

        Assert.Equal(0, ServiceCli.Run(["doctor", "--name", _name, "--data-dir", _dataDir], Env()));
        Assert.Equal(0, ServiceCli.Run(["uninstall", "--name", _name], Env()));
        Assert.Null(scm.GetConfig(_name));
    }
}
