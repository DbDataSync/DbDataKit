using DbDataKit.Runner.Kit.Changes;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit.Outbound;

/// <summary>
/// What an outbound push remembers of what it has sent: for each record's key, the hash of the content that went out (plan 27a IX22). A record whose key and hash are in the ledger is not sent again; one whose key is there with another hash is sent as a change; a key
/// that is there and is no longer in the source is a deletion.
/// </summary>
public interface ISentLedger
{
    /// <summary>The record with this key was sent with exactly this content.</summary>
    bool IsCurrent(string key, string hash);

    /// <summary>These records were sent (call it after the send returned, then save).</summary>
    void Record(IEnumerable<(string Key, string Hash)> sent);

    /// <summary>The keys sent so far, for finding what disappeared; a ledger that cannot list them says so with <see cref="CanListKeys"/> and refuses deletions.</summary>
    IReadOnlyCollection<string> Keys { get; }

    bool CanListKeys { get; }

    /// <summary>These keys were deleted at the other end.</summary>
    void Forget(IEnumerable<string> keys);
}

/// <summary>
/// The ledger a job keeps as a checkpoint of its progress: a <see cref="RowSnapshot"/> (<c>[Checkpoint] public RowSnapshot? Sent { get; set; }</c>), changed in place. Because it is part of the progress it works the same in a runner (through the protocol) and in the simulator, is saved with
/// the run's outcome, can be saved earlier at each commit point (<c>SaveProgressAsync</c>, which <see cref="PushOptions.Commit"/> calls after every batch) and is rewound with the progress.
/// </summary>
public sealed class SnapshotLedger(RowSnapshot snapshot) : ISentLedger
{
    public RowSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public bool CanListKeys => true;

    public IReadOnlyCollection<string> Keys => [.. Snapshot.Rows.Keys];

    public bool IsCurrent(string key, string hash) => Snapshot.Rows.TryGetValue(key, out var sent) && sent == hash;

    public void Record(IEnumerable<(string Key, string Hash)> sent)
    {
        foreach (var (key, hash) in sent) Snapshot.Rows[key] = hash;
    }

    public void Forget(IEnumerable<string> keys)
    {
        foreach (var key in keys) Snapshot.Rows.Remove(key);
    }
}

/// <summary>
/// The ledger over the runner's idempotency keys (<see cref="ProgressStore.TryBeginIdempotent"/>, kept per task with an expiry), for code that holds the state database itself (a test over <c>RunSimulator.Database</c>, a host's own task): a record is "sent" while its key
/// (the record's key and content hash) has not expired. It cannot list keys, so it cannot find deletions. A job that runs in a task's process has no route to these keys (the kit's protocol does not carry them) and uses <see cref="SnapshotLedger"/>.
/// </summary>
public sealed class ProgressStoreLedger(ProgressStore progress, TimeSpan? lifetime = null) : ISentLedger
{
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromDays(30);

    public bool CanListKeys => false;

    public IReadOnlyCollection<string> Keys => throw new NotSupportedException("The runner's idempotency keys cannot be listed; use a SnapshotLedger to send deletions.");

    public bool IsCurrent(string key, string hash) => progress.IdempotentResult(IdempotencyKeys.ForRecord(key, hash)) is not null;

    public void Record(IEnumerable<(string Key, string Hash)> sent)
    {
        foreach (var (key, hash) in sent)
        {
            var idempotency = IdempotencyKeys.ForRecord(key, hash);
            progress.TryBeginIdempotent(idempotency, _lifetime);
            progress.CompleteIdempotent(idempotency, "sent");
        }
    }

    public void Forget(IEnumerable<string> keys) => throw new NotSupportedException("The runner's idempotency keys cannot be listed; use a SnapshotLedger to send deletions.");
}
