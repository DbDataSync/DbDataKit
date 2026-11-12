using System.Text.Json;
using DbDataKit.Connections;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>A record an outside system refused (a dead letter of the task), as the kit lists it. The body is read with <see cref="RefusedRecords.ReadBody"/>.</summary>
/// <param name="Id">The dead letter's id: also the idempotency key to resend it under.</param>
/// <param name="Source">Where it came from: <c>record:TypeName</c> for <c>RejectAsync&lt;T&gt;</c>, the inbox source for a body that did not parse.</param>
/// <param name="Reason">Why it was refused, as the job (or the last replay) said: a fixed text, never a value of the record.</param>
/// <param name="Attempts">How many times it was refused: the first, and each failed replay.</param>
public sealed record RefusedRecord(Guid Id, string Source, string Reason, int Attempts, string Status, DateTimeOffset FirstRefusedAt, DateTimeOffset LastRefusedAt, DateTimeOffset? ResolvedAt, bool HasBody)
{
    public bool Open => Status == DeadLetter.Open;
}

public enum ReplayFailure
{
    /// <summary>The first record that cannot be sent ends the replay: the ones after it stay open and the next replay starts at it ("from the last good point"). The default: order is kept, and an outage does not make every record fail.</summary>
    Stop,

    /// <summary>The record stays open (its attempts counted) and the replay goes on with the next.</summary>
    Continue,
}

public sealed record ReplayOptions
{
    public ReplayFailure OnFailure { get; init; } = ReplayFailure.Stop;

    /// <summary>Replay at most this many (the oldest first); null: all that are open.</summary>
    public int? Limit { get; init; }
}

/// <summary>What a replay did: how many were sent, which stayed open and why (the text is as the dead letter now holds it).</summary>
public sealed record ReplayReport(int Sent, IReadOnlyList<(Guid Id, string Reason)> Failed, int NotTried)
{
    public bool Complete => Failed.Count == 0 && NotTried == 0;
}

/// <summary>
/// The kit's surface over the records an outside system refused (plan 27a IX14; the store is the task's dead letters, written by <c>RejectAsync</c>): list them with their reason, read a body, replay the open ones, discard one. A replay sends **exactly the open records,
/// once each, oldest first**: a record that goes through is marked replayed and never offered again, one that fails stays open with its attempt counted and the failure's public text (a fixed message of the failure's class, never a value, header or secret).
/// <para>
/// Sending and marking are two steps, so a crash between them replays a record that was already sent: send under <see cref="RefusedRecord.Id"/> as the idempotency key (<c>OutsideHttp.SendJsonAsync(..., idempotencyKey:)</c>) and a duplicate is harmless.
/// The operator's own screens and the runner's command for this (list, replay, discard over the runner's API) belong to plan 28a R25; this class is what they call, and what a job or a test uses in process.
/// </para>
/// </summary>
public sealed class RefusedRecords
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ProgressStore _progress;
    private readonly PayloadStore _payloads;

    /// <summary>The refused records of one task (package, channel and task name) in a state database.</summary>
    public RefusedRecords(StateDatabase database, ProgressKey task)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(task);
        _progress = new ProgressStore(database, task);
        _payloads = new PayloadStore(database);
    }

    /// <summary>The task's refused records, oldest first: the open ones unless <paramref name="status"/> says <see cref="DeadLetter.Replayed"/> or <see cref="DeadLetter.Discarded"/>.</summary>
    public IReadOnlyList<RefusedRecord> List(string status = DeadLetter.Open) =>
        [.. _progress.DeadLetters(status).Select(l => new RefusedRecord(l.Id, l.Source, l.Error, l.Attempts, l.Status, l.FirstFailedAt, l.LastFailedAt, l.ResolvedAt, l.BodyPayload is not null))];

    /// <summary>The body of a record as it was refused, or null when none was kept.</summary>
    public byte[]? ReadBody(Guid id)
    {
        var letter = _progress.DeadLetters(DeadLetter.Open).Concat(_progress.DeadLetters(DeadLetter.Replayed)).Concat(_progress.DeadLetters(DeadLetter.Discarded)).FirstOrDefault(l => l.Id == id)
            ?? throw new KeyNotFoundException($"The task has no refused record {id}.");
        if (letter.BodyPayload is not { } payload) return null;
        using var stream = _payloads.OpenRead(payload);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>The body of a record rejected with <c>RejectAsync&lt;T&gt;</c>, read as <typeparamref name="T"/>.</summary>
    public T? Read<T>(Guid id) => ReadBody(id) is { } body ? JsonSerializer.Deserialize<T>(body, Json) : default;

    /// <summary>An operator decided the record is not worth sending. False when it is not open.</summary>
    public bool Discard(Guid id) => _progress.DiscardDeadLetter(id);

    /// <summary>Sends every open record once with <paramref name="send"/> (it gets the record and its body, and throws when the outside system refuses again).</summary>
    public Task<ReplayReport> ReplayAsync(Func<RefusedRecord, byte[], CancellationToken, Task> send, ReplayOptions? options = null, CancellationToken ct = default) =>
        ReplayCoreAsync(send, options, null, ct);

    private async Task<ReplayReport> ReplayCoreAsync(Func<RefusedRecord, byte[], CancellationToken, Task> send, ReplayOptions? options, Func<RefusedRecord, bool>? where, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        options ??= new ReplayOptions();
        var open = List(DeadLetter.Open);
        if (where is not null) open = [.. open.Where(where)];
        if (options.Limit is { } limit) open = [.. open.Take(limit)];
        var sent = 0;
        var failed = new List<(Guid, string)>();
        for (var i = 0; i < open.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var record = open[i];
            try
            {
                await send(record, ReadBody(record.Id) ?? [], ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var reason = "replay failed: " + Describe(ExceptionClassifier.Unwrap(ex));
                _progress.RecordDeadLetterAttempt(record.Id, reason);
                failed.Add((record.Id, reason));
                if (options.OnFailure == ReplayFailure.Stop) return new ReplayReport(sent, failed, open.Count - i - 1);
                continue;
            }

            _progress.MarkDeadLetterReplayed(record.Id);
            sent++;
        }

        return new ReplayReport(sent, failed, 0);
    }

    /// <summary>The same for records rejected with <c>RejectAsync&lt;T&gt;</c>: only those of type <typeparamref name="T"/> are replayed, read back as <typeparamref name="T"/>; the record's id is given as the idempotency key.</summary>
    public Task<ReplayReport> ReplayAsync<T>(Func<T, Guid, CancellationToken, Task> send, ReplayOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        var source = "record:" + typeof(T).Name;
        return ReplayCoreAsync(async (record, body, token) =>
        {
            var value = JsonSerializer.Deserialize<T>(body, Json) ?? throw new TaskFailureException(TaskFailureKind.Permanent, "The record's body is empty.");
            await send(value, record.Id, token).ConfigureAwait(false);
        }, options, r => r.Source == source, ct);
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskFailureException t => t.Message,
        ConnectionFailureException c => c.Failure.PublicMessage,
        _ => ex.GetType().Name,
    };
}
