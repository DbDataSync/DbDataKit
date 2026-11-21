using System.Security.AccessControl;
using System.Security.Principal;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>
/// The folder step's check of the data folder and the switch that skips it: a folder another account could change fails the step naming the switch; with
/// the switch (the flag or the setting) the start goes on and warns, from a terminal and under a service manager alike.
/// </summary>
public sealed class FolderPermissionsTests : StartupTestBase
{
    private string SettingsFile => Path.Combine(Data, "server.yaml");

    /// <summary>Lets every account of the machine write to <paramref name="folder"/>: mode 0777 on Linux and macOS, a write grant to Users on Windows.</summary>
    internal static void LetEveryoneWrite(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(folder, (UnixFileMode)0x1FF);
        }
    }

    /// <summary>Makes <paramref name="folder"/> as strict as a folder the server made for itself: mode 0700 on Linux and macOS; on Windows a test's temporary folder already is.</summary>
    private static void KeepToMyself(string folder)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private (StartupSequence Sequence, List<string> Warnings, List<string> ServiceWarnings) Start(bool runsAsService = false)
    {
        var warnings = new List<string>();
        var serviceWarnings = new List<string>();
        var sequence = new StartupSequence(new StartupOptions
        {
            Service = SuiteServices.Runner,
            DataFolder = Data,
            LogsRoot = Logs,
            ExcludedPorts = () => [],
            Unreportable = Unreported.Add,
            Warning = warnings.Add,
            // the service manager's detector replaced: the same start as a Windows service (or from a terminal)
            RunsAsService = () => runsAsService,
            ServiceWarning = serviceWarnings.Add,
            Failed = _ => { },
        });
        return (sequence, warnings, serviceWarnings);
    }

    [Fact]
    public void A_folder_the_account_keeps_to_itself_passes_and_one_every_account_can_write_does_not()
    {
        KeepToMyself(Data);
        Assert.Empty(FolderPermissions.Problems(Data));
        Assert.Empty(FolderPermissions.Problems(Path.Combine(Data, "not-there")));   // the server makes it, as its own account

        LetEveryoneWrite(Data);
        var problem = Assert.Single(FolderPermissions.Problems(Data));
        Assert.Contains(OperatingSystem.IsWindows() ? "Its access list lets Users write to it" : "Every account on the machine can write to it (mode 0777)", problem);
    }

    [Fact]
    public async Task Without_the_switch_a_folder_another_account_could_change_fails_the_folder_step_naming_the_flag_and_the_setting()
    {
        LetEveryoneWrite(Data);
        var (sequence, warnings, _) = Start();
        var reached = false;

        var result = await sequence
            .Step(StartupStep.Folder, step => step.CheckFolderPermissions(Data, SkipFolderPermissions.Read(false, SettingsFile), SettingsFile))
            .Step(StartupStep.State, _ => reached = true)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(StartupStep.Folder, result.FailedStep);
        Assert.False(reached);
        Assert.Empty(warnings);
        var text = Read(FailureFile);
        Assert.Contains("failed at the folder step", text);
        Assert.Contains($"The folder '{Data}' is owned by {FileOwnership.DescribeOwner(Data)}, and the service runs as {StartupAccount.Current()}.", text);
        Assert.Contains($"give `server run` --skip-folder-permissions-check or set startup.skip_folder_permissions_check: true in {SettingsFile}", text);
        Assert.Contains($"Skip the check with: --skip-folder-permissions-check or startup.skip_folder_permissions_check: true in {SettingsFile}", text);
        Assert.Contains("--skip-folder-permissions-check", result.Summary());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_flag_the_same_folder_starts_with_a_warning_from_a_terminal_and_under_a_service_manager(bool runsAsService)
    {
        LetEveryoneWrite(Data);
        var (sequence, warnings, serviceWarnings) = Start(runsAsService);
        var reached = false;

        var result = await sequence
            .Step(StartupStep.Folder, step => step.CheckFolderPermissions(Data, SkipFolderPermissions.Read(true, SettingsFile), SettingsFile))
            .Step(StartupStep.State, _ => reached = true)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(reached);
        var warning = Assert.Single(warnings);
        Assert.StartsWith($"Warning: dbdatarunner skips the folder permissions check (--skip-folder-permissions-check): the data folder '{Data}' is used whatever its owner and access list.", warning);
        Assert.Contains("The check would refuse it: ", warning);
        // under a service manager the warning also goes where the service manager's log is (the event log); from a terminal standard error is enough
        Assert.Equal(runsAsService ? [warning] : [], serviceWarnings);
    }

    [Fact]
    public async Task The_setting_skips_the_check_too_and_the_warning_names_the_file()
    {
        LetEveryoneWrite(Data);
        File.WriteAllText(SettingsFile, "listen: https://127.0.0.1:7841\nstartup:\n  skip_folder_permissions_check: true\n");
        var (sequence, warnings, _) = Start();

        var result = await sequence
            .Step(StartupStep.Folder, step => step.CheckFolderPermissions(Data, SkipFolderPermissions.Read(false, SettingsFile), SettingsFile))
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains($"(startup.skip_folder_permissions_check: true in {SettingsFile})", Assert.Single(warnings));
    }

    [Fact]
    public async Task A_skipped_check_on_a_folder_that_would_pass_says_the_switch_can_go()
    {
        KeepToMyself(Data);
        var (sequence, warnings, _) = Start();

        await sequence
            .Step(StartupStep.Folder, step => step.CheckFolderPermissions(Data, new SkipFolderPermissions(true, null), SettingsFile))
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.EndsWith("The folder passes the check, so the switch can be removed.", Assert.Single(warnings));
    }

    [Fact]
    public void The_setting_is_read_strictly_from_the_startup_section()
    {
        Assert.False(FolderPermissions.SettingIn(SettingsFile));                                        // no file
        File.WriteAllText(SettingsFile, "listen: https://127.0.0.1:7841\n");
        Assert.False(FolderPermissions.SettingIn(SettingsFile));                                        // no section
        File.WriteAllText(SettingsFile, "startup:\n  skip_folder_permissions_check: false\n");
        Assert.False(FolderPermissions.SettingIn(SettingsFile));
        File.WriteAllText(SettingsFile, "Startup: { skip_folder_permissions_check: TRUE }\n");          // the Gateway matches keys without case
        Assert.True(FolderPermissions.SettingIn(SettingsFile));

        File.WriteAllText(SettingsFile, "startup:\n  skip_folder_check: true\n");
        Assert.Equal($"{SettingsFile}, line 2: 'skip_folder_check' is not a setting of startup (it has skip_folder_permissions_check).",
            Assert.Throws<InvalidOperationException>(() => FolderPermissions.SettingIn(SettingsFile)).Message);
        File.WriteAllText(SettingsFile, "startup:\n  skip_folder_permissions_check: yes please\n");
        Assert.Equal($"{SettingsFile}, line 2: startup.skip_folder_permissions_check is true or false, not 'yes please'.",
            Assert.Throws<InvalidOperationException>(() => FolderPermissions.SettingIn(SettingsFile)).Message);
        File.WriteAllText(SettingsFile, "startup: true\n");
        Assert.Contains("line 1: 'startup' holds settings", Assert.Throws<InvalidOperationException>(() => FolderPermissions.SettingIn(SettingsFile)).Message);
    }
}
