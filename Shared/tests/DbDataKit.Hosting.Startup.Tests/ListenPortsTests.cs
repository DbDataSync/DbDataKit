using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>
/// The port a server listens on: a free one of its sub-block at the first start, pinned in its settings file and kept after; a configured one never
/// moved; Windows' excluded ranges skipped, and named when they cover the whole sub-block.
/// </summary>
public sealed class ListenPortsTests : StartupTestBase
{
    private static readonly Uri RunnerDefault = new("https://127.0.0.1:7840");

    private string SettingsFile => Path.Combine(Data, "server.yaml");

    [Fact]
    public void The_sub_blocks_lie_in_the_suite_range_without_overlapping_and_each_service_has_its_own()
    {
        Assert.Equal(new PortRange(7800, 7899), SuiteServices.SuitePorts);
        Assert.Equal(new PortRange(7800, 7819), SuiteServices.Console.Ports);
        Assert.Equal(new PortRange(7820, 7839), SuiteServices.Gateway.Ports);
        Assert.Equal(new PortRange(7840, 7859), SuiteServices.Runner.Ports);
        Assert.Equal(new PortRange(7860, 7879), SuiteServices.Workspace.Ports);
        var blocks = SuiteServices.All.Select(s => s.Ports).Append(SuiteServices.SparePorts).ToList();
        Assert.All(blocks, b => Assert.True(SuiteServices.SuitePorts.Contains(b.Start) && SuiteServices.SuitePorts.Contains(b.End)));
        for (var i = 0; i < blocks.Count; i++)
            for (var j = i + 1; j < blocks.Count; j++)
                Assert.True(blocks[i].End < blocks[j].Start || blocks[j].End < blocks[i].Start, $"{blocks[i]} and {blocks[j]} overlap");
    }

    [Fact]
    public void The_first_port_held_the_next_is_chosen_and_after_the_pin_it_is_kept_when_the_first_is_free_again()
    {
        var held = new HashSet<int> { 7840 };
        var first = ListenPorts.Resolve(SuiteServices.Runner, null, SettingsFile, RunnerDefault, [], (_, port) => !held.Contains(port));
        Assert.Equal(ListenSource.Chosen, first.Source);
        Assert.Equal(new Uri("https://127.0.0.1:7841"), first.Address);
        Assert.Null(first.Failure);

        ListenPorts.Pin(SettingsFile, first.Address);
        held.Clear();
        var second = ListenPorts.Resolve(SuiteServices.Runner, null, SettingsFile, RunnerDefault, [], (_, _) => true);
        Assert.Equal(ListenSource.Settings, second.Source);
        Assert.Equal(7841, second.Port);
    }

    [Fact]
    public void A_free_port_is_found_by_binding_it()
    {
        using var held = Hold(out var port);
        var block = new PortRange(port, Math.Min(port + 20, 65535));
        var chosen = ListenPorts.Choose(block, IPAddress.Loopback, []);
        Assert.NotNull(chosen);
        Assert.NotEqual(port, chosen);
        Assert.True(block.Contains(chosen!.Value));
        Assert.False(ListenPorts.CanBind(IPAddress.Loopback, port));
    }

    [Fact]
    public void Excluded_ranges_are_skipped_without_trying_them()
    {
        var tried = new List<int>();
        var chosen = ListenPorts.Choose(SuiteServices.Runner.Ports, IPAddress.Loopback, [new PortRange(7800, 7844)], (_, port) => { tried.Add(port); return true; });
        Assert.Equal(7845, chosen);
        Assert.Equal([7845], tried);
    }

    [Fact]
    public async Task A_sub_block_the_excluded_ranges_cover_fails_the_ports_step_naming_them()
    {
        var excluded = new[] { new PortRange(7830, 7849), new PortRange(7850, 7869) };
        ListenChoice? listen = null;
        var started = false;
        var result = await Sequence(excluded)
            .Step(StartupStep.Folder, context => listen = ListenPorts.Resolve(SuiteServices.Runner, null, SettingsFile, RunnerDefault, context.Options.ExcludedPorts(), (_, _) => true))
            .Step(StartupStep.Ports, (context, ct) => context.BindAsync(listen!, _ => { started = true; return Task.CompletedTask; }, cancellationToken: ct))
            .RunAsync();

        Assert.False(started);
        Assert.Equal(StartupStep.Ports, result.FailedStep);
        // the host can still be built: the address holds the sub-block's first port
        Assert.Equal(7840, listen!.Port);
        var text = Read(FailureFile);
        Assert.Contains("No port of 7840-7859 can be bound: the excluded port ranges 7830-7849, 7850-7869 cover them all.", text);
        Assert.Contains("Ports: 7840-7859", text);
        Assert.Contains("Excluded port ranges: 7830-7849, 7850-7869", text);
        Assert.Contains("--listen", text);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_chosen_port_is_pinned_once_the_server_listens_on_it_and_a_configured_one_never()
    {
        File.WriteAllText(SettingsFile, "# the runner's settings\nstate:\n  engine: sqlite\n");
        var chosen = ListenPorts.Resolve(SuiteServices.Runner, null, SettingsFile, RunnerDefault, [], (_, port) => port != 7840);
        var result = await Sequence().Step(StartupStep.Ports, (context, ct) => context.BindAsync(chosen, _ => Task.CompletedTask, cancellationToken: ct)).RunAsync();
        Assert.True(result.Succeeded);
        var text = File.ReadAllText(SettingsFile);
        Assert.StartsWith("# the runner's settings\nstate:\n  engine: sqlite\n", text);
        Assert.Contains("\nlisten: https://127.0.0.1:7841\n", text);

        // a flag is used as given and written nowhere
        var flagged = ListenPorts.Resolve(SuiteServices.Runner, new Uri("https://127.0.0.1:9100"), SettingsFile, RunnerDefault, []);
        Assert.Equal(ListenSource.Flag, flagged.Source);
        Assert.True((await Sequence().Step(StartupStep.Ports, (context, ct) => context.BindAsync(flagged, _ => Task.CompletedTask, cancellationToken: ct)).RunAsync()).Succeeded);
        Assert.Equal(text, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public async Task A_pinned_port_another_program_holds_fails_the_ports_step_naming_it()
    {
        using var held = Hold(out var port);
        File.WriteAllText(SettingsFile, $"listen: https://127.0.0.1:{port}   # set by hand\n");
        var listen = ListenPorts.Resolve(SuiteServices.Runner, null, SettingsFile, RunnerDefault, []);
        Assert.Equal(ListenSource.Settings, listen.Source);
        Assert.Equal(port, listen.Port);

        var result = await Sequence()
            .Step(StartupStep.Ports, (context, ct) => context.BindAsync(listen, _ => Task.Run(() => Bind(listen.Port), ct), cancellationToken: ct))
            .RunAsync();
        Assert.Equal(StartupStep.Ports, result.FailedStep);
        Assert.Contains($"Port {port} cannot be bound", Read(FailureFile));
        Assert.Equal($"listen: https://127.0.0.1:{port}   # set by hand\n", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void Pinning_again_replaces_the_line_and_an_indented_listen_belongs_to_another_section()
    {
        File.WriteAllText(SettingsFile, "telemetry:\r\n  listen: nothing\r\nlisten: https://127.0.0.1:7845\r\n");
        Assert.Equal(7845, ListenPorts.ReadPinned(SettingsFile)!.Port);
        ListenPorts.Pin(SettingsFile, new Uri("https://127.0.0.1:7846/"));
        Assert.Equal("telemetry:\r\n  listen: nothing\r\nlisten: https://127.0.0.1:7846\r\n", File.ReadAllText(SettingsFile));

        File.WriteAllText(SettingsFile, "telemetry:\n  listen: nothing\n");
        Assert.Null(ListenPorts.ReadPinned(SettingsFile));
        Assert.Null(ListenPorts.ReadPinned(Path.Combine(Data, "none.yaml")));
    }

    [Theory]
    [InlineData("listen: http://127.0.0.1:7840")]
    [InlineData("listen: 7840")]
    [InlineData("listen: https://127.0.0.1")]
    public void A_listen_line_that_is_not_an_https_address_with_a_port_is_refused_naming_the_file(string line)
    {
        File.WriteAllText(SettingsFile, line + "\n");
        var error = Assert.Throws<InvalidOperationException>(() => ListenPorts.Resolve(SuiteServices.Runner, null, SettingsFile, RunnerDefault, []));
        Assert.Contains(SettingsFile, error.Message);
        Assert.Contains("https://127.0.0.1:7840", error.Message);
    }

    [Fact]
    public void The_address_bound_is_the_loopback_for_localhost_every_address_for_another_name_and_the_address_itself_otherwise()
    {
        Assert.Equal(IPAddress.Loopback, ListenPorts.AddressOf(new Uri("https://localhost:7800")));
        Assert.Equal(IPAddress.Any, ListenPorts.AddressOf(new Uri("https://runner.example:7840")));
        Assert.Equal(IPAddress.Parse("10.0.0.5"), ListenPorts.AddressOf(new Uri("https://10.0.0.5:7840")));
        Assert.Equal(IPAddress.IPv6Loopback, ListenPorts.AddressOf(new Uri("https://[::1]:7840")));
    }

    /// <summary>
    /// On Windows a range excluded with <c>netsh</c> is skipped, whether read from Windows or only refused when bound, and a sub-block it covers fails
    /// the ports step naming it. The test excludes a block of its own and removes it afterwards; adding a range needs an elevated process (the hosted
    /// Windows runner is one).
    /// </summary>
    [Fact]
    public async Task On_Windows_an_excluded_range_is_skipped_and_one_that_covers_the_sub_block_is_named()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(IsElevated(), "adding an excluded port range needs an elevated process");
        var block = FreeBlock(4);
        Assert.SkipWhen(block is null, "no four free ports in a row below the dynamic range");
        var b = block!;
        try
        {
            Exclude(b.Start, 2);
            var read = ExcludedPortRanges.Read();
            Assert.Contains(read, r => r.Contains(b.Start) && r.Contains(b.Start + 1));
            Assert.Equal(b.Start + 2, ListenPorts.Choose(b, IPAddress.Loopback, read));
            // without the list, binding finds the same: the excluded ports refuse (WSAEACCES) and are skipped
            Assert.Equal(b.Start + 2, ListenPorts.Choose(b, IPAddress.Loopback, []));
        }
        finally
        {
            Include(b.Start, 2);
        }

        try
        {
            Exclude(b.Start, 4);
            ListenChoice? listen = null;
            var result = await new StartupSequence(new StartupOptions { Service = SuiteServices.Runner, DataFolder = Data, LogsRoot = Logs, Unreportable = Unreported.Add })
                .Step(StartupStep.Folder, context => listen = ListenChoiceFor(b, context.Options.ExcludedPorts()))
                .Step(StartupStep.Ports, (context, ct) => context.BindAsync(listen!, _ => Task.CompletedTask, cancellationToken: ct))
                .RunAsync();
            Assert.Equal(StartupStep.Ports, result.FailedStep);
            var text = Read(FailureFile);
            Assert.Contains($"No port of {b} can be bound: the excluded port range", text);
            Assert.Contains("Excluded port ranges: ", text);
            Assert.Contains(b.Start.ToString(System.Globalization.CultureInfo.InvariantCulture), text);
        }
        finally
        {
            Include(b.Start, 4);
        }
    }

    /// <summary>A choice over <paramref name="block"/> the way <see cref="ListenPorts.Resolve"/> makes one for a service's sub-block.</summary>
    private static ListenChoice ListenChoiceFor(PortRange block, IReadOnlyList<PortRange> excluded)
    {
        var port = ListenPorts.Choose(block, IPAddress.Loopback, excluded);
        Assert.Null(port);
        return new ListenChoice(new Uri($"https://127.0.0.1:{block.Start}"), ListenSource.Chosen, "unused.yaml", StartupStepException.NoFreePort(block, excluded));
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Four ports in a row that are free and in no excluded range, below the dynamic range.</summary>
    private static PortRange? FreeBlock(int count)
    {
        var excluded = ExcludedPortRanges.Read();
        for (var start = 41000; start < 47000; start += 37)
        {
            var block = new PortRange(start, start + count - 1);
            if (Enumerable.Range(start, count).All(p => !excluded.Any(r => r.Contains(p)) && ListenPorts.CanBind(IPAddress.Loopback, p) && ListenPorts.CanBind(IPAddress.Any, p)))
                return block;
        }
        return null;
    }

    private static void Exclude(int start, int count) => Netsh($"int ipv4 add excludedportrange protocol=tcp startport={start} numberofports={count}", check: true);

    private static void Include(int start, int count) => Netsh($"int ipv4 delete excludedportrange protocol=tcp startport={start} numberofports={count}", check: false);

    private static void Netsh(string arguments, bool check)
    {
        using var netsh = Process.Start(new ProcessStartInfo("netsh", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = netsh.StandardOutput.ReadToEnd() + netsh.StandardError.ReadToEnd();
        netsh.WaitForExit();
        if (check && netsh.ExitCode != 0) Assert.Fail($"netsh {arguments} failed ({netsh.ExitCode}): {output}");
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
