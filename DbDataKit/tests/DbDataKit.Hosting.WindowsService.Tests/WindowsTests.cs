using DbDataKit.Security.Acl;
using System.Security.Principal;
using System.Security;

namespace DbDataKit.Hosting.WindowsService.Tests;

/// <summary>Windows-only. Anything needing admin is skipped unless DBDATA_ADMIN_TESTS=1.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsTests
{
    private static bool AdminTestsEnabled => Environment.GetEnvironmentVariable("DBDATA_ADMIN_TESTS") == "1";

    [Fact]
    public void Real_scm_reports_unknown_service_as_null_without_admin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var scm = new WindowsServiceControlManager();
        Assert.Null(scm.GetConfig("DbDataKit_NoSuchService_" + Guid.NewGuid().ToString("N")));
        var ex = Assert.Throws<ServiceControlException>(() => scm.QueryStatus("DbDataKit_NoSuchService_x"));
        Assert.Equal(ServiceControlError.NotFound, ex.Error);
    }

    [Fact]
    public void Real_scm_reads_an_existing_system_service()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var cfg = new WindowsServiceControlManager().GetConfig("EventLog");
        Assert.NotNull(cfg);
        Assert.Contains("svchost", cfg!.BinaryPathName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acl_policy_round_trips_through_reader_on_a_temp_dir()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var me = WindowsIdentity.GetCurrent().User!.Value;
        var root = Path.Combine(Path.GetTempPath(), "dbdata-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DirectoryLayout.ForProduct("P", Path.Combine(root, "pf"), Path.Combine(root, "pd"));
            var resolver = new WellKnownPrincipalResolver(me);
            var secrets = layout[LayoutRole.Secrets];
            new FileSystemAclService().Apply(secrets, resolver);

            var acl = new FileSystemAclService().Read(secrets.Path);
            Assert.NotNull(acl);
            Assert.True(acl!.InheritanceProtected);
            Assert.DoesNotContain(acl.Aces, a => WellKnownPrincipalResolver.IsBroadGroup(a.Sid));
            Assert.Contains(acl.Aces, a => a.Sid == WellKnownPrincipalResolver.AdministratorsSid);
            Assert.Contains(acl.Aces, a => a.Sid == me && (a.Rights & AccessRights.Write) != 0);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Admin_only_install_and_remove_real_service()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(AdminTestsEnabled, "Set DBDATA_ADMIN_TESTS=1 (elevated) to run against the real SCM");
        var name = "DbDataKitTest_" + Guid.NewGuid().ToString("N")[..8];
        var scm = new WindowsServiceControlManager();
        var p = new ServiceProvisioner(scm, new WindowsEventLogSourceRegistrar());
        var spec = new ServiceInstallSpec
        {
            Name = name,
            BinaryPath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Description = "DbDataKit test service (safe to delete)",
            EventLogSource = name,
        };
        try
        {
            Assert.Equal(InstallOutcome.Created, p.Install(spec).Outcome);
            Assert.Equal(InstallOutcome.Unchanged, p.Install(spec).Outcome);
            var cfg = scm.GetConfig(name)!;
            Assert.Equal(ServiceStartMode.AutomaticDelayed, cfg.StartMode);
            Assert.Equal(spec.EffectiveAccount.Name, cfg.AccountName, ignoreCase: true);
            Assert.Equal(3, cfg.FailureRestartActions);
        }
        finally
        {
            await p.UninstallAsync(name, TimeSpan.FromSeconds(10), eventLogSource: name);
        }
    }
}
