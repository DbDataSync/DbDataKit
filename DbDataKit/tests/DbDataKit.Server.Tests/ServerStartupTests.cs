using System.Net;
using System.Net.Sockets;
using DbDataKit.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.Server;

namespace DbDataKit.Server.Tests;

/// <summary>
/// <c>server run</c> starts through the shared startup sequence: a start that fails leaves <c>startup-failure-dbdatakit.txt</c> in the logs folder
/// of the data folder (<see cref="SuiteServices.LogsRootFor"/>) and exits non-zero; a clean start leaves none.
/// </summary>
public sealed class ServerStartupTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdk-start-").FullName;

    private string FailureFile => Path.Combine(SuiteServices.LogsRootFor(SuiteServices.Console, _directory), SuiteServices.Console.FailureFileName);

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
        var exit = await ConsoleCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", $"https://127.0.0.1:{port}"], new CliContext(output, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        var text = File.ReadAllText(FailureFile);
        Assert.Contains("Startup of dbdatakit", text);
        Assert.Contains("failed at the ports step", text);
        Assert.Contains($"Port {port} cannot be bound", text);
        Assert.Contains($"Port: {port}", text);
        Assert.Contains("Excluded port ranges: ", text);
        Assert.Contains("Account: ", text);
        Assert.Contains($"Data folder: {_directory}", text);
        Assert.Contains("Exception chain:", text);
        Assert.Contains(FailureFile, error.ToString());
        Assert.DoesNotContain("The console is on", output.ToString());
        Assert.False(File.Exists(Path.Combine(_directory, "start-failure.txt")));
    }

    [Fact]
    public async Task A_clean_start_leaves_no_failure_file()
    {
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        using var stop = new CancellationTokenSource();
        var run = ConsoleCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0"], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));

        await Until(() => Text(output, written).Contains("The console is on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
        Assert.True(Directory.Exists(Path.Combine(SuiteServices.LogsRootFor(SuiteServices.Console, _directory), "dbdatakit")));
        Assert.False(File.Exists(FailureFile));
        Assert.False(File.Exists(Path.Combine(_directory, "start-failure.txt")));
        stop.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task With_no_port_configured_the_server_takes_a_free_port_of_its_sub_block_and_keeps_it_after_a_restart()
    {
        var block = SuiteServices.Console.Ports;
        Assert.SkipWhen(ListenPorts.Choose(block, IPAddress.Loopback, []) is null, "every port of the sub-block is taken on this machine");
        var pinned = await StartAndStop();
        Assert.True(block.Contains(pinned), $"the server listened on {pinned}");
        Assert.Contains($"listen: https://localhost:{pinned}", File.ReadAllText(Path.Combine(_directory, "settings.yaml")));
        Assert.Equal(pinned, await StartAndStop());
    }

    /// <summary>Starts <c>server run</c> with no <c>--listen</c>, waits until it serves, stops it and returns the port it said it serves on.</summary>
    private async Task<int> StartAndStop()
    {
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        using var stop = new CancellationTokenSource();
        var run = ConsoleCli.Router(_ => null).RunAsync(["server", "run", "--repo", _directory], new CliContext(output, TextWriter.Null, TextReader.Null, stop.Token));
        await Until(() => Text(output, written).Contains("The console is on", StringComparison.Ordinal) || run.IsCompleted);
        Assert.False(run.IsCompleted, "the server stopped instead of serving " + (File.Exists(FailureFile) ? File.ReadAllText(FailureFile) : ""));
        var match = System.Text.RegularExpressions.Regex.Match(Text(output, written), @"The console is on https://localhost:(\d+)/");
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
