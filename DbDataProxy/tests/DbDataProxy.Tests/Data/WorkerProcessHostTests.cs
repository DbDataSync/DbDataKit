using System.Diagnostics;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Data;

/// <summary>The 32-bit worker's process lifecycle through Hosting.Children: kill on exit, restart, startup failure.</summary>
public sealed class WorkerProcessHostTests
{
    private sealed class Monitor(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;

        public ProxyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }

    private static WorkerProcessHost Host(string? workerPath = null, int startupSeconds = 30) =>
        new(new Monitor(new ProxyOptions { Worker = { Path = workerPath, StartupTimeoutSeconds = startupSeconds } }), NullLogger<WorkerProcessHost>.Instance);

    private static void RequireWorker()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "Needs 64-bit Windows to launch the win-x86 worker.");
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int milliseconds = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task Disposing_the_host_kills_the_worker()
    {
        RequireWorker();
        await using var host = Host(WorkerBuild.EnsurePublished());
        await host.GetAsync(TestContext.Current.CancellationToken);
        int pid = host.WorkerProcessId!.Value;
        Assert.True(IsAlive(pid));

        await host.DisposeAsync();

        Assert.True(await WaitUntil(() => !IsAlive(pid)), "the worker outlived its host");
        Assert.Null(host.WorkerProcessId);
    }

    [Fact]
    public async Task A_worker_that_dies_is_started_again_on_the_next_use()
    {
        RequireWorker();
        await using var host = Host(WorkerBuild.EnsurePublished());
        await host.GetAsync(TestContext.Current.CancellationToken);
        int first = host.WorkerProcessId!.Value;

        using (var process = Process.GetProcessById(first)) process.Kill();
        Assert.True(await WaitUntil(() => host.WorkerProcessId is null), "the exit was not noticed");

        await host.GetAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(first, host.WorkerProcessId);
        Assert.True(IsAlive(host.WorkerProcessId!.Value));
    }

    [Fact]
    public async Task A_missing_worker_is_reported_as_unavailable_with_the_expected_path()
    {
        RequireWorker();
        await using var host = Host(Path.Combine(Path.GetTempPath(), "no-such-worker-" + Guid.NewGuid().ToString("N"), "worker.exe"));
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Contains("Proxy:Worker:Path", ex.Status.Detail);
    }

    [Fact]
    public async Task A_worker_that_exits_during_startup_fails_the_call_instead_of_waiting_for_the_timeout()
    {
        RequireWorker();
        // hostname.exe rejects the unknown --port argument and exits straight away.
        string exe = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        Assert.True(File.Exists(exe));
        await using var host = Host(exe, startupSeconds: 60);

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.GetAsync(TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Contains("exited during startup", ex.Status.Detail);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "should not have waited for the startup timeout");
        Assert.Null(host.WorkerProcessId);
    }

    [Fact]
    public async Task The_worker_runs_inside_a_job_object_so_it_dies_with_the_service_even_if_the_service_is_killed()
    {
        RequireWorker();
        await using var host = Host(WorkerBuild.EnsurePublished());
        await host.GetAsync(TestContext.Current.CancellationToken);
        using var worker = Process.GetProcessById(host.WorkerProcessId!.Value);

        // The job is created with KILL_ON_JOB_CLOSE (covered by the shared Children tests); here: the worker is in one,
        // an ordinary child of this test process is not.
        Assert.True(InAnyJob(worker), "the worker was not assigned to a job object");
        using var plain = Process.Start(new ProcessStartInfo("ping.exe", "-n 20 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try { Assert.False(InAnyJob(plain)); }
        finally { plain.Kill(true); }
    }

    private static bool InAnyJob(Process process)
    {
        Assert.True(IsProcessInJob(process.SafeHandle.DangerousGetHandle(), IntPtr.Zero, out bool result));
        return result;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool result);
}
