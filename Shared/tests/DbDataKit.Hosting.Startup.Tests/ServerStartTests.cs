using System.Net;
using System.Net.Sockets;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>What a server's <c>server run</c> uses of the library besides the sequence: where its logs folder is, its ports step, what it says and what <c>service doctor</c> prints.</summary>
public sealed class ServerStartTests : StartupTestBase
{
    [Fact]
    public void An_installs_data_folder_has_the_machines_logs_folder_and_any_other_keeps_its_logs_beside_or_inside_it()
    {
        Assert.Equal(SuiteServices.MachineLogsRoot, SuiteServices.LogsRootFor(SuiteServices.Gateway, SuiteServices.MachineDataFolder(SuiteServices.Gateway)));
        // a data root of its own (DBDATAKIT_DATA): the folder named for the service has the logs folder beside it, as in an install
        Assert.Equal(Path.Combine(Root, "logs"), SuiteServices.LogsRootFor(SuiteServices.Runner, Path.Combine(Root, "runner")));
        // any other folder (a run from a terminal, a test's): inside it, never the machine's folder
        var own = Path.Combine(Root, "my-runner");
        Assert.Equal(Path.Combine(own, "logs"), SuiteServices.LogsRootFor(SuiteServices.Runner, own));
        Assert.Equal(Path.Combine(Root, "runner", "logs"), SuiteServices.LogsRootFor(SuiteServices.Gateway, Path.Combine(Root, "runner")));
    }

    [Fact]
    public async Task A_port_another_program_holds_fails_the_ports_step_naming_the_port_and_the_excluded_ranges()
    {
        using var held = Hold(out var port);
        var result = await Sequence([new PortRange(50000, 50059)])
            .Step(StartupStep.Ports, (context, ct) => context.BindAsync(_ => Task.Run(() => Bind(port), ct), [port], ct))
            .RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(StartupStep.Ports, result.FailedStep);
        Assert.Equal(1, result.ExitCode);
        var text = Read(FailureFile);
        Assert.Contains($"Port {port} cannot be bound", text);
        Assert.Contains($"Port: {port}", text);
        Assert.Contains("Excluded port ranges: 50000-50059", text);
        Assert.Contains("SocketException", text);
        Assert.Contains("failed at the ports step", result.Summary());
        Assert.Contains(FailureFile, result.Summary());
    }

    [Fact]
    public void The_port_is_read_from_the_hosts_message_and_otherwise_is_the_first_one_the_server_listens_on()
    {
        var inUse = new SocketException((int)SocketError.AddressAlreadyInUse);
        Assert.Equal(7841, StartupPorts.RefusedPort(new IOException("Failed to bind to address https://127.0.0.1:7841: address already in use.", inUse), [7800]));
        Assert.Equal(7841, StartupPorts.RefusedPort(new IOException("Failed to bind to address https://[::1]:7841: address already in use.", inUse), [7800]));
        Assert.Equal(7800, StartupPorts.RefusedPort(new SocketException((int)SocketError.AccessDenied), [0, 7800]));
        Assert.Null(StartupPorts.RefusedPort(new InvalidOperationException("Failed to bind to address https://127.0.0.1:7841."), [7800]));
    }

    [Fact]
    public async Task Another_failure_of_the_ports_step_goes_through_as_it_is()
    {
        var result = await Sequence()
            .Step(StartupStep.Ports, (context, ct) => context.BindAsync(_ => throw new InvalidOperationException("The task host is missing."), [7840], ct))
            .RunAsync();

        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal("The start failed at the ports step: The task host is missing. The full reason is in " + FailureFile + ".", result.Summary());
    }

    [Fact]
    public async Task A_failed_start_is_also_told_to_the_event_log_hook_and_a_clean_one_is_not()
    {
        var told = new List<string>();
        var options = new StartupOptions { Service = SuiteServices.Runner, DataFolder = Data, LogsRoot = Logs, ExcludedPorts = () => [], Unreportable = Unreported.Add, Failed = told.Add };
        Assert.True((await new StartupSequence(options).Step(StartupStep.Folder, _ => { }).RunAsync()).Succeeded);
        Assert.Empty(told);

        var result = await new StartupSequence(options).Step(StartupStep.State, _ => throw new IOException("no database")).RunAsync();
        Assert.Equal(result.Report, Assert.Single(told));
        Assert.True(result.FailureFileWritten);
        Assert.Empty(Unreported);
    }

    [Fact]
    public async Task Checking_the_service_prints_the_failure_file_or_says_there_is_none()
    {
        Assert.Contains("Startup-failure file: none", StartupFailureFile.DescribeLastStart(Logs, SuiteServices.Runner));

        await Sequence().Step(StartupStep.State, _ => throw new IOException("The state database cannot be opened.")).RunAsync();
        var text = StartupFailureFile.DescribeLastStart(Logs, SuiteServices.Runner);
        Assert.StartsWith($"Startup-failure file: {FailureFile} (the last start failed):", text);
        Assert.Contains("  Startup of dbdatarunner 1.2.3 failed at the state step", text);
        Assert.Contains("  The state database cannot be opened.", text);

        File.WriteAllText(FailureFile, StartupFailureFile.ProgressLine(DateTimeOffset.UnixEpoch, StartupStep.Certificate) + Environment.NewLine);
        text = StartupFailureFile.DescribeLastStart(Logs, SuiteServices.Runner);
        Assert.Contains("a start is going on, or the last one stopped without saying why", text);
        Assert.Contains("  starting since 1970-01-01T00:00:00Z, step: certificate, not finished", text);
    }

    private static TcpListener Hold(out int port)
    {
        // ExclusiveAddressUse is set on Windows only: set to false on Linux it asks for SO_REUSEPORT, and a second listener could then share the port
        var listener = new TcpListener(IPAddress.Loopback, 0);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    /// <summary>Binds the port the way a host does, and fails as a host does: the address in the message, the socket's error inside.</summary>
    private static void Bind(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Listen();
        }
        catch (SocketException e) { throw new IOException($"Failed to bind to address https://127.0.0.1:{port}: address already in use.", e); }
        throw new InvalidOperationException($"Port {port} was free: another listener should have held it.");
    }
}
