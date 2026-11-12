using System.Diagnostics;
using System.Globalization;

namespace DbDataKit.Runner.Limits;

/// <summary>
/// The fallback for memory: samples the resident memory every interval and kills the process tree when it is over the limit. On Linux the sample is the <b>sum over the process and all its descendants</b> (<c>VmRSS</c> in
/// <c>/proc/pid/status</c>, the tree from the parent pids in <c>/proc/pid/stat</c>), so a task that allocates in a child or a grandchild is judged by what the whole tree holds; pages shared between processes are counted in each, which
/// over-counts and errs toward the limit. Elsewhere it is the process itself (<see cref="Process.WorkingSet64"/>; Windows uses a job object, which covers the tree). A process can allocate between two samples, so this is "watched", not "enforced".
/// </summary>
internal sealed class MemoryWatchdog : LimitHandle
{
    private readonly Process _process;
    private readonly long _limit;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public MemoryWatchdog(Process process, long limitBytes, TimeSpan interval)
    {
        _process = process;
        _limit = limitBytes;
        Enforcement = LimitEnforcement.Watched;
        CpuEnforcement = LimitEnforcement.None;
        Mechanism = "watchdog";
        _loop = Task.Run(() => Run(interval, _stop.Token));
    }

    private async Task Run(TimeSpan interval, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            // the first sample is one interval after the start, so a handle disposed at once never samples (and a slow Windows start is not judged)
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (HasExited()) return;
                var observed = TreeResidentBytes(_process);
                if (observed > _limit)
                {
                    SetExceeded(new LimitExceeded("memory", _limit, observed,
                        $"The process (pid {Pid()}) and its children used {observed:N0} bytes of resident memory, over its limit of {_limit:N0}; the tree was killed."));
                    Kill();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private int Pid()
    {
        try { return _process.Id; } catch (InvalidOperationException) { return 0; }
    }

    private bool HasExited()
    {
        try { return _process.HasExited; } catch (InvalidOperationException) { return true; }
    }

    private void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* gone already */ }
    }

    /// <summary>Resident bytes of the process and, on Linux, of everything below it; 0 when the process is gone.</summary>
    internal static long TreeResidentBytes(Process process)
    {
        if (!OperatingSystem.IsLinux()) return ResidentBytes(process);
        try { return TreeResidentBytes(process.Id); }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>The resident bytes (<c>VmRSS</c>) of <paramref name="root"/> and every descendant, read from <c>/proc</c>.</summary>
    internal static long TreeResidentBytes(int root)
    {
        var parentOf = new Dictionary<int, int>();
        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                var stat = File.ReadAllText($"{entry}/stat");
                var close = stat.LastIndexOf(')');   // the command name is in parentheses and may hold spaces
                var fields = stat[(close + 2)..].Split(' ');   // state, ppid, ...
                parentOf[pid] = int.Parse(fields[1], CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException) { /* gone, or not readable */ }
        }

        var members = new HashSet<int> { root };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, parent) in parentOf)
                if (members.Contains(parent) && members.Add(pid)) grew = true;
        }

        long total = 0;
        foreach (var pid in members) total += ResidentOf(pid);
        return total;
    }

    private static long ResidentOf(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
                var digits = new string(line.Where(char.IsDigit).ToArray());
                return long.Parse(digits, CultureInfo.InvariantCulture) * 1024;
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return 0; }
    }

    /// <summary>Resident bytes now; 0 when the process is gone.</summary>
    internal static long ResidentBytes(Process process)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines($"/proc/{process.Id}/status"))
                {
                    if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
                    var digits = new string(line.Where(char.IsDigit).ToArray());
                    return long.Parse(digits, CultureInfo.InvariantCulture) * 1024; // reported in kB
                }

                return 0; // a zombie has no VmRSS
            }

            process.Refresh();
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            return 0;
        }
    }

    private int _disposed;

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
