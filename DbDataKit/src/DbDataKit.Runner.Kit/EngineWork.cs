namespace DbDataKit.Runner.Kit;

/// <summary>One item of an engine's own queue, as a claim hands it out (plan 28c E11). What the item means is the engine's: <see cref="Kind"/>, <see cref="Subject"/> and <see cref="Part"/> are its keys and <see cref="Payload"/> is opaque here.</summary>
/// <param name="Id">The item; what <see cref="EngineWork.StartAsync"/>, <see cref="EngineWork.FinishAsync"/> and <see cref="EngineWork.ReleaseAsync"/> take.</param>
/// <param name="Kind">What sort of work.</param>
/// <param name="Subject">The thing worked on.</param>
/// <param name="Part">A slice of the subject, empty for none.</param>
/// <param name="Resource">What two running items must not share, empty for none.</param>
/// <param name="Lane">The pool that runs it.</param>
/// <param name="RunId">The id the engine's own record of this item's run uses (and the key of <see cref="EngineWork.IsCancelRequestedAsync"/>).</param>
/// <param name="Payload">What the engine put in with the item.</param>
public sealed record RunnerWorkItem(long Id, string Kind, string Subject, string Part, string Resource, string Lane, Guid RunId, string? Payload);

/// <summary>How an item ended.</summary>
public enum WorkFinish
{
    Done,
    Failed,
    Cancelled,
}

/// <summary>What the queue knows of one subject for one kind of work: when it was last queued (among the rows retention keeps) and whether an item of it is in flight now.</summary>
public sealed record WorkSubject(DateTimeOffset LastEnqueued, bool InFlight);

/// <summary>The queue of the run's owner, for the kinds asked: whether anything at all is in flight, and per kind the subjects the queue knows.</summary>
public sealed record WorkSnapshot(bool Outstanding, IReadOnlyDictionary<string, IReadOnlyDictionary<string, WorkSubject>> Kinds)
{
    /// <summary>The subjects of one kind (empty when the kind was not asked for or has none).</summary>
    public IReadOnlyDictionary<string, WorkSubject> Of(string kind) => Kinds.TryGetValue(kind, out var subjects) ? subjects : new Dictionary<string, WorkSubject>();
}

/// <summary>An item or a run an engine named is not one of the owner this run's token is bound to (the task named by its package, channel and task). The runner refuses it; nothing was changed.</summary>
public sealed class WorkNotOwnedException(string message) : UnauthorizedAccessException(message);

/// <summary>
/// The runner's durable queue of an engine's own items (plan 28c E11), bound to the run: every call is for the run's own owner (its package, channel and task, which the token names), so a task cannot claim,
/// start, finish or release an item of another. A claim belongs to the run that took it and is renewed by the run's heartbeat; what the run still holds when it ends goes back to the queue. For work that
/// outlives a process (a bulk load's segments, the queued passes of a replication); a job's ordinary steps and units are not queued here.
/// </summary>
public sealed class EngineWork
{
    private readonly RunContext _context;

    internal EngineWork(RunContext context) => _context = context;

    /// <summary>Queues an item for this run's owner; when an equal item (same kind, subject and part) is already in flight, nothing is added and its run id is returned.</summary>
    public Task<Guid> EnqueueAsync(string kind, string subject, string part = "", string resource = "", string lane = "", string? payload = null, CancellationToken ct = default) =>
        _context.Backend.EnqueueWorkAsync(kind, subject, part, resource, lane, payload, ct);

    /// <summary>Claims the next item this run's owner has that may run now (of one lane, or any), or null.</summary>
    public Task<RunnerWorkItem?> ClaimAsync(string? lane = null, CancellationToken ct = default) => _context.Backend.ClaimWorkAsync(lane, ct);

    /// <summary>The run begins the item it claimed. False when it no longer holds it (its lease ran out): the item must not be run.</summary>
    public Task<bool> StartAsync(long id, CancellationToken ct = default) => _context.Backend.StartWorkAsync(id, ct);

    /// <summary>The item is over. False when the run no longer holds it.</summary>
    public Task<bool> FinishAsync(long id, WorkFinish outcome, CancellationToken ct = default) => _context.Backend.FinishWorkAsync(id, outcome, ct);

    /// <summary>Gives a claimed item back to the queue without running it (or after stopping it at a boundary). False when the run no longer holds it.</summary>
    public Task<bool> ReleaseAsync(long id, CancellationToken ct = default) => _context.Backend.ReleaseWorkAsync(id, ct);

    /// <summary>True once somebody asked for the item with this run id to stop (an operator, or the runner).</summary>
    public Task<bool> IsCancelRequestedAsync(Guid itemRunId, CancellationToken ct = default) => _context.Backend.IsWorkCancelRequestedAsync(itemRunId, ct);

    /// <summary>What the queue knows of the owner's subjects for these kinds, and whether anything is in flight.</summary>
    public Task<WorkSnapshot> StateAsync(IReadOnlyList<string> kinds, CancellationToken ct = default) => _context.Backend.WorkStateAsync(kinds, ct);
}
