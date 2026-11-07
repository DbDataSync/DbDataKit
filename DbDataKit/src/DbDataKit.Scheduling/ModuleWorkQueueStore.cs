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
        string? payloadJson = null, int priority = 0, DateTimeOffset? availableAt = null, Action<DbConnection, DbTransaction, Guid>? alsoWrite = null, int resourceLimit = 1) =>
        EnqueueCore(owner, kind, subject, part, resource, lane, payloadJson, priority, availableAt, alsoWrite, throwOnCollision: false, resourceLimit);

    public Guid EnqueueOrThrow(
        string owner, string kind, string subject, string part = NoPart, string resource = "", string lane = "",
        string? payloadJson = null, int priority = 0, DateTimeOffset? availableAt = null, Action<DbConnection, DbTransaction, Guid>? alsoWrite = null, int resourceLimit = 1) =>
        EnqueueCore(owner, kind, subject, part, resource, lane, payloadJson, priority, availableAt, alsoWrite, throwOnCollision: true, resourceLimit);

    private Guid EnqueueCore(
        string owner, string kind, string subject, string part, string resource, string lane,
        string? payloadJson, int priority, DateTimeOffset? availableAt, Action<DbConnection, DbTransaction, Guid>? alsoWrite, bool throwOnCollision, int resourceLimit) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(resourceLimit, 1);
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var runId = database.NewId();
            var now = Now();
            using (var cmd = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore(
                "{table:work_item}",
                "service, owner, kind, subject, part, resource, resource_limit, lane, run_id, status, priority, payload, enqueued_at, available_at",
                "$service, $owner, $kind, $subject, $part, $resource, $resource_limit, $lane, $run_id, 'Pending', $priority, $payload, $now, $available",
                "service, owner, kind, subject, part",
                InFlight)))
            {
                cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "kind", kind).Bind(database, "subject", subject)
                    .Bind(database, "part", part).Bind(database, "resource", resource).Bind(database, "resource_limit", (long)resourceLimit).Bind(database, "lane", lane).Bind(database, "run_id", runId)
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
        Action<DbConnection, DbTransaction, WorkItem, DateTimeOffset>? alsoWrite = null,
        IReadOnlyCollection<string>? excludeSubjects = null)
    {
        // a claimer that may not serve some subjects (the runner's label routing: it lacks a label the task requires) names them; those items stay Pending for a claimer that may
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                var laneFilter = lane is null ? "" : "AND w.lane = $lane";
                var runFilter = runId is null ? "" : "AND w.run_id = $run_id";
                var subjectFilter = excludeSubjects is not { Count: > 0 } ? "" : "AND w.subject NOT IN (" + string.Join(", ", excludeSubjects.Select((_, i) => "$skip" + i)) + ")";
                var capFilter = maxRunningInLane is null ? "" :
                    "AND (SELECT COUNT(*) FROM {table:work_item} c WHERE c.service = w.service AND c.owner = w.owner AND c.lane = w.lane AND c.status IN ('Claimed','Running')) < $cap";
                using var cmd = database.Command(Module, connection, $$"""
                    SELECT w.id, w.service, w.owner, w.kind, w.subject, w.part, w.resource, w.lane, w.run_id, w.status, w.priority, w.payload, w.resource_limit
                    FROM {table:work_item} w
                    WHERE w.service = $service AND w.owner = $owner AND w.status = 'Pending' AND w.available_at <= $now
                      {{laneFilter}}
                      {{runFilter}}
                      {{subjectFilter}}
                      AND NOT EXISTS (
                        SELECT 1 FROM {table:work_item} b
                        WHERE b.service = w.service AND b.owner = w.owner AND b.kind = w.kind AND b.subject = w.subject
                          AND b.status IN ('Claimed','Running') AND b.id <> w.id
                          AND (b.part = '' OR w.part = '' OR b.part = w.part))
                      AND (w.resource = '' OR (
                        SELECT COUNT(*) FROM {table:work_item} r
                        WHERE r.service = w.service AND r.resource = w.resource AND r.status IN ('Claimed','Running') AND r.id <> w.id) < w.resource_limit)
                      {{capFilter}}
                    ORDER BY w.priority DESC, w.enqueued_at ASC, w.id ASC {{database.Limit("take")}};
                    """);
                cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "now", Now());
                if (lane is not null) cmd.Bind(database, "lane", lane);
                if (runId is { } id) cmd.Bind(database, "run_id", id);
                if (excludeSubjects is { Count: > 0 }) { var n = 0; foreach (var subject in excludeSubjects) cmd.Bind(database, "skip" + n++, subject); }
                if (maxRunningInLane is { } cap) cmd.Bind(database, "cap", (long)cap);
                cmd.Bind(database, "take", 1L);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? (Item: Read(reader), ResourceLimit: reader.Int32(12)) : ((WorkItem Item, int ResourceLimit)?)null;
            });

            if (candidate is null)
                return null;
            var (item, resourceLimit) = candidate.Value;

            var claimed = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var cmd = database.Command(Module, connection, transaction, """
                    UPDATE {table:work_item} SET status = 'Claimed', claimed_at = $now, claimed_by = $worker, lease_expires_at = $lease
                    WHERE id = $id AND status = 'Pending'
                      AND ($resource = '' OR (
                        SELECT COUNT(*) FROM {table:work_item} r
                        WHERE r.service = $service AND r.resource = $resource AND r.status IN ('Claimed','Running') AND r.id <> $id) < $resource_limit);
                    """);
                var now = Now();
                cmd.Bind(database, "now", now).Bind(database, "worker", workerId).Bind(database, "lease", now + LeaseDuration).Bind(database, "id", item.Id)
                    .Bind(database, "service", service).Bind(database, "resource", item.Resource).Bind(database, "resource_limit", (long)resourceLimit);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }

                alsoWrite?.Invoke(connection, transaction, item with { Status = WorkItemStatus.Claimed }, now);
                transaction.Commit();
                return true;
            });

            if (claimed)
                return item with { Status = WorkItemStatus.Claimed };
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

    /// <summary>
    /// Marks an item Running, only while <paramref name="workerId"/> still holds it. False when the lease was lost (it ran out and the item went back to Pending or to another worker): the caller must not run the item.
    /// </summary>
    public bool TryMarkRunning(long id, string workerId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:work_item}} SET status = 'Running' WHERE id = $id AND claimed_by = $worker AND {Held};");
            cmd.Bind(database, "id", id).Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>
    /// Finishes an item (<see cref="WorkItemStatus.Done"/>, <see cref="WorkItemStatus.Failed"/> or <see cref="WorkItemStatus.Cancelled"/>), only while <paramref name="workerId"/> still holds its lease. False when it does not: a worker whose lease
    /// ran out and whose item another worker took cannot finish it late (plan 28a R09). <see cref="MarkDone"/> and <see cref="MarkFailed"/> do not check.
    /// </summary>
    public bool TryFinish(long id, string workerId, WorkItemStatus status)
    {
        if (status is not (WorkItemStatus.Done or WorkItemStatus.Failed or WorkItemStatus.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(status), status, "An item is finished as Done, Failed or Cancelled.");
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:work_item}} SET status = $status, lease_expires_at = NULL WHERE id = $id AND claimed_by = $worker AND {Held};");
            cmd.Bind(database, "status", status.ToString()).Bind(database, "id", id).Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Gives a claimed item back (Pending, no claim), only while <paramref name="workerId"/> holds it. False when it does not.</summary>
    public bool TryRelease(long id, string workerId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:work_item}} SET status = 'Pending', claimed_at = NULL, claimed_by = NULL, lease_expires_at = NULL WHERE id = $id AND claimed_by = $worker AND {Held};");
            cmd.Bind(database, "id", id).Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Gives back every item <paramref name="workerId"/> holds (a worker that stops). The count.</summary>
    public int ReleaseClaimsBy(string workerId) => Release("claimed_by = $key", workerId);

    /// <summary>
    /// Puts a claimed item back as Pending for another try (plan 28a DR8): the same row, so the same owner, kind, subject and part, with a <b>new run id</b> (the retry is a run of its own), not claimable before
    /// <paramref name="availableAt"/>, and with <paramref name="payloadJson"/> (the attempt number and what else the caller carries). Only while <paramref name="workerId"/> still holds the item; returns the new run id, or null when
    /// it does not.
    /// </summary>
    public Guid? Reschedule(long id, string workerId, DateTimeOffset availableAt, string? payloadJson) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var runId = database.NewId();
            using var cmd = database.Command(Module, connection, $$"""
                UPDATE {table:work_item} SET status = 'Pending', run_id = $run_id, available_at = $available, payload = $payload, claimed_at = NULL, claimed_by = NULL, lease_expires_at = NULL, cancel_requested_at = NULL
                WHERE id = $id AND claimed_by = $worker AND {{Held}};
                """);
            cmd.Bind(database, "run_id", runId).Bind(database, "available", availableAt).Bind(database, "payload", payloadJson, System.Data.DbType.String).Bind(database, "id", id).Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1 ? runId : (Guid?)null;
        });

    /// <summary>
    /// Gives a claimed item a new run id and payload without releasing it: for an item another worker's run left a record under (its lease ran out), so the new run has a record of its own. Only while
    /// <paramref name="workerId"/> holds the item; returns the new run id, or null.
    /// </summary>
    public Guid? Restart(long id, string workerId, string? payloadJson) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var runId = database.NewId();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:work_item}} SET run_id = $run_id, payload = $payload WHERE id = $id AND claimed_by = $worker AND {Held};");
            cmd.Bind(database, "run_id", runId).Bind(database, "payload", payloadJson, System.Data.DbType.String).Bind(database, "id", id).Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1 ? runId : (Guid?)null;
        });

    /// <summary>Replaces the payload of an item <paramref name="workerId"/> holds (what the holder learned about the work: it started, on which version). False when the lease was lost.</summary>
    public bool TrySetPayload(long id, string workerId, string? payloadJson) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:work_item}} SET payload = $payload WHERE id = $id AND claimed_by = $worker AND {Held};");
            cmd.Bind(database, "payload", payloadJson, System.Data.DbType.String).Bind(database, "id", id).Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>The unfinished items (Pending, Claimed, Running) of an owner, oldest first: what is waiting, when it may start, and who holds it.</summary>
    public IReadOnlyList<QueuedWork> ListInFlight(string owner) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                SELECT id, service, owner, kind, subject, part, resource, lane, run_id, status, priority, payload, available_at, claimed_by, lease_expires_at
                FROM {table:work_item} WHERE service = $service AND owner = $owner AND {{InFlight}} ORDER BY enqueued_at ASC, id ASC;
                """);
            cmd.Bind(database, "service", service).Bind(database, "owner", owner);
            using var reader = cmd.ExecuteReader();
            var list = new List<QueuedWork>();
            while (reader.Read())
                list.Add(new QueuedWork(Read(reader), reader.Time(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.NullableTime(14)));
            return (IReadOnlyList<QueuedWork>)list;
        });

    /// <summary>How many items each claimer holds (Claimed or Running), by the claimer's name: the group view's "running" count.</summary>
    public IReadOnlyDictionary<string, int> HeldCounts() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT claimed_by, COUNT(*) FROM {{table:work_item}} WHERE service = $service AND {Held} AND claimed_by IS NOT NULL GROUP BY claimed_by;");
            cmd.Bind(database, "service", service);
            using var reader = cmd.ExecuteReader();
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read()) map[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
            return (IReadOnlyDictionary<string, int>)map;
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
