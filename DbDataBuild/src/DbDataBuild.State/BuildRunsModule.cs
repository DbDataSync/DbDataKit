using System.Data.Common;
using DbDataKit.State;

namespace DbDataBuild.State;

/// <summary>
/// The <c>build_runs</c> module (UI09): one row for each run of a scheduled refresh plan that this Build server's scheduler finished, so the console can list the runs of its plans across servers. Until this module the scheduler kept only
/// the last start and the last exit code of a plan (the <c>scheduling</c> module's <c>job_state</c>). Install scope: <c>service</c> is in every key and statement, as in the scheduling module. A run is written when it ends (so a run in
/// flight is not listed) with the project, the plan's subject (<c>&lt;connection&gt;/&lt;plan&gt;</c>), the run id of the work item, when it started and ended and the exit code. Aged out after <see cref="RetentionDays"/> days.
/// Migrations are only ever appended.
/// </summary>
public static class BuildRunsModule
{
    public const string Name = "build_runs";

    public const double RetentionDays = 180;

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- A scheduled plan's run that has ended. owner is the hosted project, job the plan's subject ('<connection>/<plan>'), run_id the work item's run, exit_code the exit code of the refresh (0 succeeded;
            -- -1 it could not be started; anything else is the command's own failure).
            CREATE TABLE {table:plan_run} (
                {{identity:id}},
                service    {{key}} NOT NULL,
                owner      {{key}} NOT NULL,
                job        {{key}} NOT NULL,
                run_id     {{uuid}} NOT NULL,
                started_at {{time}} NOT NULL,
                ended_at   {{time}} NOT NULL,
                exit_code  {{int}} NOT NULL
            );
            CREATE INDEX {index:plan_run_by_end} ON {table:plan_run}(service, ended_at);
            CREATE INDEX {index:plan_run_by_job} ON {table:plan_run}(service, owner, job, ended_at);
            """,
        ],
        retention: [new RetentionRule("plan_run", "ended_at", RetentionDays)]);
}

/// <summary>One finished run of a scheduled plan.</summary>
public sealed record PlanRun(long Id, string Project, string Subject, Guid RunId, DateTimeOffset Started, DateTimeOffset Ended, int Exit);

/// <summary>The runs of a Build server's scheduled plans, on the <c>build_runs</c> module.</summary>
public sealed class PlanRunStore(StateDatabase database, string service)
{
    private const string Module = BuildRunsModule.Name;

    /// <summary>Records a run that has ended.</summary>
    public void Record(string project, string subject, Guid runId, DateTimeOffset started, DateTimeOffset ended, int exit) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "INSERT INTO {table:plan_run} (service, owner, job, run_id, started_at, ended_at, exit_code) VALUES ($service, $owner, $job, $run_id, $started, $ended, $exit);");
            cmd.Bind(database, "service", service).Bind(database, "owner", project).Bind(database, "job", subject).Bind(database, "run_id", runId)
                .Bind(database, "started", started).Bind(database, "ended", ended).Bind(database, "exit", (long)exit);
            cmd.ExecuteNonQuery();
        });

    /// <summary>The newest runs first, at most <paramref name="limit"/>, of one project or of all.</summary>
    public IReadOnlyList<PlanRun> Recent(int limit, string? project = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT id, owner, job, run_id, started_at, ended_at, exit_code FROM {{table:plan_run}} WHERE service = $service {(project is null ? "" : "AND owner = $owner")} ORDER BY ended_at DESC, id DESC {database.Limit("limit")};");
            cmd.Bind(database, "service", service).Bind(database, "limit", (long)limit);
            if (project is not null) cmd.Bind(database, "owner", project);
            using var reader = cmd.ExecuteReader();
            var runs = new List<PlanRun>();
            while (reader.Read()) runs.Add(new PlanRun(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.Uuid(3), reader.Time(4), reader.Time(5), (int)reader.GetInt64(6)));
            return (IReadOnlyList<PlanRun>)runs;
        });
}
