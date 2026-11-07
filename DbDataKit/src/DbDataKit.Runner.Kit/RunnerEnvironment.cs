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
}

/// <summary>Injection points for tests and the simulator (slice R03c). A hook that throws <see cref="SimulatedCrashException"/> ends the run as if the process had died at that point.</summary>
public sealed class RunnerHooks
{
    /// <summary>Called after a step's body returned and before its result is stored: the in-doubt moment. Arguments: step name, step key.</summary>
    public Func<string, string, CancellationToken, Task>? AfterStepBody { get; init; }

    /// <summary>Called after a unit's body returned and before the unit is recorded done. Arguments: unit name, unit key.</summary>
    public Func<string, string, CancellationToken, Task>? AfterUnitBody { get; init; }
}
