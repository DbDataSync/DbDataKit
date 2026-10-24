using System.Data.Common;
using DbDataKit.State;

namespace DbDataKit.Scheduling;

/// <summary>
/// The durable work queue of one service (plan 13, M02), shared by the processes that hand out work and the workers that do it: nothing else needs to reach into another
/// process, because the table is the mechanism. Built from the queue Sync has always had, with the product-specific columns replaced by a subject, a part and a resource.
/// <para>
/// **What may run together.** One item per (owner, kind, subject, part) is in flight at a time: enqueueing the same thing again finds the first (see
/// <see cref="EnqueueOrThrow"/> for the callers that must not). A claim is refused while running work conflicts with it:
/// <list type="bullet">
/// <item>the same kind of work on the same subject, when either item has no part (a whole-subject item and a part of it exclude each other; two different parts do not);</item>
/// <item>work with the same non-empty resource, anywhere in the service (two subjects that write one target take turns);</item>
/// <item>a full lane: at most <c>maxRunningInLane</c> items of one lane are claimed or running for one owner.</item>
/// </list>
/// </para>
/// <para>
/// **Leases.** A claim lasts <see cref="LeaseDuration"/> from the moment it is made or last renewed with <see cref="Heartbeat"/>. A worker that stops renewing loses the item to
/// <see cref="ReleaseExpired"/>, so work held by a worker that died does not block its subject forever.
/// </para>
/// <para>
/// The run record (run history, timings) is the caller's: this table says what is waiting and what is running, and nothing about how it went.
/// </para>
/// </summary>
/// <param name="database">The state database; its schema includes <see cref="WorkQueueSchema.Queue"/>.</param>
/// <param name="service">The service whose queue this is (<c>dbdatasync</c>, <c>dbdatabuild</c>). Every statement is scoped to it.</param>
/// <param name="time">The clock, for leases and availability; the system clock when omitted.</param>
public sealed class WorkQueueStore(StateDatabase database, string service, TimeProvider? time = null)
{
    private const string InFlight = "Status IN ('Pending','Claimed','Running')";
    private const string Held = "Status IN ('Claimed','Running')";

    /// <summary>The part of an item that has none; not null, because the column is in a unique index.</summary>
    public const string NoPart = "";

    /// <summary>How long a claim lasts without a heartbeat when the claimer does not say.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The lease a claim takes unless it is given another.</summary>
    public TimeSpan LeaseDuration { get; init; } = DefaultLease;

    private string Now() => _time.GetUtcNow().ToString("O");

    /// <summary>
    /// Queues one unit of work. If the same owner, kind, subject and part is already pending, claimed or running, nothing is added and the existing item's run id is returned:
    /// two identical requests are one piece of work.
    /// </summary>
    /// <param name="owner">Who the work belongs to.</param>
    /// <param name="kind">What sort of work.</param>
    /// <param name="subject">The thing worked on.</param>
    /// <param name="part">A slice of it, or <see cref="NoPart"/>.</param>
    /// <param name="resource">What it must not share with other running work (a target table), or empty.</param>
    /// <param name="lane">The pool that runs it, or empty.</param>
    /// <param name="payloadJson">What the product needs to run it, opaque here.</param>
    /// <param name="priority">Higher is claimed first.</param>
    /// <param name="availableAt">Not claimable before this time; now when omitted.</param>
    public Guid Enqueue(
        string owner, string kind, string subject, string part = NoPart, string resource = "", string lane = "",
        string? payloadJson = null, int priority = 0, DateTimeOffset? availableAt = null) =>
        EnqueueCore(owner, kind, subject, part, resource, lane, payloadJson, priority, availableAt, throwOnCollision: false);

    /// <summary>Like <see cref="Enqueue"/>, except that finding the same work in flight throws <see cref="WorkQueueCollisionException"/>: for a request that is not the same thing as whatever is already running.</summary>
    public Guid EnqueueOrThrow(
        string owner, string kind, string subject, string part = NoPart, string resource = "", string lane = "",
        string? payloadJson = null, int priority = 0, DateTimeOffset? availableAt = null) =>
        EnqueueCore(owner, kind, subject, part, resource, lane, payloadJson, priority, availableAt, throwOnCollision: true);

    private Guid EnqueueCore(
        string owner, string kind, string subject, string part, string resource, string lane,
        string? payloadJson, int priority, DateTimeOffset? availableAt, bool throwOnCollision) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var runId = Guid.NewGuid();
            var now = Now();
            using (var cmd = database.Command(connection, database.Dialect.InsertOrIgnore(
                "WorkQueue",
                "Service, Owner, Kind, Subject, Part, Resource, Lane, RunId, Status, Priority, PayloadJson, EnqueuedAtUtc, AvailableAtUtc",
                "$service, $owner, $kind, $subject, $part, $resource, $lane, $runId, 'Pending', $priority, $payload, $now, $available",
                "Service, Owner, Kind, Subject, Part",
                InFlight)))
            {
                cmd.Bind(database, "service", service);
                cmd.Bind(database, "owner", owner);
                cmd.Bind(database, "kind", kind);
                cmd.Bind(database, "subject", subject);
                cmd.Bind(database, "part", part);
                cmd.Bind(database, "resource", resource);
                cmd.Bind(database, "lane", lane);
                cmd.Bind(database, "runId", runId.ToString());
                cmd.Bind(database, "priority", priority);
                cmd.Bind(database, "payload", payloadJson);
                cmd.Bind(database, "now", now);
                cmd.Bind(database, "available", (availableAt ?? _time.GetUtcNow()).ToString("O"));
                if (cmd.ExecuteNonQuery() == 1)
                    return runId;
            }

            if (throwOnCollision)
                throw new WorkQueueCollisionException(owner, kind, subject, part);

            using var existing = database.Command(connection, $"""
                SELECT RunId FROM WorkQueue
                WHERE Service = $service AND Owner = $owner AND Kind = $kind AND Subject = $subject AND Part = $part AND {InFlight};
                """);
            existing.Bind(database, "service", service);
            existing.Bind(database, "owner", owner);
            existing.Bind(database, "kind", kind);
            existing.Bind(database, "subject", subject);
            existing.Bind(database, "part", part);
            return Guid.Parse((string)existing.ExecuteScalar()!);
        });

    /// <summary>
    /// Claims the next item of an owner that may run now, or null. Two steps (pick, then a conditional update), so a lost race shows as zero rows updated and the pick is tried again,
    /// and no lock is held while a worker works.
    /// </summary>
    /// <param name="owner">Whose work to claim.</param>
    /// <param name="workerId">Who is claiming; recorded, and required to renew the lease.</param>
    /// <param name="lane">Only claim from this lane, or from any when null.</param>
    /// <param name="maxRunningInLane">The most items of one lane that may be claimed or running for the owner at once; no limit when null.</param>
    /// <param name="runId">Test-only: claim this run rather than whatever is next.</param>
    public WorkItem? TryClaimNext(string owner, string workerId, string? lane = null, int? maxRunningInLane = null, Guid? runId = null)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(connection, $"""
                    SELECT Id, Service, Owner, Kind, Subject, Part, Resource, Lane, RunId, Status, Priority, PayloadJson
                    FROM WorkQueue w
                    WHERE w.Service = $service AND w.Owner = $owner AND w.Status = 'Pending' AND w.AvailableAtUtc <= $now
                      {(lane is null ? "" : "AND w.Lane = $lane")}
                      {(runId is null ? "" : "AND w.RunId = $runId")}
                      AND NOT EXISTS (
                        SELECT 1 FROM WorkQueue b
                        WHERE b.Service = w.Service AND b.Owner = w.Owner AND b.Kind = w.Kind AND b.Subject = w.Subject
                          AND b.Status IN ('Claimed','Running') AND b.Id <> w.Id
                          AND (b.Part = '' OR w.Part = '' OR b.Part = w.Part))
                      AND (w.Resource = '' OR NOT EXISTS (
                        SELECT 1 FROM WorkQueue r
                        WHERE r.Service = w.Service AND r.Resource = w.Resource AND r.Status IN ('Claimed','Running') AND r.Id <> w.Id))
                      {(maxRunningInLane is null ? "" : """
                      AND (SELECT COUNT(*) FROM WorkQueue c
                           WHERE c.Service = w.Service AND c.Owner = w.Owner AND c.Lane = w.Lane AND c.Status IN ('Claimed','Running')) < $cap
                      """)}
                    ORDER BY w.Priority DESC, w.EnqueuedAtUtc ASC, w.Id ASC {database.Limit("take")};
                    """);
                cmd.Bind(database, "service", service);
                cmd.Bind(database, "owner", owner);
                cmd.Bind(database, "now", Now());
                if (lane is not null) cmd.Bind(database, "lane", lane);
                if (runId is { } id) cmd.Bind(database, "runId", id.ToString());
                if (maxRunningInLane is { } cap) cmd.Bind(database, "cap", cap);
                cmd.Bind(database, "take", 1);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? Read(reader) : null;
            });

            if (candidate is null)
                return null;

            var claimed = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(connection, """
                    UPDATE WorkQueue SET Status = 'Claimed', ClaimedAtUtc = $now, ClaimedBy = $worker, LeaseExpiresUtc = $lease
                    WHERE Id = $id AND Status = 'Pending';
                    """);
                var now = _time.GetUtcNow();
                cmd.Bind(database, "now", now.ToString("O"));
                cmd.Bind(database, "worker", workerId);
                cmd.Bind(database, "lease", (now + LeaseDuration).ToString("O"));
                cmd.Bind(database, "id", candidate.Id);
                return cmd.ExecuteNonQuery() == 1;
            });

            if (claimed)
                return candidate with { Status = WorkItemStatus.Claimed };
            // another claimer got it first; pick again
        }

        return null;
    }

    /// <summary>Renews the lease of an item this worker holds. False when the worker no longer holds it (the lease ran out and the item went back to the queue, or it finished).</summary>
    public bool Heartbeat(long id, string workerId, TimeSpan? lease = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"UPDATE WorkQueue SET LeaseExpiresUtc = $lease WHERE Id = $id AND ClaimedBy = $worker AND {Held};");
            cmd.Bind(database, "lease", (_time.GetUtcNow() + (lease ?? LeaseDuration)).ToString("O"));
            cmd.Bind(database, "id", id);
            cmd.Bind(database, "worker", workerId);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Returns every claimed or running item whose lease has run out to the queue, so another worker can take it. The count released.</summary>
    public int ReleaseExpired() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                UPDATE WorkQueue SET Status = 'Pending', ClaimedAtUtc = NULL, ClaimedBy = NULL, LeaseExpiresUtc = NULL
                WHERE Service = $service AND {Held} AND LeaseExpiresUtc < $now;
                """);
            cmd.Bind(database, "service", service);
            cmd.Bind(database, "now", Now());
            return cmd.ExecuteNonQuery();
        });

    /// <summary>The claimer has begun the work.</summary>
    public void MarkRunning(long id) => SetStatus(id, WorkItemStatus.Running);

    /// <summary>The work finished.</summary>
    public void MarkDone(long id) => SetStatus(id, WorkItemStatus.Done);

    /// <summary>The work failed.</summary>
    public void MarkFailed(long id) => SetStatus(id, WorkItemStatus.Failed);

    /// <summary>Gives a claim up without doing the work (back to pending): the claimer found it could not run it after all.</summary>
    public void ReleaseClaim(long id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"UPDATE WorkQueue SET Status = 'Pending', ClaimedAtUtc = NULL, ClaimedBy = NULL, LeaseExpiresUtc = NULL WHERE Id = $id AND {Held};");
            cmd.Bind(database, "id", id);
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Returns every claimed or running item of an owner to the queue, for when the worker holding them died without saying so. Pending rather than failed: nobody saw the work fail.
    /// Only correct once the caller knows the worker is gone.
    /// </summary>
    public int ReleaseClaimsForOwner(string owner) =>
        Release("Owner = $key", owner);

    /// <summary>Returns the claimed or running item of one run to the queue.</summary>
    public int ReleaseClaimsForRun(Guid runId) =>
        Release("RunId = $key", runId.ToString());

    private int Release(string where, string key) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                UPDATE WorkQueue SET Status = 'Pending', ClaimedAtUtc = NULL, ClaimedBy = NULL, LeaseExpiresUtc = NULL
                WHERE Service = $service AND {where} AND {Held};
                """);
            cmd.Bind(database, "service", service);
            cmd.Bind(database, "key", key);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>Cancels an item nobody has claimed. False when it is already claimed (ask with <see cref="RequestCancel"/>), finished or unknown.</summary>
    public bool TryCancelPending(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE WorkQueue SET Status = 'Cancelled' WHERE Service = $service AND RunId = $runId AND Status = 'Pending';");
            cmd.Bind(database, "service", service);
            cmd.Bind(database, "runId", runId.ToString());
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Asks the worker holding an item to stop it; the worker polls <see cref="IsCancelRequested"/>. False when nothing of that run is claimed or running.</summary>
    public bool RequestCancel(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                UPDATE WorkQueue SET CancelRequestedUtc = $now
                WHERE Service = $service AND RunId = $runId AND {Held} AND CancelRequestedUtc IS NULL;
                """);
            cmd.Bind(database, "service", service);
            cmd.Bind(database, "runId", runId.ToString());
            cmd.Bind(database, "now", Now());
            cmd.ExecuteNonQuery();

            using var check = database.Command(connection, $"SELECT COUNT(*) FROM WorkQueue WHERE Service = $service AND RunId = $runId AND {Held} AND CancelRequestedUtc IS NOT NULL;");
            check.Bind(database, "service", service);
            check.Bind(database, "runId", runId.ToString());
            return Convert.ToInt64(check.ExecuteScalar()) > 0;
        });

    /// <summary>True once somebody has asked for this run to stop.</summary>
    public bool IsCancelRequested(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT COUNT(*) FROM WorkQueue WHERE Service = $service AND RunId = $runId AND CancelRequestedUtc IS NOT NULL;");
            cmd.Bind(database, "service", service);
            cmd.Bind(database, "runId", runId.ToString());
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    /// <summary>True while anything of the owner is pending, claimed or running (of one lane only, when given): what a worker's drain loop asks.</summary>
    public bool HasOutstandingWork(string owner, string? lane = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT COUNT(*) FROM WorkQueue
                WHERE Service = $service AND Owner = $owner AND {InFlight} {(lane is null ? "" : "AND Lane = $lane")};
                """);
            cmd.Bind(database, "service", service);
            cmd.Bind(database, "owner", owner);
            if (lane is not null) cmd.Bind(database, "lane", lane);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    /// <summary>True while this kind of work on this subject is pending, claimed or running (any part of it): what a scheduler asks before queueing it again.</summary>
    public bool HasInFlight(string owner, string kind, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT COUNT(*) FROM WorkQueue
                WHERE Service = $service AND Owner = $owner AND Kind = $kind AND Subject = $subject AND {InFlight};
                """);
            cmd.Bind(database, "service", service);
            cmd.Bind(database, "owner", owner);
            cmd.Bind(database, "kind", kind);
            cmd.Bind(database, "subject", subject);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    /// <summary>The owners that hold claimed or running items: what reconciliation after a restart asks, because an item claimed by a worker that died before starting it has no run record to be found by.</summary>
    public IReadOnlyList<string> OwnersWithHeldWork() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"SELECT DISTINCT Owner FROM WorkQueue WHERE Service = $service AND {Held};");
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
            using var cmd = database.Command(connection, "UPDATE WorkQueue SET Status = $status WHERE Id = $id;");
            cmd.Bind(database, "status", status.ToString());
            cmd.Bind(database, "id", id);
            cmd.ExecuteNonQuery();
        });

    private static WorkItem Read(DbDataReader reader) => new(
        Convert.ToInt64(reader.GetValue(0)),
        reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        Guid.Parse(reader.GetString(8)),
        Enum.Parse<WorkItemStatus>(reader.GetString(9)),
        Convert.ToInt32(reader.GetValue(10)),
        reader.IsDBNull(11) ? null : reader.GetString(11));
}
