using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// Where an in-process run happens: the state database (with the <c>runner_progress</c>, <c>runs</c> and <c>payloads</c> modules), and the package and channel the progress is kept under. The optional members
/// are what a host or a test adds.
/// </summary>
public sealed class RunnerEnvironment(StateDatabase database, string package, string channel)
{
    public StateDatabase Database { get; } = database ?? throw new ArgumentNullException(nameof(database));

    public string Package { get; } = package;

    public string Channel { get; } = channel;

    /// <summary>The service name recorded on the run (<c>runs.run.service</c>).</summary>
    public string Service { get; init; } = "runner";

    /// <summary>Opens the connection a slot is bound to; without it a slot cannot be opened.</summary>
    public SlotOpener? SlotOpener { get; init; }

    /// <summary>The caps of step results (<c>step.max_result_bytes</c>) and streams.</summary>
    public StepOptions? Steps { get; init; }

    /// <summary>The most a unit loop may run at once, whatever the job asks (the task's and the connection's concurrency limit); null: the job's own value.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>Points where a test makes the process "die" (see <see cref="RunnerHooks"/>).</summary>
    public RunnerHooks? Hooks { get; init; }

    public TimeProvider Time => Database.Time;

    /// <summary>A dry run (plan 27a IX16): the job reads what it reads, the kit's clients and landing helpers refuse writes and list what they would have sent, and nothing the run keeps (progress, the file ledger, the inbox) changes.</summary>
    public bool DryRun { get; init; }

    internal RunSettings Settings => new(Hooks, MaxParallelism, Database.NewId, DryRun);
}

/// <summary>Injection points for tests and the simulator (slice R03c). A hook that throws <see cref="SimulatedCrashException"/> ends the run as if the process had died at that point.</summary>
public sealed class RunnerHooks
{
    /// <summary>Called when a step is about to run its body (the step is recorded started), before the body. Arguments: step name, step key.</summary>
    public Func<string, string, CancellationToken, Task>? BeforeStepBody { get; init; }

    /// <summary>
    /// Called after a streaming step's body returned and before its payload is committed: a crash here is a write interrupted at its very end (an incomplete payload, nothing stored). Arguments: step name, step key.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? BeforePayloadCommit { get; init; }

    /// <summary>
    /// Called after a step's body returned and before its result is stored: the in-doubt moment. For a streaming step the payload is already committed (complete), so a crash here leaves a complete payload the retry
    /// adopts (plan 28b K14). Arguments: step name, step key.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? AfterStepBody { get; init; }

    /// <summary>Called when a unit is about to run its body, before it. Arguments: unit name, unit key.</summary>
    public Func<string, string, CancellationToken, Task>? BeforeUnitBody { get; init; }

    /// <summary>Called at the start of each <c>SaveProgressAsync</c> of the job, with its number (1 for the first), before anything is written; a hook may throw <see cref="ProgressConflictException"/>.</summary>
    public Func<int, CancellationToken, Task>? BeforeProgressSave { get; init; }

    /// <summary>Told of every step the run finished with, as it is added to the run's report (also in an attempt that then crashes, whose <see cref="RunResult"/> never exists).</summary>
    public Action<StepReport>? StepReported { get; init; }

    /// <summary>Told of every unit the run finished with (see <see cref="StepReported"/>).</summary>
    public Action<UnitReport>? UnitReported { get; init; }

    /// <summary>Told of every <c>run.Lineage(reads, writes)</c> the job reports, as the backend is given it.</summary>
    public Action<IReadOnlyList<string>, IReadOnlyList<string>>? LineageReported { get; init; }

    /// <summary>Told of every <c>Count</c> the job makes: the counter's name and the amount.</summary>
    public Action<string, long>? Counted { get; init; }

    /// <summary>Called after a unit's body returned and before the unit is recorded done. Arguments: unit name, unit key.</summary>
    public Func<string, string, CancellationToken, Task>? AfterUnitBody { get; init; }
}
