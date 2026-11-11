using DbDataKit.Monitoring;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit.Testing;

/// <summary>One attempt the simulator ran: how it ended and what it did. A crash (<see cref="Crashed"/>) is a process that died: its run is recorded <see cref="RunOutcome.Interrupted"/> and no <see cref="Result"/> exists.</summary>
/// <param name="Number">1 for the first attempt of the chain.</param>
/// <param name="RunId">The run record in the state database.</param>
/// <param name="Result">What the kit returned; null for a crash.</param>
/// <param name="Steps">The steps the attempt finished (also for a crash: those before it).</param>
/// <param name="Units">The units the attempt finished.</param>
/// <param name="Counters">What the attempt counted, by counter name (also for a crash).</param>
public sealed record SimulatedAttempt(
    int Number, Guid RunId, RunOutcome Outcome, bool Crashed, RunResult? Result, IReadOnlyList<StepReport> Steps, IReadOnlyList<UnitReport> Units, IReadOnlyDictionary<string, long> Counters);

/// <summary>The units of one kind in the attempt chain as stored: how many in each state, and which keys.</summary>
public sealed record UnitsSummary(string Name, IReadOnlyList<UnitRecord> Records)
{
    public int Total => Records.Count;

    public int Completed => Count(UnitState.Completed);

    public int Failed => Count(UnitState.Failed);

    public int Skipped => Count(UnitState.Skipped);

    /// <summary>Begun and not finished: a crash or a cancellation left them (they run again on the retry).</summary>
    public int Started => Count(UnitState.Started);

    public int Count(string state) => Records.Count(r => r.State == state);

    public IReadOnlyList<string> Keys(string state) => [.. Records.Where(r => r.State == state).Select(r => r.Key).Order(StringComparer.Ordinal)];

    public UnitRecord? Of(string key) => Records.FirstOrDefault(r => r.Key == key);
}

/// <summary>The completed result of a streaming step, as stored.</summary>
public sealed record StepPayload(string Key, Guid Id, long Size, string? Sha256, string? MediaType, Func<byte[]> Read)
{
    public byte[] ReadAllBytes() => Read();
}

/// <summary>
/// Runs a job in memory over the real stores on SQLite (plan 28b section 10), so the semantics are the kit's own: the same step memory, unit skipping, checkpoint-with-the-outcome and recovery as in a runner.
/// <para>
/// <see cref="RunAsync"/> runs one attempt of one attempt chain; call it again for the retry, which finds what the earlier attempts left. Failures are injected with typed calls (the step, unit and counter
/// values the job declares, not strings): a rename breaks the test at compile time. An injection fires <c>times</c> times in all, across attempts, then stays quiet, so "fail once, then the retry runs".
/// </para>
/// </summary>
/// <typeparam name="TJob">The job class.</typeparam>
public sealed class RunSimulator<TJob> : IDisposable
    where TJob : RunnerJobBase, new()
{
    private enum Kind { CrashBeforeBody, CrashAfterBody, CrashMidStream, CancelStep, CancelUnit, FailUnit, Conflict }

    private sealed class Injection(Kind kind, string name, string? key, int times, int number = 0, TaskFailureKind failure = TaskFailureKind.Permanent)
    {
        public Kind Kind { get; } = kind;

        public string Name { get; } = name;

        public string? Key { get; } = key;

        public int Remaining { get; set; } = times;

        public int Number { get; } = number;

        public TaskFailureKind Failure { get; } = failure;
    }

    private sealed class Tally
    {
        public List<StepReport> Steps { get; } = [];

        public List<UnitReport> Units { get; } = [];

        public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private readonly DirectoryInfo _directory;
    private readonly RunnerEnvironment _environment;
    private readonly object? _input;
    private readonly string _chain = Guid.NewGuid().ToString("D");
    private readonly List<Injection> _injections = [];
    private readonly List<SimulatedAttempt> _attempts = [];
    private readonly List<Progress> _progress = [];
    private readonly Dictionary<string, int> _executions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counted = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancel;
    private Tally _tally = new();

    /// <summary>A simulator with a state database of its own (a temporary SQLite file, removed by <see cref="Dispose"/>).</summary>
    /// <param name="input">The job's input record; null: the default of its type.</param>
    /// <param name="slots">Opens the connection a slot is bound to; without it a job that opens a slot fails.</param>
    /// <param name="steps">The caps of step results and streams.</param>
    /// <param name="maxParallelism">The most a unit loop may run at once, whatever the job asks (1 makes a test deterministic).</param>
    /// <param name="package">The package name progress is kept under.</param>
    /// <param name="channel">The channel name progress is kept under.</param>
    public RunSimulator(object? input = null, SlotOpener? slots = null, StepOptions? steps = null, int? maxParallelism = null, string package = "simulator", string channel = "test")
    {
        _input = input;
        _directory = Directory.CreateTempSubdirectory("dbdatakit-sim-");
        var layout = StateLayout.Create(StateEngineIds.Sqlite, [ProgressModule.Module, RunsModule.Module], StateScope.Standalone);
        Database = new StateDatabase(Path.Combine(_directory.FullName, "state.db"), layout, null, null);
        _environment = new RunnerEnvironment(Database, package, channel)
        {
            SlotOpener = slots,
            Steps = steps,
            MaxParallelism = maxParallelism,
            Hooks = new RunnerHooks
            {
                BeforeStepBody = BeforeStepBody,
                BeforePayloadCommit = (name, key, _) => Fire(Kind.CrashMidStream, name, key),
                AfterStepBody = (name, key, _) => Fire(Kind.CrashAfterBody, name, key),
                BeforeUnitBody = BeforeUnitBody,
                BeforeProgressSave = BeforeProgressSave,
                StepReported = report => { lock (_gate) _tally.Steps.Add(report); },
                UnitReported = report => { lock (_gate) _tally.Units.Add(report); },
                Counted = (name, n) => { lock (_gate) { _tally.Counters[name] = _tally.Counters.GetValueOrDefault(name) + n; _counted[name] = _counted.GetValueOrDefault(name) + n; } },
            },
        };
        TaskName = JobRunner.TaskNameOf(typeof(TJob));
    }

    /// <summary>The state database the job runs over: the real stores, for a test to look into.</summary>
    public StateDatabase Database { get; }

    /// <summary>The job's task name (its <c>[RunnerTask]</c>).</summary>
    public string TaskName { get; }

    /// <summary>The attempt chain every <see cref="RunAsync"/> belongs to.</summary>
    public string Chain => _chain;

    /// <summary>The attempts run so far, in order.</summary>
    public IReadOnlyList<SimulatedAttempt> Attempts => [.. _attempts];

    // ---- injection

    /// <summary>
    /// The process dies after the step's body returned and before its result is stored: the in-doubt case. The body ran (its effect happened), nothing is recorded, and the retry finds the step in doubt and applies
    /// its recovery. For a streaming step the payload is already committed, so the retry adopts it (plan 28b K14).
    /// </summary>
    /// <param name="step">The step, as declared (<c>DailyDropSteps.Download</c>).</param>
    /// <param name="times">How many times in all, across attempts.</param>
    /// <param name="key">Only the step with this key (the key the step's key function makes); null: any.</param>
    public RunSimulator<TJob> FailAfterBody(StepDefinition step, int times = 1, string? key = null) => Add(new Injection(Kind.CrashAfterBody, Name(step), key, times));

    /// <summary>The process dies as the step is about to run its body (the step is recorded started, the body has not run): in doubt on the retry although the body did nothing.</summary>
    public RunSimulator<TJob> FailBeforeBody(StepDefinition step, int times = 1, string? key = null) => Add(new Injection(Kind.CrashBeforeBody, Name(step), key, times));

    /// <summary>The process dies at the very end of a streaming step's write, before its payload commits: an incomplete payload is left, nothing is adopted, and the step's recovery applies on the retry.</summary>
    public RunSimulator<TJob> FailMidStream<TIn>(StreamStep<TIn> step, int times = 1, string? key = null) => Add(new Injection(Kind.CrashMidStream, Name(step), key, times));

    /// <summary>The run is cancelled as the step is about to run its body: the run ends <see cref="RunOutcome.Stopped"/> and the step stays started (in doubt on the retry).</summary>
    public RunSimulator<TJob> CancelAt(StepDefinition step, int times = 1, string? key = null) => Add(new Injection(Kind.CancelStep, Name(step), key, times));

    /// <summary>The run is cancelled as a unit is about to run its body (the unit with <paramref name="key"/>, or any): the run ends <see cref="RunOutcome.Stopped"/> and the unit stays started, so it runs again on the retry.</summary>
    public RunSimulator<TJob> CancelAt(UnitDefinition unit, string? key = null, int times = 1) => Add(new Injection(Kind.CancelUnit, Name(unit), key, times));

    /// <summary>The <paramref name="n"/>th <c>SaveProgressAsync</c> of an attempt (1 for the first) finds that another run moved a checkpoint: nothing is written, and the run ends <see cref="RunOutcome.Stopped"/>.</summary>
    public RunSimulator<TJob> ConflictOnSave(int n, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        return Add(new Injection(Kind.Conflict, "", null, times, n));
    }

    /// <summary>The body of the unit with <paramref name="key"/> throws as it starts: the unit is recorded failed, the others go on, and the run ends <see cref="RunOutcome.CompletedWithFailures"/> (or fails, with <c>OnFailure.Stop</c>).</summary>
    /// <param name="failure">How the failure classifies (what a retry should do).</param>
    public RunSimulator<TJob> FailUnit(UnitDefinition unit, string key, int times = 1, TaskFailureKind failure = TaskFailureKind.Permanent)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Add(new Injection(Kind.FailUnit, Name(unit), key, times, failure: failure));
    }

    /// <summary><see cref="FailUnit(UnitDefinition, string, int, TaskFailureKind)"/> for the item whose key the unit's key function makes.</summary>
    public RunSimulator<TJob> FailUnit<T>(Unit<T> unit, T item, int times = 1, TaskFailureKind failure = TaskFailureKind.Permanent) =>
        FailUnit(unit, (unit ?? throw new ArgumentNullException(nameof(unit))).KeyOf(item), times, failure);

    private static string Name(StepDefinition step) => (step ?? throw new ArgumentNullException(nameof(step))).Name;

    private static string Name(UnitDefinition unit) => (unit ?? throw new ArgumentNullException(nameof(unit))).Name;

    private RunSimulator<TJob> Add(Injection injection)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(injection.Remaining, 1);
        lock (_gate) _injections.Add(injection);
        return this;
    }

    private static bool KeyMatches(string? wanted, string stored) => wanted is null || stored == wanted || stored.EndsWith("::" + wanted, StringComparison.Ordinal);

    /// <summary>Fires the first injection of the kind that matches; throws what it stands for.</summary>
    private Task Fire(Kind kind, string name, string key)
    {
        Injection? hit = null;
        lock (_gate)
        {
            hit = _injections.FirstOrDefault(i => i.Kind == kind && i.Remaining > 0 && i.Name == name && KeyMatches(i.Key, key));
            if (hit is not null) hit.Remaining--;
        }

        if (hit is null) return Task.CompletedTask;
        switch (kind)
        {
            case Kind.CancelStep:
            case Kind.CancelUnit:
                _cancel!.Cancel();
                throw new OperationCanceledException($"Injected cancellation at '{name}' (key '{key}').", _cancel.Token);
            case Kind.FailUnit:
                throw new TaskFailureException(hit.Failure, $"Injected failure of the unit '{name}' with key '{key}'.", key);
            default:
                throw new SimulatedCrashException($"Injected crash {(kind == Kind.CrashAfterBody ? "after" : kind == Kind.CrashMidStream ? "while writing" : "before")} the body of '{name}' (key '{key}').");
        }
    }

    private async Task BeforeStepBody(string name, string key, CancellationToken ct)
    {
        await Fire(Kind.CrashBeforeBody, name, key).ConfigureAwait(false);
        await Fire(Kind.CancelStep, name, key).ConfigureAwait(false);
        lock (_gate) _executions[name] = _executions.GetValueOrDefault(name) + 1;       // counted once the body really starts
    }

    private async Task BeforeUnitBody(string name, string key, CancellationToken ct)
    {
        await Fire(Kind.CancelUnit, name, key).ConfigureAwait(false);
        await Fire(Kind.FailUnit, name, key).ConfigureAwait(false);
    }

    private Task BeforeProgressSave(int number, CancellationToken ct)
    {
        lock (_gate)
        {
            var hit = _injections.FirstOrDefault(i => i.Kind == Kind.Conflict && i.Remaining > 0 && i.Number == number);
            if (hit is null) return Task.CompletedTask;
            hit.Remaining--;
        }

        throw new ProgressConflictException($"Injected conflict at the {number}th SaveProgressAsync: another run moved a checkpoint first.");
    }

    // ---- running

    /// <summary>Runs the next attempt of the chain and returns it. A crash does not throw: it is the attempt's outcome (<see cref="SimulatedAttempt.Crashed"/>).</summary>
    public async Task<SimulatedAttempt> RunAsync(CancellationToken ct = default)
    {
        var number = _attempts.Count + 1;
        var runId = Database.NewId();
        _tally = new Tally();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancel = cancel;
        RunResult? result = null;
        var crashed = false;
        try
        {
            result = await JobRunner.RunAsync(typeof(TJob), _environment, _chain, number, _input, cancel.Token, runId).ConfigureAwait(false);
        }
        catch (SimulatedCrashException)
        {
            crashed = true;
            // what the runner does about a process that vanished (plan 28a R06): the run is Interrupted, and the steps still started are in doubt
            new RunStore(Database, _environment.Service).Finish(runId, RunOutcome.Interrupted);
            new StepStore(Database, new ProgressKey(_environment.Package, _environment.Channel, TaskName)).MarkInDoubt(_chain, number);
        }

        Progress progress = await JobRunner.ReadProgressAsync(typeof(TJob), _environment, CancellationToken.None).ConfigureAwait(false);
        SimulatedAttempt attempt;
        lock (_gate)
        {
            attempt = new SimulatedAttempt(number, runId, crashed ? RunOutcome.Interrupted : result!.Outcome, crashed, result, [.. _tally.Steps], [.. _tally.Units], new Dictionary<string, long>(_tally.Counters));
            _attempts.Add(attempt);
            _progress.Add(progress);
        }

        return attempt;
    }

    // ---- reporting

    private SimulatedAttempt Last => _attempts.Count > 0 ? _attempts[^1] : throw new InvalidOperationException("The job has not run: call RunAsync first.");

    /// <summary>How the latest attempt ended; <see cref="RunOutcome.Interrupted"/> when it crashed.</summary>
    public RunOutcome Outcome => Last.Outcome;

    /// <summary>The failure the latest attempt ended with, as the runner would be told; null for a crash or a run that did not fail.</summary>
    public TaskFailure? Failure => Last.Result?.End.Failure;

    /// <summary>How many times the step's body started, across all attempts (a body that was crashed or cancelled after it started counts; one that was crashed before it started does not).</summary>
    public int Executions(StepDefinition step)
    {
        lock (_gate) return _executions.GetValueOrDefault(Name(step));
    }

    /// <summary>
    /// What the step did in the latest attempt: <see cref="StepStatus.Ran"/>, <see cref="StepStatus.Reused"/>, <see cref="StepStatus.Recovered"/> or <see cref="StepStatus.Failed"/>; the last report when it ran for several keys. Null when
    /// the attempt did not finish the step (it crashed or was cancelled in it, or never reached it).
    /// </summary>
    public StepStatus? StatusOf(StepDefinition step) => StatusOf(step, Last.Number);

    /// <summary><see cref="StatusOf(StepDefinition)"/> for attempt <paramref name="attempt"/> (1 for the first).</summary>
    public StepStatus? StatusOf(StepDefinition step, int attempt) => Attempt(attempt).Steps.LastOrDefault(s => s.Name == Name(step))?.Status;

    /// <summary>The step's reports (one per key) in the latest attempt.</summary>
    public IReadOnlyList<StepReport> StepReports(StepDefinition step) => [.. Last.Steps.Where(s => s.Name == Name(step))];

    /// <summary>How much of the counter was counted, across all attempts (work that was done, including by an attempt that then crashed). <see cref="CountedIn"/> asks for one attempt.</summary>
    public long Counted(Counter counter)
    {
        lock (_gate) return _counted.GetValueOrDefault((counter ?? throw new ArgumentNullException(nameof(counter))).Name);
    }

    /// <summary>How much of the counter attempt <paramref name="attempt"/> counted.</summary>
    public long CountedIn(Counter counter, int attempt) => Attempt(attempt).Counters.GetValueOrDefault((counter ?? throw new ArgumentNullException(nameof(counter))).Name);

    /// <summary>
    /// The job's stored progress after the latest attempt (a fresh object, bound as a run binds it). <see cref="Keyed{T}"/> members read the store when first used, so read them straight after the attempt.
    /// </summary>
    public TProgress Progress<TProgress>() where TProgress : Progress => Progress<TProgress>(Last.Number);

    /// <summary>The stored progress after attempt <paramref name="attempt"/> (1 for the first); the flat members are as they were then.</summary>
    public TProgress Progress<TProgress>(int attempt) where TProgress : Progress
    {
        var found = Attempt(attempt).Number - 1;
        lock (_gate) return _progress[found] as TProgress ?? throw new InvalidOperationException($"The job's progress is {_progress[found].GetType().Name}, not {typeof(TProgress).Name}.");
    }

    /// <summary>The units of one kind in the chain as stored now: how many ran, failed, were skipped or are left started, and which keys.</summary>
    public UnitsSummary Units(UnitDefinition unit)
    {
        var name = Name(unit);
        return new UnitsSummary(name, [.. new UnitStore(Database, Key).List(_chain).Where(u => u.Name == name).OrderBy(u => u.Key, StringComparer.Ordinal)]);
    }

    /// <summary>The completed results of a streaming step, one per key, with their bytes readable.</summary>
    public IReadOnlyList<StepPayload> Payloads(StepDefinition step)
    {
        var name = Name(step);
        var payloads = new PayloadStore(Database);
        var list = new List<StepPayload>();
        foreach (var row in new StepStore(Database, Key).List(_chain).Where(s => s.Name == name && s.State == StepState.Completed && s.ResultPayload is not null))
        {
            var id = row.ResultPayload!.Value;
            var info = payloads.Get(id) ?? throw new InvalidOperationException($"The payload {id} of the step '{name}' (key '{row.Key}') does not exist.");
            list.Add(new StepPayload(row.Key, id, info.Size, info.Sha256, info.MediaType, () => ReadAll(payloads, id)));
        }

        return list;
    }

    /// <summary>The steps of the chain as stored (state, attempt, result), for a test that wants the rows.</summary>
    public IReadOnlyList<StepRecord> StoredSteps() => new StepStore(Database, Key).List(_chain);

    /// <summary>The run log of every attempt, in order: what the kit and the job (<c>Run.Log</c>) wrote.</summary>
    public IReadOnlyList<RunLogLine> Log
    {
        get
        {
            var runs = new RunStore(Database, _environment.Service);
            return [.. _attempts.SelectMany(a => runs.Logs(a.RunId, limit: 10_000))];
        }
    }

    private ProgressKey Key => new(_environment.Package, _environment.Channel, TaskName);

    private SimulatedAttempt Attempt(int attempt) =>
        attempt >= 1 && attempt <= _attempts.Count ? _attempts[attempt - 1] : throw new ArgumentOutOfRangeException(nameof(attempt), $"There is no attempt {attempt}; {_attempts.Count} have run.");

    private static byte[] ReadAll(PayloadStore payloads, Guid id)
    {
        using var reader = payloads.OpenRead(id);
        using var memory = new MemoryStream();
        reader.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Removes the temporary state database.</summary>
    public void Dispose()
    {
        try { _directory.Delete(recursive: true); }
        catch (IOException) { /* a scratch directory left behind is untidy and harmless */ }
        catch (UnauthorizedAccessException) { }
    }
}
