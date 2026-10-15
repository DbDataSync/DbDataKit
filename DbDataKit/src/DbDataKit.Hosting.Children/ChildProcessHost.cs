using System.Diagnostics;

namespace DbDataKit.Hosting.Children;

public sealed class ChildProcessSpec
{
    public required string FileName { get; init; }

    /// <summary>Arguments. Must not carry secrets; the token travels in the environment.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Handshake transport. Null picks the platform default. <see cref="ChildEndpointKind.None"/> launches without a
    /// listener, for children that host their own server (for example Kestrel on a port the host chooses); the job
    /// object, kill-on-dispose, parent identity and output capture still apply.
    /// </summary>
    public ChildEndpointKind? Transport { get; init; }

    /// <summary>Called for each line the child writes to standard output. Output is always drained, even when null.</summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Called for each line the child writes to standard error. Output is always drained, even when null.</summary>
    public Action<string>? OnErrorLine { get; init; }

    /// <summary>Windows job limits.</summary>
    public long? MemoryLimitBytes { get; init; }

    public int? CpuPercent { get; init; }
}

public sealed record ChildExitReport(int ExitCode, TimeSpan Uptime, bool KilledByHost);

/// <summary>One launched child. Disposing kills it (job object closes on Windows; tree kill elsewhere).</summary>
public sealed class ChildProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly IChildTransportListener _listener;
    private readonly JobObjectHolder _job;
    private readonly string _token;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly TaskCompletionSource<ChildExitReport> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _killed;

    internal ChildProcess(Process process, IChildTransportListener listener, JobObjectHolder job, string token)
    {
        _process = process;
        _listener = listener;
        _job = job;
        _token = token;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _exited.TrySetResult(
            new ChildExitReport(SafeExitCode(), DateTimeOffset.UtcNow - _started, _killed));
        if (process.HasExited)
        {
            _exited.TrySetResult(new ChildExitReport(SafeExitCode(), DateTimeOffset.UtcNow - _started, _killed));
        }
    }

    public int ProcessId => _process.Id;

    /// <summary>Where the child connects; throws when launched with <see cref="ChildEndpointKind.None"/>.</summary>
    public ChildEndpoint Endpoint => _listener.Endpoint;

    public Task<ChildExitReport> Exited => _exited.Task;

    public ProcessStartInfo StartInfo => _process.StartInfo;

    /// <summary>Wait for the child to connect and authenticate; returns the duplex stream.</summary>
    public Task<Stream> AcceptAsync(CancellationToken cancellationToken) => _listener.AcceptAsync(_token, cancellationToken);

    public void Kill()
    {
        _killed = true;
        try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) Kill();
        _job.Dispose();
        await _listener.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }

    private int SafeExitCode()
    {
        try { return _process.ExitCode; } catch (InvalidOperationException) { return -1; }
    }
}

internal sealed class JobObjectHolder(IDisposable? job) : IDisposable
{
    public IDisposable? Target { get; } = job;

    public void Dispose() => Target?.Dispose();
}

/// <summary>
/// Launches children so they cannot outlive the host and cannot be reached by other local processes:
/// listener bound first, per-launch token and parent identity in the environment, job object on Windows.
/// </summary>
public static class ChildProcessHost
{
    public static ChildProcess Launch(ChildProcessSpec spec)
    {
        var token = ChildEnvironment.NewToken();
        var listener = ChildTransports.CreateListener(spec.Transport);
        JobObjectHolder? job = null;
        Process? process = null;
        try
        {
            var self = Process.GetCurrentProcess();
            var hasTransport = spec.Transport != ChildEndpointKind.None;
            var info = new ChildLaunchInfo(token, hasTransport ? listener.Endpoint : new ChildEndpoint(ChildEndpointKind.None, ""), self.Id, ChildEnvironment.StartTicksOf(self));

            var psi = new ProcessStartInfo(spec.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (spec.WorkingDirectory is not null) psi.WorkingDirectory = spec.WorkingDirectory;
            foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
            foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;
            ChildEnvironment.Apply(psi.Environment, info);

            job = new JobObjectHolder(OperatingSystem.IsWindows() ? new JobObject(spec.MemoryLimitBytes, spec.CpuPercent) : null);
            process = Process.Start(psi) ?? throw new InvalidOperationException("The child process did not start.");
            // The redirected pipes must be drained or a chatty child blocks on a full buffer.
            var onOut = spec.OnOutputLine;
            var onErr = spec.OnErrorLine;
            process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) onOut?.Invoke(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) onErr?.Invoke(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // Tiny window between start and assignment; documented in the README. Nested jobs are fine on Win8+.
            if (OperatingSystem.IsWindows())
            {
                AssignToJob(job, process);
            }

            return new ChildProcess(process, listener, job, token);
        }
        catch
        {
            try { process?.Kill(true); } catch (InvalidOperationException) { }
            job?.Dispose();
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssignToJob(JobObjectHolder holder, Process process) => ((JobObject)holder.Target!).Assign(process);
}

/// <summary>Restarts a child per <see cref="RestartPolicy"/> until it ends cleanly, the policy gives up, or cancellation.</summary>
public sealed class ChildSupervisor(RestartPolicy policy, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? time = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IList<ChildExitReport> Exits { get; } = [];

    /// <summary>Returns the last exit report. <paramref name="onStarted"/> typically accepts the transport connection.</summary>
    public async Task<ChildExitReport> RunAsync(ChildProcessSpec spec, Func<ChildProcess, Task>? onStarted, CancellationToken ct)
    {
        var tracker = new RestartTracker(policy);
        while (true)
        {
            ChildExitReport report;
            await using (var child = ChildProcessHost.Launch(spec))
            {
                var callback = onStarted is null ? Task.CompletedTask : onStarted(child);
                using var reg = ct.Register(child.Kill);
                report = await child.Exited.ConfigureAwait(false);
                try { await callback.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }

            Exits.Add(report);
            ct.ThrowIfCancellationRequested();
            var decision = tracker.OnExit(_time.GetUtcNow(), report.ExitCode, report.Uptime);
            if (!decision.Restart) return report;
            await _delay(decision.Delay, ct).ConfigureAwait(false);
        }
    }
}
