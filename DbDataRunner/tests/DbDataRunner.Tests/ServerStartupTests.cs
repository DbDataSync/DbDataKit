using System.Net;
using System.Net.Sockets;
using DbDataKit.Cli;
using DbDataKit.Hosting.Startup;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>
/// <c>server run</c> starts through the shared startup sequence: a start that fails leaves <c>startup-failure-dbdatarunner.txt</c> in the logs folder
/// of the data folder (<see cref="SuiteServices.LogsRootFor"/>) and exits non-zero; a clean start leaves none.
/// </summary>
public sealed class ServerStartupTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdr-start-").FullName;

    /// <summary>
    /// <c>server run</c> starts the task host published beside the program, and this test's program is the test host: the server's build output's task
    /// host folder is copied beside it once (new files, never an edit of a file the build made).
    /// </summary>
    private static readonly Lazy<bool> TaskHostBesideTheTests = new(() =>
    {
        if (DbDataKit.Runner.RunnerTaskHost.Find(DbDataKit.Runner.RunnerTaskHost.ProgramDirectory) is not null) return true;
        var from = Path.Combine(ServerBuild.Directory, DbDataKit.Runner.RunnerTaskHost.Folder);
        var to = Path.Combine(DbDataKit.Runner.RunnerTaskHost.ProgramDirectory, DbDataKit.Runner.RunnerTaskHost.Folder);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(file, target);
        }
        return true;
    });

    public ServerStartupTests() => _ = TaskHostBesideTheTests.Value;

    private string FailureFile => SuiteServices.Runner.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Runner, _directory));

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
        var exit = await RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", $"https://127.0.0.1:{port}"], new CliContext(output, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        var text = File.ReadAllText(FailureFile);
        Assert.Contains("Startup of dbdatarunner", text);
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
        var exit = await RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0"], new CliContext(TextWriter.Null, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        var text = File.ReadAllText(FailureFile);
        Assert.Contains("failed at the folder step", text);
        Assert.Contains("--skip-folder-permissions-check or set startup.skip_folder_permissions_check: true in ", text);
        Assert.Contains("--skip-folder-permissions-check", error.ToString());

        // the same folder with the flag: the server starts, says so on standard error, and leaves no failure file
        var warning = await StartWarnedAndStop(["--skip-folder-permissions-check"]);
        Assert.Contains("Warning: dbdatarunner skips the folder permissions check (--skip-folder-permissions-check)", warning);
        Assert.Contains("The check would refuse it: ", warning);

        // the setting in the settings file does the same, and the warning names the file
        var settingsFile = Path.Combine(_directory, "server.yaml");
        File.WriteAllText(settingsFile, "startup:\n  skip_folder_permissions_check: true\n");
        Assert.Contains($"(startup.skip_folder_permissions_check: true in {settingsFile})", await StartWarnedAndStop([]));
    }

    /// <summary>Starts <c>server run</c> on the folder with <paramref name="extra"/>, waits until it serves, stops it and returns what it wrote on standard error.</summary>
    private async Task<string> StartWarnedAndStop(string[] extra)
    {
        var (written, warned) = (new StringWriter(), new StringWriter());
        var (output, errors) = (TextWriter.Synchronized(written), TextWriter.Synchronized(warned));
        using var stop = new CancellationTokenSource();
        var run = RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0", .. extra], new CliContext(output, errors, TextReader.Null, stop.Token));
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
    public async Task A_listen_setting_that_is_a_list_makes_the_server_listen_on_every_address_of_it()
    {
        // two free ports: held for a moment to be named, then given back
        int first, second;
        using (var a = Hold(out first)) using (var b = Hold(out second)) { }
        File.WriteAllText(Path.Combine(_directory, "server.yaml"), $"listen: [https://127.0.0.1:{first}, https://127.0.0.1:{second}]\n");
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        using var stop = new CancellationTokenSource();
        var run = RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));
        await Until(() => Text(output, written).Contains("Serving on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
        foreach (var port in new[] { first, second })
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            Assert.True(client.Connected, $"nothing listens on {port}");
        }
        // a list is the same as one address: the first is the one the server reports
        Assert.Contains($"https://127.0.0.1:{first}", Text(output, written));
        stop.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task A_listen_flag_that_is_a_list_is_read_and_a_bad_address_in_it_is_refused()
    {
        var error = new StringWriter();
        var exit = await RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0;7841"], new CliContext(TextWriter.Null, error, TextReader.Null));
        Assert.NotEqual(0, exit);
        Assert.Contains("--listen", error.ToString());
        Assert.Contains("'7841'", error.ToString());
    }

    [Fact]
    public async Task A_clean_start_leaves_no_failure_file()
    {
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        using var stop = new CancellationTokenSource();
        var run = RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0"], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));

        await Until(() => Text(output, written).Contains("Serving on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
        Assert.True(Directory.Exists(Path.Combine(SuiteServices.LogsRootFor(SuiteServices.Runner, _directory), "dbdatarunner")));
        Assert.False(File.Exists(FailureFile));
        Assert.False(File.Exists(Path.Combine(_directory, "start-failure.txt")));
        stop.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task With_no_port_configured_the_server_takes_a_free_port_of_its_sub_block_keeps_it_after_a_restart_and_fails_when_it_is_held()
    {
        // the sub-block's first free port is held, so the server takes a later one
        var first = ListenPorts.Choose(SuiteServices.Runner.Ports, System.Net.IPAddress.Loopback, []);
        Assert.SkipWhen(first is null, "every port of the Runner's sub-block is taken on this machine");
        int pinned;
        using (Hold(first!.Value))
        {
            pinned = await StartAndStop();
            Assert.True(pinned > first.Value && SuiteServices.Runner.Ports.Contains(pinned), $"the server listened on {pinned}");
            Assert.Contains($"listen: https://127.0.0.1:{pinned}", File.ReadAllText(Path.Combine(_directory, "server.yaml")));
        }

        // the first port is free again: the pinned one is kept
        Assert.Equal(pinned, await StartAndStop());

        // the pinned port held: the server never moves, the ports step fails naming it
        using (Hold(pinned))
        {
            var error = new StringWriter();
            var exit = await RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory], new CliContext(TextWriter.Null, error, TextReader.Null));
            Assert.NotEqual(0, exit);
            var text = File.ReadAllText(FailureFile);
            Assert.Contains("failed at the ports step", text);
            Assert.Contains($"Port {pinned} cannot be bound", text);
        }
        Assert.Contains($"listen: https://127.0.0.1:{pinned}", File.ReadAllText(Path.Combine(_directory, "server.yaml")));
    }

    [Fact]
    public async Task A_bare_health_check_asks_the_address_the_server_pinned_and_the_default_one_when_there_is_no_settings_file()
    {
        // the sub-block's first port is held by a listener that is not the server (7840 on a free machine), so the server pins the next (7841)
        // and a check that asked the default address would not be answered
        var first = ListenPorts.Choose(SuiteServices.Runner.Ports, IPAddress.Loopback, []);
        Assert.SkipWhen(first is null, "every port of the Runner's sub-block is taken on this machine");
        using (Hold(first!.Value))
        {
            var written = new StringWriter();
            var output = TextWriter.Synchronized(written);
            using var stop = new CancellationTokenSource();
            var run = RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));
            await Until(() => Text(output, written).Contains("Serving on", StringComparison.Ordinal) || run.IsCompleted);
            Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
            var pinned = ListenPorts.ReadPinned(Path.Combine(_directory, "server.yaml"));
            Assert.NotNull(pinned);
            Assert.NotEqual(first.Value, pinned.Port);

            var check = new CliContext(new StringWriter(), new StringWriter(), TextReader.Null);
            var exit = await RunnerCli.Router(_ => null).RunAsync(["health", "check", "--repo", _directory], check);

            Assert.True(exit == CliExit.Ok, check.Error.ToString());
            Assert.Equal($"https://127.0.0.1:{pinned.Port}/health/live", check.Data["url"]);
            stop.Cancel();
            Assert.Equal(0, await run);
        }

        // a folder with no settings file: the default address is asked
        var empty = Directory.CreateTempSubdirectory("dbdr-health-").FullName;
        try
        {
            var check = new CliContext(new StringWriter(), new StringWriter(), TextReader.Null);
            await RunnerCli.Router(_ => null).RunAsync(["health", "check", "--repo", empty, "--timeout", "2"], check);
            Assert.Equal(RunnerCli.HealthUrl, check.Data["url"]);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    /// <summary>Starts <c>server run</c> with no <c>--listen</c>, waits until it serves, stops it and returns the port it said it serves on.</summary>
    private async Task<int> StartAndStop()
    {
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        using var stop = new CancellationTokenSource();
        var run = RunnerCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));
        await Until(() => Text(output, written).Contains("Serving on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
        var match = System.Text.RegularExpressions.Regex.Match(Text(output, written), @"Serving on https://127\.0\.0\.1:(\d+)/");
        Assert.True(match.Success, Text(output, written));
        stop.Cancel();
        Assert.Equal(0, await run);
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static TcpListener Hold(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        listener.Start();
        return listener;
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
