using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// A job's own verdict on its failure: retryable or not, by <see cref="Kind"/> (plan 28b section 8). <see cref="Item"/> names the record it failed on, when there is one. Anything else a job throws is classified by
/// its type (<see cref="JobRunner"/>).
/// </summary>
public class TaskFailureException(TaskFailureKind kind, string message, string? item = null, Exception? inner = null) : Exception(message, inner)
{
    public TaskFailureKind Kind { get; } = kind;

    public string? Item { get; } = item;

    public TaskFailure ToFailure() => new(Kind, Message, Item);
}

/// <summary>A checkpoint moved since the run read it (another run got there first, or an administrator rewound it): the job should stop, and the run is recorded <c>Stopped</c> (plan 28b section 5).</summary>
public sealed class ProgressConflictException(string message) : Exception(message);

/// <summary>
/// A stand-in for the process ending at this point (a kill, a power loss): nothing is recorded, no step is failed, the run stays <c>Running</c>. For tests and the simulator (slice R03c); a job never throws it.
/// </summary>
public sealed class SimulatedCrashException(string message) : Exception(message);

/// <summary>A payload in the state database: a streamed step's result, a rejected record, a file a job named. <see cref="OpenRead"/> streams it back one chunk at a time and checks its hash at the end.</summary>
public sealed class PayloadRef
{
    private readonly Func<Guid, Stream> _open;

    internal PayloadRef(Guid id, long size, string? sha256, string? mediaType, Func<Guid, Stream> open)
    {
        Id = id;
        Size = size;
        Sha256 = sha256;
        MediaType = mediaType;
        _open = open;
    }

    public Guid Id { get; }

    public long Size { get; }

    public string? Sha256 { get; }

    public string? MediaType { get; }

    /// <summary>A forward-only stream of the bytes, holding one chunk at a time; at its end the size and SHA-256 recorded at write time are checked.</summary>
    public Stream OpenRead() => _open(Id);

    /// <summary>The whole payload in memory, refused over <paramref name="maxBytes"/> (16 MiB by default): a payload that big is read as a stream.</summary>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken ct = default, long maxBytes = 16 * 1024 * 1024)
    {
        if (Size > maxBytes) throw new InvalidOperationException($"The payload {Id} is {Size} bytes, over the {maxBytes}-byte limit for reading it whole; read it as a stream (OpenRead).");
        await using var stream = OpenRead();
        using var memory = new MemoryStream(checked((int)Size));
        await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    public override string ToString() => $"payload {Id} ({Size} bytes)";
}

/// <summary>The few things of a run not typed by the job (plan 28b section 2).</summary>
public interface IRun
{
    Guid Id { get; }

    /// <summary>The attempt number, 1 for the first run of an attempt chain.</summary>
    int Attempt { get; }

    string Channel { get; }

    string Package { get; }

    /// <summary>The task's name.</summary>
    string Task { get; }

    /// <summary>The run's log: lines go to the run's log in the state database.</summary>
    ILogger Log { get; }

    /// <summary>What this job reads and writes, for lineage (plan 28b section 3). Sent to the runner (one-way); recorded nowhere durable yet (the lineage slice gives it a place).</summary>
    void Lineage(IEnumerable<string> reads, IEnumerable<string> writes);

    /// <summary>
    /// True when this run is a dry run (plan 27a IX16, <c>tasks run --dry-run</c>): it reads the outside system and writes nothing. The kit's own parts already behave (progress is not saved, the file ledger and the inbox are not
    /// changed, a refused record is listed instead of kept); a job gives its clients the same word: <c>OutsideHttpOptions.DryRun</c>, a <c>WriteGate</c> with <c>DryRun</c>. A job that writes through code of its own is not guarded.
    /// </summary>
    bool DryRun { get; }

    /// <summary>Says in the run's log what a dry run would have sent or written (nothing is written by this call but the log line); a job calls it where it skips a write because <see cref="DryRun"/> is true.</summary>
    void WouldDo(string what);
}

/// <summary>The inbox of a job (plan 28b section 7): what the serving layer received for the task, handed out exactly once.</summary>
public sealed class Inbox<T>
{
    private readonly RunContext _run;

    internal Inbox(RunContext run) => _run = run;

    /// <summary>The next received item, parsed as <typeparamref name="T"/>, or null when none waits. A body that does not parse becomes a dead letter and the next item is taken.</summary>
    public async Task<InboxItem<T>?> TakeAsync(CancellationToken ct)
    {
        if (_run.Settings.DryRun)
        {
            // an item is handed out once: taking one would be a write, and peeking is not offered, so a dry run sees an empty inbox and says so
            _run.Run.WouldDo("take the next inbox item (a dry run takes none)");
            return null;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var taken = await _run.Backend.TakeInboxAsync(ct).ConfigureAwait(false);
            if (taken is null) return null;
            var body = taken.Body is { } payload ? await _run.ReadPayloadAsync(payload, ct).ConfigureAwait(false) : [];
            T? value;
            try
            {
                value = System.Text.Json.JsonSerializer.Deserialize<T>(body, RunContext.Json);
            }
            catch (System.Text.Json.JsonException ex)
            {
                await _run.RejectBodyAsync($"inbox:{taken.Source}", body, $"The body is not a {typeof(T).Name}: {ex.Message}", taken.Id, ct).ConfigureAwait(false);
                continue;
            }

            if (value is null)
            {
                await _run.RejectBodyAsync($"inbox:{taken.Source}", body, $"The body is empty, not a {typeof(T).Name}.", taken.Id, ct).ConfigureAwait(false);
                continue;
            }

            _run.OpenInbox.TryAdd(taken.Id, 0);
            return new InboxItem<T>(_run, taken.Id, taken.Source, value, body);
        }
    }
}

/// <summary>One item of the inbox. Complete it or reject it; one that is neither when the run ends is released and taken again.</summary>
public sealed class InboxItem<T>
{
    private readonly RunContext _run;
    private readonly byte[] _body;

    internal InboxItem(RunContext run, Guid id, string source, T value, byte[] body)
    {
        _run = run;
        Id = id;
        Source = source;
        Value = value;
        _body = body;
    }

    public Guid Id { get; }

    public string Source { get; }

    public T Value { get; }

    public Task CompleteAsync(CancellationToken ct)
    {
        _run.OpenInbox.TryRemove(Id, out _);
        return _run.Backend.CompleteInboxAsync(Id, ct);
    }

    /// <summary>Makes a dead letter of the item (replayable later) and counts a rejected record: the run ends <c>CompletedWithFailures</c>.</summary>
    public async Task RejectAsync(string reason, CancellationToken ct)
    {
        await _run.RejectBodyAsync($"inbox:{Source}", _body, reason, Id, ct).ConfigureAwait(false);
        _run.OpenInbox.TryRemove(Id, out _);
    }
}

/// <summary>The file ledger of a job (plan 28b section 7, plan 29 section 4.2): a file that arrives twice runs once.</summary>
public sealed class Files
{
    private readonly RunContext _run;

    internal Files(RunContext run) => _run = run;

    /// <summary>A handle for the file, or null when a file with this content hash was already processed on this connection (whatever it was called).</summary>
    public async Task<FileClaim?> ClaimAsync(string connection, string path, long size, DateTimeOffset modified, string hash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        // a dry run leaves the ledger as it is, so it cannot tell a content it has seen: every file is claimed (and a rerun of the real run is what skips the seen ones)
        if (_run.Settings.DryRun) return new FileClaim(_run, connection, path, hash);
        var seen = await _run.Backend.ClaimFileAsync(connection, path, size, modified, hash, ct).ConfigureAwait(false);
        return seen ? null : new FileClaim(_run, connection, path, hash);
    }
}

/// <summary>A file the job is processing.</summary>
public sealed class FileClaim
{
    private readonly RunContext _run;

    internal FileClaim(RunContext run, string connection, string path, string hash)
    {
        _run = run;
        Connection = connection;
        Path = path;
        Hash = hash;
    }

    public string Connection { get; }

    public string Path { get; }

    public string Hash { get; }

    /// <summary>Marks the file processed, with a word for how it went (<c>done</c> by default).</summary>
    public Task CompleteAsync(string outcome = "done", CancellationToken ct = default)
    {
        if (!_run.Settings.DryRun) return _run.Backend.MarkFileProcessedAsync(Connection, Hash, outcome, ct);
        _run.Run.WouldDo($"mark the file '{Path}' processed ({outcome}) in the file ledger");
        return Task.CompletedTask;
    }
}

/// <summary>How a step ended, as the run reports it (the stored state is <see cref="StepState"/>).</summary>
public enum StepStatus
{
    Ran,
    Reused,
    Recovered,
    Failed,
}

public sealed record StepReport(string Name, string Key, StepStatus Status, TimeSpan Duration, IReadOnlyDictionary<string, long> Counts);

public sealed record UnitReport(string Name, string Key, string State, TimeSpan Duration, IReadOnlyDictionary<string, long> Counts, string? Error);

/// <summary>What a run came to: how it ended, and what it did on the way. <see cref="End"/> is the message the runner would be sent (plan 28a R02).</summary>
public sealed record RunResult(
    Guid RunId,
    int Attempt,
    TaskEnd End,
    IReadOnlyList<StepReport> Steps,
    IReadOnlyList<UnitReport> Units,
    IReadOnlyDictionary<string, long> Counters,
    string? LastStatus)
{
    public Monitoring.RunOutcome Outcome => End.ToRunOutcome();
}
