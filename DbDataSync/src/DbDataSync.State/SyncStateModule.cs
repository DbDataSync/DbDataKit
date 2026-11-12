using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataSync.State;

/// <summary>
/// The <c>sync_state</c> module (plan 28c section 5.1, slice E14): what is Sync's own in its state. Everything that is not is in a shared module: a run (the job, when, the outcome, rows, the failure classified) and its log
/// are in <c>runs</c>, the work queue, run locks and the pause of a replication are in <c>scheduling</c>, the feed is in <c>notifications</c>, who paused what is in <c>audit</c>, and the position of each table of a mapping is a
/// checkpoint in <c>runner_progress</c>. This module holds what a run of Sync has beyond a run (<c>run_detail</c>), the polling gate's trail (<c>change_check</c>), the index of verification results
/// (<c>verification_result</c>) and the bulk-load batches (<c>bulk_load_batch</c>). One copy per group.
/// <para>
/// The stored format is fixed here, and a migration is only ever appended (plan 29 section 3.1). Retention (owner decision 2026-11-07, plan 28c QE9): a run's detail goes with its run after 30 days, the polling gate's trail
/// after 7 days; verification results and bulk-load batches are kept until their replication is removed.
/// </para>
/// </summary>
public static class SyncStateModule
{
    public const string Name = "sync_state";

    /// <summary>How long a run's detail is kept: the age of the run it belongs to (<see cref="RunsModule"/>).</summary>
    public const double RunDetailDays = 30;

    /// <summary>How long the polling gate's trail is kept (the trail is history, not mechanism).</summary>
    public const double ChangeCheckDays = 7;

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- What a run of Sync has beyond a run of any service (the run itself is in runs.run, written in the same transaction: its job is the replication, which task_name repeats so that Sync's own queries need no join, its started_at is when the run was asked for,
            -- its outcome and rows are the shared view of this row's status and rows_written). run_kind is Primary, BulkLoad, Verification or ReconcileDeletes. mapping_name is the table mapping the
            -- run is for. segment_label names the segment a bulk-load run covers, null for a run with none. status is Queued, Pending, Running, Succeeded, Failed or Cancelled, exactly as Sync names them.
            -- The three times are in the order they happen: enqueued_at (asked for), claimed_at (a worker took it), began_at (the work started), then ended_at. The reader, staging and writer columns are the
            -- timings of a run that was traced, null for one that was not. previous_position and new_position are where a pass started from and where it left off, opaque text (a log sequence number
            -- as hex, a change-tracking version in decimal, a time), null unless the run made a new position durable. error_summary is the one line the runs tab shows; error_detail is the whole
            -- exception for its popup; failure_kind says why a run failed where the product can act on it (PositionExpired, MetadataNotCached).
            CREATE TABLE {table:run_detail} (
                run_id                    {{uuid}} PRIMARY KEY,
                task_name                 {{key}} NOT NULL,
                run_kind                  {{key}} NOT NULL,
                mapping_name              {{key}} NOT NULL DEFAULT '',
                segment_label             {{text}} NULL,
                status                    {{key}} NOT NULL,
                pid                       {{int}} NULL,
                enqueued_at               {{time}} NOT NULL,
                claimed_at                {{time}} NULL,
                began_at                  {{time}} NULL,
                ended_at                  {{time}} NULL,
                rows_read                 {{int}} NOT NULL DEFAULT 0,
                rows_written              {{int}} NOT NULL DEFAULT 0,
                error_summary             {{text}} NULL,
                failure_kind              {{text}} NULL,
                error_detail              {{text}} NULL,
                reader_kind               {{text}} NULL,
                reader_first_row_ms       {{int}} NULL,
                reader_lifetime_ms        {{int}} NULL,
                staging_kind              {{text}} NULL,
                staging_duration_ms       {{int}} NULL,
                writer_kind               {{text}} NULL,
                writer_duration_ms        {{int}} NULL,
                previous_position         {{text}} NULL,
                new_position              {{text}} NULL,
                bulk_load_batch_id        {{key}} NULL
            );
            CREATE INDEX {index:run_detail_by_task} ON {table:run_detail}(task_name, enqueued_at, run_id);
            CREATE INDEX {index:run_detail_by_kind} ON {table:run_detail}(task_name, run_kind, mapping_name);
            CREATE INDEX {index:run_detail_by_status} ON {table:run_detail}(status);
            CREATE INDEX {index:run_detail_by_batch} ON {table:run_detail}(bulk_load_batch_id);

            -- The polling gate's trail: what the gate read from a source (a position, or nothing) and when. connection_name and source_database say which source, source_kind how it was read.
            -- value_text is what it read, null when it read nothing. source_at is the time the source itself said, where it said one. History, not mechanism: kept for seven days.
            CREATE TABLE {table:change_check} (
                {{identity:id}},
                connection_name  {{key}} NOT NULL,
                source_database  {{key}} NOT NULL,
                source_kind      {{key}} NOT NULL,
                value_text       {{text}} NULL,
                checked_at       {{time}} NOT NULL,
                source_at        {{time}} NULL
            );
            CREATE INDEX {index:change_check_by_time} ON {table:change_check}(checked_at);
            CREATE INDEX {index:change_check_by_source} ON {table:change_check}(connection_name, source_database, source_kind, checked_at);

            -- Where a verification result is, not what it says: the result is a file (result_path). One row for each check of a run.
            CREATE TABLE {table:verification_result} (
                {{identity:id}},
                run_id            {{uuid}} NOT NULL,
                task_name         {{key}} NOT NULL,
                mapping_name      {{key}} NOT NULL,
                check_name        {{key}} NOT NULL,
                completed_at      {{time}} NOT NULL,
                source_read_at    {{time}} NOT NULL,
                target_read_at    {{time}} NOT NULL,
                groups_compared   {{int}} NOT NULL,
                differing_groups  {{int}} NOT NULL,
                result_path       {{text}} NOT NULL
            );
            CREATE INDEX {index:verification_result_by_task} ON {table:verification_result}(task_name, completed_at);
            CREATE UNIQUE INDEX {index:verification_result_by_check} ON {table:verification_result}(run_id, check_name);

            -- A bulk load that was split into segments: one row for the batch, and the segments are the runs whose run_detail names the batch. The estimate is what was known when the batch was made.
            CREATE TABLE {table:bulk_load_batch} (
                batch_id        {{key}} PRIMARY KEY,
                task_name       {{key}} NOT NULL,
                mapping_name    {{key}} NOT NULL,
                created_at      {{time}} NOT NULL,
                segment_count   {{int}} NOT NULL,
                estimated_rows  {{int}} NULL,
                estimate_caveat {{text}} NULL
            );
            CREATE INDEX {index:bulk_load_batch_by_task} ON {table:bulk_load_batch}(task_name, created_at);
            """,
        ],
        dependsOn: [RunsModule.Module],
        retention:
        [
            // a run's detail goes with its run (the runs module removes the run and its log at the same age)
            new RetentionRule("run_detail", "enqueued_at", RunDetailDays, KeyColumn: "run_id"),
            new RetentionRule("change_check", "checked_at", ChangeCheckDays),
        ]);
}

/// <summary>Sync's state as the shared state layer sees it: which modules, for which group and service.</summary>
public static class SyncState
{
    /// <summary>The service name Sync's rows carry in every shared module (runs, queue, notifications, audit).</summary>
    public const string ServiceId = "dbdatasync";

    /// <summary>The group a stand-alone Sync belongs to until Sync has groups of its own (E12): one group, <c>main</c>.</summary>
    public const string DefaultGroup = "main";

    public static StateScope Scope { get; } = new(DefaultGroup, ServiceId);

    /// <summary>Who is recorded as applying a migration: the service and its version.</summary>
    public static string AppliedBy => $"{ServiceId} {typeof(SyncState).Assembly.GetName().Version}";

    /// <summary>The modules Sync's state is built from: its own and the shared ones it uses (<c>quality</c>: the results of the checks stored with its replications, plan 23a FQ10; <c>lineage</c>: its graph and the runs along its edges, plan 18 G07b).</summary>
    public static IReadOnlyList<StateModule> Modules { get; } =
    [
        SyncStateModule.Module,
        RunsModule.Module,
        SchedulingModule.Module,
        NotificationsModule.Module,
        AuditModule.Module,
        ProgressModule.Module,
        QualityModule.Module,
        DbDataKit.Lineage.LineageModule.Module,
        DbDataKit.State.Tracking.TrackingModule.Module,   // plan 28c E17: the copy of the units a target holds, and the divergences found
    ];
}
