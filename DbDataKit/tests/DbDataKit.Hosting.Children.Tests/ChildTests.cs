using System.Diagnostics;
using System.Text;

namespace DbDataKit.Hosting.Children.Tests;

public class PolicyTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Clean_exit_is_not_restarted_unless_always_on()
    {
        Assert.False(new RestartTracker(new RestartPolicy()).OnExit(T, 0, TimeSpan.FromSeconds(1)).Restart);
        Assert.True(new RestartTracker(new RestartPolicy { RestartOnCleanExit = true }).OnExit(T, 0, TimeSpan.FromSeconds(1)).Restart);
    }

    [Fact]
    public void Backoff_doubles_caps_and_gives_up()
    {
        var t = new RestartTracker(new RestartPolicy { MaxRestarts = 4, InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(3) });
        var delays = Enumerable.Range(0, 4).Select(i => t.OnExit(T.AddSeconds(i), 1, TimeSpan.FromSeconds(1)).Delay.TotalSeconds).ToArray();
        Assert.Equal([1, 2, 3, 3], delays);
        var last = t.OnExit(T.AddSeconds(5), 1, TimeSpan.FromSeconds(1));
        Assert.False(last.Restart);
        Assert.Contains("gave up", last.Reason);
    }

    [Fact]
    public void Stable_run_resets_backoff_and_old_restarts_age_out()
    {
        var t = new RestartTracker(new RestartPolicy { MaxRestarts = 2, Window = TimeSpan.FromMinutes(1) });
        t.OnExit(T, 1, TimeSpan.FromSeconds(1));
        t.OnExit(T.AddSeconds(1), 1, TimeSpan.FromSeconds(1));
        Assert.Equal(2, t.ConsecutiveFailures);
        Assert.Equal(1.0, t.OnExit(T.AddMinutes(5), 1, TimeSpan.FromMinutes(1)).Delay.TotalSeconds); // stable run resets
        Assert.Equal(1, t.ConsecutiveFailures);
    }

    [Fact]
    public void Never_policy_does_not_restart() =>
        Assert.False(new RestartTracker(RestartPolicy.Never).OnExit(T, 1, TimeSpan.Zero).Restart);

    [Fact]
    public void Endpoint_round_trips()
    {
        foreach (var e in new[]
        {
            new ChildEndpoint(ChildEndpointKind.NamedPipe, "abc"),
            new ChildEndpoint(ChildEndpointKind.UnixSocket, "/tmp/x.sock"),
            new ChildEndpoint(ChildEndpointKind.LoopbackTcp, "127.0.0.1:5000"),
        })
        {
            Assert.Equal(e, ChildEndpoint.Parse(e.ToString()));
        }
    }
}

public class TransportTests
{
    public static IEnumerable<TheoryDataRow<ChildEndpointKind>> Kinds() =>
        [new(ChildEndpointKind.NamedPipe), new(ChildEndpointKind.UnixSocket), new(ChildEndpointKind.LoopbackTcp)];

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Correct_token_connects_and_wrong_token_is_rejected(ChildEndpointKind kind)
    {
        Assert.SkipWhen(kind == ChildEndpointKind.NamedPipe && !OperatingSystem.IsWindows(), "Named pipes are exercised on Windows");
        await using var listener = ChildTransports.CreateListener(kind);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var accept = listener.AcceptAsync("right-token", cts.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ChildTransports.ConnectAsync(listener.Endpoint, "wrong-token", cts.Token));
        Assert.False(accept.IsCompleted); // the bad client did not satisfy the accept

        await using var client = await ChildTransports.ConnectAsync(listener.Endpoint, "right-token", cts.Token);
        await using var server = await accept;
        var buf = new byte[4];
        var read = server.ReadExactlyAsync(buf, cts.Token).AsTask(); // unbuffered pipes: read must be pending
        await client.WriteAsync(Encoding.UTF8.GetBytes("ping"), cts.Token);
        await read;
        Assert.Equal("ping", Encoding.UTF8.GetString(buf));
    }

    [Fact]
    public async Task Tcp_listener_holds_its_port_for_the_launch()
    {
        await using var listener = ChildTransports.CreateListener(ChildEndpointKind.LoopbackTcp);
        var port = int.Parse(listener.Endpoint.Address.Split(':')[1]);
        // Binding the same port again must fail while the listener is alive: no allocate-then-release race.
        var second = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        Assert.Throws<System.Net.Sockets.SocketException>(() => second.Start());
    }

    [Fact]
    public void Default_transport_is_pipe_on_windows_and_uds_elsewhere()
    {
        using var l = new DisposableAdapter(ChildTransports.CreateListener());
        Assert.Equal(OperatingSystem.IsWindows() ? ChildEndpointKind.NamedPipe : ChildEndpointKind.UnixSocket, l.Inner.Endpoint.Kind);
    }

    private sealed class DisposableAdapter(IChildTransportListener inner) : IDisposable
    {
        public IChildTransportListener Inner { get; } = inner;

        public void Dispose() => Inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public class ProcessTests
{
    private static string ChildDll => Path.Combine(AppContext.BaseDirectory, "DbDataKit.Hosting.Children.TestChild.dll");

    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static ChildProcessSpec Spec(ChildEndpointKind? transport, params string[] args) => new()
    {
        FileName = Dotnet,
        Arguments = [ChildDll, .. args],
        Transport = transport,
    };

    private static async Task<bool> WaitFor(Func<bool> condition, int ms = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ChildEndpointKind.LoopbackTcp)]
    public async Task Child_connects_with_token_from_environment_and_echoes(ChildEndpointKind? transport)
    {
        await using var child = ChildProcessHost.Launch(Spec(transport, "echo"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var stream = await child.AcceptAsync(cts.Token);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("hello"), cts.Token);
        var buf = new byte[64];
        var n = await stream.ReadAsync(buf, cts.Token);
        Assert.Equal("echo:hello", Encoding.UTF8.GetString(buf, 0, n));
        Assert.Equal(0, (await child.Exited).ExitCode);
    }

    [Fact]
    public async Task Output_is_drained_and_reported_line_by_line_without_a_transport()
    {
        var outLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var spec = new ChildProcessSpec
        {
            FileName = Dotnet,
            Arguments = [ChildDll, "chatter", "20000"], // far more than a pipe buffer
            Transport = ChildEndpointKind.None,
            OnOutputLine = outLines.Enqueue,
            OnErrorLine = errLines.Enqueue,
        };
        await using var child = ChildProcessHost.Launch(spec);
        var report = await child.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, report.ExitCode);
        Assert.True(await WaitFor(() => outLines.Count == 20000 && errLines.Count == 20000, 20000));
        Assert.Throws<InvalidOperationException>(() => child.Endpoint);
        Assert.False(child.StartInfo.Environment.ContainsKey(ChildEnvironment.EndpointVariable));
        Assert.True(child.StartInfo.Environment.ContainsKey(ChildEnvironment.ParentPidVariable));
    }

    [Fact]
    public async Task Output_is_drained_when_no_handler_is_given()
    {
        await using var child = ChildProcessHost.Launch(Spec(null, "chatter", "20000"));
        Assert.Equal(0, (await child.Exited.WaitAsync(TimeSpan.FromSeconds(60))).ExitCode);
    }

    [Fact]
    public async Task Token_is_in_the_environment_and_never_in_argv()
    {
        await using var child = ChildProcessHost.Launch(Spec(null, "printenv", ChildEnvironment.TokenVariable));
        var token = child.StartInfo.Environment[ChildEnvironment.TokenVariable]!;
        Assert.True(token.Length >= 40);
        Assert.DoesNotContain(child.StartInfo.ArgumentList, a => a.Contains(token));
        Assert.DoesNotContain(token, child.StartInfo.Arguments);
        var output = await child.Exited;
        Assert.Equal(0, output.ExitCode);
    }

    [Fact]
    public async Task Disposing_the_host_kills_the_child_and_its_descendants()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "dbdata-child-" + Guid.NewGuid().ToString("N"));
        var child = ChildProcessHost.Launch(Spec(null, "spawn", pidFile));
        Assert.True(await WaitFor(() => File.Exists(pidFile) && File.Exists(pidFile + ".grand")));
        var childPid = int.Parse(File.ReadAllText(pidFile));
        var grandPid = int.Parse(await ReadWhenReady(pidFile + ".grand"));

        await child.DisposeAsync();

        Assert.True(await WaitFor(() => !IsAlive(childPid)), "child still alive");
        Assert.True(await WaitFor(() => !IsAlive(grandPid)), "grandchild still alive");
        File.Delete(pidFile);
        File.Delete(pidFile + ".grand");
    }

    [Fact]
    public async Task Child_exits_when_its_parent_dies()
    {
        // The "parent" is a throwaway sleeper; the watcher is told to monitor it.
        var parentFile = Path.Combine(Path.GetTempPath(), "dbdata-parent-" + Guid.NewGuid().ToString("N"));
        var readyFile = parentFile + ".ready";
        await using var fakeParent = ChildProcessHost.Launch(Spec(null, "sleep", parentFile));
        Assert.True(await WaitFor(() => File.Exists(parentFile)));
        var parentPid = int.Parse(await ReadWhenReady(parentFile));
        using var parentProc = Process.GetProcessById(parentPid);

        var spec = new ChildProcessSpec
        {
            FileName = Dotnet,
            Arguments = [ChildDll, "watch", readyFile],
            Environment = new Dictionary<string, string>
            {
                ["WATCH_PID"] = parentPid.ToString(),
                ["WATCH_START"] = parentProc.StartTime.ToUniversalTime().Ticks.ToString(),
            },
        };
        await using var watcher = ChildProcessHost.Launch(spec);
        Assert.True(await WaitFor(() => File.Exists(readyFile)));

        parentProc.Kill();
        var report = await watcher.Exited.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(42, report.ExitCode);
        File.Delete(parentFile);
        File.Delete(readyFile);
    }

    [Fact]
    public async Task Supervisor_restarts_with_backoff_then_gives_up()
    {
        var delays = new List<TimeSpan>();
        var supervisor = new ChildSupervisor(
            new RestartPolicy { MaxRestarts = 2, InitialDelay = TimeSpan.FromSeconds(1) },
            (d, _) => { delays.Add(d); return Task.CompletedTask; });
        var last = await supervisor.RunAsync(Spec(null, "exit", "3"), null, CancellationToken.None);
        Assert.Equal(3, last.ExitCode);
        Assert.Equal(3, supervisor.Exits.Count); // first run + 2 restarts
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Supervisor_stops_on_clean_exit()
    {
        var supervisor = new ChildSupervisor(new RestartPolicy(), (_, _) => Task.CompletedTask);
        var last = await supervisor.RunAsync(Spec(null, "exit", "0"), null, CancellationToken.None);
        Assert.Equal(0, last.ExitCode);
        Assert.Single(supervisor.Exits);
    }

    [Fact]
    public void Launch_info_is_read_back_by_the_child_side()
    {
        var vars = new Dictionary<string, string?>
        {
            [ChildEnvironment.TokenVariable] = "t",
            [ChildEnvironment.EndpointVariable] = "tcp:127.0.0.1:9",
            [ChildEnvironment.ParentPidVariable] = "12",
            [ChildEnvironment.ParentStartVariable] = "34",
        };
        var info = ChildEnvironment.TryRead(k => vars.GetValueOrDefault(k))!;
        Assert.Equal(new ChildLaunchInfo("t", new ChildEndpoint(ChildEndpointKind.LoopbackTcp, "127.0.0.1:9"), 12, 34), info);
        Assert.Null(ChildEnvironment.TryRead(_ => null));
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<string> ReadWhenReady(string path)
    {
        for (var i = 0; i < 200; i++)
        {
            try
            {
                var t = File.ReadAllText(path);
                if (t.Length > 0) return t;
            }
            catch (IOException) { }

            await Task.Delay(50);
        }

        throw new TimeoutException(path);
    }
}
