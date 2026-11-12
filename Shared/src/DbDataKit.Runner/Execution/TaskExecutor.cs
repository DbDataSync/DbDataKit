using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DbDataKit.Hosting.Children;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Limits;
using DbDataKit.Runner.Protocol;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.State;
using DbDataKit.Telemetry;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Execution;

/// <summary>
/// Runs one task in its own process and reports how it ended (plan 28a R06, DR1, DR3, DR5, DR6). For each run it: records the run (with package, version, channel and attempt); makes a scratch directory (the
/// process's working directory and temp directory, removed after the run); puts the attempt's in-doubt steps right (<see cref="InDoubtReconciler"/>); registers a run token with the runner's protocol host; starts the
/// process with the endpoint and the token in its <b>environment</b> (never argv, never a file) and nothing else of the runner's environment; applies the limits; copies the process's output into the run's log
/// through the shared redactor; enforces the time limit and a cancel with its grace period; and reconciles the end.
/// <para>
/// <b>The end.</b> The task's end message wins over the exit code: a process that ends its run and then crashes keeps the outcome it reported. Without an end message the executor decides, in this order: the
/// process was killed for its time limit (<c>TimedOut</c>); the runner asked it to stop (<c>Cancelled</c>); a limit killed it (<c>LimitExceeded</c>); it exited with code 0 (<c>ProtocolViolation</c>, a task says
/// how it ended); it exited with another code (<c>ProcessExited</c>). The run record is finished exactly once, whatever happens, and unfinished runs of a runner that died are marked interrupted by
/// <see cref="InterruptStaleRuns"/>.
/// </para>
/// </summary>
public sealed class TaskExecutor : ITaskRunner
{
    private static readonly Task Never = new TaskCompletionSource().Task;

    /// <summary>The product label of the runner's telemetry (plan 22a GP28).</summary>
    public const string RunnerProduct = "dbdatarunner";

    private readonly StateDatabase _database;
    private readonly RunnerProtocolHost _host;
    private readonly TaskExecutorOptions _options;
    private readonly InDoubtReconciler _reconciler;

    public TaskExecutor(StateDatabase database, RunnerProtocolHost host, TaskExecutorOptions options)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DataDirectory)) throw new ArgumentException("The executor needs the runner's data directory (scratch directories are made in it).", nameof(options));
        _reconciler = new InDoubtReconciler(database);
    }

    /// <summary>
    /// At the start of a runner: marks the runs of this runner's service that began before <paramref name="startedBefore"/> and are still running as interrupted (the process that ran them went away), and removes the
    /// scratch directories no run owns. Returns the number of runs marked.
    /// </summary>
    public int InterruptStaleRuns(DateTimeOffset startedBefore)
    {
        var scratch = Path.Combine(_options.DataDirectory, "scratch");
        if (Directory.Exists(scratch))
            foreach (var leftover in Directory.EnumerateDirectories(scratch)) TryDeleteTree(leftover);
        return new RunStore(_database, _options.Service).InterruptRunning(startedBefore);
    }

    /// <summary>Runs the task and returns when its process is gone and its run record is finished.</summary>
    public async Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var runId = request.RunId ?? _database.NewId();
        var runs = new RunStore(_database, _options.Service);
        var clock = Stopwatch.StartNew();
        runs.Start(request.Task.ToString(), runId, package: request.Task.Package, packageVersion: request.PackageVersion, channel: request.Task.Channel, attempt: request.Attempt);

        // plan 22a GP28: a task start is counted by the task's name, and the run is one span with its count, duration and outcome (never the failure's text or a task's output)
        HostTelemetry.TaskStarted(RunnerProduct, request.Task.Task);
        using var scope = HostTelemetry.StartRun(RunnerProduct, "task", request.Task.Task);
        var log = new OutputLogger(runs, runId, _options.Redactor, _options.MaxLogLineChars, _options.MaxLogLines);
        TaskRunResult? result = null;
        try
        {
            result = await RunProcessAsync(request, runId, log, clock, ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            // exactly one finish, also when the executor's own code threw: the task's end message already finished the run record if there was one, and this call then changes nothing
            try
            {
                await log.CompleteAsync().ConfigureAwait(false);
            }
            finally
            {
                var end = result?.End ?? new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, "The runner failed while it was running this task; see the runner's own log."));
                var finish = end.ToRunFinish();
                KeepLineage(request, runId, result);   // before the run is finished, so a run that shows as finished has its lineage kept
                runs.Finish(runId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
                if (end.State is TaskEndState.Succeeded) scope.Succeeded();
                else if (end.State is TaskEndState.Stopped) scope.Cancelled();
                else scope.Failed();
            }
        }
    }

    /// <summary>
    /// Keeps what the task reported it read and wrote (plan 27a IX12) so the runner's lineage route can show the outside systems it touches. The run is over and its outcome stands: a state that cannot take the rows is a warning in the executor's log,
    /// never an error of the run.
    /// </summary>
    private void KeepLineage(TaskRunRequest request, Guid runId, TaskRunResult? result)
    {
        if (result?.Lineage is not { Count: > 0 } lines) return;
        try
        {
            new TaskLineageStore(_database).Record(request.Task.Package, request.Task.Channel, request.Task.Task, runId, lines.SelectMany(l => l.Reads), lines.SelectMany(l => l.Writes));
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException or InvalidOperationException)
        {
            _options.Logger?.LogWarning("The lineage run {Run} of task {Task} reported could not be kept ({Type}).", runId, request.Task.Task, ex.GetType().Name);
        }
    }

    private async Task<TaskRunResult> RunProcessAsync(TaskRunRequest request, Guid runId, OutputLogger log, Stopwatch clock, CancellationToken ct)
    {
        var task = request.Task;
        var scratch = Path.Combine(Path.GetFullPath(_options.DataDirectory), "scratch", runId.ToString("N"));
        var timeout = request.Limits.Timeout ?? _options.DefaultTimeout;
        var memoryMb = request.Limits.MemoryMb ?? _options.DefaultMemoryMb;
        var limitSpec = new LimitSpec(memoryMb is { } mb ? mb * 1024L * 1024L : null, CpuPercentOf(request.Limits.Cpu));

        IReadOnlyList<StepRecord> inDoubt = [];
        string? token = null;
        ChildProcess? child = null;
        LimitHandle? limits = null;
        Process? limited = null;
        TaskEnd? end = null;
        var endReceived = false;
        ChildExitReport? exit = null;
        IReadOnlyList<LineageEntry>? reported = null;
        var killed = false;
        LimitExceeded? exceeded = null;
        LimitExceeded? scratchExceeded = null;
        Task? scratchWatch = null;
        using var scratchStop = new CancellationTokenSource();
        long scratchBytes = 0;
        var scratchOver = false;
        try
        {
            CreateScratch(scratch);
            if (request is { RuntimeDirectory: not null, RuntimeKey: { } runtimeKey }) Directory.CreateDirectory(ExtractDirectoryOf(runtimeKey));
            inDoubt = _reconciler.Reconcile(task, request.Chain, request.Attempt);
            foreach (var step in inDoubt)
                log.Note("Warning", $"The step '{step.Name}' ({step.Key}) was started by attempt {step.Attempt} of this chain and never completed; it is in doubt, and the job's recovery for it decides what happens.");

            var start = new TaskStart(task, runId, request.Attempt, request.PackageVersion, request.Inputs, request.ConnectionHandles, null, request.CancellationGrace);
            var lifetime = (timeout ?? TimeSpan.FromDays(1)) + request.CancellationGrace + _options.ExitWait + TimeSpan.FromMinutes(5);
            // the logins of the slots that ask for injection (plan 28c E02): resolved here, put in the child's environment and nowhere else; a login that cannot be resolved fails the run before a process exists
            IReadOnlyDictionary<string, string> injected = request.Slots.Count > 0 && _options.Slots is { } slots
                ? await slots.EnvironmentAsync(request.ConnectionHandles, request.Slots, ct).ConfigureAwait(false)
                : new Dictionary<string, string>();
            token = _host.Register(new RunGrant(start, request.Chain, new RunnerEnvironment(_database, task.Package, task.Channel) { Service = _options.Service }, lifetime, _options.Slots, HostFinishesRun: true));

            var spec = BuildSpec(request, scratch, token, log, injected);
            child = ChildProcessHost.Launch(spec);
            (limits, limited) = ApplyLimits(child, limitSpec);
            log.Note("Information", Describe(request, child.ProcessId, timeout, limitSpec, limits));

            scratchWatch = _options.ScratchSampleInterval is { } sample && sample > TimeSpan.Zero ? WatchScratchAsync(child, scratch, sample, scratchStop.Token, over => scratchExceeded = over) : null;
            var monitor = await MonitorAsync(child, token, timeout, request.CancellationGrace, ct).ConfigureAwait(false);
            exit = monitor.Exit;
            killed = monitor.Killed;
            try
            {
                // the process can be gone before its last lines were delivered; a grandchild that holds the pipes open must not hold the run
                await child.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                log.Note("Warning", "The process's output did not end within 5 seconds of the process; a child of the task may still hold it open.");
            }

            exceeded = limits.ExceededInfo ?? scratchExceeded;
            var telemetry = _host.TelemetryOf(token);
            reported = telemetry?.Lineage;
            var taskEnd = telemetry?.End;
            if (taskEnd is not null)
            {
                end = taskEnd;
                endReceived = true;
            }
            else if (monitor.TimedOut) end = TaskEndMapping.TimedOut(timeout!.Value);
            else if (monitor.CancelRequested) end = TaskEndMapping.Cancelled("The runner asked the task to stop" + (monitor.Killed ? $" and it did not within the grace period of {request.CancellationGrace}; its process was killed." : "."));
            else if (exceeded is not null) end = TaskEndMapping.LimitExceeded(exceeded.Limit);
            else end = TaskEndMapping.ProcessExited(exit.ExitCode);

            log.Note(endReceived ? "Information" : "Error",
                $"The process exited with code {exit.ExitCode} after {exit.Uptime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s{(killed ? ", killed by the runner" : "")}; " +
                (endReceived ? $"the task ended the run: {end.State}." : $"the task sent no end message, so the run ends {end.State}: {end.Failure?.Message}"));
            if (exceeded is not null) log.Note("Error", exceeded.Reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            // the run could not be set up or started: a failed run, not a crash of the executor
            end = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, $"The task could not be started: {ex.Message}"));
            log.Note("Error", end.Failure!.Message);
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.Exited.IsCompleted) child.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // already gone
                }

                limits?.Dispose();
                limited?.Dispose();
                await child.DisposeAsync().ConfigureAwait(false);
            }

            await scratchStop.CancelAsync().ConfigureAwait(false);
            if (scratchWatch is not null) await scratchWatch.ConfigureAwait(false);
            if (token is not null) _host.Unregister(token);
            (scratchBytes, scratchOver) = CheckAndRemoveScratch(scratch, log);
        }

        return new TaskRunResult(runId, end!, endReceived, exit?.ExitCode, killed, clock.Elapsed, log.StdoutLines, log.StderrLines, log.DroppedLines, log.TruncatedLines,
            limits?.Enforcement ?? LimitEnforcement.None, limits?.CpuEnforcement ?? LimitEnforcement.None, limits?.Mechanism ?? "none", scratchBytes, scratchOver, inDoubt) { Lineage = reported is { Count: > 0 } ? reported : null };
    }

    /// <summary>
    /// Stops a task that fills its scratch directory while it runs (plan 28a R27): the size of the directory is read every <paramref name="interval"/> and the process tree is killed when it passes the limit, with the run ending as a
    /// limit exceeded (<c>scratch</c>). The size is read by walking the directory, so a task can write more than the limit between two looks: like the memory watchdog, this is watched, not an operating-system quota.
    /// </summary>
    private async Task WatchScratchAsync(ChildProcess child, string scratch, TimeSpan interval, CancellationToken token, Action<LimitExceeded> exceeded)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (child.Exited.IsCompleted) return;
                long bytes;
                try { bytes = SizeOf(scratch); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (bytes <= _options.MaxScratchBytes) continue;
                exceeded(new LimitExceeded("scratch", _options.MaxScratchBytes, bytes,
                    $"The task's scratch directory held {bytes:N0} bytes, over the limit of {_options.MaxScratchBytes:N0}; the task was stopped."));
                try { child.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* gone already */ }
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct Monitored(ChildExitReport Exit, bool TimedOut, bool CancelRequested, bool Killed);

    /// <summary>Waits for the process, enforcing the time limit (a hard stop), a cancel (asked first, killed after the grace period), and a process that has sent its end and does not exit.</summary>
    private async Task<Monitored> MonitorAsync(ChildProcess child, string token, TimeSpan? timeout, TimeSpan grace, CancellationToken ct)
    {
        using var stop = new CancellationTokenSource();
        var cancelRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => cancelRequested.TrySetResult());

        var timedOut = false;
        var asked = false;
        var killed = false;
        Task limit = timeout is { } t ? Task.Delay(t, stop.Token) : Never;
        Task cancel = cancelRequested.Task;
        Task ended = _host.WaitForEndAsync(token);
        Task deadline = Never;
        try
        {
            while (!child.Exited.IsCompleted)
            {
                var done = await Task.WhenAny(child.Exited, limit, cancel, ended, deadline).ConfigureAwait(false);
                if (done == child.Exited) break;
                if (done == limit)
                {
                    timedOut = true;
                    killed = true;
                    child.Kill();
                    limit = Never;
                }
                else if (done == cancel)
                {
                    asked = true;
                    _host.Cancel(token, "The runner asked this task to stop.");
                    deadline = Earlier(deadline, grace + _options.KillSlack, stop.Token);
                    cancel = Never;
                }
                else if (done == ended)
                {
                    // the task has said how it ended; a process that then does not exit is killed, and its outcome stands
                    deadline = Earlier(deadline, _options.ExitWait, stop.Token);
                    ended = Never;
                }
                else
                {
                    killed = true;
                    child.Kill();
                    deadline = Never;
                }
            }

            return new Monitored(await child.Exited.ConfigureAwait(false), timedOut, asked, killed);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The sooner of the current deadline and a new one after <paramref name="after"/>.</summary>
    private static Task Earlier(Task current, TimeSpan after, CancellationToken stop)
    {
        var next = Task.Delay(after, stop);
        return ReferenceEquals(current, Never) ? next : Task.WhenAny(current, next).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private ChildProcessSpec BuildSpec(TaskRunRequest request, string scratch, string token, OutputLogger log, IReadOnlyDictionary<string, string> injected)
    {
        var package = Path.GetFullPath(request.PackageDirectory);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in _options.InheritedVariables)
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        foreach (var (name, value) in _options.ExtraEnvironment) environment[name] = value;
        foreach (var (name, value) in injected) environment[name] = value;
        var temp = Path.Combine(scratch, "tmp");
        environment["TMPDIR"] = temp;
        environment["TMP"] = temp;
        environment["TEMP"] = temp;
        environment[RunnerProtocolNames.ScratchVariable] = scratch;
        environment[RunnerProtocolNames.PackageDirectoryVariable] = package;
        var runtime = request.RuntimeDirectory is null ? null : Path.GetFullPath(request.RuntimeDirectory);
        if (runtime is not null)
        {
            environment[RunnerProtocolNames.RuntimeDirectoryVariable] = runtime;
            if (request.RuntimeKey is { } key) environment[ExtractDirectoryVariable] = ExtractDirectoryOf(key);
        }
        environment[RunnerProtocolNames.TaskVariable] = request.Task.Task;
        environment[RunnerProtocolNames.EndpointVariable] = _host.Endpoint.ToString();
        environment[RunnerProtocolNames.TokenVariable] = token;

        string fileName;
        List<string> arguments = [];
        if (request.Launch.Kind == LaunchKind.Assembly)
        {
            var assembly = request.Launch.Assembly ?? throw new ArgumentException("The package is launched as an assembly, and its launch names none.");
            environment[RunnerProtocolNames.AssemblyVariable] = InsidePackage(package, assembly);
            (fileName, var prefix) = TaskHostCommand();
            arguments.AddRange(prefix);
        }
        else
        {
            var command = request.Launch.Command ?? throw new ArgumentException("The package is launched as an executable, and its launch names no command.");
            command = command.Replace("{package}", package, StringComparison.Ordinal).Replace("{runtime}", runtime ?? package, StringComparison.Ordinal);
            // a command that is a file of the package (of the runtime, for a package run by one) is that file; any other is a program of the runner, found on its PATH
            var home = runtime ?? package;
            fileName = Path.IsPathRooted(command) ? command : File.Exists(Path.Combine(home, command)) ? InsidePackage(home, command) : command;
            if (!Path.IsPathRooted(command) && fileName != command) MakeExecutable(fileName);
            arguments.AddRange(request.Launch.ArgList.Concat(request.Entry?.ArgList ?? []).Select(a => a.Replace("{package}", package, StringComparison.Ordinal).Replace("{runtime}", runtime ?? package, StringComparison.Ordinal)));
        }

        return new ChildProcessSpec
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = runtime is null ? scratch : package,   // a definition run by a runtime starts in the definition (read-only; the task writes to its scratch directory)
            Environment = environment,
            ClearEnvironment = true,
            Transport = ChildEndpointKind.None,
            OnOutputLine = log.Stdout,
            OnErrorLine = log.Stderr,
        };
    }

    /// <summary>The variable a .NET single-file executable reads for the directory it unpacks its natives into (default <c>~/.net</c>).</summary>
    public const string ExtractDirectoryVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    /// <summary>The extraction directory of a runtime version (<c>name@version</c>): <see cref="TaskExecutorOptions.RuntimeExtractDirectory"/> or <c>&lt;data&gt;/cache/runtimes</c>, then the key made safe as a directory name.</summary>
    internal string ExtractDirectoryOf(string key)
    {
        var root = _options.RuntimeExtractDirectory ?? Path.Combine(_options.DataDirectory, "cache", "runtimes");
        return Path.Combine(Path.GetFullPath(root), new string([.. key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@' ? c : '_')]));
    }

    private (string FileName, string[] Prefix) TaskHostCommand()
    {
        var path = _options.TaskHostPath;
        if (path is null)
        {
            var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dbdatakit-taskhost.exe" : "dbdatakit-taskhost");
            path = File.Exists(apphost) ? apphost : Path.Combine(AppContext.BaseDirectory, "dbdatakit-taskhost.dll");
        }

        if (!File.Exists(path)) throw new InvalidOperationException($"The task host '{path}' does not exist; an assembly package is started through dbdatakit-taskhost, which is installed beside the runner.");
        return path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? (DotnetHost(), [path]) : (path, []);
    }

    private static string DotnetHost()
    {
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return current;
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromSdk && File.Exists(fromSdk)) return fromSdk;
        // <root>/shared/Microsoft.NETCore.App/<version>/ is the runtime directory; the dotnet host is in <root>
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime)));
        if (root is not null)
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }

        return "dotnet";
    }

    /// <summary>
    /// A file of a package view is a hard link to (the same file as) a stored object with mode 0444 (no artifact says it is a program), so a launch command that is a file of the package would be refused with "permission denied". The object it
    /// names is made executable (0555) before the start; every view that links the same file sees it, which is harmless for a file nobody runs by that name. Only on a system with Unix modes.
    /// </summary>
    internal static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            var mode = File.GetUnixFileMode(target);
            const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & Execute) != Execute) File.SetUnixFileMode(target, mode | Execute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not ours to change (a read-only mount): the start says why it cannot run
        }
    }

    /// <summary>The full path of a file the manifest names inside the package, refused when it leaves the package.</summary>
    private static string InsidePackage(string package, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = package.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal)) throw new ArgumentException($"'{relative}' leaves the package directory; a launch names a file inside the package.");
        return full;
    }

    /// <summary>Applies the limits to the process. The <see cref="Process"/> object stays open for as long as the handle needs it (a watchdog samples it), and the caller disposes both.</summary>
    private (LimitHandle Limits, Process? Process) ApplyLimits(ChildProcess child, LimitSpec spec)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(child.ProcessId);
            return (ProcessLimits.Apply(process, spec, _options.Limits), process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // the process is already gone: there is nothing to limit, and the run ends with what it did
            process?.Dispose();
            return (ProcessLimits.Apply(Process.GetCurrentProcess(), new LimitSpec()), null);
        }
    }

    private static int? CpuPercentOf(decimal? cores) =>
        cores is { } c ? (int)Math.Clamp(Math.Ceiling(c / Environment.ProcessorCount * 100m), 1m, 100m) : null;

    private static string Describe(TaskRunRequest request, int pid, TimeSpan? timeout, LimitSpec spec, LimitHandle limits)
    {
        var how = request.Launch.Kind == LaunchKind.Assembly ? "through the task host" : "as an executable";
        string Limit(long? bytes) => bytes is { } b ? $"{b / (1024 * 1024):N0} MiB ({limits.Enforcement.ToString().ToLowerInvariant()} by {limits.Mechanism})" : "none";
        return $"Started {request.Task} version {request.PackageVersion}, attempt {request.Attempt}, {how} (pid {pid}); time limit {(timeout is { } t ? t.ToString() : "none")}, memory limit {Limit(spec.MemoryBytes)}, " +
               $"cpu limit {(spec.CpuPercent is { } c ? $"{c}% of the machine ({limits.CpuEnforcement.ToString().ToLowerInvariant()})" : "none")}.";
    }

    private static void Validate(TaskRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(request.PackageDirectory) || !Directory.Exists(request.PackageDirectory)) problems.Add($"The package directory '{request.PackageDirectory}' does not exist.");
        if (string.IsNullOrWhiteSpace(request.PackageVersion)) problems.Add("The package version is empty.");
        if (string.IsNullOrWhiteSpace(request.Chain)) problems.Add("The attempt chain is empty.");
        if (request.Attempt < 1) problems.Add($"The attempt number is {request.Attempt}; the first attempt is 1.");
        if (request.CancellationGrace < TimeSpan.Zero) problems.Add("The cancellation grace is negative.");
        if (request.Limits.Timeout is { } t && t <= TimeSpan.Zero) problems.Add("The time limit is not above zero.");
        if (request.Launch.Kind == LaunchKind.Assembly && string.IsNullOrWhiteSpace(request.Launch.Assembly)) problems.Add("The launch is an assembly and names none.");
        if (request.Launch.Kind == LaunchKind.Executable && string.IsNullOrWhiteSpace(request.Launch.Command)) problems.Add("The launch is an executable and names no command.");
        if (problems.Count > 0) throw new ArgumentException("The task cannot be run: " + string.Join(" ", problems), nameof(request));
    }

    private static void CreateScratch(string scratch)
    {
        Directory.CreateDirectory(Path.Combine(scratch, "tmp"));
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(scratch, owner);
            File.SetUnixFileMode(Path.Combine(scratch, "tmp"), owner);
        }
    }

    /// <summary>
    /// What the scratch directory holds, a warning when it is over its limit (enforcing it during the run needs operating-system quotas), and then it is removed. The process is gone, so nothing writes to it.
    /// </summary>
    private (long Bytes, bool Over) CheckAndRemoveScratch(string scratch, OutputLogger log)
    {
        long bytes = 0;
        var over = false;
        if (!Directory.Exists(scratch)) return (0, false);
        try
        {
            try
            {
                bytes = SizeOf(scratch);
                over = bytes > _options.MaxScratchBytes;
                if (over)
                {
                    var message = $"The task's scratch directory held {bytes:N0} bytes at the end of the run, over the limit of {_options.MaxScratchBytes:N0}. A task that is still running when it passes the limit is stopped (the scratch limit is watched every {_options.ScratchSampleInterval?.TotalSeconds:0.##} s while it runs); this one was over at the end.";
                    log.Note("Warning", message);
                    _options.Logger?.LogWarning("{Message}", message);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Note("Warning", $"The size of the task's scratch directory could not be read: {ex.Message}");
            }
        }
        finally
        {
            // removed whatever happened above: a logger that throws does not leave a task's files behind
            if (!TryDeleteTree(scratch))
            {
                var message = $"The task's scratch directory '{scratch}' could not be removed; the next start of the runner removes it.";
                log.Note("Warning", message);
                _options.Logger?.LogWarning("{Message}", message);
            }
        }

        return (bytes, over);
    }

    private static long SizeOf(string directory)
    {
        long total = 0;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            if (entry is FileInfo file) total += file.Length;
        return total;
    }

    /// <summary>Removes a tree a task may have made read-only; false when something stays.</summary>
    internal static bool TryDeleteTree(string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 1) return false;
                MakeWritable(path);
            }
        }

        return false;
    }

    private static void MakeWritable(string path)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(e => e.FullName).Append(path))
            {
                if (OperatingSystem.IsWindows()) File.SetAttributes(entry, FileAttributes.Normal);
                else File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what cannot be changed stays, and the caller reports it
        }
    }
}
