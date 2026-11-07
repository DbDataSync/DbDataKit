using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace DbDataKit.Runner.Limits;

/// <summary>
/// Linux cgroup v2 limits for a process: a child cgroup of the runner's own (a delegated subtree: the account can create directories in it and the memory and cpu controllers are on for its children),
/// with <c>memory.max</c> and <c>cpu.max</c>; the pid is moved in, and the cgroup is removed after the process exits.
/// </summary>
internal static class CgroupV2
{
    private const int CpuPeriodMicroseconds = 100_000;

    private static readonly ConcurrentDictionary<string, (bool Memory, bool Cpu)?> Probed = new();

    /// <summary>The directory children may be made in, or null when this is not a cgroup v2 machine or the runner's cgroup cannot be found.</summary>
    internal static string? FindBase(LimitOptions? options = null)
    {
        if (options?.CgroupBase is { } given) return given;
        try
        {
            string? mount = null;
            foreach (var line in File.ReadLines("/proc/self/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length > 2 && parts[2] == "cgroup2")
                {
                    mount = parts[1];
                    break;
                }
            }

            if (mount is null) return null;
            var own = File.ReadLines("/proc/self/cgroup").FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal))?[3..];
            if (own is null) return null;
            // a pure v2 machine mounts at /sys/fs/cgroup; a hybrid one at /sys/fs/cgroup/unified, where the v1 controllers are not ours to use
            var path = Path.Combine(mount, own.TrimStart('/'));
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Which controllers a child cgroup gets here, found by making one and looking; null when no child can be made or neither controller is on. Remembered per base.</summary>
    internal static (bool Memory, bool Cpu)? Probe(LimitOptions? options = null)
    {
        var baseDir = FindBase(options);
        if (baseDir is null) return null;
        return Probed.GetOrAdd(baseDir, ProbeCore);
    }

    private static (bool Memory, bool Cpu)? ProbeCore(string baseDir)
    {
        var probe = Path.Combine(baseDir, "dbdatakit-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(probe);
            if (!File.Exists(Path.Combine(probe, "memory.max")) || !File.Exists(Path.Combine(probe, "cpu.max")))
            {
                // the controllers are on for children only when the parent's subtree_control says so; a cgroup that holds processes cannot change that, and the write is refused
                foreach (var controller in new[] { "+memory", "+cpu" })
                    TryWrite(Path.Combine(baseDir, "cgroup.subtree_control"), controller);
            }

            var memory = File.Exists(Path.Combine(probe, "memory.max"));
            var cpu = File.Exists(Path.Combine(probe, "cpu.max"));
            return memory || cpu ? (memory, cpu) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(probe, false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool TryWrite(string file, string text)
    {
        try
        {
            File.WriteAllText(file, text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A handle holding the process in a new cgroup, or null when the limits asked for cannot be held that way here.</summary>
    internal static LimitHandle? TryApply(Process process, LimitSpec spec, LimitOptions options)
    {
        if (Probe(options) is not { } caps) return null;
        if ((spec.MemoryBytes is not null && !caps.Memory) || (spec.MemoryBytes is null && !caps.Cpu)) return null;
        var baseDir = FindBase(options)!;
        var dir = Path.Combine(baseDir, $"dbdatakit-{process.Id}-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            Directory.CreateDirectory(dir);
            if (spec.MemoryBytes is { } memory)
            {
                File.WriteAllText(Path.Combine(dir, "memory.max"), memory.ToString(CultureInfo.InvariantCulture));
                TryWrite(Path.Combine(dir, "memory.swap.max"), "0");   // swapping must not hide the use
                TryWrite(Path.Combine(dir, "memory.oom.group"), "1");  // the kernel kills the whole group, not one process of it
            }

            var cpuHeld = false;
            if (spec.CpuPercent is { } cpu && caps.Cpu)
            {
                // percent of the whole machine, as the job object does it: quota is in microseconds per period, across all CPUs
                var quota = (long)cpu * CpuPeriodMicroseconds * Environment.ProcessorCount / 100;
                File.WriteAllText(Path.Combine(dir, "cpu.max"), $"{quota} {CpuPeriodMicroseconds}");
                cpuHeld = true;
            }

            File.WriteAllText(Path.Combine(dir, "cgroup.procs"), process.Id.ToString(CultureInfo.InvariantCulture));
            return new Handle(process, dir, spec, cpuHeld);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(dir, false); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return null; // the process may have exited, or the kernel refused: the watchdog takes over
        }
    }

    private sealed class Handle : LimitHandle
    {
        private readonly Process _process;
        private readonly string _dir;
        private readonly long? _memoryLimit;
        private readonly object _gate = new();
        private bool _removed;

        public Handle(Process process, string dir, LimitSpec spec, bool cpuHeld)
        {
            _process = process;
            _dir = dir;
            _memoryLimit = spec.MemoryBytes;
            Enforcement = spec.MemoryBytes is null ? LimitEnforcement.None : LimitEnforcement.Enforced;
            CpuEnforcement = cpuHeld ? LimitEnforcement.Enforced : LimitEnforcement.None;
            Mechanism = "cgroup v2";
            _ = Task.Run(async () =>
            {
                try { await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                Remove(killRemaining: false);
            });
        }

        protected override bool CheckExceeded()
        {
            lock (_gate)
            {
                if (_removed || _memoryLimit is not { } limit) return false;
                return ReadOom(limit);
            }
        }

        private bool ReadOom(long limit)
        {
            try
            {
                var kills = File.ReadLines(Path.Combine(_dir, "memory.events"))
                    .Select(l => l.Split(' '))
                    .Where(p => p.Length == 2 && p[0] == "oom_kill")
                    .Select(p => long.Parse(p[1], CultureInfo.InvariantCulture))
                    .FirstOrDefault();
                if (kills <= 0) return false;
                var peak = limit;
                try { peak = long.Parse(File.ReadAllText(Path.Combine(_dir, "memory.peak")).Trim(), CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { }
                SetExceeded(new LimitExceeded("memory", limit, peak, $"The process went over its memory limit of {limit:N0} bytes (peak {peak:N0}); the kernel killed it."));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                return false;
            }
        }

        private void Remove(bool killRemaining)
        {
            lock (_gate)
            {
                if (_removed) return;
                if (_memoryLimit is { } limit) ReadOom(limit);
                if (killRemaining)
                {
                    if (!TryWrite(Path.Combine(_dir, "cgroup.kill"), "1"))
                    {
                        try { _process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                }

                // a cgroup can be removed only when it holds no process: wait for the last one to be gone
                for (var i = 0; i < 40; i++)
                {
                    try
                    {
                        Directory.Delete(_dir, false);
                        _removed = true;
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (!Directory.Exists(_dir)) { _removed = true; return; }
                        Thread.Sleep(50);
                    }
                }
            }
        }

        public override void Dispose() => Remove(killRemaining: true);
    }
}
