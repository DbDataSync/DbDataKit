using DbDataKit.Monitoring;

namespace DbDataKit.Store;

/// <summary>What one pass of the expiry did: the drafts it removed and the notices it sent.</summary>
public sealed record DraftSweepResult(int Removed, int Warned);

/// <summary>
/// One pass of the expiry of idle previews over the drafts of one copy of a model (plan 21 CH07), shared by every host that holds drafts: the Store's server and the Gateway (its store module and the apps host's previews write
/// into the same drafts). <see cref="DraftExpiry.Plan"/> decides; this sends the notices (one before, once, and one after the removal) into the notification feed and removes what is idle. The clock is a parameter.
/// </summary>
public static class DraftSweep
{
    public const string WarningKind = "PreviewExpiring";
    public const string RemovedKind = "PreviewRemoved";

    /// <summary>Runs one pass over the drafts <paramref name="writer"/> sees. <paramref name="sincePrevious"/> is the time since the previous pass: a notice is sent only for a draft whose notice day fell inside it, so it is sent once and not on every pass.</summary>
    public static async Task<DraftSweepResult> RunOnceAsync(ModelWriter writer, string modelName, INotificationFeed feed, int idleDays, int noticeDays, DateTimeOffset now, TimeSpan sincePrevious,
        string warningKind = WarningKind, string removedKind = RemovedKind, CancellationToken cancellationToken = default)
    {
        int removed = 0, warned = 0;
        var plan = DraftExpiry.Plan(await writer.DraftsAsync(cancellationToken).ConfigureAwait(false), now, idleDays, noticeDays);
        foreach (var draft in plan.Warn)
        {
            var noticeAt = draft.LastAt.AddDays(idleDays - Math.Min(noticeDays, idleDays - 1));
            if (noticeAt <= now - sincePrevious) continue;
            feed.Raise(warningKind, $"The preview '{draft.Id}' of the model '{modelName}' has had no write since {draft.LastAt:yyyy-MM-dd} and is removed with its draft on {draft.LastAt.AddDays(idleDays):yyyy-MM-dd}; write to it to keep it.", mappingName: modelName);
            warned++;
        }
        if (plan.Remove.Count > 0)
        {
            await writer.DiscardDraftsAsync(plan.Remove.Select(d => d.Id), cancellationToken).ConfigureAwait(false);
            foreach (var draft in plan.Remove)
            {
                feed.Raise(removedKind, $"The preview '{draft.Id}' of the model '{modelName}' was removed with its draft: it had no write for {idleDays} days.", mappingName: modelName);
                removed++;
            }
        }
        return new DraftSweepResult(removed, warned);
    }
}
