using DbDataKit.State;

namespace DbDataKit.Scheduling;

/// <summary>The table of <see cref="JobPauseStore"/>, a fragment a product includes after <see cref="JobStateSchema.Table"/> when its scheduler can be paused.</summary>
public static class JobPauseSchema
{
    public const string Table =
        """
        -- A job someone paused: who, and when. A job with no row here is not paused.
        CREATE TABLE ScheduleJobPauses (
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            Job {{key}} NOT NULL,
            PausedBy {{text}} NOT NULL,
            PausedAtUtc {{text}} NOT NULL,
            PRIMARY KEY (Service, Owner, Job)
        );
        """;
}

/// <summary>What a scheduler remembers about jobs someone paused (plan 13, M11): a paused job's scheduled times are passed over, not run, and resuming does not run them afterwards.</summary>
public sealed class JobPauseStore(StateDatabase database, string service)
{
    public (string By, DateTimeOffset At)? Get(string owner, string job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT PausedBy, PausedAtUtc FROM ScheduleJobPauses WHERE Service = $service AND Owner = $owner AND Job = $job;");
            Bind(cmd, owner, job);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ((string, DateTimeOffset)?)(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture)) : null;
        });

    public bool IsPaused(string owner, string job) => Get(owner, job) is not null;

    public void Set(string owner, string job, string by, DateTimeOffset at) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var insert = database.Command(connection, database.Dialect.InsertOrIgnore("ScheduleJobPauses", "Service, Owner, Job, PausedBy, PausedAtUtc", "$service, $owner, $job, $by, $at", "Service, Owner, Job")))
            {
                Bind(insert, owner, job);
                insert.Bind(database, "by", by);
                insert.Bind(database, "at", at.ToString("O"));
                insert.ExecuteNonQuery();
            }
        });

    public void Clear(string owner, string job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM ScheduleJobPauses WHERE Service = $service AND Owner = $owner AND Job = $job;");
            Bind(cmd, owner, job);
            cmd.ExecuteNonQuery();
        });

    private void Bind(System.Data.Common.DbCommand cmd, string owner, string job)
    {
        cmd.Bind(database, "service", service);
        cmd.Bind(database, "owner", owner);
        cmd.Bind(database, "job", job);
    }
}
