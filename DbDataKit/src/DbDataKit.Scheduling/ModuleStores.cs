using System.Data.Common;
using DbDataKit.State;

namespace DbDataKit.Scheduling;

/// <summary>Run locks on the <c>scheduling</c> module (plan 29a Q11): the same behaviour and methods as <see cref="RunLockStore"/>, which it replaces when each product moves.</summary>
public sealed class ModuleRunLockStore(StateDatabase database, string service)
{
    private const string Module = SchedulingModule.Name;

    /// <summary>Takes the lock for <paramref name="runId"/>. False when someone holds it.</summary>
    public bool TryAcquire(string owner, string kind, string subject, Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.InsertOrIgnore(
                "{table:run_lock}", "service, owner, kind, subject, run_id, acquired_at", "$service, $owner, $kind, $subject, $run_id, $now", "service, owner, kind, subject"));
            Bind(cmd, owner, kind, subject);
            cmd.Bind(database, "run_id", runId).Bind(database, "now", database.Time.GetUtcNow());
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>The run that holds the lock, or null when nobody does.</summary>
    public Guid? Holder(string owner, string kind, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT run_id FROM {table:run_lock} WHERE service = $service AND owner = $owner AND kind = $kind AND subject = $subject;");
            Bind(cmd, owner, kind, subject);
            return cmd.ExecuteScalar() is { } value and not DBNull ? StateValues.ToGuid(value) : (Guid?)null;
        });

    public void Release(string owner, string kind, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:run_lock} WHERE service = $service AND owner = $owner AND kind = $kind AND subject = $subject;");
            Bind(cmd, owner, kind, subject);
            cmd.ExecuteNonQuery();
        });

    public bool IsLocked(string owner, string kind, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:run_lock} WHERE service = $service AND owner = $owner AND kind = $kind AND subject = $subject;");
            Bind(cmd, owner, kind, subject);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    private void Bind(DbCommand cmd, string owner, string kind, string subject) =>
        cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "kind", kind).Bind(database, "subject", subject);
}

/// <summary>A scheduler's per-job record on the module: the same behaviour and methods as <see cref="JobStateStore"/>.</summary>
public sealed class ModuleJobStateStore(StateDatabase database, string service)
{
    private const string Module = SchedulingModule.Name;

    public JobState? Get(string owner, string job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT handled_up_to_at, last_started_at, last_exit FROM {table:job_state} WHERE service = $service AND owner = $owner AND job = $job;");
            Bind(cmd, owner, job);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new JobState(reader.NullableTime(0), reader.NullableTime(1), reader.NullableInt32(2)) : null;
        });

    public void SaveHandled(string owner, string job, DateTimeOffset handledUpTo) => Set(owner, job, "handled_up_to_at", handledUpTo);

    public void SaveStarted(string owner, string job, DateTimeOffset started) => Set(owner, job, "last_started_at", started);

    public void SaveExit(string owner, string job, int exit) => Set(owner, job, "last_exit", (long)exit);

    private void Set(string owner, string job, string column, object value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var insert = database.Command(Module, connection, database.Dialect.InsertOrIgnore("{table:job_state}", "service, owner, job", "$service, $owner, $job", "service, owner, job")))
            {
                Bind(insert, owner, job);
                insert.ExecuteNonQuery();
            }

            using var update = database.Command(Module, connection, $"UPDATE {{table:job_state}} SET {column} = $value WHERE service = $service AND owner = $owner AND job = $job;");
            Bind(update, owner, job);
            update.Bind(database, "value", value);
            update.ExecuteNonQuery();
        });

    private void Bind(DbCommand cmd, string owner, string job) =>
        cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "job", job);
}

/// <summary>Jobs someone paused, on the module: the same behaviour and methods as <see cref="JobPauseStore"/>.</summary>
public sealed class ModuleJobPauseStore(StateDatabase database, string service)
{
    private const string Module = SchedulingModule.Name;

    public (string By, DateTimeOffset At)? Get(string owner, string job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT paused_by, paused_at FROM {table:job_pause} WHERE service = $service AND owner = $owner AND job = $job;");
            Bind(cmd, owner, job);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ((string, DateTimeOffset)?)(reader.GetString(0), reader.Time(1)) : null;
        });

    public bool IsPaused(string owner, string job) => Get(owner, job) is not null;

    public void Set(string owner, string job, string by, DateTimeOffset at) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection, database.Dialect.InsertOrIgnore("{table:job_pause}", "service, owner, job, paused_by, paused_at", "$service, $owner, $job, $by, $at", "service, owner, job"));
            Bind(insert, owner, job);
            insert.Bind(database, "by", by).Bind(database, "at", at);
            insert.ExecuteNonQuery();
        });

    /// <summary>
    /// Pauses a job with a note, or replaces who, when and the note of a job that is paused already (re-pausing with a new note is an act of its own, unlike <see cref="Set"/>, which keeps the first).
    /// On a connection and transaction the caller owns, so the pause and what records it commit together.
    /// </summary>
    public void Pause(string owner, string job, string by, DateTimeOffset at, string? note, DbConnection connection, DbTransaction? transaction)
    {
        using var cmd = database.Command(Module, connection, transaction, database.Dialect.Upsert(
            "{table:job_pause}", "service, owner, job, paused_by, paused_at, note", "$service, $owner, $job, $by, $at, $note", "service, owner, job",
            "paused_by = EXCLUDED.paused_by, paused_at = EXCLUDED.paused_at, note = EXCLUDED.note"));
        Bind(cmd, owner, job);
        cmd.Bind(database, "by", by).Bind(database, "at", at).Bind(database, "note", note, System.Data.DbType.String);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The note a job was paused with, or null (not paused, or paused without one). Whether the job is paused is <see cref="Get"/>.</summary>
    public string? Note(string owner, string job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT note FROM {table:job_pause} WHERE service = $service AND owner = $owner AND job = $job;");
            Bind(cmd, owner, job);
            return cmd.ExecuteScalar() as string;
        });

    /// <summary>Whether a job is paused, on a connection and transaction the caller owns (what a write that must know the earlier state reads before it changes it).</summary>
    public bool IsPaused(string owner, string job, DbConnection connection, DbTransaction? transaction)
    {
        using var cmd = database.Command(Module, connection, transaction, "SELECT 1 FROM {table:job_pause} WHERE service = $service AND owner = $owner AND job = $job;");
        Bind(cmd, owner, job);
        return cmd.ExecuteScalar() is not null;
    }

    public void Clear(string owner, string job, DbConnection connection, DbTransaction? transaction)
    {
        using var cmd = database.Command(Module, connection, transaction, "DELETE FROM {table:job_pause} WHERE service = $service AND owner = $owner AND job = $job;");
        Bind(cmd, owner, job);
        cmd.ExecuteNonQuery();
    }

    public void Clear(string owner, string job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:job_pause} WHERE service = $service AND owner = $owner AND job = $job;");
            Bind(cmd, owner, job);
            cmd.ExecuteNonQuery();
        });

    private void Bind(DbCommand cmd, string owner, string job) =>
        cmd.Bind(database, "service", service).Bind(database, "owner", owner).Bind(database, "job", job);
}
