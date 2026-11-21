using System.Net;
using System.Net.Sockets;
using DbDataKit.Cli;
using DbDataKit.Hosting.Startup;
using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>
/// <c>server run</c> starts through the shared startup sequence: a start that fails leaves <c>startup-failure-dbdataworkspace.txt</c> in the logs folder
/// of the data folder (<see cref="SuiteServices.LogsRootFor"/>) and exits non-zero; a clean start leaves none.
/// </summary>
public sealed class ServerStartupTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdw-start-").FullName;

    private string FailureFile => SuiteServices.Workspace.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Workspace, _directory));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }

    [Fact]
    public async Task A_port_another_listener_holds_fails_the_ports_step_and_the_failure_file_says_why()
    {
        using var held = Hold(out var port);
        var (output, error) = (new StringWriter(), new StringWriter());

        // no variable of the environment reaches the command: SQLite in the directory
        var exit = await WorkspaceCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", $"https://127.0.0.1:{port}"], new CliContext(output, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        var text = File.ReadAllText(FailureFile);
        Assert.Contains("Startup of dbdataworkspace", text);
        Assert.Contains("failed at the ports step", text);
        Assert.Contains($"Port {port} cannot be bound", text);
        Assert.Contains($"Port: {port}", text);
        Assert.Contains("Excluded port ranges: ", text);
        Assert.Contains("Account: ", text);
        Assert.Contains($"Data folder: {_directory}", text);
        Assert.Contains("Exception chain:", text);
        Assert.Contains(FailureFile, error.ToString());
        Assert.DoesNotContain("Serving on", output.ToString());
        Assert.False(File.Exists(Path.Combine(_directory, "start-failure.txt")));
    }

    [Fact]
    public async Task A_folder_every_account_can_write_fails_the_folder_step_naming_the_switch_and_starts_with_a_warning_when_it_is_skipped()
    {
        LetEveryoneWrite(_directory);
        var error = new StringWriter();
        var exit = await WorkspaceCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0"], new CliContext(TextWriter.Null, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        var text = File.ReadAllText(FailureFile);
        Assert.Contains("failed at the folder step", text);
        Assert.Contains("--skip-folder-permissions-check or set startup.skip_folder_permissions_check: true in ", text);
        Assert.Contains("--skip-folder-permissions-check", error.ToString());

        // the same folder with the flag: the server starts, says so on standard error, and leaves no failure file
        var warning = await StartWarnedAndStop(["--skip-folder-permissions-check"]);
        Assert.Contains("Warning: dbdataworkspace skips the folder permissions check (--skip-folder-permissions-check)", warning);
        Assert.Contains("The check would refuse it: ", warning);
    }

    /// <summary>Starts <c>server run</c> on the folder with <paramref name="extra"/>, waits until it serves, stops it and returns what it wrote on standard error.</summary>
    private async Task<string> StartWarnedAndStop(string[] extra)
    {
        var (written, warned) = (new StringWriter(), new StringWriter());
        var (output, errors) = (TextWriter.Synchronized(written), TextWriter.Synchronized(warned));
        using var stop = new CancellationTokenSource();
        var run = WorkspaceCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0", .. extra], new CliContext(output, errors, TextReader.Null, stop.Token));
        await Until(() => Text(output, written).Contains("Serving on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : "") + Text(errors, warned));
        Assert.False(File.Exists(FailureFile));
        stop.Cancel();
        Assert.Equal(0, await run);
        return Text(errors, warned);
    }

    /// <summary>Lets every account of the machine write to <paramref name="folder"/>: mode 0777 on Linux and macOS, a write grant to Users on Windows.</summary>
    private static void LetEveryoneWrite(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(folder, (UnixFileMode)0x1FF);
        }
    }

    [Fact]
    public async Task A_clean_start_leaves_no_failure_file()
    {
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        using var stop = new CancellationTokenSource();
        var run = WorkspaceCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0"], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));

        await Until(() => Text(output, written).Contains("Serving on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
        Assert.True(Directory.Exists(Path.Combine(SuiteServices.LogsRootFor(SuiteServices.Workspace, _directory), "dbdataworkspace")));
        Assert.False(File.Exists(FailureFile));
        Assert.False(File.Exists(Path.Combine(_directory, "start-failure.txt")));
        stop.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task With_no_port_configured_the_server_takes_a_free_port_of_its_sub_block_and_keeps_it_after_a_restart()
    {
        var block = SuiteServices.Workspace.Ports;
        Assert.SkipWhen(ListenPorts.Choose(block, IPAddress.Loopback, []) is null, "every port of the sub-block is taken on this machine");
        var pinned = await StartAndStop();
        Assert.True(block.Contains(pinned), $"the server listened on {pinned}");
        Assert.Contains($"listen: https://127.0.0.1:{pinned}", File.ReadAllText(Path.Combine(_directory, "server.yaml")));
        Assert.Equal(pinned, await StartAndStop());
    }

    /// <summary>Starts <c>server run</c> with no <c>--listen</c>, waits until it serves, stops it and returns the port it said it serves on.</summary>
    private async Task<int> StartAndStop()
    {
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        using var stop = new CancellationTokenSource();
        var run = WorkspaceCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));
        await Until(() => Text(output, written).Contains("Serving on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
        var match = System.Text.RegularExpressions.Regex.Match(Text(output, written), @"Serving on https://127\.0\.0\.1:(\d+)/");
        Assert.True(match.Success, Text(output, written));
        stop.Cancel();
        Assert.Equal(0, await run);
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static TcpListener Hold(out int port)
    {
        // ExclusiveAddressUse is set on Windows only: set to false on Linux it asks for SO_REUSEPORT, and the server could then share the port
        var listener = new TcpListener(IPAddress.Loopback, 0);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    /// <summary>What the server wrote so far: a synchronized writer locks on itself, so reading under that lock never meets a write.</summary>
    private static string Text(TextWriter synchronized, StringWriter written)
    {
        lock (synchronized) return written.ToString();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }
}
