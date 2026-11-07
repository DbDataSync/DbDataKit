using System.Data.Common;

namespace DbDataSync.State;

/// <summary>Where a bulk load is, taken as a whole rather than one segment at a time.</summary>
public enum BulkLoadState
{
    /// <summary>At least one segment is still queued or running.</summary>
    Running,

    /// <summary>Every segment finished, and every one that finished succeeded.</summary>
    Completed,

    /// <summary>Every segment finished, and at least one failed.</summary>
    CompletedWithFailures,
}

/// <param name="SegmentCount">How many segments this bulk load was split into, recorded at enqueue —
/// not <c>COUNT</c> of the runs, which can undercount if an equivalent segment was already in flight
/// when this batch was queued.</param>
/// <param name="RowsCopied">Rows written to the target across the segments that have finished. Segment
/// runs write their totals only on completion, so this steps up per segment rather than streaming.</param>
/// <param name="EstimatedRows">One catalog-statistics estimate for the whole source table, read once
/// at enqueue. Null when there was no table to estimate (a query source) or the driver has no catalog
/// to read.</param>
/// <param name="EstimateCaveat">Why the estimate should be read loosely, if it should — e.g.
/// <c>"ignores row filter"</c> when the mapping narrows its source but the estimate counts the whole
/// table. Null when the estimate is clean.</param>
public sealed record BulkLoadBatchProgress(
    string BatchId,
    string MappingName,
    DateTimeOffset CreatedAtUtc,
    int SegmentCount,
    int SegmentsSucceeded,
    int SegmentsFailed,
    int SegmentsRunning,
    long RowsRead,
    long RowsCopied,
    long? EstimatedRows,
    string? EstimateCaveat,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastActivityUtc)
{
    public BulkLoadState State =>
        SegmentsSucceeded + SegmentsFailed < SegmentCount ? BulkLoadState.Running
        : SegmentsFailed > 0 ? BulkLoadState.CompletedWithFailures
        : BulkLoadState.Completed;
}

/// <summary>
/// A position in <see cref="BulkLoadBatchStore.GetHistory"/>'s keyset — mirrors <c>RunHistoryCursor</c>
/// (phase 104), see phase 139.
/// <para>
/// <c>(CreatedAtUtc, BatchId)</c>, not an offset, for the same reason <c>RunHistoryCursor</c> isn't
/// one: the history is append-heavy, so an offset would shift under a page as new batches land.
/// <c>BatchId</c> is a <c>Guid.NewGuid().ToString("N")</c> stamped once at enqueue — not a meaningful
/// order, only a deterministic (ordinary string comparison) tiebreak for two batches created in the
/// same instant, the same role <c>RunId</c> plays for <c>RunHistoryCursor</c> despite being just as
/// arbitrary an order there.
/// </para>
/// </summary>
public readonly record struct BulkLoadHistoryCursor(DateTimeOffset CreatedAtUtc, string BatchId);

/// <summary>
/// One page of <see cref="BulkLoadBatchStore.GetHistory"/>, and where the next one starts — mirrors
/// <c>RunHistoryPage</c>. Unlike that type this does not implement <c>IReadOnlyList</c> itself: every
/// caller of <c>GetHistory</c> is the new history endpoint, so there is no pre-paging call site to keep
/// compiling unaware a next page exists.
/// </summary>
public sealed record BulkLoadHistoryPage(IReadOnlyList<BulkLoadBatchProgress> Batches, BulkLoadHistoryCursor? NextCursor);

/// <summary>
/// The batch view over a bulk load's segment runs. <c>BulkLoadBatches</c> holds what a segment doesn't
/// carry — the planned segment count and one whole-table row estimate — and the per-segment progress
/// is aggregated from <c>TaskRuns</c>, which already records every segment's status and final row
/// counts. This is the query nobody had written, the same way <see cref="RunMetricsStore"/> was.
/// </summary>
public sealed class BulkLoadBatchStore(StateDatabase database)
{
    private const string Module = SyncStateModule.Name;

    /// <summary>
    /// A batch with the progress of its segments, which are the runs whose detail names the batch: one row for each batch, counted from the runs. The same select serves one batch, the recent ones and the
    /// history page, so the three can never count differently.
    /// </summary>
    private const string Select = """
        SELECT b.batch_id, b.mapping_name, b.created_at, b.segment_count,
               b.estimated_rows, b.estimate_caveat,
               SUM(CASE WHEN r.status = 'Succeeded' THEN 1 ELSE 0 END) AS segments_succeeded,
               SUM(CASE WHEN r.status = 'Failed'    THEN 1 ELSE 0 END) AS segments_failed,
               SUM(CASE WHEN r.status = 'Running'   THEN 1 ELSE 0 END) AS segments_running,
               SUM(r.rows_read)     AS rows_read,
               SUM(r.rows_written)  AS rows_copied,
               MIN(r.began_at)      AS first_began_at,
               MAX(r.ended_at)      AS last_ended_at
        FROM {table:bulk_load_batch} b
        LEFT JOIN {table:run_detail} r ON r.bulk_load_batch_id = b.batch_id
        """;

    private const string GroupBy = """
        GROUP BY b.batch_id, b.mapping_name, b.created_at, b.segment_count, b.estimated_rows, b.estimate_caveat
        """;

    public void CreateBatch(
        string batchId, string taskName, string mappingName, int segmentCount,
        long? estimatedRows, string? estimateCaveat) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, """
                INSERT INTO {table:bulk_load_batch}
                    (batch_id, task_name, mapping_name, created_at, segment_count, estimated_rows, estimate_caveat)
                VALUES ($batch, $task, $mapping, $created, $segments, $estimated, $caveat);
                """);
            cmd.Bind(database, "batch", batchId);
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "created", database.Time.GetUtcNow());
            cmd.Bind(database, "segments", (long)segmentCount);
            cmd.Bind(database, "estimated", estimatedRows, System.Data.DbType.Int64);
            cmd.Bind(database, "caveat", estimateCaveat, System.Data.DbType.String);
            cmd.ExecuteNonQuery();
        });

    public void DeleteBatch(string batchId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:bulk_load_batch} WHERE batch_id = $batch;");
            cmd.Bind(database, "batch", batchId);
            cmd.ExecuteNonQuery();
        });

    public BulkLoadBatchProgress? GetBatch(string batchId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"{Select} WHERE b.batch_id = $batch {GroupBy};");
            cmd.Bind(database, "batch", batchId);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadBatch(reader) : null;
        });

    public IReadOnlyList<BulkLoadBatchProgress> GetRecentBulkLoads(string taskName, int limit) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"{Select} WHERE b.task_name = $task {GroupBy} ORDER BY b.created_at DESC {database.Limit("limit")};");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "limit", (long)limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<BulkLoadBatchProgress>();
            while (reader.Read())
                results.Add(ReadBatch(reader));
            return (IReadOnlyList<BulkLoadBatchProgress>)results;
        });

    /// <summary>
    /// A page of a replication's bulk-load history, newest first, keyset-paged on (created_at, batch id): the predicate is written the expanded way because SQL Server has no row-value comparison.
    /// One row over <paramref name="limit"/> is fetched, so "is there a next page" is answered by what already came back.
    /// </summary>
    public BulkLoadHistoryPage GetHistory(
        string taskName, string? mappingName = null, BulkLoadHistoryCursor? cursor = null, int limit = 20) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"""
                {Select}
                WHERE b.task_name = $task
                {(mappingName is null ? "" : "AND b.mapping_name = $mapping")}
                {(cursor is null ? "" : "AND (b.created_at < $cursor_time OR (b.created_at = $cursor_time AND b.batch_id < $cursor_batch))")}
                {GroupBy}
                ORDER BY b.created_at DESC, b.batch_id DESC
                {database.Limit("fetch_limit")};
                """);
            cmd.Bind(database, "task", taskName);
            if (mappingName is not null)
                cmd.Bind(database, "mapping", mappingName);
            if (cursor is not null)
            {
                cmd.Bind(database, "cursor_time", cursor.Value.CreatedAtUtc);
                cmd.Bind(database, "cursor_batch", cursor.Value.BatchId);
            }

            // one more than asked for
            cmd.Bind(database, "fetch_limit", (long)limit + 1);

            using var reader = cmd.ExecuteReader();
            var results = new List<BulkLoadBatchProgress>();
            while (reader.Read())
                results.Add(ReadBatch(reader));

            BulkLoadHistoryCursor? next = null;
            if (results.Count > limit)
            {
                results.RemoveAt(results.Count - 1);
                var last = results[^1];
                next = new BulkLoadHistoryCursor(last.CreatedAtUtc, last.BatchId);
            }

            return new BulkLoadHistoryPage(results, next);
        });

    private static BulkLoadBatchProgress ReadBatch(DbDataReader reader) => new(
        BatchId: reader.GetString(0),
        MappingName: reader.GetString(1),
        CreatedAtUtc: reader.Time(2),
        SegmentCount: reader.Int32(3),
        SegmentsSucceeded: reader.IsDBNull(6) ? 0 : reader.Int32(6),
        SegmentsFailed: reader.IsDBNull(7) ? 0 : reader.Int32(7),
        SegmentsRunning: reader.IsDBNull(8) ? 0 : reader.Int32(8),
        RowsRead: reader.IsDBNull(9) ? 0 : reader.Int64(9),
        RowsCopied: reader.IsDBNull(10) ? 0 : reader.Int64(10),
        EstimatedRows: reader.NullableInt64(4),
        EstimateCaveat: reader.IsDBNull(5) ? null : reader.GetString(5),
        StartedAtUtc: reader.NullableTime(11),
        LastActivityUtc: reader.NullableTime(12));
}
