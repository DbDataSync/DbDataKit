namespace DbDataKit.State;

/// <summary>The schema for the notification feed and each user's cursor into it (plan 13, U04).</summary>
public static class NotificationSchema
{
    /// <summary>The feed, and the per-user cursor.</summary>
    public const string Feed =
        """
        -- The notification feed, and the per-user cursor into it — see phase 77.
        --
        -- Global and append-only: one row per notable event, visible to everybody, in the same
        -- Id-ordered shape Logs has. That shape is the point rather than a coincidence — a client
        -- polls with WHERE Id > $sinceId and gets exactly what it has not seen, with no server-side
        -- session and nothing to reconcile if a poll is missed or repeated. Per-user rows were the
        -- alternative and were rejected in planning: broadcast semantics were what was asked for, and
        -- a fan-out row per user per event would multiply this table by the size of the org to store
        -- the same sentence N times.
        --
        -- Kind is a category, not a message. This phase writes exactly one value into it (RunFailed);
        -- phase 80 adds two more and later phases will add others, and none of them should be a
        -- migration. A UI that wants to filter or icon-code by kind reads this column; a UI that wants
        -- to render the event reads Message.
        --
        -- Message is stored, not composed at read time from the context columns. The context that
        -- explains a failure — the error summary, the position that expired — lives in rows that are
        -- themselves pruned on their own schedule, so a feed that rebuilt its sentences by joining
        -- back to TaskRuns would start saying less about older events precisely as they became harder
        -- to remember. A notification is a record of what was said, at the moment it was said.
        --
        -- TaskName/MappingName/RunId are nullable because not every kind has all three: a paused
        -- replication has no run and no mapping, and a future kind may have neither. They exist beside
        -- Message so a later notification centre can link to the thing a row is about without parsing
        -- the sentence, which is the one thing a rendered string cannot be asked to support.
        CREATE TABLE Notifications (
            {{identity:Id}},
            Kind         {{key}} NOT NULL,   -- 'RunFailed' | 'ReplicationPaused' | 'PositionExpired'
            CreatedAtUtc {{key}} NOT NULL,
            TaskName     {{text}} NULL,
            MappingName  {{text}} NULL,
            RunId        {{text}} NULL,
            Message      {{text}} NOT NULL
        );

        -- On CreatedAtUtc alone, for the age purge. Reads by Id use the primary key, and Kind is
        -- indexed as part of nothing on the same reasoning ChangeCheckHistory's group columns are: no
        -- reader filters on it yet, and an index for a hypothetical one costs every write.
        CREATE INDEX IX_Notifications_CreatedAt ON Notifications(CreatedAtUtc);

        -- One row per user, holding the highest Id that user has acknowledged. Everything above it is
        -- unread; the count is a comparison, not a per-notification flag table.
        --
        -- **No row is written for a deployment with authentication disabled.** There is no user id to
        -- key one to, and inventing a shared sentinel would mean one person's dismissal silencing the
        -- badge for everybody at a terminal where nobody can be told apart. That mode has a defined
        -- answer instead — everything always reads as unread — stated by the API rather than left to
        -- a null key landing somewhere by accident. See NotificationStore.
        CREATE TABLE NotificationReadState (
            UserId                 {{key}} NOT NULL,
            LastSeenNotificationId {{int}} NOT NULL,
            UpdatedAtUtc           {{text}} NOT NULL,
            PRIMARY KEY (UserId)
        );
        """;
}
