namespace DbDataKit.Store;

/// <summary>What the expiry of idle drafts decided at a moment: the drafts to warn (idle long enough that the end is near) and the drafts to remove.</summary>
public sealed record DraftExpiryPlan(IReadOnlyList<DraftSummary> Warn, IReadOnlyList<DraftSummary> Remove);

/// <summary>
/// Preview lifetimes for drafts (plan 21 CH07): a draft that has had no write for <c>idleDays</c> (the setting <c>previews.idle_days</c>, 30 by default) is removed with its writes, after a notice <c>noticeDays</c> before.
/// The decision is a pure function of the clock it is given, so a test injects the day; <see cref="RunAsync"/> applies it to a copy of a model. A merged or abandoned branch removes its preview at once through
/// <see cref="ModelWriter.DiscardDraftAsync"/>, which needs no clock.
/// </summary>
public static class DraftExpiry
{
    public const int DefaultIdleDays = 30;
    public const int DefaultNoticeDays = 7;

    public static DraftExpiryPlan Plan(IEnumerable<DraftSummary> drafts, DateTimeOffset now, int idleDays = DefaultIdleDays, int noticeDays = DefaultNoticeDays)
    {
        if (idleDays < 1) throw new ArgumentOutOfRangeException(nameof(idleDays), "A draft is idle for at least one day before it expires.");
        var warn = new List<DraftSummary>();
        var remove = new List<DraftSummary>();
        foreach (var draft in drafts)
        {
            var expires = draft.LastAt.AddDays(idleDays);
            if (now >= expires) remove.Add(draft);
            else if (now >= expires.AddDays(-Math.Min(noticeDays, idleDays - 1))) warn.Add(draft);
        }
        return new DraftExpiryPlan(warn, remove);
    }

    /// <summary>Removes the drafts the plan says are idle and returns the plan (so the caller sends the notices for <see cref="DraftExpiryPlan.Warn"/> and records what was removed).</summary>
    public static async Task<DraftExpiryPlan> RunAsync(ModelWriter writer, DateTimeOffset now, int idleDays = DefaultIdleDays, int noticeDays = DefaultNoticeDays, CancellationToken cancellationToken = default)
    {
        var plan = Plan(await writer.DraftsAsync(cancellationToken).ConfigureAwait(false), now, idleDays, noticeDays);
        if (plan.Remove.Count > 0) await writer.DiscardDraftsAsync(plan.Remove.Select(d => d.Id), cancellationToken).ConfigureAwait(false);
        return plan;
    }
}
