using System.Data.Common;

using DbDataKit.Monitoring;

namespace DbDataKit.State;

/// <summary>
/// The global notification feed and the per-user cursors into it — see phase 77.
/// <para>
/// **One append-only table everyone reads, in <c>Logs</c>' shape.** A caller polls with the highest
/// <c>Id</c> it holds and is handed everything above it; nothing here is per-consumer except the
/// cursor, and the cursor is one number. That is what makes a missed poll, a repeated poll and a
/// second browser tab all cost nothing.
/// </para>
/// <para>
/// **The producers are not here.** A notification about a failed run is written inside the same
/// transaction that records the failure, by the store that owns that table — see
/// <c>TaskRunStore.CompleteRun</c> and <c>TaskRunStore.SetPaused</c> — because a
/// notification that could land without the event it describes, or the event without it, is worse
/// than either alone. <see cref="Insert"/> is the shared statement those call sites write through.
/// </para>
/// </summary>
public sealed class NotificationStore(StateDatabase database) : INotificationFeed
{
    /// <summary>
    /// Writes one notification on a connection somebody else owns, so it can join a transaction that
    /// is already recording the thing being announced.
    /// <para>
    /// Internal, and deliberately the only way a row gets in: every producer this project has is a
    /// store already writing the underlying event, and a public "notify about anything" entry point
    /// would invite a second write from a caller with nothing to be atomic with.
    /// </para>
    /// </summary>
    public static void Insert(
        StateDatabase database, DbConnection connection, DbTransaction? transaction,
        string kind, string message, string? taskName, string? mappingName, Guid? runId)
    {
        using var cmd = database.Command(connection, transaction, """
            INSERT INTO Notifications (Kind, CreatedAtUtc, TaskName, MappingName, RunId, Message)
            VALUES ($kind, $now, $taskName, $mappingName, $runId, $message);
            """);
        cmd.Bind(database, "kind", kind);
        cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Bind(database, "taskName", (object?)taskName ?? DBNull.Value);
        cmd.Bind(database, "mappingName", (object?)mappingName ?? DBNull.Value);
        cmd.Bind(database, "runId", (object?)runId?.ToString() ?? DBNull.Value);
        cmd.Bind(database, "message", message);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Raises a notification with nothing to be atomic with — the public counterpart to
    /// <see cref="Insert"/>, for the one shape of producer that is not itself writing another table in
    /// the same transaction. <see cref="Insert"/>'s own doc comment explains why every other producer
    /// goes through it as a joint write instead ("a public 'notify about anything' entry point would
    /// invite a second write from a caller with nothing to be atomic with"); phase 82's daily
    /// certificate-expiry check (<c>DbDataSync.Api.Services.CertificateExpiryService</c>) is the first
    /// caller that is genuinely standalone — it is not itself writing a row anywhere else this event
    /// could be paired with, so its own connection is the right shape rather than a second "insert on
    /// someone else's transaction" caller with no transaction to give it.
    /// </summary>
    public void Raise(string kind, string message, string? taskName = null, string? mappingName = null, Guid? runId = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            Insert(database, connection, transaction: null, kind, message, taskName, mappingName, runId);
        });

    /// <summary>
    /// Everything newer than <paramref name="sinceId"/>, oldest first. Null is the initial load —
    /// the whole table, which retention already bounds.
    /// </summary>
    public IReadOnlyList<NotificationRecord> List(long? sinceId = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, sinceId is null
                ? "SELECT Id, Kind, CreatedAtUtc, TaskName, MappingName, RunId, Message FROM Notifications ORDER BY Id;"
                : "SELECT Id, Kind, CreatedAtUtc, TaskName, MappingName, RunId, Message FROM Notifications WHERE Id > $sinceId ORDER BY Id;");
            if (sinceId is not null)
                cmd.Bind(database, "sinceId", sinceId.Value);

            using var reader = cmd.ExecuteReader();
            var results = new List<NotificationRecord>();
            while (reader.Read())
                results.Add(new NotificationRecord(
                    reader.Int64(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                    reader.GetString(6)));

            return (IReadOnlyList<NotificationRecord>)results;
        });

    /// <summary>How many rows sit above a cursor. Null counts the whole table, which is what an
    /// unauthenticated deployment and a user who has never acknowledged anything both mean.</summary>
    public int UnreadCount(long? lastSeenId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, lastSeenId is null
                ? "SELECT COUNT(*) FROM Notifications;"
                : "SELECT COUNT(*) FROM Notifications WHERE Id > $lastSeenId;");
            if (lastSeenId is not null)
                cmd.Bind(database, "lastSeenId", lastSeenId.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    /// <summary>Where this user's cursor is. Null for a user who has never marked anything seen —
    /// which is not the same as a cursor at 0, since 0 would also be the answer for a user who
    /// acknowledged an empty feed.</summary>
    public long? GetCursor(string readerId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "SELECT LastSeenNotificationId FROM NotificationReadState WHERE UserId = $userId;");
            cmd.Bind(database, "userId", readerId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.NullableInt64(0) : null;
        });

    /// <summary>
    /// Advances this user's cursor, and only ever forwards.
    /// <para>
    /// Two tabs polling the same feed will report different high-water marks, and the older one
    /// arriving second must not resurrect notifications the newer one already cleared. Monotonic here
    /// rather than in the caller, because every caller would have to get it right and the store is
    /// where the invariant can actually be enforced.
    /// </para>
    /// </summary>
    public void MarkSeen(string readerId, long lastSeenNotificationId)
    {
        // Read before the write connection is opened, not inside it: nesting a second connection
        // inside an open one is how a SQLite writer deadlocks against its own reader.
        var highWaterMark = Math.Max(lastSeenNotificationId, GetCursor(readerId) ?? 0);

        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "NotificationReadState",
                "UserId, LastSeenNotificationId, UpdatedAtUtc",
                "$userId, $lastSeen, $now",
                "UserId",
                "LastSeenNotificationId = EXCLUDED.LastSeenNotificationId, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "userId", readerId);
            cmd.Bind(database, "lastSeen", highWaterMark);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Deletes notifications older than <paramref name="maxAge"/>. Null keeps everything, matching how
    /// every other retention cap in this project reads a configured 0.
    /// <para>
    /// Cursors are left alone. A cursor is one small row per user that names an Id which may no longer
    /// exist, and an Id above every surviving row is exactly right — it means "nothing unread", which
    /// is what somebody who has read everything should still see after the rows they read are gone.
    /// </para>
    /// </summary>
    public int PruneNotifications(TimeSpan? maxAge) =>
        database.Retry(() =>
        {
            if (maxAge is null)
                return 0;

            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "DELETE FROM Notifications WHERE CreatedAtUtc < $cutoff;");
            cmd.Bind(database, "cutoff", DateTimeOffset.UtcNow.Subtract(maxAge.Value).ToString("O"));
            return cmd.ExecuteNonQuery();
        });
}
