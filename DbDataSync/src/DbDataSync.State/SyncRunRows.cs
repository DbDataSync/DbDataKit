using System.Data.Common;

namespace DbDataSync.State;

/// <summary>The two rows a run is made of (<c>runs.run</c> and <c>sync_state.run_detail</c>), written together.</summary>
internal static class SyncRunRows
{
    /// <summary>Writes a new queued run on a connection and transaction the caller owns: the work queue's enqueue, so a queued backlog is in run history at once.</summary>
    public static void Insert(
        StateDatabase database, DbConnection connection, DbTransaction transaction, Guid runId, string taskName, RunKind kind, string mappingName,
        string? segmentLabel, string? bulkLoadBatchId, DateTimeOffset enqueuedAt)
    {
        // the shared run: the replication is the job and the run begins when it is asked for, which is also what Sync orders and pages its history by
        new DbDataKit.State.RunStore(database, SyncState.ServiceId).Start(taskName, runId, enqueuedAt, connection, transaction);

        using var cmd = database.Command(SyncStateModule.Name, connection, transaction, """
            INSERT INTO {table:run_detail} (run_id, task_name, run_kind, mapping_name, segment_label, status, enqueued_at, rows_read, rows_written, bulk_load_batch_id)
            VALUES ($run, $task, $kind, $mapping, $segment, $status, $enqueued, 0, 0, $batch);
            """);
        cmd.Bind(database, "run", runId).Bind(database, "task", taskName).Bind(database, "kind", kind.ToString()).Bind(database, "mapping", mappingName)
            .Bind(database, "segment", segmentLabel, System.Data.DbType.String).Bind(database, "status", RunStatus.Queued.ToString()).Bind(database, "enqueued", enqueuedAt)
            .Bind(database, "batch", bulkLoadBatchId, System.Data.DbType.String);
        cmd.ExecuteNonQuery();
    }
}
