using System.Diagnostics;

namespace DbDataKit.Runner.Limits.Tests;

/// <summary>Plan 28a R07, the library half: the watchdog kills an allocating child, cgroup enforcement where the machine allows it, and the capability report.</summary>
public sealed class ProcessLimitsTests
{
    private const long MiB = 1024 * 1024;
    private static string ChildDll => Path.Combine(AppContext.BaseDirectory, "DbDataKit.Runner.Limits.TestChild.dll");

    private static Process Start(params string[] childArgs)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(ChildDll);
        foreach (var a in childArgs) info.ArgumentList.Add(a);
        return Process.Start(info)!;
    }

    private static bool Exited(Process p, TimeSpan within) => p.WaitForExit(within);

    [Fact]
    public void The_watchdog_kills_a_child_that_allocates_over_its_limit_and_reports_what_it_saw()
    {
        using var child = Start("alloc", "600");
        // the runtime itself is tens of MB resident; the limit is well under what the child will touch and well over its start
        var limit = 250 * MiB;
        using var handle = ProcessLimits.Apply(child, new LimitSpec(MemoryBytes: limit), new LimitOptions { DisableCgroup = true, DisableJobObject = true, SampleInterval = TimeSpan.FromMilliseconds(100) });

        Assert.Equal(LimitEnforcement.Watched, handle.Enforcement);
        Assert.Equal(LimitEnforcement.None, handle.CpuEnforcement);
        Assert.Equal("watchdog", handle.Mechanism);
        Assert.True(Exited(child, TimeSpan.FromSeconds(60)), "the child was not killed");
        Assert.True(handle.Exceeded);
        var info = handle.ExceededInfo!;
        Assert.Equal("memory", info.Limit);
        Assert.Equal(limit, info.LimitValue);
        Assert.True(info.ObservedValue > limit, $"observed {info.ObservedValue}");
        Assert.Contains("killed", handle.ExceededReason);
    }

    [Fact]
    public void The_watchdog_sums_the_process_tree_so_an_allocation_in_a_grandchild_is_killed()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The tree is summed from /proc on Linux; Windows holds the tree in a job object.");
        using var child = Start("grandchild", "600");
        var limit = 250 * MiB;
        using var handle = ProcessLimits.Apply(child, new LimitSpec(MemoryBytes: limit), new LimitOptions { DisableCgroup = true, DisableJobObject = true, SampleInterval = TimeSpan.FromMilliseconds(100) });
        var line = child.StandardOutput.ReadLine();
        Assert.StartsWith("grandchild ", line);
        var grandchildId = int.Parse(line!["grandchild ".Length..]);

        Assert.True(Exited(child, TimeSpan.FromSeconds(60)), "the tree was not killed");
        Assert.True(handle.Exceeded);
        Assert.True(handle.ExceededInfo!.ObservedValue > limit, $"observed {handle.ExceededInfo.ObservedValue}");
        // the process itself (a runtime that starts another process and waits) never holds 250 MiB: only the sum over its tree can have passed the limit, which a sampler of the process alone never saw
        Assert.True(Gone(grandchildId, TimeSpan.FromSeconds(10)), "the grandchild outlived its parent's kill");
    }

    private static bool Gone(int pid, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }
            Thread.Sleep(50);
        }

        return false;
    }

    [Fact]
    public void The_watchdog_leaves_a_child_under_its_limit_alone()
    {
        using var child = Start("alloc", "16");
        using var handle = ProcessLimits.Apply(child, new LimitSpec(MemoryBytes: 2048 * MiB), new LimitOptions { DisableCgroup = true, DisableJobObject = true, SampleInterval = TimeSpan.FromMilliseconds(50) });
        try
        {
            string? line;
            do { line = child.StandardOutput.ReadLine(); } while (line is not null && line != "ready");
            Assert.Equal("ready", line);
            Thread.Sleep(300);
            Assert.False(child.HasExited);
            Assert.False(handle.Exceeded);
            Assert.Null(handle.ExceededReason);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void No_limits_asked_for_is_none_and_bad_limits_are_refused()
    {
        using var child = Start("idle");
        try
        {
            using var handle = ProcessLimits.Apply(child, new LimitSpec());
            Assert.Equal(LimitEnforcement.None, handle.Enforcement);
            Assert.False(handle.Exceeded);
            Assert.Throws<ArgumentOutOfRangeException>(() => ProcessLimits.Apply(child, new LimitSpec(MemoryBytes: 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ProcessLimits.Apply(child, new LimitSpec(CpuPercent: 150)));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void The_watchdog_stops_when_the_handle_is_disposed()
    {
        using var child = Start("idle");
        try
        {
            var handle = ProcessLimits.Apply(child, new LimitSpec(MemoryBytes: 1 * MiB), new LimitOptions { DisableCgroup = true, DisableJobObject = true, SampleInterval = TimeSpan.FromSeconds(30) });
            handle.Dispose(); // the first sample is a long way off; disposing must not wait for it
            handle.Dispose();
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void A_cgroup_holds_the_memory_limit_where_the_machine_delegates_one()
    {
        var caps = ProcessLimits.Probe();
        if (!OperatingSystem.IsLinux() || caps.Mechanism != "cgroup v2" || caps.Memory != LimitEnforcement.Enforced)
        {
            Assert.Skip($"No delegated cgroup v2 subtree with the memory controller on this machine (probe says: {caps.Mechanism}, memory {caps.Memory}); the cgroup path ran nowhere here.");
        }

        using var child = Start("alloc", "600");
        using var handle = ProcessLimits.Apply(child, new LimitSpec(MemoryBytes: 250 * MiB));

        Assert.Equal(LimitEnforcement.Enforced, handle.Enforcement);
        Assert.Equal("cgroup v2", handle.Mechanism);
        Assert.True(Exited(child, TimeSpan.FromSeconds(60)), "the kernel did not kill the child");
        Assert.True(handle.Exceeded);
    }

    [Fact]
    public void A_base_that_is_not_a_delegated_cgroup_falls_back_to_the_watchdog()
    {
        // a plain directory stands in for a delegated cgroup: it has no memory.max the kernel made, so the probe says "not available" and the watchdog is used
        var fake = Directory.CreateTempSubdirectory("limits-fake-cgroup").FullName;
        try
        {
            using var child = Start("idle");
            try
            {
                using var handle = ProcessLimits.Apply(child, new LimitSpec(MemoryBytes: 2048 * MiB), new LimitOptions { CgroupBase = fake });
                if (OperatingSystem.IsLinux())
                {
                    Assert.Equal("watchdog", handle.Mechanism);
                    Assert.Empty(Directory.GetDirectories(fake));
                }
            }
            finally
            {
                child.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            Directory.Delete(fake, true);
        }
    }

    [Fact]
    public void The_capability_report_names_the_enforcement_in_use()
    {
        var caps = LimitCapabilities.Detect();
        var probe = ProcessLimits.Probe();

        Assert.Contains(caps["memory_limit"], new[] { "enforced", "watched" });
        Assert.Contains(caps["cpu_limit"], new[] { "enforced", "none" });
        Assert.Equal(probe.Memory == LimitEnforcement.Enforced ? "enforced" : "watched", caps["memory_limit"]);
        Assert.Equal(probe.Mechanism, caps["limit_mechanism"]);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other", caps["os"]);
        Assert.Equal("unknown", caps["network"]); // no hosts configured
        if (OperatingSystem.IsWindows()) Assert.Equal("job object", caps["limit_mechanism"]);
    }

    [Fact]
    public void With_the_cgroup_disabled_the_report_says_watched_and_cpu_none()
    {
        var caps = LimitCapabilities.Detect(new LimitCapabilityOptions { Limits = new LimitOptions { DisableCgroup = true, DisableJobObject = true } });

        Assert.Equal("watched", caps["memory_limit"]);
        Assert.Equal("none", caps["cpu_limit"]);
        Assert.Equal("watchdog", caps["limit_mechanism"]);
    }

    [Fact]
    public async Task The_network_test_says_outside_for_a_reachable_host_and_none_for_one_that_is_not()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var reachable = await LimitCapabilities.DetectAsync(new LimitCapabilityOptions { NetworkProbeHosts = [$"127.0.0.1:{port}"] });
        Assert.Equal("outside", reachable["network"]);

        listener.Stop();
        var closed = await LimitCapabilities.DetectAsync(new LimitCapabilityOptions { NetworkProbeHosts = [$"127.0.0.1:{port}"], NetworkProbeTimeout = TimeSpan.FromSeconds(1) });
        Assert.Equal("none", closed["network"]);
    }

    [Fact]
    public async Task The_network_test_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LimitCapabilities.DetectAsync(new LimitCapabilityOptions { NetworkProbeHosts = ["10.255.255.1:81"], NetworkProbeTimeout = TimeSpan.FromSeconds(30) }, cts.Token));
    }
}
