namespace DbDataKit.Runner.Kit;

/// <summary>What <see cref="JobRunner"/> needs of a job without its type arguments.</summary>
public abstract class RunnerJobBase
{
    private protected RunnerJobBase()
    {
    }

    internal abstract Type InputType { get; }

    internal abstract Type ProgressType { get; }

    internal abstract Type SlotsType { get; }

    internal abstract void Bind(object input, Progress progress, Slots slots, RunContext context);

    internal abstract Task ExecuteAsync(CancellationToken ct);
}

/// <summary>
/// A job (plan 28b section 2): a class that overrides <see cref="RunAsync"/>. Its input, progress and connection slots are types; its steps, units and counters are declared static values; everything the runner
/// provides is a member of this class, so there is nothing to construct, register or inject.
/// </summary>
/// <typeparam name="TInput">A record: what the manifest's <c>inputs</c> declares (<see cref="NoInput"/> for none).</typeparam>
/// <typeparam name="TProgress">The typed checkpoints and state, read at start and saved with the outcome (<see cref="NoProgress"/> for none).</typeparam>
/// <typeparam name="TSlots">The typed connections (<see cref="NoSlots"/> for none).</typeparam>
public abstract class RunnerJob<TInput, TProgress, TSlots> : RunnerJobBase
    where TProgress : Kit.Progress, new()
    where TSlots : Kit.Slots, new()
{
    private RunContext? _context;
    private StepEngine? _steps;
    private UnitEngine? _units;
    private TInput? _input;
    private TProgress? _progress;
    private TSlots? _slots;

    /// <summary>Run id, attempt, channel, package, task, the run's log: the few things not typed by the job.</summary>
    protected IRun Run => Context.Run;

    /// <summary>What the manifest's <c>inputs</c> declares.</summary>
    protected TInput Input => _input!;

    /// <summary>Typed checkpoints and state: reading a member is memory; setting one marks it changed, and the changes are saved with the run's outcome.</summary>
    protected TProgress Progress => _progress!;

    /// <summary>Typed connections: <c>Slots.Warehouse</c> is already the right kind of handle.</summary>
    protected TSlots Slots => _slots!;

    /// <summary>The file ledger (plan 28b section 7).</summary>
    protected Files Files => new(Context);

    private RunContext Context => _context ?? throw new InvalidOperationException("The job is not running: start it with JobRunner.RunAsync.");

    protected abstract Task RunAsync(CancellationToken ct);

    internal override Type InputType => typeof(TInput);

    internal override Type ProgressType => typeof(TProgress);

    internal override Type SlotsType => typeof(TSlots);

    internal override void Bind(object input, Kit.Progress progress, Kit.Slots slots, RunContext context)
    {
        _input = (TInput)input;
        _progress = (TProgress)progress;
        _slots = (TSlots)slots;
        _context = context;
        _steps = new StepEngine(context);
        _units = new UnitEngine(context, _steps);
    }

    internal override Task ExecuteAsync(CancellationToken ct) => RunAsync(ct);

    /// <summary>A value step with this input. Choose what a crash in the middle means (<c>Repeatable</c>, <c>Ask</c> or <c>Check</c>), then <c>RunAsync</c>: without a recovery there is nothing to run.</summary>
    protected StepCall<TIn, TOut> Step<TIn, TOut>(ValueStep<TIn, TOut> step, TIn input) => new(_steps ?? throw new InvalidOperationException("The job is not running."), step, input);

    /// <summary>A streaming step with this input: its result is a <see cref="PayloadRef"/>, written in chunks.</summary>
    protected StreamStepCall<TIn> Step<TIn>(StreamStep<TIn> step, TIn input) => new(_steps ?? throw new InvalidOperationException("The job is not running."), step, input);

    /// <summary>
    /// Runs the body for each item, <paramref name="parallelism"/> at a time (clamped by the task's concurrency limit). A unit that completed in this attempt chain is skipped on a retry. A unit that throws is
    /// recorded failed and the others go on, and the run ends <c>CompletedWithFailures</c>; <see cref="OnFailure.Stop"/> stops at the first.
    /// </summary>
    protected Task ForEachAsync<T>(Unit<T> unit, IEnumerable<T> items, int parallelism, Func<T, CancellationToken, Task> body, CancellationToken ct, UnitOptions? options = null) =>
        (_units ?? throw new InvalidOperationException("The job is not running.")).RunAsync(unit, UnitEngine.Async(items), parallelism, body, ct, options);

    /// <summary>The same for items that are produced as they are enumerated.</summary>
    protected Task ForEachAsync<T>(Unit<T> unit, IAsyncEnumerable<T> items, int parallelism, Func<T, CancellationToken, Task> body, CancellationToken ct, UnitOptions? options = null) =>
        (_units ?? throw new InvalidOperationException("The job is not running.")).RunAsync(unit, items, parallelism, body, ct, options);

    /// <summary>What was received for this task (webhooks the serving layer verified and stored): each item exactly once across runners.</summary>
    protected Inbox<T> Inbox<T>() => new(Context);

    /// <summary>Stores the record as a payload and a dead letter, replayable in the console. The run ends <c>CompletedWithFailures</c>.</summary>
    protected Task RejectAsync<T>(T record, string reason, CancellationToken ct) => Context.RejectAsync(record, reason, ct);

    /// <summary>How much of something was done; counted against the unit or step it is made in, and the run.</summary>
    protected void Count(Counter counter, long n) => Context.Count(counter, n);

    /// <summary>What I am doing now: one line, replacing the last.</summary>
    protected void Status(string text) => Context.SetStatus(text);

    /// <summary>
    /// A commit point inside a long run: saves the progress changed so far (a crash then loses nothing before it). Throws <see cref="ProgressConflictException"/> when another run moved a checkpoint first; the job
    /// should stop, and the run is recorded <c>Stopped</c>.
    /// </summary>
    protected async Task SaveProgressAsync(CancellationToken ct)
    {
        var binder = Context.Progress ?? throw new InvalidOperationException("The job has no progress to save.");
        if (!await binder.SaveAsync(null, ct).ConfigureAwait(false))
            throw new ProgressConflictException("A checkpoint of this task moved since the run read it (another run got there first, or it was rewound); this run stops without saving.");
    }
}
