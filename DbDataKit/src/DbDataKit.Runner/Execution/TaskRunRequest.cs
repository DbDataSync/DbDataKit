using DbDataKit.Runner.Limits;
using DbDataKit.State;

namespace DbDataKit.Runner.Execution;

/// <summary>
/// One run of one task, as the scheduler hands it to the executor (plan 28a R06). The package is named by the directory of its materialized version (the artifact cache's read-only view, DR13); the launch
/// kind and the task's entry come from the manifest; the limits are the manifest's, and the executor fills in its defaults for what the manifest leaves out.
/// </summary>
public sealed record TaskRunRequest
{
    /// <summary>Which task of which package on which channel.</summary>
    public required TaskRef Task { get; init; }

    /// <summary>The root of the materialized version (<see cref="MaterializedVersion.Directory"/>): the task process may read it and never write it.</summary>
    public required string PackageDirectory { get; init; }

    /// <summary>The version's name, recorded on the run (<c>runs.run.package_version</c>) and given to the task.</summary>
    public required string PackageVersion { get; init; }

    /// <summary>An assembly started through the task host, or an executable.</summary>
    public required PackageLaunch Launch { get; init; }

    /// <summary>
    /// The root of the runtime package's view when the package is run by a runtime (<c>launch.runtime</c>, plan 28c E04): <see cref="Launch"/> then holds the runtime's command and arguments, the command is a file of this directory, and
    /// <see cref="PackageDirectory"/> (the definition) is the working directory. Null for a package that runs itself.
    /// </summary>
    public string? RuntimeDirectory { get; init; }

    /// <summary>What the manifest's task entry says (extra arguments of an executable); null: none.</summary>
    public TaskEntry? Entry { get; init; }

    /// <summary>The run's id: the work item's run id when it came from the queue, so the two records match; null: a new one.</summary>
    public Guid? RunId { get; init; }

    /// <summary>The attempt chain: a retry of a work item passes the same chain, so its steps and units answer from memory and a dead attempt's started steps are put in doubt first.</summary>
    public required string Chain { get; init; }

    /// <summary>1 for the first run of the chain.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The task's inputs as text; the kit binds them to the job's input record.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>Connection slot to the name of a handle; names only, never a connection string or a secret.</summary>
    public IReadOnlyDictionary<string, string> ConnectionHandles { get; init; } = new Dictionary<string, string>();

    /// <summary>The task's connection slots as the manifest declares them: the ones that name an injection form (<see cref="ConnectionSlot.Env"/>) have their login put in the process's environment (plan 28c E02).</summary>
    public IReadOnlyList<ConnectionSlot> Slots { get; init; } = [];

    /// <summary>The manifest's limits: time, memory and CPU (in cores, so 1.5 is a core and a half) are applied by the executor; concurrency and attempts are the scheduler's.</summary>
    public TaskLimits Limits { get; init; } = TaskLimits.None;

    /// <summary>How long a task has to stop after a cancel before its process tree is killed. Default 30 seconds.</summary>
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What happened to one run: how it ended, how its process ended, and what the executor watched on the way.</summary>
/// <param name="RunId">The run record.</param>
/// <param name="End">How the run ended: the task's end message, or what the executor made of a process that gave none (R02's mapping). The run record holds the same.</param>
/// <param name="EndReceived">True when <see cref="End"/> is the task's own end message.</param>
/// <param name="ExitCode">The process's exit code; null when no process was started.</param>
/// <param name="KilledByExecutor">The executor killed the process tree (time limit, a cancel the task ignored, a process that ended its run and did not exit).</param>
/// <param name="Duration">From the start of the run to its end, scratch removal included.</param>
/// <param name="StdoutLines">Lines the process wrote to standard output (before the cap).</param>
/// <param name="StderrLines">Lines the process wrote to standard error (before the cap).</param>
/// <param name="DroppedLines">Lines left out of the run's log because it reached its cap of lines.</param>
/// <param name="TruncatedLines">Lines cut at the per-line cap.</param>
/// <param name="MemoryLimit">How the memory limit was held: enforced by the system, watched by the executor's watchdog, or none.</param>
/// <param name="CpuLimit">How the CPU limit was held.</param>
/// <param name="LimitMechanism">"job object", "cgroup v2", "watchdog" or "none".</param>
/// <param name="ScratchBytes">What the scratch directory held when the process was gone (before it was removed).</param>
/// <param name="ScratchOverLimit">Whether that was over the scratch limit (a warning in the log; the run's outcome is not changed).</param>
/// <param name="InDoubtSteps">The steps of earlier attempts of the chain that were still started and were put in doubt before this attempt began.</param>
public sealed record TaskRunResult(
    Guid RunId,
    TaskEnd End,
    bool EndReceived,
    int? ExitCode,
    bool KilledByExecutor,
    TimeSpan Duration,
    long StdoutLines,
    long StderrLines,
    long DroppedLines,
    long TruncatedLines,
    LimitEnforcement MemoryLimit,
    LimitEnforcement CpuLimit,
    string LimitMechanism,
    long ScratchBytes,
    bool ScratchOverLimit,
    IReadOnlyList<StepRecord> InDoubtSteps)
{
    /// <summary>What the queue item becomes (R02's mapping).</summary>
    public Scheduling.WorkItemStatus WorkItemStatus => End.ToWorkItemStatus();

    /// <summary>The run's outcome as the run store records it.</summary>
    public Monitoring.RunOutcome Outcome => End.ToRunOutcome();
}
