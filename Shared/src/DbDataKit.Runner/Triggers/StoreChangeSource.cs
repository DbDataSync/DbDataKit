namespace DbDataKit.Runner.Triggers;

/// <summary>One applied change of a DbDataStore model as the change feed says it (plan 15 ST03, <c>GET /api/v1/events</c> of a Store): the sequence only grows; <paramref name="By"/> is who wrote it.</summary>
public sealed record StoreChange(long Sequence, string ChangeId, string Model, IReadOnlyList<string> Entities, string By);

/// <summary>
/// What a source gave for a look: the changes after the cursor (all models of the feed that the source was asked for, at most the limit, in order), the highest sequence it looked at (so the cursor moves past events of other models too) and whether the
/// feed had already dropped events after the cursor (a feed is cut to its newest half when it grows; a reader that is far behind misses some).
/// </summary>
public sealed record StoreChangePage(IReadOnlyList<StoreChange> Changes, long Last, bool Missed = false);

/// <summary>
/// Where the trigger engine learns of the changes of a DbDataStore model (plan 27a IX19, R13): a feed with a sequence a reader keeps. The Store's own feed is <c>GET /api/v1/events?after=</c>; a source that reads it needs the Store's address and a
/// pairing of the runner with it (not built: <see cref="TriggerOptions.StoreChanges"/> is null until a host gives one, and a <c>store_change</c> trigger then says so).
/// </summary>
public interface IStoreChangeSource
{
    /// <summary>The sequence of the newest change of the feed (0 when it has none): where a trigger that has just been seen starts following from.</summary>
    Task<long> LatestAsync(string model, CancellationToken ct);

    /// <summary>The changes with a sequence above <paramref name="after"/>, at most <paramref name="limit"/>.</summary>
    Task<StoreChangePage> ReadAsync(string model, long after, int limit, CancellationToken ct);
}
