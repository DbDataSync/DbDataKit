using System.Data.Common;
using DbDataKit.State;

namespace DbDataKit.Scheduling;

/// <summary>
/// The durable work queue of one service on the <c>scheduling</c> module (plan 29a Q11): the same behaviour and the same public methods as <see cref="WorkQueueStore"/>, which it replaces
/// when each product moves (Q17, Q22). See that class for what may run together and how leases work; both are unchanged. The clock is the database's (<see cref="StateDatabase.Time"/>),
/// and an item's run id is a UUID.
/// </summary>
/// <param name="database">The state database; its modules include <see cref="SchedulingModule.Module"/>.</param>
/// <param name="service">The service whose queue this is. Every statement is scoped to it.</param>
public sealed class ModuleWorkQueueStore(StateDatabase database, string service)
{
    private const string Module = SchedulingModule.Name;
    private const string InFlight = "status IN ('Pending','Claimed','Running')";
    private const string Held = "status IN ('Claimed','Running')";

    public const string NoPart = WorkQueueStore.NoPart;

    public static readonly TimeSpan DefaultLease = WorkQueueStore.DefaultLease;

    public TimeSpan LeaseDuration { get; init; } = DefaultLease;

    private DateTimeOffset Now() => database.Time.GetUtcNow();

    public Guid Enqueue(
        string owner, string kind, string subject, string part = NoPart, string resource = "", string lane = "",
        string? payloadJson = null, int priority = 0, DateTimeOffset? availableAt = null, Action<DbConnection, DbTransaction, Guid>? alsoWrite = null) =>
        EnqueueCore(owner, kind, subject, part, resource, lane, payloadJson, priority, availableAt, alsoWrite, throwOnCollision: false);

    public Guid EnqueueOrThrow(
        string owner, string kind, string subject, string part = NoPart, string resource = "", string lane = "",
        string? payloadJson = null, int priority = 0, DateTimeOffset? availableAt = null, Action<DbConnection, DbTransaction, Guid>? alsoWrite = null) =>
        EnqueueCore(owner, kind, subject, part, resource, lane, payloadJson, priority, availableAt, alsoWrite, throwOnCollision: true);

    private Guid EnqueueCore(
        string owner, string kind, string subject, string part, string resource, string lane,
        string? payloadJson, int priority, DateTimeOffset? availableAt, Action<DbConnection, DbTransaction, Guid>? alsoWrite, bool throwOnCollision) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var runId = database.NewId();
            var now = Now();
            using (var cmd = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore(
                "{table:work_item}",
                "service, owner, kind, subject, part, resource, lane, run_id, status, priority, payload, enqueued_at, available_at",
                "$service, $owner, $kind, $subject, $part, $resource, $lane, $run_id, 'Pending', $priority, $payload, $now, $available",
                "service, owner, kind, subject, part",
                InFlight)))
            {
                cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "kind", kind).Bind(database, "subject", subject)
                    .Bind(database, "part", part).Bind(database, "resource", resource).Bind(database, "lane", lane).Bind(database, "run_id", runId)
                    .Bind(database, "priority", (long)priority).Bind(database, "payload", payloadJson, System.Data.DbType.String)
                    .Bind(database, "now", now).Bind(database, "available", availableAt ?? now);
                if (cmd.ExecuteNonQuery() == 1)
                {
                    alsoWrite?.Invoke(connection, transaction, runId);
                    transaction.Commit();
                    return runId;
                }
            }

            transaction.Rollback();
            if (throwOnCollision)
                throw new WorkQueueCollisionException(owner, kind, subject, part);

            using var existing = database.Command(Module, connection, $$"""
                SELECT run_id FROM {table:work_item}
                WHERE service = $service AND owner = $owner AND kind = $kind AND subject = $subject AND part = $part AND {{InFlight}};
                """);
            existing.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "kind", kind).Bind(database, "subject", subject).Bind(database, "part", part);
            return StateValues.ToGuid(existing.ExecuteScalar()!);
        });

    public WorkItem? TryClaimNext(
        string owner, string workerId, string? lane = null, int? maxRunningInLane = null, Guid? runId = null,
        Action<DbConnection, DbTransaction, WorkItem, DateTimeOffset>? alsoWrite = null)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                var laneFilter = lane is null ? "" : "AND w.lane = $lane";
                var runFilter = runId is null ? "" : "AND w.run_id = $run_id";
                var capFilter = maxRunningInLane is null ? "" :
                    "AND (SELECT COUNT(*) FROM {table:work_item} c WHERE c.service = w.service AND c.owner = w.owner AND c.lane = w.lane AND c.status IN ('Claimed','Running')) < $cap";
                using var cmd = database.Command(Module, connection, $$"""
                    SELECT w.id, w.service, w.owner, w.kind, w.subject, w.part, w.resource, w.lane, w.run_id, w.status, w.priority, w.payload
                    FROM {table:work_item} w
                    WHERE w.service = $service AND w.owner = $owner AND w.status = 'Pending' AND w.available_at <= $now
                      {{laneFilter}}
                      {{runFilter}}
                      AND NOT EXISTS (
                        SELECT 1 FROM {table:work_item} b
                        WHERE b.service = w.service AND b.owner = w.owner AND b.kind = w.kind AND b.subject = w.subject
                          AND b.status IN ('Claimed','Running') AND b.id <> w.id
                          AND (b.part = '' OR w.part = '' OR b.part = w.part))
                      AND (w.resource = '' OR NOT EXISTS (
                        SELECT 1 FROM {table:work_item} r
                        WHERE r.service = w.service AND r.resource = w.resource AND r.status IN ('Claimed','Running') AND r.id <> w.id))
                      {{capFilter}}
                    ORDER BY w.priority DESC, w.enqueued_at ASC, w.id ASC {{database.Limit("take")}};
                    """);
                cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "now", Now());
                if (lane is not null) cmd.Bind(database, "lane", lane);
                if (runId is { } id) cmd.Bind(database, "run_id", id);
                if (maxRunningInLane is { } cap) cmd.Bind(database, "cap", (long)cap);
                cmd.Bind(database, "take", 1L);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? Read(reader) : null;
            });

            if (candidate is null)
                return null;

            var claimed = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var cmd = database.Command(Module, connection, transaction, """
                    UPDATE {table:work_item} SET status = 'Claimed', claimed_at = $now, claimed_by = $worker, lease_expires_at = $lease
                    WHERE id = $id AND status = 'Pending';
                    """);
                var now = Now();
                cmd.Bind(database, "now", now).Bind(database, "worker", workerId).Bind(database, "lease", now + LeaseDuration).Bind(database, "id", candidate.Id);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }

                alsoWrite?.Invoke(connection, transaction, candidate with { Status = WorkItemStatus.Claimed }, now);
                transaction.Commit();
                return true;
            });

            if (claimed)
                return candidate with { Status = WorkItemStatus.Claimed };
            // another claimer got it first; pick again
        }

        return null;
    }

    public bool Heartbeat(long id, string workerId, TimeSpan? lease = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:work_item}} SET lease_expires_at = $lease WHERE id = $id AND claimed_by = $worker AND {Held};");
            cmd.Bind(database, "lease", Now() + (lease ?? LeaseDuration)).Bind(database, "id", id).Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1;
        });

    public int ReleaseExpired() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                UPDATE {table:work_item} SET status = 'Pending', claimed_at = NULL, claimed_by = NULL, lease_expires_at = NULL
                WHERE service = $service AND {{Held}} AND lease_expires_at < $now;
                """);
            cmd.Bind(database, "service", service).Bind(database, "now", Now());
            return cmd.ExecuteNonQuery();
        });

    public void MarkRunning(long id) => SetStatus(id, WorkItemStatus.Running);

    public void MarkDone(long id) => SetStatus(id, WorkItemStatus.Done);

    public void MarkFailed(long id) => SetStatus(id, WorkItemStatus.Failed);

    public void ReleaseClaim(long id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:work_item}} SET status = 'Pending', claimed_at = NULL, claimed_by = NULL, lease_expires_at = NULL WHERE id = $id AND {Held};");
            cmd.Bind(database, "id", id);
            cmd.ExecuteNonQuery();
        });

    public int ReleaseClaimsForOwner(string owner) => Release("owner = $key", owner);

    public int ReleaseClaimsForRun(Guid runId) => Release("run_id = $key", runId);

    private int Release(string where, object key) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                UPDATE {table:work_item} SET status = 'Pending', claimed_at = NULL, claimed_by = NULL, lease_expires_at = NULL
                WHERE service = $service AND {{where}} AND {{Held}};
                """);
            cmd.Bind(database, "service", service).Bind(database, "key", key);
            return cmd.ExecuteNonQuery();
        });

    public bool TryCancelPending(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:work_item} SET status = 'Cancelled' WHERE service = $service AND run_id = $run_id AND status = 'Pending';");
            cmd.Bind(database, "service", service).Bind(database, "run_id", runId);
            return cmd.ExecuteNonQuery() == 1;
        });

    public bool RequestCancel(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                UPDATE {table:work_item} SET cancel_requested_at = $now
                WHERE service = $service AND run_id = $run_id AND {{Held}} AND cancel_requested_at IS NULL;
                """);
            cmd.Bind(database, "service", service).Bind(database, "run_id", runId).Bind(database, "now", Now());
            cmd.ExecuteNonQuery();

            using var check = database.Command(Module, connection, $"SELECT COUNT(*) FROM {{table:work_item}} WHERE service = $service AND run_id = $run_id AND {Held} AND cancel_requested_at IS NOT NULL;");
            check.Bind(database, "service", service).Bind(database, "run_id", runId);
            return Convert.ToInt64(check.ExecuteScalar()) > 0;
        });

    public bool IsCancelRequested(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:work_item} WHERE service = $service AND run_id = $run_id AND cancel_requested_at IS NOT NULL;");
            cmd.Bind(database, "service", service).Bind(database, "run_id", runId);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    public bool HasOutstandingWork(string owner, string? lane = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                SELECT COUNT(*) FROM {table:work_item}
                WHERE service = $service AND owner = $owner AND {{InFlight}} {{(lane is null ? "" : "AND lane = $lane")}};
                """);
            cmd.Bind(database, "service", service).Bind(database, "owner", owner);
            if (lane is not null) cmd.Bind(database, "lane", lane);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    public bool HasInFlight(string owner, string kind, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                SELECT COUNT(*) FROM {table:work_item}
                WHERE service = $service AND owner = $owner AND kind = $kind AND subject = $subject AND {{InFlight}};
                """);
            cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "kind", kind).Bind(database, "subject", subject);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    public IReadOnlyList<string> OwnersWithHeldWork() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT DISTINCT owner FROM {{table:work_item}} WHERE service = $service AND {Held};");
            cmd.Bind(database, "service", service);
            using var reader = cmd.ExecuteReader();
            var owners = new List<string>();
            while (reader.Read()) owners.Add(reader.GetString(0));
            return (IReadOnlyList<string>)owners;
        });

    private void SetStatus(long id, WorkItemStatus status) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:work_item} SET status = $status WHERE id = $id;");
            cmd.Bind(database, "status", status.ToString()).Bind(database, "id", id);
            cmd.ExecuteNonQuery();
        });

    private static WorkItem Read(DbDataReader reader) => new(
        reader.Int64(0),
        reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.Uuid(8),
        Enum.Parse<WorkItemStatus>(reader.GetString(9)),
        reader.Int32(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));
}
