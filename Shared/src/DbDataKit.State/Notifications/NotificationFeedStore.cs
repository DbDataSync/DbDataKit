using System.Data.Common;
using DbDataKit.Monitoring;

namespace DbDataKit.State;

/// <summary>
/// The <c>notifications</c> module (plan 29 section 3.4): the feed every service writes and each reader's position in it. One table per install, with the service that wrote each
/// row, so services that share a database share one feed: <see cref="NotificationFeedStore"/> reads one service's rows, or every service's for the console.
/// </summary>
public static class NotificationsModule
{
    public const string Name = "notifications";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- The feed: global within the install, append-only, ordered by id. A reader polls with the highest id it holds and is given everything above it, so a missed poll,
            -- a repeated one and a second browser tab cost nothing and no per-reader state is kept but one number (reader_position).
            -- kind is a category (RunFailed, CertificateExpiring, ...), not a sentence; message is the sentence as it was said when it was said, kept because the rows that
            -- explain it are pruned on their own schedule. service_id says which service wrote the row, for databases that several services share.
            CREATE TABLE {table:notification} (
                {{identity:id}},
                service_id   {{key}} NOT NULL,
                kind         {{key}} NOT NULL,
                created_at   {{time}} NOT NULL,
                task_name    {{text}} NULL,
                mapping_name {{text}} NULL,
                run_id       {{uuid}} NULL,
                message      {{text}} NOT NULL
            );

            CREATE INDEX {index:notification_by_created} ON {table:notification}(created_at);
            CREATE INDEX {index:notification_by_service} ON {table:notification}(service_id, id);

            -- One row per reader (a signed-in user, or a console): the highest notification id it has acknowledged. Everything above is unread. Current state: no age limit.
            CREATE TABLE {table:reader_position} (
                reader_id     {{key}} PRIMARY KEY,
                last_seen_id  {{int}} NOT NULL,
                updated_at    {{time}} NOT NULL
            );
            """,
        ],
        retention: [new RetentionRule("notification", "created_at")]);
}

/// <summary>
/// The notification feed on the <c>notifications</c> module, behind the same contract as the older store (<see cref="INotificationFeed"/>). Built for one service
/// (<paramref name="serviceId"/>): its <c>Raise</c> writes under that id and its <c>List</c> and <c>UnreadCount</c> read that service's rows, unless <paramref name="allServices"/>
/// (the console reading a shared database). Producers that must be atomic with the event they announce write through <see cref="Insert"/> in their own transaction.
/// </summary>
public sealed class NotificationFeedStore(StateDatabase database, string serviceId, bool allServices = false) : INotificationFeed
{
    private const string Module = NotificationsModule.Name;

    /// <summary>Writes a notification on a connection the caller owns, so it joins the transaction that records the thing being announced.</summary>
    public static void Insert(
        StateDatabase database, DbConnection connection, DbTransaction? transaction, string serviceId,
        string kind, string message, string? taskName, string? mappingName, Guid? runId)
    {
        using var cmd = database.Command(Module, connection, transaction,
            "INSERT INTO {table:notification} (service_id, kind, created_at, task_name, mapping_name, run_id, message) VALUES ($service, $kind, $now, $task, $mapping, $run, $message);");
        cmd.Bind(database, "service", serviceId)
            .Bind(database, "kind", kind)
            .Bind(database, "now", database.Time.GetUtcNow())
            .Bind(database, "task", taskName, System.Data.DbType.String)
            .Bind(database, "mapping", mappingName, System.Data.DbType.String)
            .Bind(database, "run", runId, System.Data.DbType.Guid)
            .Bind(database, "message", message);
        cmd.ExecuteNonQuery();
    }

    public void Raise(string kind, string message, string? taskName = null, string? mappingName = null, Guid? runId = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            Insert(database, connection, null, serviceId, kind, message, taskName, mappingName, runId);
        });

    private string Scope(string prefix) => allServices ? "" : $"{prefix} service_id = $service";

    public IReadOnlyList<NotificationRecord> List(long? sinceId = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var where = string.Join(" AND ", new[] { sinceId is null ? "" : "id > $since", Scope("") }.Where(c => c.Length > 0));
            using var cmd = database.Command(Module, connection,
                $"SELECT id, kind, created_at, task_name, mapping_name, run_id, message FROM {{table:notification}} {(where.Length == 0 ? "" : "WHERE " + where)} ORDER BY id;");
            if (sinceId is not null) cmd.Bind(database, "since", sinceId.Value);
            if (!allServices) cmd.Bind(database, "service", serviceId);

            using var reader = cmd.ExecuteReader();
            var results = new List<NotificationRecord>();
            while (reader.Read())
                results.Add(new NotificationRecord(
                    reader.Int64(0), reader.GetString(1), reader.Time(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.NullableUuid(5), reader.GetString(6)));
            return (IReadOnlyList<NotificationRecord>)results;
        });

    public int UnreadCount(long? lastSeenId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var where = string.Join(" AND ", new[] { lastSeenId is null ? "" : "id > $seen", Scope("") }.Where(c => c.Length > 0));
            using var cmd = database.Command(Module, connection, $"SELECT COUNT(*) FROM {{table:notification}} {(where.Length == 0 ? "" : "WHERE " + where)};");
            if (lastSeenId is not null) cmd.Bind(database, "seen", lastSeenId.Value);
            if (!allServices) cmd.Bind(database, "service", serviceId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    public long? GetCursor(string readerId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT last_seen_id FROM {table:reader_position} WHERE reader_id = $reader;");
            cmd.Bind(database, "reader", readerId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.NullableInt64(0) : null;
        });

    /// <summary>Moves a reader's position forward, never back: two tabs report different high-water marks and the older arriving second must not bring cleared notifications back.</summary>
    public void MarkSeen(string readerId, long lastSeenNotificationId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            // one statement, so two tabs cannot both read the old value: the position only ever takes the larger of the stored and the incoming
            var sql = database.Dialect.Upsert(
                "{table:reader_position}", "reader_id, last_seen_id, updated_at", "$reader, $seen, $now", "reader_id",
                "last_seen_id = CASE WHEN EXCLUDED.last_seen_id > EXISTING.last_seen_id THEN EXCLUDED.last_seen_id ELSE EXISTING.last_seen_id END, updated_at = EXCLUDED.updated_at");
            using var cmd = database.Command(Module, connection, sql);
            cmd.Bind(database, "reader", readerId).Bind(database, "seen", lastSeenNotificationId).Bind(database, "now", database.Time.GetUtcNow());
            cmd.ExecuteNonQuery();
        });

    /// <summary>Deletes this service's notifications older than <paramref name="maxAge"/>; the shared <see cref="RetentionJob"/> does the same for everything on its own schedule.</summary>
    public int PruneNotifications(TimeSpan? maxAge) =>
        database.Retry(() =>
        {
            if (maxAge is null) return 0;
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:notification} WHERE service_id = $service AND created_at < $cutoff;");
            cmd.Bind(database, "service", serviceId).Bind(database, "cutoff", database.Time.GetUtcNow() - maxAge.Value);
            return cmd.ExecuteNonQuery();
        });
}
