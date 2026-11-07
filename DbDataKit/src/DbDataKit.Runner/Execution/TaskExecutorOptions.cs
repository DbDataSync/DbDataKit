using DbDataKit.Runner.Limits;
using DbDataKit.Secrets;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Execution;

/// <summary>What the executor needs of its runner, and the defaults a manifest's silence falls back to.</summary>
public sealed class TaskExecutorOptions
{
    /// <summary>The runner's data directory: scratch directories are made under <c>scratch/</c> in it. Required.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// The task host to start for an assembly package: <c>dbdatakit-taskhost</c> (the apphost) or <c>dbdatakit-taskhost.dll</c> (started through <c>dotnet</c>). Null: the one beside the running program, the apphost
    /// if there is one.
    /// </summary>
    public string? TaskHostPath { get; init; }

    /// <summary>The service name recorded on every run (<c>runs.run.service</c>), and the one <see cref="TaskExecutor.InterruptStaleRuns"/> works on. Default <c>runner</c>.</summary>
    public string Service { get; init; } = "runner";

    /// <summary>
    /// What a task's output lines are scrubbed with before they reach the run's log (the shared redactor, plan 28 section 3). Null: nothing is registered, and only the redactor's own patterns (credential fragments in
    /// connection strings) would apply, so a runner always passes the one that knows its secrets.
    /// </summary>
    public IRedactor? Redactor { get; init; }

    /// <summary>The time limit of a task whose manifest names none. Default one hour; null: no limit.</summary>
    public TimeSpan? DefaultTimeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The memory limit, in MiB, of a task whose manifest names none. Default 2048; null: no limit.</summary>
    public int? DefaultMemoryMb { get; init; } = 2048;

    /// <summary>How big a scratch directory may be at the end of a run. Default 1 GiB (plan 28a QR1). Checked after the run: a bigger one is a warning in the log and in the result, not a failure.</summary>
    public long MaxScratchBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>The longest log line kept, in characters; a longer one is cut and says by how much. Default 4096.</summary>
    public int MaxLogLineChars { get; init; } = 4096;

    /// <summary>The most output lines kept in one run's log; the rest are counted and one line says so. Default 10,000.</summary>
    public int MaxLogLines { get; init; } = 10_000;

    /// <summary>How long a process that has sent its end message may take to exit before its tree is killed (the outcome is kept). Default 10 seconds.</summary>
    public TimeSpan ExitWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait, beyond the cancellation grace, before a task that was asked to stop is killed (the task ends its own run at the grace period). Default 500 milliseconds.</summary>
    public TimeSpan KillSlack { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The variables of the runner's own environment a task process may see, besides the ones the executor sets. Everything else is withheld, because a runner's environment holds what a task must not have (state
    /// credentials, secrets). The default is what a process needs to start: <c>PATH</c>, <c>HOME</c>, locale and time zone, the .NET root, and on Windows the system root.
    /// </summary>
    public IReadOnlyCollection<string> InheritedVariables { get; init; } =
        ["PATH", "HOME", "USER", "LANG", "LC_ALL", "TZ", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "SystemRoot", "windir", "COMSPEC", "PATHEXT"];

    /// <summary>
    /// The directory under which a runtime package's single-file executable unpacks its natives (plan 28c E05, decision ED8): a directory per runtime version, <c>&lt;this&gt;/&lt;name&gt;@&lt;version&gt;</c>, made by the executor and given to the
    /// process as <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c>. It is outside every view (a view is read-only) and shared by the runs of one version: the first start fills it and the others reuse it. Null: <c>&lt;data&gt;/cache/runtimes</c>.
    /// </summary>
    public string? RuntimeExtractDirectory { get; init; }

    /// <summary>Variables added to every task process (a proxy setting, say). They are visible to the task and must not hold secrets.</summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>Turns a task's connection slots into logins: the environment of the slots that ask for injection, and the answer to the protocol's <c>ResolveConnection</c>. Null: a runner with no connection bindings (tasks with slots then get no login).</summary>
    public DbDataKit.Runner.Protocol.Server.ISlotResolver? Slots { get; init; }

    /// <summary>How limits are applied (the defaults are the real machine; tests make the watchdog quick or forbid a cgroup).</summary>
    public LimitOptions? Limits { get; init; }

    /// <summary>Where the executor's own warnings go (they also go to the run's log).</summary>
    public ILogger? Logger { get; init; }
}
