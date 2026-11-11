using DbDataKit.State;

namespace DbDataKit.Scheduling;

/// <summary>
/// Keeps two runs of the same kind of work on the same subject of one owner from overlapping (plan 13, M02). The queue's claim already skips a conflicting item, so this is the
/// guard a run takes for itself once it starts: a second acquire finds the first and fails, however the two got there.
/// </summary>
/// <param name="database">The state database; its schema includes <see cref="WorkQueueSchema.Queue"/>.</param>
/// <param name="service">The service whose locks these are.</param>
/// <param name="time">The clock; the system clock when omitted.</param>
public sealed class RunLockStore(StateDatabase database, string service, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Takes the lock for <paramref name="runId"/>. False when someone holds it.</summary>
    public bool TryAcquire(string owner, string kind, string subject, Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.InsertOrIgnore(
                "RunLocks", "Service, Owner, Kind, Subject, RunId, AcquiredAtUtc", "$service, $owner, $kind, $subject, $runId, $now",
                "Service, Owner, Kind, Subject"));
            Bind(cmd, owner, kind, subject);
            cmd.Bind(database, "runId", runId.ToString());
            cmd.Bind(database, "now", _time.GetUtcNow().ToString("O"));
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Gives the lock up.</summary>
    public void Release(string owner, string kind, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM RunLocks WHERE Service = $service AND Owner = $owner AND Kind = $kind AND Subject = $subject;");
            Bind(cmd, owner, kind, subject);
            cmd.ExecuteNonQuery();
        });

    /// <summary>Whether anyone holds it.</summary>
    public bool IsLocked(string owner, string kind, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT COUNT(*) FROM RunLocks WHERE Service = $service AND Owner = $owner AND Kind = $kind AND Subject = $subject;");
            Bind(cmd, owner, kind, subject);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    private void Bind(System.Data.Common.DbCommand cmd, string owner, string kind, string subject)
    {
        cmd.Bind(database, "service", service);
        cmd.Bind(database, "owner", owner);
        cmd.Bind(database, "kind", kind);
        cmd.Bind(database, "subject", subject);
    }
}
