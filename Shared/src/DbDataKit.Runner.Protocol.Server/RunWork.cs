using DbDataKit.Runner.Kit;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>
/// The durable queue of an engine's own items for one run (plan 28c E11), on the <c>scheduling</c> module under its own service name beside the runner's queue of task runs. The owner is the run's package, channel and
/// task, taken from the run's identity and never from a request, so every call is for the owner the run's token names; an item named by id or by run id that belongs to another owner is refused
/// (<see cref="WorkNotOwnedException"/>). An item is claimed by this run's worker name (the run id), so only this run can start, finish or release it, and what the run holds is renewed by its heartbeat and given back when it ends.
/// </summary>
internal sealed class RunWork(StateDatabase database, RunIdentity identity, TimeSpan? lease = null)
{
    /// <summary>The service name of engines' work in the queue: not the runner's own (<c>runner</c>), so the runner's loop never sees these rows and an engine never sees the runner's.</summary>
    public const string Service = "runner.work";

    /// <summary>The service name the runner's own scheduler keeps its job state and pauses under (<c>RunnerNames.EngineService</c>, which this project does not reference); a task paused there is paused for a run that asks.</summary>
    public const string SchedulerService = "runner";

    private readonly ModuleJobPauseStore _pauses = new(database, SchedulerService);
    private readonly ModuleWorkQueueStore _queue = new(database, Service) { LeaseDuration = lease ?? ModuleWorkQueueStore.DefaultLease };
    private int _used;

    /// <summary>Whether this run has used the queue at all (what the heartbeat and the end need to look at).</summary>
    public bool Used => Volatile.Read(ref _used) != 0;

    public static string OwnerOf(TaskRef task) => $"{task.Package}/{task.Channel}/{task.Task}";

    public string Owner { get; } = OwnerOf(identity.Task);

    /// <summary>The name the run's claims are made under: one run, one claimer.</summary>
    public string Worker { get; } = "run:" + identity.RunId.ToString("D");

    private void Touch() => Interlocked.Exchange(ref _used, 1);

    private WorkItem Own(long id)
    {
        var item = _queue.Get(id);
        if (item is null || !string.Equals(item.Owner, Owner, StringComparison.Ordinal))
            throw new WorkNotOwnedException($"The work item {id} is not one of '{Owner}', which this run's token is bound to.");
        return item;
    }

    public Guid Enqueue(string kind, string subject, string part, string resource, string lane, string? payload)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("A work item has a kind.");
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("A work item has a subject.");
        Touch();
        return _queue.Enqueue(Owner, kind, subject, part ?? "", resource ?? "", lane ?? "", string.IsNullOrEmpty(payload) ? null : payload);
    }

    public RunnerWorkItem? Claim(string? lane)
    {
        Touch();
        // an item a drain that died still held goes back first (its lease ran out), so the owner's next drain finds it
        _queue.ReleaseExpired();
        return _queue.TryClaimNext(Owner, Worker, string.IsNullOrEmpty(lane) ? null : lane) is { } item
            ? new RunnerWorkItem(item.Id, item.Kind, item.Subject, item.Part, item.Resource, item.Lane, item.RunId, item.PayloadJson)
            : null;
    }

    public bool Start(long id)
    {
        Own(id);
        return _queue.TryMarkRunning(id, Worker);
    }

    public bool Finish(long id, WorkFinish outcome)
    {
        Own(id);
        return _queue.TryFinish(id, Worker, outcome switch { WorkFinish.Done => WorkItemStatus.Done, WorkFinish.Failed => WorkItemStatus.Failed, _ => WorkItemStatus.Cancelled });
    }

    public bool Release(long id)
    {
        Own(id);
        return _queue.TryRelease(id, Worker);
    }

    public bool IsCancelRequested(Guid itemRunId)
    {
        var item = _queue.FindByRun(itemRunId);
        if (item is not null && !string.Equals(item.Owner, Owner, StringComparison.Ordinal))
            throw new WorkNotOwnedException($"The run {itemRunId:D} is not an item of '{Owner}', which this run's token is bound to.");
        return item is not null && _queue.IsCancelRequested(itemRunId);
    }

    public WorkSnapshot State(IReadOnlyList<string> kinds)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, WorkSubject>>(StringComparer.Ordinal);
        foreach (var kind in kinds.Distinct(StringComparer.Ordinal))
            result[kind] = _queue.SubjectsOf(Owner, kind).ToDictionary(s => s.Key, s => new WorkSubject(s.Value.LastEnqueued, s.Value.InFlight), StringComparer.Ordinal);
        return new WorkSnapshot(_queue.HasOutstandingWork(Owner), result, _pauses.IsPaused($"{identity.Task.Package}/{identity.Task.Channel}", identity.Task.Task));
    }

    /// <summary>The run is alive: every claim it holds lasts another lease.</summary>
    public int Renew() => _queue.RenewClaimsBy(Worker);

    /// <summary>The run is over: whatever it still holds goes back to the queue (Pending; nobody saw the work fail). The count.</summary>
    public int ReleaseAll() => _queue.ReleaseClaimsBy(Worker);
}
