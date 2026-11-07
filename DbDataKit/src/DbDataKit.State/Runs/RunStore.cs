using System.Data.Common;
using System.Text;
using DbDataKit.Monitoring;

namespace DbDataKit.State;

/// <summary>
/// The <c>runs</c> module (plan 29 section 3.4): the one run model of the suite (<see cref="RunRecord"/>: job, start, end, outcome, rows, the item that failed, the failure classified)
/// and each run's log, for every service of an install in one place, so the console reads a Build run and a Sync run with one query. Build's tracking tables stay on its target and
/// are read into this later (plan 29 section 4a); this holds what a service keeps for itself. History: removed after the retention age (30 days).
/// </summary>
public static class RunsModule
{
    public const string Name = "runs";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- One run of a job. service is the service that ran it; job_id is what it schedules (a replication, a refresh plan). outcome is Running until the run ends.
            -- failure_kind and failure_message classify a failure and never hold a secret. rows_moved is set when the product counts.
            CREATE TABLE {table:run} (
                id               {{uuid}} PRIMARY KEY,
                service          {{key}} NOT NULL,
                job_id           {{key}} NOT NULL,
                started_at       {{time}} NOT NULL,
                finished_at      {{time}} NULL,
                outcome          {{key}} NOT NULL,
                rows_moved       {{int}} NULL,
                failed_item      {{text}} NULL,
                failure_kind     {{key}} NULL,
                failure_message  {{text}} NULL
            );
            CREATE INDEX {index:run_by_job} ON {table:run}(service, job_id, started_at);
            CREATE INDEX {index:run_by_outcome} ON {table:run}(outcome, started_at);

            -- What a run said while it ran, in order; read in pages by id.
            CREATE TABLE {table:run_log} (
                {{identity:id}},
                run_id     {{uuid}} NOT NULL,
                logged_at  {{time}} NOT NULL,
                level      {{key}} NOT NULL,
                item       {{text}} NULL,
                message    {{text}} NOT NULL
            );
            CREATE INDEX {index:run_log_by_run} ON {table:run_log}(run_id, id);
            """,
            """
            -- What the runner (plan 28) records about a run: the package, the version of it that ran, the channel it ran on and which attempt this was (1 is the first). Null for a run of a product
            -- that has no packages, and for every run written before this migration.
            ALTER TABLE {table:run} {{addcolumn}} package {{key}} NULL;
            ALTER TABLE {table:run} {{addcolumn}} package_version {{key}} NULL;
            ALTER TABLE {table:run} {{addcolumn}} channel {{key}} NULL;
            ALTER TABLE {table:run} {{addcolumn}} attempt {{int}} NULL;
            CREATE INDEX {index:run_by_package} ON {table:run}(package, channel, started_at);
            """,
        ],
        retention:
        [
            // a run and its log go together; a log line also goes on its own age, for a run that is still going after the age
            new RetentionRule("run", "started_at", Children: [new RetentionChild("run_log", "run_id")]),
            new RetentionRule("run_log", "logged_at"),
        ]);
}

/// <summary>A run as stored: which service ran it, and the run itself.</summary>
/// <param name="Package">The package a runner ran, or null for a run that had none.</param>
/// <param name="PackageVersion">The version of the package that ran.</param>
/// <param name="Channel">The channel it ran on.</param>
/// <param name="Attempt">Which attempt of the work item this run was (1 is the first), or null when not recorded.</param>
public sealed record StoredRun(Guid Id, string Service, RunRecord Run, string? Package = null, string? PackageVersion = null, string? Channel = null, int? Attempt = null);

/// <summary>One line of a run's log.</summary>
public sealed record RunLogLine(long Id, Guid RunId, DateTimeOffset At, string Level, string? Item, string Message);

/// <summary>Which runs to read, newest first; every field narrows.</summary>
/// <param name="StartedBefore">Page by passing the <c>Started</c> of the last run seen.</param>
public sealed record RunQuery(string? Service = null, string? JobId = null, RunOutcome? Outcome = null, DateTimeOffset? StartedSince = null, DateTimeOffset? StartedBefore = null, int Limit = 100, string? Package = null, string? Channel = null);

/// <summary>
/// Writes and reads runs and their logs for one service (<paramref name="service"/> is recorded on every run it starts). A service that records a run in the same transaction as another
/// change (the queue's claim) passes a connection and transaction.
/// </summary>
public sealed class RunStore(StateDatabase database, string service)
{
    private const string Module = RunsModule.Name;
    private const string RunColumns = "id, service, job_id, started_at, finished_at, outcome, rows_moved, failed_item, failure_kind, failure_message, package, package_version, channel, attempt";

    /// <summary>Records that a run began. <paramref name="runId"/> is the queue's run id when the run came from the work queue, so the two records match; a new id otherwise.</summary>
    public Guid Start(string jobId, Guid? runId = null, DateTimeOffset? at = null, DbConnection? connection = null, DbTransaction? transaction = null,
        string? package = null, string? packageVersion = null, string? channel = null, int? attempt = null)
    {
        var id = runId ?? database.NewId();
        Write(connection, c =>
        {
            using var cmd = database.Command(Module, c, transaction, "INSERT INTO {table:run} (id, service, job_id, started_at, outcome, package, package_version, channel, attempt) VALUES ($id, $service, $job, $at, $outcome, $package, $package_version, $channel, $attempt);");
            cmd.Bind(database, "id", id).Bind(database, "service", service).Bind(database, "job", jobId).Bind(database, "at", at ?? database.Time.GetUtcNow()).Bind(database, "outcome", RunOutcome.Running.ToString())
                .Bind(database, "package", package, System.Data.DbType.String).Bind(database, "package_version", packageVersion, System.Data.DbType.String)
                .Bind(database, "channel", channel, System.Data.DbType.String).Bind(database, "attempt", attempt, System.Data.DbType.Int32);
            cmd.ExecuteNonQuery();
        });
        return id;
    }

    /// <summary>Records how a run ended. False when the run is not one that is still running (it was finished already, or there is none): a run ends once.</summary>
    public bool Finish(Guid runId, RunOutcome outcome, long? rows = null, string? failedItem = null, RunFailure? failure = null, DateTimeOffset? at = null, DbConnection? connection = null, DbTransaction? transaction = null)
    {
        if (outcome == RunOutcome.Running) throw new ArgumentException("A run finishes with an outcome other than Running.", nameof(outcome));
        var changed = 0;
        Write(connection, c =>
        {
            using var cmd = database.Command(Module, c, transaction,
                "UPDATE {table:run} SET finished_at = $at, outcome = $outcome, rows_moved = $rows, failed_item = $item, failure_kind = $kind, failure_message = $message WHERE id = $id AND outcome = $running;");
            cmd.Bind(database, "at", at ?? database.Time.GetUtcNow()).Bind(database, "outcome", outcome.ToString()).Bind(database, "rows", rows, System.Data.DbType.Int64)
                .Bind(database, "item", failedItem, System.Data.DbType.String).Bind(database, "kind", failure?.Kind, System.Data.DbType.String)
                .Bind(database, "message", failure?.Message, System.Data.DbType.String).Bind(database, "id", runId).Bind(database, "running", RunOutcome.Running.ToString());
            changed = cmd.ExecuteNonQuery();
        });
        return changed == 1;
    }

    /// <summary>Marks every run of this service that is still Running and started before <paramref name="startedBefore"/> as Interrupted (the process that was running them went away); the count.</summary>
    public int InterruptRunning(DateTimeOffset startedBefore) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:run} SET outcome = $interrupted, finished_at = $now WHERE service = $service AND outcome = $running AND started_at < $before;");
            cmd.Bind(database, "interrupted", RunOutcome.Interrupted.ToString()).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "service", service)
                .Bind(database, "running", RunOutcome.Running.ToString()).Bind(database, "before", startedBefore);
            return cmd.ExecuteNonQuery();
        });

    public StoredRun? Get(Guid runId) => Read(new RunQuery(), "id = $id", c => c.Bind(database, "id", runId)).FirstOrDefault();

    /// <summary>The most recent run of a job of this service, or null.</summary>
    public StoredRun? LastRun(string jobId) => Read(new RunQuery(service, jobId, Limit: 1)).FirstOrDefault();

    /// <summary>
    /// The runs of a job of this service that ended at or after <paramref name="since"/>, oldest end first (up to <paramref name="limit"/>): what a trigger that follows the job reads. Ties on the end time are ordered by id
    /// (the text of the id, so the order is the same on every engine).
    /// </summary>
    public IReadOnlyList<StoredRun> FinishedSince(string jobId, DateTimeOffset since, int limit = 200)
    {
        var runs = Read(new RunQuery(service, jobId, Limit: 1000), "finished_at IS NOT NULL AND finished_at >= $finished_since", c => c.Bind(database, "finished_since", since));
        return [.. runs.OrderBy(r => r.Run.Finished).ThenBy(r => r.Id.ToString("N"), StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 1000))];
    }

    /// <summary>Runs newest first, narrowed by the query: one service's, one job's, or everyone's.</summary>
    public IReadOnlyList<StoredRun> Read(RunQuery? query = null) => Read(query ?? new RunQuery(), null, null);

    /// <summary>Records one line of a run's log.</summary>
    public long Log(Guid runId, string level, string message, string? item = null, DateTimeOffset? at = null)
    {
        long id = 0;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var sql = "INSERT INTO {table:run_log} (run_id, logged_at, level, item, message) VALUES ($run, $at, $level, $item, $message)" +
                      (database.Dialect.Engine == StateEngineIds.MsSql ? "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT);" : " RETURNING id;");
            using var cmd = database.Command(Module, connection, sql);
            cmd.Bind(database, "run", runId).Bind(database, "at", at ?? database.Time.GetUtcNow()).Bind(database, "level", level).Bind(database, "item", item, System.Data.DbType.String).Bind(database, "message", message);
            id = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });
        return id;
    }

    /// <summary>A run's log in order, a page at a time: pass the last id seen as <paramref name="afterId"/>.</summary>
    public IReadOnlyList<RunLogLine> Logs(Guid runId, long? afterId = null, int limit = 200) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT id, run_id, logged_at, level, item, message FROM {{table:run_log}} WHERE run_id = $run {(afterId is null ? "" : "AND id > $after")} ORDER BY id {database.Limit("limit")};");
            cmd.Bind(database, "run", runId);
            if (afterId is { } after) cmd.Bind(database, "after", after);
            cmd.Bind(database, "limit", (long)Math.Clamp(limit, 1, 1000));
            using var reader = cmd.ExecuteReader();
            var lines = new List<RunLogLine>();
            while (reader.Read())
                lines.Add(new RunLogLine(reader.Int64(0), reader.Uuid(1), reader.Time(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
            return (IReadOnlyList<RunLogLine>)lines;
        });

    private IReadOnlyList<StoredRun> Read(RunQuery query, string? extraWhere, Action<DbCommand>? bindExtra) =>
        database.Retry(() =>
        {
            var where = new StringBuilder("1 = 1");
            if (extraWhere is not null) where.Append(" AND ").Append(extraWhere);
            if (query.Service is not null) where.Append(" AND service = $service");
            if (query.JobId is not null) where.Append(" AND job_id = $job");
            if (query.Outcome is not null) where.Append(" AND outcome = $outcome");
            if (query.StartedSince is not null) where.Append(" AND started_at >= $since");
            if (query.StartedBefore is not null) where.Append(" AND started_at < $before");
            if (query.Package is not null) where.Append(" AND package = $package");
            if (query.Channel is not null) where.Append(" AND channel = $channel");

            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {RunColumns} FROM {{table:run}} WHERE {where} ORDER BY started_at DESC, id DESC {database.Limit("limit")};");
            bindExtra?.Invoke(cmd);
            if (query.Service is { } s) cmd.Bind(database, "service", s);
            if (query.JobId is { } j) cmd.Bind(database, "job", j);
            if (query.Outcome is { } o) cmd.Bind(database, "outcome", o.ToString());
            if (query.StartedSince is { } since) cmd.Bind(database, "since", since);
            if (query.StartedBefore is { } before) cmd.Bind(database, "before", before);
            if (query.Package is { } package) cmd.Bind(database, "package", package);
            if (query.Channel is { } channel) cmd.Bind(database, "channel", channel);
            cmd.Bind(database, "limit", (long)Math.Clamp(query.Limit, 1, 1000));

            using var reader = cmd.ExecuteReader();
            var runs = new List<StoredRun>();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var id = reader.Uuid(0);
                var failure = Text(8) is { } kind ? new RunFailure(kind, Text(9) ?? "") : null;
                runs.Add(new StoredRun(id, reader.GetString(1), new RunRecord(reader.GetString(2), id.ToString("D"), reader.Time(3), reader.NullableTime(4),
                    Enum.Parse<RunOutcome>(reader.GetString(5)), reader.NullableInt64(6), Text(7), failure), Text(10), Text(11), Text(12), reader.NullableInt32(13)));
            }

            return (IReadOnlyList<StoredRun>)runs;
        });

    private void Write(DbConnection? connection, Action<DbConnection> work) =>
        database.Retry(() =>
        {
            if (connection is not null)
            {
                work(connection);
                return;
            }

            using var own = database.OpenConnection();
            work(own);
        });
}
