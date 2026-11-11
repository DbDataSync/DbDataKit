using System.Diagnostics;
using DbDataKit.Hosting.Children;

namespace DbDataKit.Runner.Limits;

/// <summary>
/// Puts memory and CPU limits on a process (plan 28a DR6): a job object on Windows; on Linux a cgroup v2 child when the runner's account has a delegated, writable subtree with the controllers;
/// otherwise a watchdog for memory (CPU is then not limited). The runner reports which is in use (<see cref="LimitCapabilities"/>), it does not refuse to run.
/// </summary>
public static class ProcessLimits
{
    /// <summary>
    /// Applies <paramref name="spec"/> to a started <paramref name="process"/>. Never throws for a mechanism that is not available: it falls back (cgroup to watchdog) and the handle says what holds.
    /// The caller disposes the handle when the process is done.
    /// </summary>
    public static LimitHandle Apply(Process process, LimitSpec spec, LimitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(spec);
        options ??= new LimitOptions();
        if (spec.MemoryBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(spec), "A memory limit is more than zero bytes.");
        if (spec.CpuPercent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(spec), "A CPU limit is 1 to 100 percent.");
        if (spec.MemoryBytes is null && spec.CpuPercent is null) return new NoLimits();

        if (OperatingSystem.IsWindows() && !options.DisableJobObject && WindowsJob.TryApply(process, spec) is { } job)
            return job;

        if (OperatingSystem.IsLinux() && !options.DisableCgroup && CgroupV2.TryApply(process, spec, options) is { } cgroup)
            return cgroup;

        // no OS enforcement: memory is watched, CPU is not limited
        return spec.MemoryBytes is { } memory ? new MemoryWatchdog(process, memory, options.SampleInterval) : new NoLimits();
    }

    /// <summary>What <see cref="Apply"/> would use on this machine for each limit, without starting anything.</summary>
    public static (LimitEnforcement Memory, LimitEnforcement Cpu, string Mechanism) Probe(LimitOptions? options = null)
    {
        options ??= new LimitOptions();
        if (OperatingSystem.IsWindows() && !options.DisableJobObject) return (LimitEnforcement.Enforced, LimitEnforcement.Enforced, "job object");
        if (OperatingSystem.IsLinux() && !options.DisableCgroup && CgroupV2.Probe() is { } caps)
            return (caps.Memory ? LimitEnforcement.Enforced : LimitEnforcement.Watched, caps.Cpu ? LimitEnforcement.Enforced : LimitEnforcement.None, "cgroup v2");
        return (LimitEnforcement.Watched, LimitEnforcement.None, "watchdog");
    }

    private sealed class NoLimits : LimitHandle
    {
        public NoLimits()
        {
            Enforcement = LimitEnforcement.None;
            CpuEnforcement = LimitEnforcement.None;
        }

        public override void Dispose()
        {
        }
    }

    private sealed class WindowsJob : LimitHandle
    {
        private readonly JobObject _job;

        private WindowsJob(JobObject job, LimitSpec spec)
        {
            _job = job;
            Enforcement = spec.MemoryBytes is null ? LimitEnforcement.None : LimitEnforcement.Enforced;
            CpuEnforcement = spec.CpuPercent is null ? LimitEnforcement.None : LimitEnforcement.Enforced;
            Mechanism = "job object";
        }

        public static WindowsJob? TryApply(Process process, LimitSpec spec)
        {
            if (!OperatingSystem.IsWindows()) return null;
            JobObject? job = null;
            try
            {
                job = new JobObject(spec.MemoryBytes, spec.CpuPercent);
                job.Assign(process);
                return new WindowsJob(job, spec);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                job?.Dispose(); // fall back to the watchdog (the process may already be in a job that does not nest)
                return null;
            }
        }

        // the job kills what goes over a per-process memory limit, and nothing here learns why a process died
        public override void Dispose()
        {
            if (OperatingSystem.IsWindows()) _job.Dispose();
        }
    }
}
