namespace DbDataKit.Upgrade.Tests;

/// <summary>UC09: the helper's command is built without <c>Assembly.Location</c> (empty in a single-file app, IL3000): the executable itself for a single-file or apphost install, <c>dotnet app.dll</c> for a framework-dependent one.</summary>
public sealed class LauncherCommandTests
{
    private static readonly string Program = Path.Combine(Path.GetTempPath(), "prog");
    private static readonly string Copy = Path.Combine(Path.GetTempPath(), "helper-1");
    private static HelperInvocation Invocation => new(Path.GetTempPath(), "dbdatatest", Program, "svc", "1", "key", "https://127.0.0.1:1/version");

    [Fact]
    public void A_single_file_install_runs_its_own_executable_from_the_copy()
    {
        var exe = Path.Combine(Program, OperatingSystem.IsWindows() ? "dbdatatest.exe" : "dbdatatest");
        var command = ProcessUpgradeLauncher.Command(Invocation, Copy, exe, exe, systemd: false);
        Assert.Equal(Path.Combine(Copy, Path.GetFileName(exe)), command[0]);
        Assert.Equal(HelperInvocation.Flag, command[1]);
    }

    [Fact]
    public void A_framework_dependent_install_runs_dotnet_with_the_entry_dll_of_the_copy()
    {
        var dotnet = Path.Combine(Path.GetTempPath(), "dotnet-root", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var dll = Path.Combine(Program, "dbdatatest.dll");
        var command = ProcessUpgradeLauncher.Command(Invocation, Copy, dotnet, dll, systemd: true);
        Assert.Equal("/usr/bin/systemd-run", command[0]);
        var at = command.IndexOf(dotnet);
        Assert.Equal(Path.Combine(Copy, "dbdatatest.dll"), command[at + 1]);
        Assert.Equal(HelperInvocation.Flag, command[at + 2]);
    }

    [Fact]
    public void A_framework_dependent_install_without_a_known_entry_is_refused()
    {
        var dotnet = Path.Combine(Path.GetTempPath(), "dotnet");
        Assert.Throws<InvalidOperationException>(() => ProcessUpgradeLauncher.Command(Invocation, Copy, dotnet, null, systemd: false));
    }
}
