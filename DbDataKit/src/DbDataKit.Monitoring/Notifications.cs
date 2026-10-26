namespace DbDataKit.Monitoring;

/// <summary>What a notification is about. Stored as text, not as an enum ordinal, so an unknown value
/// written by a newer build reads back as itself rather than as whichever kind happens to share its
/// number.</summary>
public static class NotificationKinds
{
    /// <summary>A run ended failed — phase 77.</summary>
    public const string RunFailed = "RunFailed";

    /// <summary>Somebody held a replication — phase 80. Pauses only; a resume is not news.</summary>
    public const string ReplicationPaused = "ReplicationPaused";

    /// <summary>A run failed because the source discarded the history its position pointed into —
    /// phase 80. A run failure with a known fix, so it is its own kind rather than a
    /// <see cref="RunFailed"/> row whose message happens to say so.</summary>
    public const string PositionExpired = "PositionExpired";

    /// <summary>A run failed because a reader, writer or staging provider needed a column's cached
    /// shape and the mapping's metadata cache (phase 90) didn't have it — phase 91. Its own kind for
    /// the same reason <see cref="PositionExpired"/> is: a known fix (Refresh metadata) that a feed
    /// should be able to find without matching on prose.</summary>
    public const string MetadataNotCached = "MetadataNotCached";

    /// <summary>The bound Windows certificate is within its configured warning window of
    /// <c>NotAfter</c> — phase 82, raised by <c>DbDataSync.Api.Services.CertificateExpiryService</c> at
    /// most once a day.</summary>
    public const string CertificateExpiring = "CertificateExpiring";

    /// <summary>The bound Windows certificate is past <c>NotAfter</c> — phase 82. Its own kind rather
    /// than a louder repeat of <see cref="CertificateExpiring"/>, the same way <see
    /// cref="PositionExpired"/> is its own kind rather than a <see cref="RunFailed"/> in disguise: past
    /// expiry is a different, worse state (the site is actively broken for every browser, not merely
    /// approaching it) and a feed should be able to find those without matching on prose.</summary>
    public const string CertificateExpired = "CertificateExpired";

    /// <summary>A client was locked out after too many failed logins — the Proxy (plan 13, P10). The message names the user and the client address, never a password.</summary>
    public const string LoginLockedOut = "LoginLockedOut";

    /// <summary>A statement was refused on a read-only data source — the Proxy (plan 13, P10). The message names the user, the data source and the rule, never the statement.</summary>
    public const string StatementRefused = "StatementRefused";

    /// <summary>A data source turned a caller away because every slot was in use — the Proxy (plan 13, P10).</summary>
    public const string LimitReached = "LimitReached";
}

/// <param name="Kind">One of <see cref="NotificationKinds"/> — but read as an open set: a row written
/// by a newer build carries a kind this one has never heard of, and rendering it as its message is
/// always better than dropping it.</param>
/// <param name="TaskName">What the notification is about, where that is a replication. Null for a
/// kind that is not about one.</param>
public sealed record NotificationRecord(
    long Id,
    string Kind,
    DateTimeOffset CreatedAtUtc,
    string? TaskName,
    string? MappingName,
    Guid? RunId,
    string Message);

/// <param name="LastSeenNotificationId">Where this caller's cursor is, or null if it has never been
/// advanced — and also null for every caller when the deployment does not authenticate, which
/// <paramref name="Personalized"/> is what distinguishes.</param>
/// <param name="Personalized">
/// Whether the read state in this answer belongs to somebody. False when authentication is disabled
/// and there is no user id to key a cursor to: every notification reads as unread, permanently, for
/// everyone at that deployment. Said out loud in the payload because a badge that never clears is
/// otherwise indistinguishable from one that is broken.
/// </param>
public sealed record NotificationFeed(
    IReadOnlyList<NotificationRecord> Notifications,
    long? LastSeenNotificationId,
    int UnreadCount,
    bool Personalized);

/// <summary>
/// The notification feed a product keeps and a console reads (plan 13, M05): one append-only feed everybody sees, and a read position per reader. A reader is whoever the
/// product keys the cursor to: a signed-in user, or a console, so two consoles reading the same server read independently. A client polls with the highest id it holds
/// and is handed everything above it.
/// </summary>
public interface INotificationFeed
{
    /// <summary>Everything newer than <paramref name="sinceId"/>, oldest first; null is the whole feed (retention already bounds it).</summary>
    IReadOnlyList<NotificationRecord> List(long? sinceId = null);

    /// <summary>How many notifications are newer than a reader's cursor; null (never read) counts them all.</summary>
    int UnreadCount(long? lastSeenId);

    /// <summary>Where a reader's cursor is, or null when it has never moved.</summary>
    long? GetCursor(string readerId);

    /// <summary>Moves a reader's cursor to <paramref name="lastSeenNotificationId"/>. It only moves forward: reading an older id again changes nothing.</summary>
    void MarkSeen(string readerId, long lastSeenNotificationId);

    /// <summary>Writes a standalone notification (one not paired with a row another store is writing).</summary>
    void Raise(string kind, string message, string? taskName = null, string? mappingName = null, Guid? runId = null);

    /// <summary>Deletes notifications older than <paramref name="maxAge"/> (null keeps everything) and returns how many went. Cursors are left alone.</summary>
    int PruneNotifications(TimeSpan? maxAge);
}
