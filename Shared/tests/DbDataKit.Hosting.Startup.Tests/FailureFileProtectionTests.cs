using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DbDataKit.Security.Acl;
using Microsoft.Win32.SafeHandles;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>Who can open the failure file and the logs folders.</summary>
public sealed class FailureFileProtectionTests : StartupTestBase
{
    private async Task<StartupResult> Fail() =>
        await Sequence().Step(StartupStep.State, _ => throw new InvalidOperationException("down")).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task On_Linux_the_file_is_mode_0600_its_logs_folder_0700_and_a_shared_folder_it_made_1770()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux only");

        await Fail();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FailureFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(Logs, "dbdatarunner")));
        Assert.Equal(SharedLogsFolder.LinuxMode, File.GetUnixFileMode(Logs));
    }

    [Fact]
    public async Task On_Windows_the_file_grants_only_its_account_SYSTEM_and_Administrators()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        await Fail();

        var allowed = new HashSet<SecurityIdentifier>
        {
            WindowsIdentity.GetCurrent().User!,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        foreach (var path in new[] { FailureFile, Path.Combine(Logs, "dbdatarunner") })
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            Assert.True(security.AreAccessRulesProtected, $"{path} inherits its parent's access list");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow)
                    Assert.Contains((SecurityIdentifier)rule.IdentityReference, allowed);
        }
    }

    [Fact]
    public async Task On_Windows_another_account_cannot_open_the_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(AclHelper.IsElevatedAdministrator(), "making a local account needs an elevated administrator");

        await Fail();

        // A second, ordinary local account stands for the second service's account: like it, it is neither the first service's account, SYSTEM nor an administrator.
        var name = "dbdkst" + Guid.NewGuid().ToString("N")[..8];
        var password = "Aa1!" + Guid.NewGuid().ToString("N")[..10]; // 14 characters: a longer one makes `net user` ask a question
        Assert.SkipUnless(Net("user", name, password, "/add") == 0, "`net user /add` failed");
        try
        {
            Assert.SkipUnless(LogonUser(name, ".", password, LogonNetwork, ProviderDefault, out var token), $"LogonUser failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            using (token)
            {
                var opened = WindowsIdentity.RunImpersonated(token, () =>
                {
                    try
                    {
                        using var stream = new FileStream(FailureFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        return true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return false;
                    }
                });
                Assert.False(opened, "another account opened the failure file");
            }
        }
        finally
        {
            Net("user", name, "/delete");
        }
    }

    [Fact]
    public void A_failure_file_another_account_owns_is_not_written_and_the_reason_goes_elsewhere()
    {
        var path = SuiteServices.Runner.FailureFileIn(Logs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "left by another account");
        var file = new StartupFailureFile(Logs, SuiteServices.Runner, Unreported.Add) { IsOwned = p => p != path };

        Assert.False(file.Prepare());
        file.WriteProgress(DateTimeOffset.UnixEpoch, StartupStep.Folder);
        file.WriteFailure("the reason");
        file.Delete();

        Assert.Equal("left by another account", File.ReadAllText(path));
        var said = Assert.Single(Unreported);
        Assert.Contains(path, said);
        Assert.Contains("nothing is written there", said);
    }

    [Fact]
    public void A_logs_subfolder_another_account_owns_is_not_written()
    {
        var own = Path.Combine(Logs, "dbdatarunner");
        Directory.CreateDirectory(own);
        var file = new StartupFailureFile(Logs, SuiteServices.Runner, Unreported.Add) { IsOwned = p => p != own };

        Assert.False(file.Prepare());
        file.WriteFailure("the reason");

        Assert.False(File.Exists(FailureFile));
        Assert.Contains(own, Assert.Single(Unreported));
    }

    [Fact]
    public void The_files_and_folders_a_process_makes_are_its_own()
    {
        var path = Path.Combine(Root, "mine.txt");
        File.WriteAllText(path, "");

        Assert.True(FileOwnership.IsOwnedByCurrentAccount(path));
        Assert.True(FileOwnership.IsOwnedByCurrentAccount(Data));
        Assert.False(FileOwnership.IsOwnedByCurrentAccount(Path.Combine(Root, "missing")));
        Assert.NotNull(FileOwnership.DescribeOwner(path));
    }

    [Fact]
    public void On_Windows_the_shared_logs_folder_gives_each_writer_create_rights_on_that_folder_only()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        Assert.True(SharedLogsFolder.Ensure(Logs, ["CurrentUser"]));

        var security = new DirectoryInfo(Logs).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && r.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid) && r.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.CreatorOwnerSid) && r.PropagationFlags == PropagationFlags.InheritOnly);
        if (!AclHelper.IsElevatedAdministrator())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var mine = Assert.Single(rules, r => r.IdentityReference.Equals(me));
            Assert.Equal(InheritanceFlags.None, mine.InheritanceFlags);
            Assert.True(mine.FileSystemRights.HasFlag(FileSystemRights.CreateFiles));
            Assert.False(mine.FileSystemRights.HasFlag(FileSystemRights.ReadData));
        }
        Assert.False(SharedLogsFolder.Ensure(Logs, ["CurrentUser"]));
    }

    private const int LogonNetwork = 3;
    private const int ProviderDefault = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);

    private static int Net(params string[] args)
    {
        var info = new ProcessStartInfo("net") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var net = Process.Start(info)!;
        net.StandardInput.Close();
        net.StandardOutput.ReadToEnd();
        net.StandardError.ReadToEnd();
        net.WaitForExit(60_000);
        return net.ExitCode;
    }
}
