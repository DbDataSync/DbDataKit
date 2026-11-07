using System.Data.Common;
using System.Text.Json;
using DbDataKit.Scheduling;

namespace DbDataSync.State;

/// <summary>
/// Sync's run history and pause state, on the shared modules (plan 28c slice E14): an adapter that keeps the methods Sync has always called. A run is a row of <c>runs.run</c> (the one run model of the suite: the
/// replication is the job, the run's <c>started_at</c> is when it was asked for) and a row of <c>sync_state.run_detail</c> (what only Sync has: kind, mapping, segment, status, counts, timings, positions, error
/// detail), written in one transaction. A replication's pause is a row of <c>scheduling.job_pause</c> and every pause, resume and mapping hold is an entry of the <c>audit</c>. A failure or a pause is announced in the
/// <c>notifications</c> feed in the transaction that records it.
/// </summary>
public sealed class TaskRunStore(StateDatabase database)
{
    private const string Module = SyncStateModule.Name;

    /// <summary>The job a replication's pause is recorded under in <c>scheduling.job_pause</c> (the replication is the owner; its one scheduled task is <c>drain</c>, plan 28c section 4.3).</summary>
    public const string PauseJob = "drain";

    private readonly ModuleJobPauseStore _pauses = new(database, SyncState.ServiceId);

    /// <summary>
    /// Every column <see cref="ReadRun"/> reads, in the order it reads them — named once because the queries select exactly this list and <see cref="ReadRun"/> addresses it by ordinal.
    /// The three timestamps are listed in the order they happen, which is also the order <c>TaskRunRecord</c> declares them.
    /// </summary>
    private const string RunColumns =
        "run_id, task_name, pid, status, run_kind, mapping_name, segment_label, " +
        "enqueued_at, claimed_at, began_at, ended_at, rows_read, rows_written, " +
        "error_summary, failure_kind, reader_kind, reader_first_row_ms, reader_lifetime_ms, " +
        "staging_kind, staging_duration_ms, writer_kind, writer_duration_ms, previous_position, new_position, " +
        "error_detail";

    /// <summary>
    /// Last enqueue time of the given kind per table mapping, in one query — what SchedulerService's due-ness check needs. One query per replication per tick, not one per mapping per tick: a replication can
    /// have hundreds of mappings. The enqueue time, not the start (phase 73): due-ness asks "has it been N seconds since we last asked for this", and asking is the enqueue.
    /// </summary>
    private IReadOnlyDictionary<string, DateTimeOffset> LastEnqueueByMapping(string taskName, RunKind kind) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT mapping_name, MAX(enqueued_at) FROM {table:run_detail} WHERE task_name = $task AND run_kind = $kind GROUP BY mapping_name;");
            cmd.Bind(database, "task", taskName).Bind(database, "kind", kind.ToString());
            using var reader = cmd.ExecuteReader();
            var results = new Dictionary<string, DateTimeOffset>();
            while (reader.Read())
                results[reader.GetString(0)] = reader.Time(1);
            return (IReadOnlyDictionary<string, DateTimeOffset>)results;
        });

    /// <summary>Last Primary-run enqueue time per table mapping (see <see cref="LastEnqueueByMapping"/>).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> GetLastPrimaryEnqueueByMapping(string taskName) => LastEnqueueByMapping(taskName, RunKind.Primary);

    /// <summary>Sibling of <see cref="GetLastPrimaryEnqueueByMapping"/>, filtered to <see cref="RunKind.ReconcileDeletes"/> — phase 125's scheduled cadence.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> GetLastReconcileEnqueueByMapping(string taskName) => LastEnqueueByMapping(taskName, RunKind.ReconcileDeletes);

    /// <summary>
    /// Rows each mapping's successful <see cref="RunKind.Primary"/> passes have read since that mapping's own cutoff — phase 125's after-change trigger input. One bounded query, not one per mapping: the shared
    /// floor (the earliest cutoff) bounds the read and each mapping's own cutoff is applied in memory.
    /// </summary>
    public IReadOnlyDictionary<string, long> GetRowsReadSincePerMapping(string taskName, IReadOnlyDictionary<string, DateTimeOffset> sinceByMapping)
    {
        if (sinceByMapping.Count == 0)
            return new Dictionary<string, long>();

        var floor = sinceByMapping.Values.Min();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT mapping_name, ended_at, rows_read FROM {table:run_detail} WHERE task_name = $task AND run_kind = $kind AND status = $status AND ended_at > $floor;");
            cmd.Bind(database, "task", taskName).Bind(database, "kind", RunKind.Primary.ToString()).Bind(database, "status", RunStatus.Succeeded.ToString()).Bind(database, "floor", floor);
            using var reader = cmd.ExecuteReader();
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var mappingName = reader.GetString(0);
                if (!sinceByMapping.TryGetValue(mappingName, out var since))
                    continue;   // not a mapping this call asked about

                if (reader.Time(1) <= since)
                    continue;   // older than this mapping's own cutoff: the shared floor is a lower bound, not the answer

                totals[mappingName] = totals.GetValueOrDefault(mappingName) + reader.Int64(2);
            }

            return (IReadOnlyDictionary<string, long>)totals;
        });
    }

    /// <summary>
    /// Nothing to record any more: a replication is a definition, and whether it is enabled is its schedule being active and its pause (<c>scheduling.job_pause</c>), not a row of Sync's. Kept so the
    /// engine and the state channel need no change (plan 28c section 5.1: "<c>UpsertTask</c> becomes a no-op").
    /// </summary>
    public void UpsertTask(string taskName, bool enabled)
    {
    }

    /// <summary>
    /// Holds a replication, or releases it, and records that somebody did (phase 64). The pause itself (<c>scheduling.job_pause</c>, with who, when and the note), the audit entry and the notification are
    /// written in one transaction: a replication paused with nothing saying who paused it, or a notification about a pause that did not happen, is what one transaction rules out. A pause again with a new note
    /// replaces the note and the person (and writes its own audit entry, because re-pausing with a new note is an act somebody performed); a resume removes the pause row, and with it the note.
    /// </summary>
    /// <param name="note">Null and empty are stored as given: the popup lets an operator deliberately clear the note, and "cleared it" is a different act from "never wrote one".</param>
    public void SetPaused(string taskName, bool paused, string? note, string performedBy) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // Read before the write, so the notification can tell a replication being held from one that already was.
            var wasPaused = _pauses.IsPaused(taskName, PauseJob, connection, transaction);
            if (paused)
                _pauses.Pause(taskName, PauseJob, performedBy, database.Time.GetUtcNow(), note, connection, transaction);
            else
                _pauses.Clear(taskName, PauseJob, connection, transaction);

            RecordPauseEvent(connection, transaction, taskName, mappingName: null, paused ? PauseVerbs.Pause : PauseVerbs.Resume, note, performedBy);
            NotifyIfNewlyPaused(connection, transaction, taskName, paused, note, performedBy, wasPaused);
            transaction.Commit();
        });

    private void RecordPauseEvent(DbConnection connection, DbTransaction? transaction, string taskName, string? mappingName, string verb, string? note, string performedBy)
    {
        var detail = new Dictionary<string, string>();
        if (note is not null) detail["note"] = note;
        if (mappingName is not null) detail["mapping"] = mappingName;
        DbDataKit.State.AuditLog.Record(database, connection, transaction, SyncState.ServiceId, new DbDataKit.State.AuditEvent(
            "person", string.IsNullOrWhiteSpace(performedBy) ? "unknown" : performedBy, verb,
            TargetKind: PauseVerbs.TargetKind, TargetId: taskName, Detail: detail.Count == 0 ? null : JsonSerializer.Serialize(detail)));
    }

    /// <summary>The verbs of the audit entries a pause writes. The target is the replication (<see cref="TargetKind"/>).</summary>
    private static class PauseVerbs
    {
        public const string TargetKind = "replication";
        public const string Pause = "replication.pause";
        public const string Resume = "replication.resume";
        public const string Hold = "mapping.hold";
        public const string Release = "mapping.release";
        public static readonly string[] All = [Pause, Resume, Hold, Release];
    }

    /// <summary>
    /// The pause notification — phase 80. Pauses only, never resumes: a hold is something people who were not in the room need to be told about, a resume is the world going back to how it is supposed to be.
    /// On the transition, matching the run-failure producer: pausing an already-paused replication writes its own audit entry (the history is an audit trail) but is not news a second time. In the
    /// transaction that writes the pause, so all of it commits or none: a notification about a pause that did not happen is the worst of the outcomes.
    /// </summary>
    private void NotifyIfNewlyPaused(DbConnection connection, DbTransaction transaction, string taskName, bool paused, string? note, string performedBy, bool wasPaused)
    {
        if (!paused || wasPaused)
            return;

        var reason = string.IsNullOrWhiteSpace(note) ? "" : $" — {note}";
        DbDataKit.State.NotificationFeedStore.Insert(
            database, connection, transaction, SyncState.ServiceId, NotificationKinds.ReplicationPaused,
            $"Replication '{taskName}' was paused by {performedBy}{reason}", taskName, mappingName: null, runId: null);
    }

    /// <summary>Whether this replication is currently held. A task with no pause row has never been paused, which is not paused.</summary>
    public bool IsPaused(string taskName) => GetPauseState(taskName).Paused;

    /// <summary>The current hold and the note that came with it, in one read.</summary>
    public (bool Paused, string? Note) GetPauseState(string taskName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(DbDataKit.Scheduling.SchedulingModule.Name, connection, "SELECT note FROM {table:job_pause} WHERE service = $service AND owner = $owner AND job = $job;");
            cmd.Bind(database, "service", SyncState.ServiceId).Bind(database, "owner", taskName).Bind(database, "job", PauseJob);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (true, reader.IsDBNull(0) ? null : reader.GetString(0)) : (false, (string?)null);
        });

    /// <summary>
    /// Every pause and resume for a replication, most recent first — both grains phase 131 covers, interleaved: the replication grain (<see cref="SetPaused"/>, <c>MappingName</c> null) and the table-mapping
    /// grain (<see cref="SetMappingHold"/>, <c>MappingName</c> set). Read from the audit, newest first by its id, which is the only monotonic thing here: two actions in one clock tick keep their order.
    /// </summary>
    public IReadOnlyList<PauseEventRecord> GetPauseHistory(string taskName, int limit = 50)
    {
        var audit = new DbDataKit.State.AuditLog(database, SyncState.ServiceId);
        var events = new List<PauseEventRecord>();
        long? before = null;
        while (events.Count < limit)
        {
            var page = audit.Read(new DbDataKit.State.AuditQuery(BeforeId: before, TargetKind: PauseVerbs.TargetKind, TargetId: taskName, ServiceId: SyncState.ServiceId, Limit: 200));
            if (page.Count == 0) break;
            foreach (var record in page)
            {
                if (!PauseVerbs.All.Contains(record.Entry.Verb)) continue;
                string? note = null, mapping = null;
                if (record.Entry.Detail is { } detail)
                {
                    using var document = JsonDocument.Parse(detail);
                    if (document.RootElement.TryGetProperty("note", out var n)) note = n.GetString();
                    if (document.RootElement.TryGetProperty("mapping", out var m)) mapping = m.GetString();
                }

                events.Add(new PauseEventRecord(record.Id, taskName, mapping, record.Entry.Verb is PauseVerbs.Pause or PauseVerbs.Hold ? PauseActions.Paused : PauseActions.Resumed, note, record.At, record.Entry.ActorId));
                if (events.Count == limit) break;
            }

            before = page[^1].Id;
        }

        return events;
    }

    /// <summary>
    /// A table mapping's own pause history entry — the write phase 64 never needed a second grain for. History only, no current-state write: the mapping's current hold is its read hold in the position
    /// store (<see cref="ChangeWatermarkStore.SetReadHold"/>), and a second copy would be a second source of truth. Deliberately no notification (phase 131's open product question).
    /// </summary>
    public void SetMappingHold(string taskName, string mappingName, bool held, string? note, string performedBy) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            RecordPauseEvent(connection, null, taskName, mappingName, held ? PauseVerbs.Hold : PauseVerbs.Release, note, performedBy);
        });

    /// <summary>
    /// Transitions an existing Queued row (written by <c>WorkQueueStore.Enqueue</c> when the work was queued) to Running, once a worker actually claims and begins processing it. This is where the start of the
    /// work is written (phase 73), because this call is the moment the run starts; one statement sets it alongside the status, so a run cannot be Running without a start time or the reverse.
    /// </summary>
    public void BeginRun(Guid runId, int? pid) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:run_detail} SET status = $status, pid = $pid, began_at = $began WHERE run_id = $run;");
            cmd.Bind(database, "status", RunStatus.Running.ToString()).Bind(database, "pid", pid, System.Data.DbType.Int32).Bind(database, "began", database.Time.GetUtcNow()).Bind(database, "run", runId);
            cmd.ExecuteNonQuery();
        });

    /// <summary>Records which segment a run covers, as soon as the run knows (phase 203M) — a pass that reads exactly one segment inline, whose label was not known when it was enqueued.</summary>
    public void SetSegmentLabel(Guid runId, string label) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:run_detail} SET segment_label = $label WHERE run_id = $run;");
            cmd.Bind(database, "label", label).Bind(database, "run", runId);
            cmd.ExecuteNonQuery();
        });

    /// <param name="failureKind">Why it failed, when that is something the product can act on — see <see cref="RunFailureKinds"/>. Null for the ordinary case.</param>
    /// <param name="timing">Per-stage timing, for a mapping that opted into tracing. Null leaves every timing column null rather than writing zeros: "not measured" and "measured as nothing" are different answers.</param>
    /// <param name="previousWatermark">Where this pass started from, and where it left off (<paramref name="newWatermark"/>) — both null unless the run actually made a new position durable (phase 71). A failed run passes null for both.</param>
    /// <param name="newWatermark">See <paramref name="previousWatermark"/>.</param>
    /// <param name="errorDetail">The full exception for the Runs tab's failure popup. Null on success, and on a failure path that has no exception object to hand it.</param>
    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null, RunTiming? timing = null,
        string? previousWatermark = null, string? newWatermark = null, string? errorDetail = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // Read before the write, so "was this run already Failed" is answerable — see NotifyIfNewlyFailed. In one transaction with the update, because a notification that could commit without
            // the failure it announces, or a failure without it, is the pair phase 77 exists to keep together.
            var before = ReadOutcomeContext(connection, transaction, runId);
            var ended = database.Time.GetUtcNow();

            using (var cmd = database.Command(Module, connection, transaction, """
                UPDATE {table:run_detail}
                SET status = $status, ended_at = $ended, rows_read = $rows_read, rows_written = $rows_written,
                    error_summary = $error, failure_kind = $failure_kind,
                    reader_kind = $reader_kind, reader_first_row_ms = $first_row, reader_lifetime_ms = $reader_lifetime,
                    staging_kind = $staging_kind, staging_duration_ms = $staging_duration,
                    writer_kind = $writer_kind, writer_duration_ms = $writer_duration,
                    previous_position = $previous, new_position = $new, error_detail = $detail
                WHERE run_id = $run;
                """))
            {
                cmd.Bind(database, "status", status.ToString()).Bind(database, "ended", ended).Bind(database, "rows_read", rowsRead).Bind(database, "rows_written", rowsWritten)
                    .Bind(database, "error", errorSummary, System.Data.DbType.String).Bind(database, "failure_kind", failureKind, System.Data.DbType.String)
                    .Bind(database, "reader_kind", timing?.ReaderKind, System.Data.DbType.String).Bind(database, "first_row", timing?.ReaderTimeToFirstRowMs, System.Data.DbType.Int64)
                    .Bind(database, "reader_lifetime", timing?.ReaderLifetimeMs, System.Data.DbType.Int64).Bind(database, "staging_kind", timing?.StagingKind, System.Data.DbType.String)
                    .Bind(database, "staging_duration", timing?.StagingDurationMs, System.Data.DbType.Int64).Bind(database, "writer_kind", timing?.WriterKind, System.Data.DbType.String)
                    .Bind(database, "writer_duration", timing?.WriterDurationMs, System.Data.DbType.Int64).Bind(database, "previous", previousWatermark, System.Data.DbType.String)
                    .Bind(database, "new", newWatermark, System.Data.DbType.String).Bind(database, "detail", errorDetail, System.Data.DbType.String).Bind(database, "run", runId);
                cmd.ExecuteNonQuery();
            }

            // the shared view of the same run: how it ended, rows moved, and for a failure the item and the classified reason (never a secret: the summary is what Sync already shows)
            var outcome = OutcomeOf(status);
            var failed = status == RunStatus.Failed;
            using (var cmd = database.Command(connection, transaction,
                "UPDATE {table:runs.run} SET finished_at = $finished, outcome = $outcome, rows_moved = $rows, failed_item = $item, failure_kind = $kind, failure_message = $message WHERE id = $id;"))
            {
                var mapping = before?.MappingName;
                object? finished = outcome == DbDataKit.Monitoring.RunOutcome.Running ? null : ended;
                cmd.Bind(database, "finished", finished, TypeIfNull(finished, System.Data.DbType.DateTimeOffset)).Bind(database, "outcome", outcome.ToString()).Bind(database, "rows", rowsWritten)
                    .Bind(database, "item", failed && !string.IsNullOrEmpty(mapping) ? mapping : null, System.Data.DbType.String)
                    .Bind(database, "kind", failed ? failureKind ?? "Error" : null, System.Data.DbType.String)
                    .Bind(database, "message", failed ? errorSummary ?? "The run failed." : null, System.Data.DbType.String).Bind(database, "id", runId);
                cmd.ExecuteNonQuery();
            }

            NotifyIfNewlyFailed(connection, transaction, runId, status, errorSummary, failureKind, before);
            transaction.Commit();
        });

    /// <summary>How a Sync status reads in the shared run model: queued, pending and running are all "under way"; a cancelled run was stopped on purpose.</summary>
    internal static DbDataKit.Monitoring.RunOutcome OutcomeOf(RunStatus status) => status switch
    {
        RunStatus.Succeeded => DbDataKit.Monitoring.RunOutcome.Succeeded,
        RunStatus.Failed => DbDataKit.Monitoring.RunOutcome.Failed,
        RunStatus.Cancelled => DbDataKit.Monitoring.RunOutcome.Stopped,
        _ => DbDataKit.Monitoring.RunOutcome.Running,
    };

    /// <summary>What a run was before it was completed — enough to notify about it, and enough to tell a first completion from a repeat of one. Null where the run has no row.</summary>
    private (string TaskName, string? MappingName, string Status)? ReadOutcomeContext(DbConnection connection, DbTransaction transaction, Guid runId)
    {
        using var cmd = database.Command(Module, connection, transaction, "SELECT task_name, mapping_name, status FROM {table:run_detail} WHERE run_id = $run;");
        cmd.Bind(database, "run", runId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)) : null;
    }

    /// <summary>
    /// The run-failure notification — phase 77's producer, and phase 80's watermark-expiry one. Here rather than at the call sites that complete runs (a worker through the state channel, a locally-hosted
    /// runner, the supervisor's orphans, journal recovery): every one of those is a failure somebody should be told about. On the transition, not on the write: a completion can arrive twice (journal recovery
    /// replaying an entry whose original write did land), and a second announcement of the same failure is noise. A position expiry (<see cref="RunFailureKinds.PositionExpired"/>) and a missing metadata
    /// cache (<see cref="RunFailureKinds.MetadataNotCached"/>) are their own kinds, because each has its own one-click fix and a feed should find them without matching on prose.
    /// </summary>
    private void NotifyIfNewlyFailed(
        DbConnection connection, DbTransaction transaction, Guid runId, RunStatus status,
        string? errorSummary, string? failureKind, (string TaskName, string? MappingName, string Status)? before)
    {
        if (status != RunStatus.Failed || before is not { } run)
            return;

        if (string.Equals(run.Status, nameof(RunStatus.Failed), StringComparison.Ordinal))
            return;

        var subject = run.MappingName is { } mapping ? $"'{run.TaskName}' (mapping '{mapping}')" : $"'{run.TaskName}'";
        var (kind, message) = failureKind switch
        {
            RunFailureKinds.PositionExpired => (NotificationKinds.PositionExpired, $"Source position for {subject} has expired. {errorSummary}"),
            RunFailureKinds.MetadataNotCached => (NotificationKinds.MetadataNotCached, $"Cached metadata for {subject} is missing. {errorSummary}"),
            _ => (NotificationKinds.RunFailed, $"Run of {subject} failed: {errorSummary ?? "no error was recorded."}"),
        };

        DbDataKit.State.NotificationFeedStore.Insert(database, connection, transaction, SyncState.ServiceId, kind, message, run.TaskName, run.MappingName, runId);
    }

    /// <summary>
    /// A completing run's <see cref="RunKind"/>, bulk-load batch id and mapping — read directly rather than through <see cref="GetRun"/>, which does not carry the batch id (it exists to drive the Monitoring
    /// rollup, not the Runs tab). Used by <c>LocalRunnerState.CompleteRun</c> (phase 134) to decide whether this was a bulk-load segment and whether its batch might now be the one a mapping's pending
    /// initial load is waiting on.
    /// </summary>
    public (RunKind RunKind, string? BulkLoadBatchId, string? MappingName)? GetRunKindAndBatch(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT run_kind, bulk_load_batch_id, mapping_name FROM {table:run_detail} WHERE run_id = $run;");
            cmd.Bind(database, "run", runId);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? ((RunKind RunKind, string? BulkLoadBatchId, string? MappingName)?)(Enum.Parse<RunKind>(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
                : null;
        });

    public TaskRunRecord? GetRun(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {RunColumns} FROM {{table:run_detail}} WHERE run_id = $run;");
            cmd.Bind(database, "run", runId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRun(reader) : null;
        });

    /// <summary>
    /// Run history for a task, filtered and paged — see phase 104. A null <paramref name="runKind"/> mixes Primary and BulkLoad rows; scheduling due-ness checks must always pass Primary explicitly.
    /// The keyset predicate is written the expanded way on purpose (<c>enqueued_at &lt; $t OR (enqueued_at = $t AND run_id &lt; $id)</c>): SQL Server has no row-value comparison. The run id breaks a tie
    /// on the enqueue time deterministically. One row over <paramref name="limit"/> is fetched, so "is there a next page" is answered by what already came back; its key becomes the next cursor.
    /// </summary>
    public RunHistoryPage GetRunHistory(
        string taskName, RunKind? runKind = null, string? mappingName = null, RunStatus? status = null,
        RunHistoryCursor? cursor = null, int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                SELECT {{RunColumns}}
                FROM {table:run_detail}
                WHERE task_name = $task
                {{(runKind is null ? "" : "AND run_kind = $kind")}}
                {{(mappingName is null ? "" : "AND mapping_name = $mapping")}}
                {{(status is null ? "" : "AND status = $status")}}
                {{(cursor is null ? "" : "AND (enqueued_at < $cursor_time OR (enqueued_at = $cursor_time AND run_id < $cursor_run))")}}
                ORDER BY enqueued_at DESC, run_id DESC
                {{database.Limit("fetch_limit")}};
                """);
            cmd.Bind(database, "task", taskName);
            if (runKind is not null) cmd.Bind(database, "kind", runKind.Value.ToString());
            if (mappingName is not null) cmd.Bind(database, "mapping", mappingName);
            if (status is not null) cmd.Bind(database, "status", status.Value.ToString());
            if (cursor is not null)
            {
                cmd.Bind(database, "cursor_time", cursor.Value.EnqueuedAtUtc);
                cmd.Bind(database, "cursor_run", cursor.Value.RunId);
            }

            cmd.Bind(database, "fetch_limit", (long)limit + 1);

            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));

            RunHistoryCursor? next = null;
            if (results.Count > limit)
            {
                results.RemoveAt(results.Count - 1);
                var last = results[^1];
                next = new RunHistoryCursor(last.EnqueuedAtUtc!.Value, last.RunId);
            }

            return new RunHistoryPage(results, next);
        });

    /// <summary>
    /// Run history for one specific table mapping (either kind) — used by scheduling due-ness and by the SPA's per-mapping history views. Kept separate from <see cref="GetRunHistory"/>: that one is
    /// cursor-paged and this is a plain, unpaged, most-recent-first list with no keyset logic of its own to drift out of step with the general one's.
    /// </summary>
    public IReadOnlyList<TaskRunRecord> GetMappingRunHistory(string taskName, RunKind runKind, string mappingName, int limit = 50) =>
        database.Retry(() => Query("task_name = $task AND run_kind = $kind AND mapping_name = $mapping", $"ORDER BY enqueued_at DESC, run_id DESC {database.Limit("limit")}",
            c => c.Bind(database, "task", taskName).Bind(database, "kind", runKind.ToString()).Bind(database, "mapping", mappingName).Bind(database, "limit", (long)limit)));

    /// <summary>Runs still marked Running in the store — used to reconcile against live OS processes on API startup (architecture/detailed-design.md §3.1).</summary>
    public IReadOnlyList<TaskRunRecord> GetRunningRuns() =>
        database.Retry(() => Query("status = $status", "", c => c.Bind(database, "status", RunStatus.Running.ToString())));

    /// <summary>
    /// Rows that ended at or after <paramref name="sinceUtc"/> — lets RunMonitorService notice a run that completed its entire lifecycle (Queued, Running, terminal) between two polling ticks, which
    /// <see cref="GetActiveRuns"/> alone can miss entirely.
    /// </summary>
    public IReadOnlyList<TaskRunRecord> GetRecentlyEndedRuns(DateTimeOffset sinceUtc) =>
        database.Retry(() => Query("ended_at IS NOT NULL AND ended_at >= $since", "", c => c.Bind(database, "since", sinceUtc)));

    /// <summary>Rows in Queued or Running status — what RunMonitorService watches for completion once one worker process can back many concurrently-active run ids.</summary>
    public IReadOnlyList<TaskRunRecord> GetActiveRuns() =>
        database.Retry(() => Query("status IN ($queued, $running)", "", c => c.Bind(database, "queued", RunStatus.Queued.ToString()).Bind(database, "running", RunStatus.Running.ToString())));

    private IReadOnlyList<TaskRunRecord> Query(string where, string tail, Action<DbCommand> bind)
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, $"SELECT {RunColumns} FROM {{table:run_detail}} WHERE {where} {tail};");
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        var results = new List<TaskRunRecord>();
        while (reader.Read())
            results.Add(ReadRun(reader));
        return results;
    }

    /// <summary>
    /// Deletes finished runs beyond either retention cap, with their log lines and their shared run records. Returns how many runs went. Both caps are applied in one statement, and a row failing either
    /// is pruned: a row is either within retention or it is not. A run that has not finished is never pruned, whatever its age: an in-flight run is about to be written to, and deleting the row
    /// underneath its own worker would turn a slow pass into a lost one. (The module's own retention, <see cref="SyncStateModule.RunDetailDays"/> days with the run, is the shared retention job's; this is
    /// the operator's own tighter or looser window.)
    /// </summary>
    /// <param name="maxPerMapping">Counted per (task, mapping), not globally: a continuous replication of one busy table produces runs orders of magnitude faster than a quiet mapping beside it, and a global cap would let the busy one evict the quiet one's entire history.</param>
    public int PruneRuns(TimeSpan? maxAge, int? maxPerMapping) =>
        database.Retry(() =>
        {
            if (maxAge is null && maxPerMapping is null)
                return 0;

            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // The three tables are one transaction: the log has no enforced foreign key, and a crash between the statements would leave lines of a run that no longer exists, which nothing would clean up.
            // The run detail goes last, because the subquery that names the doomed runs reads it.
            int Delete(string table, string column)
            {
                using var cmd = database.Command(Module, connection, transaction, $"DELETE FROM {table} WHERE {column} IN ({DoomedRuns});");
                AddPruneParameters(cmd, maxAge, maxPerMapping);
                return cmd.ExecuteNonQuery();
            }

            Delete("{table:runs.run_log}", "run_id");
            Delete("{table:runs.run}", "id");
            var deleted = Delete("{table:sync_state.run_detail}", "run_id");
            transaction.Commit();
            return deleted;
        });

    /// <summary>The runs both caps condemn, as one subquery used by the deletes above. A null cap is written <c>$param IS NULL OR ...</c> rather than by building different SQL.</summary>
    private const string DoomedRuns = """
        SELECT run_id FROM (
            SELECT run_id, enqueued_at,
                   ROW_NUMBER() OVER (PARTITION BY task_name, mapping_name ORDER BY enqueued_at DESC) AS recency
            FROM {table:sync_state.run_detail}
            WHERE ended_at IS NOT NULL
        ) ranked
        WHERE ($cutoff IS NOT NULL AND enqueued_at < $cutoff)
           OR ($max_per_mapping IS NOT NULL AND recency > $max_per_mapping)
        """;

    /// <summary>The parameter type to state for a value: only where it is null, because a value that is there carries its own type (and a time is bound as text on SQLite).</summary>
    private static System.Data.DbType? TypeIfNull(object? value, System.Data.DbType type) => value is null ? type : null;

    private void AddPruneParameters(DbCommand cmd, TimeSpan? maxAge, int? maxPerMapping)
    {
        // typed explicitly: both are null on the ordinary path and used only in an IS NOT NULL test, from which PostgreSQL cannot infer a type
        object? cutoff = maxAge is { } age ? database.Time.GetUtcNow() - age : null;
        cmd.Bind(database, "cutoff", cutoff, TypeIfNull(cutoff, System.Data.DbType.DateTimeOffset));
        cmd.Bind(database, "max_per_mapping", maxPerMapping, System.Data.DbType.Int32);
    }

    private static TaskRunRecord ReadRun(DbDataReader reader) => new(
        reader.Uuid(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : (int)reader.Int64(2),
        Enum.Parse<RunStatus>(reader.GetString(3)),
        Enum.Parse<RunKind>(reader.GetString(4)),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.NullableTime(7),
        reader.NullableTime(8),
        reader.NullableTime(9),
        reader.NullableTime(10),
        reader.Int64(11),
        reader.Int64(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        ReadTiming(reader),
        reader.IsDBNull(22) ? null : reader.GetString(22),
        reader.IsDBNull(23) ? null : reader.GetString(23),
        reader.IsDBNull(24) ? null : reader.GetString(24));

    /// <summary>The timing columns as a record, or null when the run was never traced: null when every column is, rather than an all-null record.</summary>
    private static RunTiming? ReadTiming(DbDataReader reader)
    {
        const int first = 15;
        var traced = false;
        for (var i = first; i < first + 7; i++)
            traced |= !reader.IsDBNull(i);

        if (!traced)
            return null;

        return new RunTiming(
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.Int64(16),
            reader.IsDBNull(17) ? null : reader.Int64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.Int64(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.Int64(21));
    }
}
