using DbDataKit.State;

namespace DbDataKit.Scheduling;

/// <summary>What a scheduler remembers about one job between ticks and restarts.</summary>
/// <param name="HandledUpTo">Scheduled times up to here are dealt with (see <see cref="ScheduleDecision.HandledUpTo"/>).</param>
/// <param name="LastStarted">When the last run began.</param>
/// <param name="LastExit">How the last run ended (an exit code), or null while it has not ended or never ran.</param>
public sealed record JobState(DateTimeOffset? HandledUpTo, DateTimeOffset? LastStarted, int? LastExit);

/// <summary>The table of <see cref="JobStateStore"/>, a fragment a product includes beside <see cref="WorkQueueSchema.Queue"/> when its scheduler needs it.</summary>
public static class JobStateSchema
{
    public const string Table =
        """
        -- What a scheduler remembers per job: up to when its scheduled times are dealt with, when its last run began and how it ended.
        -- A job is named by its service and owner (a project, a replication) and its own name within it.
        CREATE TABLE ScheduleJobs (
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            Job {{key}} NOT NULL,
            HandledUpToUtc {{text}} NULL,
            LastStartedUtc {{text}} NULL,
            LastExit {{int}} NULL,
            PRIMARY KEY (Service, Owner, Job)
        );
        """;
}

/// <summary>A scheduler's per-job record (plan 13, M08): written by the scheduler as it decides and as runs begin and end, read at the next tick and after a restart.</summary>
/// <param name="database">The state database; its schema includes <see cref="JobStateSchema.Table"/>.</param>
/// <param name="service">The service whose jobs these are.</param>
public sealed class JobStateStore(StateDatabase database, string service)
{
    public JobState? Get(string owner, string job) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT HandledUpToUtc, LastStartedUtc, LastExit FROM ScheduleJobs WHERE Service = $service AND Owner = $owner AND Job = $job;");
            Bind(cmd, owner, job);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            static DateTimeOffset? When(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), System.Globalization.CultureInfo.InvariantCulture);
            return new JobState(When(reader, 0), When(reader, 1), reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)));
        });

    public void SaveHandled(string owner, string job, DateTimeOffset handledUpTo) => Set(owner, job, "HandledUpToUtc", handledUpTo.ToString("O"));

    public void SaveStarted(string owner, string job, DateTimeOffset started) => Set(owner, job, "LastStartedUtc", started.ToString("O"));

    public void SaveExit(string owner, string job, int exit) => Set(owner, job, "LastExit", exit);

    private void Set(string owner, string job, string column, object value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var insert = database.Command(connection, database.Dialect.InsertOrIgnore("ScheduleJobs", "Service, Owner, Job", "$service, $owner, $job", "Service, Owner, Job")))
            {
                Bind(insert, owner, job);
                insert.ExecuteNonQuery();
            }
            using var update = database.Command(connection, $"UPDATE ScheduleJobs SET {column} = $value WHERE Service = $service AND Owner = $owner AND Job = $job;");
            Bind(update, owner, job);
            update.Bind(database, "value", value);
            update.ExecuteNonQuery();
        });

    private void Bind(System.Data.Common.DbCommand cmd, string owner, string job)
    {
        cmd.Bind(database, "service", service);
        cmd.Bind(database, "owner", owner);
        cmd.Bind(database, "job", job);
    }
}
