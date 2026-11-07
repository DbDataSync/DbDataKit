using System.Diagnostics;
using System.Globalization;

namespace DbDataKit.Runner.Limits;

/// <summary>
/// The fallback for memory: samples the process's resident memory every interval (<c>VmRSS</c> in <c>/proc/pid/status</c> on Linux, <see cref="Process.WorkingSet64"/> elsewhere) and kills the
/// process tree when it is over the limit. A process can allocate between two samples, so this is "watched", not "enforced".
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
                var observed = ResidentBytes(_process);
                if (observed > _limit)
                {
                    SetExceeded(new LimitExceeded("memory", _limit, observed,
                        $"The process (pid {Pid()}) used {observed:N0} bytes of resident memory, over its limit of {_limit:N0}; it was killed."));
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
