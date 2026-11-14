using System.Collections.Concurrent;
using System.Text.Json;
using DbDataKit.Monitoring;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Kit;

/// <summary>Counts made inside a scope (the run, a unit, a step): a count is added to the scope and every scope around it.</summary>
internal sealed class CountScope(CountScope? parent)
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    private CountScope? Parent { get; } = parent;

    public void Add(string name, long n)
    {
        for (var scope = this; scope is not null; scope = scope.Parent) scope._counts.AddOrUpdate(name, n, (_, old) => old + n);
    }

    public IReadOnlyDictionary<string, long> Snapshot() => new Dictionary<string, long>(_counts, StringComparer.Ordinal);
}

/// <summary>What the kit's logic needs besides the backend: where a test makes the process die, the cap on units at once, and how a new id is made. In-process it comes from the <see cref="RunnerEnvironment"/>; remotely, from the options of <see cref="RemoteJobHost"/>.</summary>
internal sealed record RunSettings(RunnerHooks? Hooks, int? MaxParallelism, Func<Guid> NewId, bool DryRun = false);

/// <summary>The step a body is running in, so a step called inside it is refused naming both.</summary>
internal sealed record StepFrame(string Name, string Key);

/// <summary>The unit a body is running in: steps inside it take its key into theirs.</summary>
internal sealed record UnitFrame(string Name, string Key);

/// <summary>
/// Everything one running job shares: the backend, the typed progress, what the run did so far (reports, counts, failures). The members of <see cref="RunnerJob{TInput,TProgress,TSlots}"/> are thin calls into it.
/// </summary>
internal sealed class RunContext
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentQueue<StepReport> _steps = new();
    private readonly ConcurrentQueue<UnitReport> _units = new();
    private readonly ConcurrentDictionary<string, object> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _checkedTypes = new(StringComparer.Ordinal);
    private readonly object _failureLock = new();
    private long _rejected;
    private long _failedUnits;
    private string? _firstFailed;
    private string? _status;

    public RunContext(IRunBackend backend, RunSettings settings)
    {
        Backend = backend;
        Settings = settings;
        Root = new CountScope(null);
        Log = new RunLogger(backend);
        Run = new RunFacade(backend.Identity, Log, backend, settings.DryRun, settings.Hooks?.LineageReported);
    }

    public IRunBackend Backend { get; }

    public RunSettings Settings { get; }

    public CountScope Root { get; }

    public ILogger Log { get; }

    public IRun Run { get; }

    public ProgressBinder? Progress { get; set; }

    public ConcurrentDictionary<Guid, byte> OpenInbox { get; } = new();

    public AsyncLocal<CountScope?> Scope { get; } = new();

    public AsyncLocal<StepFrame?> Step { get; } = new();

    public AsyncLocal<UnitFrame?> Unit { get; } = new();

    public CountScope CurrentScope => Scope.Value ?? Root;

    public string? LastStatus => _status;

    public IReadOnlyList<StepReport> StepReports => [.. _steps];

    public IReadOnlyList<UnitReport> UnitReports => [.. _units];

    public long Rejected => Interlocked.Read(ref _rejected);

    public long FailedUnits => Interlocked.Read(ref _failedUnits);

    public string? FirstFailedItem => _firstFailed;

    public void AddStep(StepReport report)
    {
        _steps.Enqueue(report);
        Settings.Hooks?.StepReported?.Invoke(report);
    }

    public void AddUnit(UnitReport report)
    {
        _units.Enqueue(report);
        Settings.Hooks?.UnitReported?.Invoke(report);
    }

    public void Count(Counter counter, long n)
    {
        ArgumentNullException.ThrowIfNull(counter);
        CurrentScope.Add(counter.Name, n);
        Backend.Count(counter.Name, n);
        Settings.Hooks?.Counted?.Invoke(counter.Name, n);
    }

    public void SetStatus(string text)
    {
        _status = text;
        Backend.Status(text);
    }

    public void NoteFailedItem(string item)
    {
        lock (_failureLock) _firstFailed ??= item;
    }

    public void NoteFailedUnit(string item)
    {
        Interlocked.Increment(ref _failedUnits);
        NoteFailedItem(item);
    }

    /// <summary>The step and unit names are fixed strings, unique per job: a second definition under a name is refused at first use (the analyzer finds it earlier).</summary>
    public void Register(string kind, string name, object definition)
    {
        var existing = _definitions.GetOrAdd(kind + ":" + name, definition);
        if (!ReferenceEquals(existing, definition))
            throw new InvalidOperationException($"Two {kind} definitions in this job are named '{name}'; names are unique per job (the console groups by name).");
    }

    /// <summary>Checks once per result type that it comes back from JSON equal to what went in, so a result that does not round-trip fails at its first use and not on the retry that needs it.</summary>
    public string SerializeResult<T>(string stepName, T value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (_checkedTypes.TryAdd(typeof(T).FullName ?? typeof(T).Name, true))
        {
            string again;
            try
            {
                again = JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(json, Json), Json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"The result type {typeof(T).Name} of the step '{stepName}' does not round-trip through System.Text.Json: {ex.Message}", ex);
            }

            if (again != json)
                throw new InvalidOperationException($"The result type {typeof(T).Name} of the step '{stepName}' does not round-trip through System.Text.Json (it came back as {again}, from {json}); use a record with settable or constructor-bound members.");
        }

        return json;
    }

    public async Task<byte[]> ReadPayloadAsync(Guid id, CancellationToken ct)
    {
        await using var stream = Backend.GetPayload(id).OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    /// <summary>Stores a body (an inbox item that does not parse, a record the job refused) as a dead letter and counts a rejected record, which ends the run <c>CompletedWithFailures</c>.</summary>
    public async Task RejectBodyAsync(string source, byte[] body, string reason, Guid id, CancellationToken ct)
    {
        if (Settings.DryRun) Run.WouldDo($"keep the record ({source}) as a dead letter: {reason}");
        else await Backend.AddDeadLetterAsync(source, reason, body, id, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _rejected);
        NoteFailedItem($"dead letter {id}");
        Backend.Log("Warning", $"Rejected a record ({source}): {reason}", id.ToString("D"));
    }

    public Task RejectAsync<T>(T record, string reason, CancellationToken ct) =>
        RejectBodyAsync($"record:{typeof(T).Name}", JsonSerializer.SerializeToUtf8Bytes(record, Json), reason, Settings.NewId(), ct);

    /// <summary>A hook that throws <see cref="SimulatedCrashException"/> makes the process "die" here.</summary>
    public Task AfterStepBodyAsync(string name, string key, CancellationToken ct) => Settings.Hooks?.AfterStepBody?.Invoke(name, key, ct) ?? Task.CompletedTask;

    public Task BeforeStepBodyAsync(string name, string key, CancellationToken ct) => Settings.Hooks?.BeforeStepBody?.Invoke(name, key, ct) ?? Task.CompletedTask;

    public Task BeforePayloadCommitAsync(string name, string key, CancellationToken ct) => Settings.Hooks?.BeforePayloadCommit?.Invoke(name, key, ct) ?? Task.CompletedTask;

    public Task BeforeUnitBodyAsync(string name, string key, CancellationToken ct) => Settings.Hooks?.BeforeUnitBody?.Invoke(name, key, ct) ?? Task.CompletedTask;

    public Task BeforeProgressSaveAsync(int number, CancellationToken ct) => Settings.Hooks?.BeforeProgressSave?.Invoke(number, ct) ?? Task.CompletedTask;

    public Task AfterUnitBodyAsync(string name, string key, CancellationToken ct) => Settings.Hooks?.AfterUnitBody?.Invoke(name, key, ct) ?? Task.CompletedTask;
}

internal sealed class RunFacade(RunIdentity identity, ILogger log, IRunBackend backend, bool dryRun = false, Action<IReadOnlyList<string>, IReadOnlyList<string>>? lineageReported = null) : IRun
{
    public bool DryRun => dryRun;

    public void WouldDo(string what) => backend.Log("Information", $"Dry run, would: {what}", DryRunItem);

    public const string DryRunItem = "dry-run";

    public Guid Id => identity.RunId;

    public int Attempt => identity.Attempt;

    public string Channel => identity.Task.Channel;

    public string Package => identity.Task.Package;

    public string Task => identity.Task.Task;

    public ILogger Log => log;

    public void Lineage(IEnumerable<string> reads, IEnumerable<string> writes)
    {
        IReadOnlyList<string> read = [.. reads ?? []], written = [.. writes ?? []];
        backend.Lineage(read, written);
        lineageReported?.Invoke(read, written);
    }
}

/// <summary>An <see cref="ILogger"/> whose lines go to the run's log in the state database (the logger of <see cref="IRun.Log"/>).</summary>
internal sealed class RunLogger(IRunBackend backend) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message += " " + exception.GetType().Name + ": " + exception.Message;
        backend.Log(logLevel.ToString(), message);
    }
}
