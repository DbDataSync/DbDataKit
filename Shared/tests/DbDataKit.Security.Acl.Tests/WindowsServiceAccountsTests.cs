using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Xunit;

namespace DbDataKit.Security.Acl.Tests;

/// <summary>
/// Which services run on a data directory, so the elevated command line accepts their accounts on the key files the services made (the suite's Scoop install, where a
/// command run by the administrator opens files the service made first). Found on a machine whose administrator could not read one service key: the whole list came back
/// empty and every elevated command that opened the console's secret store crashed with "unexpected grant to NT SERVICE\DbDataKit".
/// </summary>
public class WindowsServiceAccountsTests
{
    private const string ProgramData = "C:\\ProgramData";

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        WindowsServiceAccounts.Merged(variables.ToDictionary(v => v.Name, v => v.Value), null);

    [Theory]
    // the console, the Runner and the Workspace are registered with their folder on the command line
    [InlineData("\"C:\\ProgramData\\DbDataKit\\bin\\1\\console\\dbdatakit.exe\" server run --repo C:\\ProgramData\\DbDataKit\\server", "C:\\ProgramData\\DbDataKit\\server", true)]
    [InlineData("\"C:\\ProgramData\\DbDataKit\\bin\\1\\console\\dbdatakit.exe\" server run --repo C:\\ProgramData\\DbDataKit\\server", "C:\\ProgramData\\DbDataKit\\gateway", false)]
    // the Gateway is registered as `server run` and keeps the folder the environment gives it, else %ProgramData%\DbDataKit\gateway
    [InlineData("\"C:\\ProgramData\\DbDataKit\\bin\\1\\gateway\\dbdatagateway.exe\" server run", "C:\\ProgramData\\DbDataKit\\gateway", true)]
    [InlineData("\"C:\\ProgramData\\DbDataKit\\bin\\1\\gateway\\dbdatagateway.exe\" server run", "c:\\programdata\\dbdatakit\\gateway\\", true)]
    [InlineData("\"C:\\ProgramData\\DbDataKit\\bin\\1\\gateway\\dbdatagateway.exe\" server run", "C:\\ProgramData\\DbDataKit\\server", false)]
    [InlineData("C:\\Program Files\\DbDataGateway\\dbdatagateway.exe server run", "C:\\ProgramData\\DbDataKit\\gateway", true)]
    // a folder on the command line decides alone
    [InlineData("\"C:\\x\\dbdatagateway.exe\" server run --Gateway:DataDirectory=D:\\gw", "C:\\ProgramData\\DbDataKit\\gateway", false)]
    [InlineData("\"C:\\x\\dbdatagateway.exe\" server run --Gateway:DataDirectory=D:\\gw", "D:\\gw", true)]
    // anything that is not a product of the suite never runs on a folder it does not name
    [InlineData("\"C:\\Windows\\System32\\svchost.exe\" -k netsvcs", "C:\\ProgramData\\DbDataKit\\gateway", false)]
    [InlineData("\"C:\\tools\\notdbdatagateway.exe\" server run", "C:\\ProgramData\\DbDataKit\\gateway", false)]
    public void A_service_runs_on_the_folder_its_command_line_names_or_its_product_resolves(string commandLine, string directory, bool expected) =>
        Assert.Equal(expected, WindowsServiceAccounts.RunsOn(commandLine, directory, Environment(), ProgramData));

    [Fact]
    public void The_folder_a_service_resolves_follows_its_variables_in_the_products_order()
    {
        const string gateway = "\"C:\\x\\dbdatagateway.exe\" server run";
        // DBDATAKIT_DATA (a machine variable, plan 33 SC31) moves every folder; the service's own DBDATAKIT_DATA_GATEWAY wins over it
        Assert.True(WindowsServiceAccounts.RunsOn(gateway, "D:\\data\\gateway", Environment(("DBDATAKIT_DATA", "D:\\data")), ProgramData));
        Assert.False(WindowsServiceAccounts.RunsOn(gateway, "C:\\ProgramData\\DbDataKit\\gateway", Environment(("DBDATAKIT_DATA", "D:\\data")), ProgramData));
        Assert.True(WindowsServiceAccounts.RunsOn(gateway, "E:\\gw", Environment(("DBDATAKIT_DATA", "D:\\data"), ("DBDATAKIT_DATA_GATEWAY", "E:\\gw")), ProgramData));
        // the service key's own Environment value wins over the machine's
        var merged = WindowsServiceAccounts.Merged(new Dictionary<string, string> { ["DBDATAKIT_DATA"] = "D:\\data" }, ["DBDATAKIT_DATA=F:\\mine", "not a variable"]);
        Assert.True(WindowsServiceAccounts.RunsOn(gateway, "F:\\mine\\gateway", merged, ProgramData));
    }

    [Fact]
    public void The_virtual_account_of_a_service_is_known_without_the_service()
    {
        // `sc showsid TrustedInstaller`: the algorithm Windows uses for every NT SERVICE\<name>
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", WindowsServiceAccounts.VirtualAccountSid("TrustedInstaller"));
        Assert.Equal(WindowsServiceAccounts.VirtualAccountSid("DbDataKit"), WindowsServiceAccounts.VirtualAccountSid("dbdatakit"));
    }

    [Theory]
    [InlineData("C:\\ProgramData\\DbDataKit\\server", "DbDataKit")]
    [InlineData("C:\\ProgramData\\DbDataKit\\gateway\\", "dbdatagateway")]
    [InlineData("D:/data/runner", "DbDataRunner")]
    [InlineData("C:\\ProgramData\\DbDataKit\\workspace", "DbDataWorkspace")]
    [InlineData("C:\\ProgramData\\DbDataKit\\sync", null)]
    [InlineData("C:\\Users\\me\\AppData\\Local\\dbdatakit\\console", null)]
    public void The_folder_named_for_a_service_keeps_that_services_account_after_it_is_removed(string directory, string? service) =>
        Assert.Equal(service, WindowsServiceAccounts.OwnServiceOf(directory));

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_service_key_the_administrator_cannot_read_does_not_hide_the_others()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(AclHelper.IsElevatedAdministrator(), "needs an elevated administrator (it writes service keys)");
        var id = Guid.NewGuid().ToString("N")[..12];
        var directory = Path.Combine(Path.GetTempPath(), "dbdata-svc-" + id);
        var services = @"SYSTEM\CurrentControlSet\Services\";
        var protectedName = "zz-dbdata-test-protected-" + id;
        var serviceName = "zz-dbdata-test-service-" + id;
        try
        {
            // a key only SYSTEM reads, as security software keeps its own (the administrator may still delete it)
            using (var key = Registry.LocalMachine.CreateSubKey(services + protectedName))
            {
                key.SetValue("ImagePath", "C:\\Windows\\System32\\cmd.exe");
                var security = new RegistrySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), RegistryRights.Delete | RegistryRights.ChangePermissions, AccessControlType.Allow));
                key.SetAccessControl(security);
            }
            Assert.Throws<System.Security.SecurityException>(() => Registry.LocalMachine.OpenSubKey(services + protectedName)?.Dispose());
            // a service of the suite on the directory, as `service install` registers one (the key alone: the service manager does not know it until a restart)
            using (var key = Registry.LocalMachine.CreateSubKey(services + serviceName))
            {
                key.SetValue("ImagePath", $"\"C:\\x\\dbdatakit.exe\" server run --repo {directory}", RegistryValueKind.ExpandString);
                key.SetValue("ObjectName", "NT AUTHORITY\\LocalService");
            }

            Assert.Contains("NT AUTHORITY\\LocalService", WindowsServiceAccounts.On(directory));
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(services + serviceName, throwOnMissingSubKey: false);
            try { Registry.LocalMachine.DeleteSubKey(services + protectedName, throwOnMissingSubKey: false); } catch (UnauthorizedAccessException) { }
        }
    }
}
