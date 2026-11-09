using DbDataKit.Runner.Kit;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// The runner's queue of a drain's own owner, as the drain's parts use it (plan 28c E11): <see cref="EngineWork"/> of the kit in production, a fake in a test. Every call is for the replication this drain's token is
/// bound to; nothing here names another.
/// </summary>
internal interface IDrainQueue
{
    Task<Guid> EnqueueAsync(string kind, string subject, string part, string resource, string lane, string? payload, CancellationToken ct);

    Task<RunnerWorkItem?> ClaimAsync(string? lane, CancellationToken ct);

    Task<bool> StartAsync(long id, CancellationToken ct);

    Task<bool> FinishAsync(long id, WorkFinish outcome, CancellationToken ct);

    Task<bool> ReleaseAsync(long id, CancellationToken ct);

    Task<bool> IsCancelRequestedAsync(Guid itemRunId, CancellationToken ct);

    Task<WorkSnapshot> StateAsync(IReadOnlyList<string> kinds, CancellationToken ct);
}

/// <summary><see cref="IDrainQueue"/> over the kit's <see cref="EngineWork"/>, which is the protocol's <c>EnqueueWork</c>, <c>ClaimWork</c>, <c>StartWork</c>, <c>FinishWork</c>, <c>ReleaseWork</c>, <c>IsCancelRequested</c> and <c>WorkState</c>.</summary>
internal sealed class KitDrainQueue(EngineWork work) : IDrainQueue
{
    public Task<Guid> EnqueueAsync(string kind, string subject, string part, string resource, string lane, string? payload, CancellationToken ct) => work.EnqueueAsync(kind, subject, part, resource, lane, payload, ct);

    public Task<RunnerWorkItem?> ClaimAsync(string? lane, CancellationToken ct) => work.ClaimAsync(lane, ct);

    public Task<bool> StartAsync(long id, CancellationToken ct) => work.StartAsync(id, ct);

    public Task<bool> FinishAsync(long id, WorkFinish outcome, CancellationToken ct) => work.FinishAsync(id, outcome, ct);

    public Task<bool> ReleaseAsync(long id, CancellationToken ct) => work.ReleaseAsync(id, ct);

    public Task<bool> IsCancelRequestedAsync(Guid itemRunId, CancellationToken ct) => work.IsCancelRequestedAsync(itemRunId, ct);

    public Task<WorkSnapshot> StateAsync(IReadOnlyList<string> kinds, CancellationToken ct) => work.StateAsync(kinds, ct);
}

/// <summary>The drain lost the claim on an item (its lease ran out and the runner gave it to another run): the item must not be run by this one.</summary>
internal sealed class WorkClaimLostException(long id, string mapping) : InvalidOperationException($"The claim on work item {id} ('{mapping}') is no longer this run's: its lease ran out and the queue gave it back. This run does not run it.");
