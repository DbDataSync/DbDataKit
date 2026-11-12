using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace DbDataKit.Security.Acl.Tests;

/// <summary>
/// A secret store's key files are made by the service (its account) or by the elevated command line (an administrator): the same files must open from both, with the service's
/// account on the list, and nobody else on it. The identities of a service are "S-1-5-19" (LocalService) here, a stand-in for `NT SERVICE\DbDataKit`.
/// </summary>
public class ElevatedAdministratorTests
{
    private const string Service = "S-1-5-19";

    private static string Temp() => Path.Combine(Path.GetTempPath(), "dbdata-acl-admin-" + Guid.NewGuid().ToString("N"));

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_file_made_by_an_elevated_administrator_with_the_service_listed_opens_from_the_administrator_and_from_the_service()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(AclHelper.IsElevatedAdministrator(), "needs an elevated administrator");
        var path = Temp();
        try
        {
            File.WriteAllText(path, "key");
            // made as the administrator: the account itself is not named, the Administrators group it works through is
            AclHelper.Restrict(path, ["CurrentUser", "SYSTEM", "Administrators", Service]);
            var me = WindowsIdentity.GetCurrent().User!;
            var granted = new FileInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => (SecurityIdentifier)r.IdentityReference).ToList();
            Assert.DoesNotContain(me, granted);
            Assert.Contains(new SecurityIdentifier(Service), granted);

            // the administrator's view: its own account is on the list, covered by the group
            Assert.True(AclHelper.Verify(path, ["CurrentUser", "SYSTEM", "Administrators", Service]).InSync);
            // the service's view: its own account (the stand-in) is the current user, the same set
            Assert.True(AclHelper.Verify(path, [Service, "SYSTEM", "Administrators"]).InSync);
        }
        finally { File.Delete(path); }
    }

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_grant_to_anyone_else_is_still_refused_from_both_views()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(AclHelper.IsElevatedAdministrator(), "needs an elevated administrator");
        var path = Temp();
        try
        {
            File.WriteAllText(path, "key");
            AclHelper.Restrict(path, ["CurrentUser", "SYSTEM", "Administrators", Service]);
            var security = new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);

            foreach (var view in new[] { new[] { "CurrentUser", "SYSTEM", "Administrators", Service }, new[] { Service, "SYSTEM", "Administrators" } })
            {
                var report = AclHelper.Verify(path, view);
                Assert.False(report.InSync);
                Assert.Contains(report.Drift, d => d.Description.StartsWith("unexpected grant to"));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_service_that_is_not_on_the_list_is_an_unexpected_grant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(AclHelper.IsElevatedAdministrator(), "needs an elevated administrator");
        var path = Temp();
        try
        {
            File.WriteAllText(path, "key");
            AclHelper.Restrict(path, ["CurrentUser", "SYSTEM", "Administrators", Service]);
            // a reader that does not know the service (no service registered on its directory) refuses what grants it
            Assert.Contains(AclHelper.Verify(path, ["CurrentUser", "SYSTEM", "Administrators"]).Drift, d => d.Description.StartsWith("unexpected grant to"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"c:\\program files\\x\\dbdatakit.exe\" server run --repo c:\\programdata\\dbdatakit\\server", "c:\\programdata\\dbdatakit\\server", true)]
    [InlineData("c:\\x\\dbdatarunner.exe server run --repo \"c:\\programdata\\dbdatakit\\runner\" --other", "c:\\programdata\\dbdatakit\\runner", true)]
    [InlineData("c:\\x\\dbdatakit.exe server run --repo c:\\programdata\\dbdatakit\\server2", "c:\\programdata\\dbdatakit\\server", false)]
    [InlineData("c:\\x\\dbdatakit.exe server run --repo c:\\programdata\\dbdatakit\\runner", "c:\\programdata\\dbdatakit\\server", false)]
    [InlineData("c:\\x\\dbdatakit.exe server run --repo c:\\programdata\\dbdatakit\\server\\", "c:\\programdata\\dbdatakit\\server\\", true)]
    public void A_service_runs_on_a_directory_only_when_its_command_line_names_the_whole_path(string commandLine, string directory, bool expected) =>
        Assert.Equal(expected, WindowsServiceAccounts.Names(commandLine, directory));
}
