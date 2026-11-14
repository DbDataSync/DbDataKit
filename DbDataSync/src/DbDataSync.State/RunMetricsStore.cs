using System.Data.Common;

namespace DbDataSync.State;

/// <param name="StartUtc">The bucket's left edge; it covers up to the next bucket's edge.</param>
public sealed record RunMetricsBucket(DateTimeOffset StartUtc, int Runs, int Failures, long RowsWritten);

/// <param name="LastCompletedPassUtc">
/// When the most recent **successful** pass finished, and deliberately not bounded by the window: it
/// is at its most useful exactly when it is older than the window, because that is the case where a
/// replication has stopped. Null when there has never been one.
/// </param>
/// <param name="ProcessingP50Ms">
/// Processing time: <c>EndedAtUtc - StartedAtUtc</c> — the run itself, with no queue in it. Named for
/// the span it measures rather than "duration", which was the word that let phase 72 measure it from
/// the wrong column for a whole phase without anybody noticing.
/// <para>
/// Queue time (<c>StartedAtUtc - EnqueuedAtUtc</c>) is deliberately not folded back in, and is not
/// aggregated here at all: a slow source and a busy worker pool are different problems, and one number
/// could not tell them apart. It is a per-run figure in the run list.
/// </para>
/// <para>
/// Null when nothing in the window finished — a run still going has no processing time, and inventing
/// one would put a number in a card that means nothing. Null too when nothing in the window ever began.
/// </para>
/// </param>
public sealed record RunMetrics(
    string TaskName,
    RunKind? RunKind,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Runs,
    int Failures,
    long RowsRead,
    long RowsWritten,
    double? ProcessingP50Ms,
    double? ProcessingP95Ms,
    double? ProcessingMaxMs,
    DateTimeOffset? LastCompletedPassUtc,
    IReadOnlyList<RunMetricsBucket> Buckets);

/// <summary>
/// Aggregates over <c>TaskRuns</c>, which already holds everything these numbers are made of — kind,
/// status, start, end, rows read and rows written, per run. This is the query nobody had written, not
/// data nobody had recorded.
/// <para>
/// Timestamps are compared as text, which is correct here because every writer stores
/// <c>DateTimeOffset.UtcNow.ToString("O")</c> — a fixed-width ISO-8601 string at offset +00:00, where
/// lexicographic and chronological order coincide. The same assumption the work queue's own
/// availability check has always made.
/// </para>
/// </summary>
public sealed class RunMetricsStore(StateDatabase database)
{
    /// <param name="more">
    /// Runs this state does not hold, counted as if it did (plan 28c E22 P5): the passes the Runner ran for the replication (its pass records, as the run history reads them). A run of another task, of another kind than
    /// <paramref name="runKind"/>, or queued outside the window is left out by the same rules as a row of the table.
    /// </param>
    public RunMetrics Get(
        string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc, RunKind? runKind, int buckets, IReadOnlyList<TaskRunRecord>? more = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();

            var kindClause = runKind is null ? "" : " AND run_kind = $runKind";
            var totals = ReadTotals(connection, taskName, fromUtc, toUtc, runKind, kindClause);
            var processing = ReadProcessingTimes(connection, taskName, fromUtc, toUtc, runKind, kindClause);
            var lastCompleted = ReadLastCompletedPass(connection, taskName, runKind, kindClause);
            var bucketList = ReadBuckets(connection, taskName, fromUtc, toUtc, runKind, kindClause, buckets);

            var extra = (more ?? []).Where(r => r.TaskName == taskName && (runKind is null || r.RunKind == runKind)).ToList();
            if (extra.Count > 0)
            {
                var inWindow = extra.Where(r => r.EnqueuedAtUtc is { } at && at >= fromUtc && at < toUtc).ToList();
                totals = (totals.Runs + inWindow.Count, totals.Failures + inWindow.Count(r => r.Status == RunStatus.Failed),
                    totals.RowsRead + inWindow.Sum(r => r.RowsRead), totals.RowsWritten + inWindow.Sum(r => r.RowsWritten));
                processing.AddRange(inWindow.Where(r => r.StartedAtUtc is not null && r.EndedAtUtc is not null).Select(r => Math.Max(0, (r.EndedAtUtc!.Value - r.StartedAtUtc!.Value).TotalMilliseconds)));
                processing.Sort();
                var newest = extra.Where(r => r.Status == RunStatus.Succeeded && r.EndedAtUtc is not null).Select(r => r.EndedAtUtc).Max();
                if (newest is not null && (lastCompleted is null || newest > lastCompleted)) lastCompleted = newest;
                bucketList = AddToBuckets(bucketList, fromUtc, toUtc, inWindow);
            }

            return new RunMetrics(
                taskName, runKind, fromUtc, toUtc,
                totals.Runs, totals.Failures, totals.RowsRead, totals.RowsWritten,
                Percentile(processing, 0.50), Percentile(processing, 0.95),
                processing.Count == 0 ? null : processing[^1],
                lastCompleted,
                bucketList);
        });

    /// <summary>The buckets with <paramref name="runs"/> (all inside the window) added, by the same rule as <see cref="ReadBuckets"/>.</summary>
    private static IReadOnlyList<RunMetricsBucket> AddToBuckets(IReadOnlyList<RunMetricsBucket> buckets, DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<TaskRunRecord> runs)
    {
        if (buckets.Count == 0) return buckets;
        var width = (toUtc - fromUtc) / buckets.Count;
        var result = buckets.ToArray();
        foreach (var run in runs)
        {
            var index = width <= TimeSpan.Zero ? 0 : Math.Clamp((int)((run.EnqueuedAtUtc!.Value - fromUtc) / width), 0, buckets.Count - 1);
            var b = result[index];
            result[index] = b with { Runs = b.Runs + 1, Failures = b.Failures + (run.Status == RunStatus.Failed ? 1 : 0), RowsWritten = b.RowsWritten + run.RowsWritten };
        }

        return result;
    }

    private (int Runs, int Failures, long RowsRead, long RowsWritten) ReadTotals(
        DbConnection connection, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RunKind? runKind, string kindClause)
    {
        using var cmd = database.Command(SyncStateModule.Name, connection, TotalsSql(kindClause));
        Bind(cmd, taskName, fromUtc, toUtc, runKind);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, 0, 0, 0);

        // SUM over no rows is NULL in SQL, and zero is the honest reading of "nothing happened" —
        // a dash where a zero belongs is exactly the invented reading phase 15 refused.
        return (
            reader.Int32(0),
            reader.IsDBNull(1) ? 0 : reader.Int32(1),
            reader.IsDBNull(2) ? 0 : reader.Int64(2),
            reader.IsDBNull(3) ? 0 : reader.Int64(3));
    }

    /// <summary>
    /// The aggregate, as one string, so that <see cref="ExplainTotals"/> can ask the planner about the
    /// statement this actually runs rather than about a copy of it that could drift from it.
    /// </summary>
    private static string TotalsSql(string kindClause) => $$"""
        SELECT COUNT(*),
               SUM(CASE WHEN status = 'Failed' THEN 1 ELSE 0 END),
               SUM(rows_read),
               SUM(rows_written)
        FROM {table:run_detail}
        WHERE task_name = $taskName AND enqueued_at >= $from AND enqueued_at < $to{{kindClause}};
        """;

    /// <summary>
    /// How SQLite says it will answer the aggregate. TaskRuns grows without bound and a console that
    /// offers 7 days invites a large scan, so "does this use the index" is a question worth being able
    /// to ask — of the real statement, and from a test.
    /// </summary>
    public IReadOnlyList<string> ExplainTotals(
        string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc, RunKind? runKind) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(SyncStateModule.Name, connection, "EXPLAIN QUERY PLAN " + TotalsSql(runKind is null ? "" : " AND run_kind = $runKind"));
            Bind(cmd, taskName, fromUtc, toUtc, runKind);

            using var reader = cmd.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read())
                plan.Add(reader.GetString(reader.GetOrdinal("detail")));
            return (IReadOnlyList<string>)plan;
        });

    /// <summary>
    /// Every finished run's processing time, ascending, for percentiles computed in C#.
    /// <para>
    /// **<c>EndedAtUtc - StartedAtUtc</c>**, where <c>StartedAtUtc</c> now means what it says (phase
    /// 73): the moment <c>BeginRun</c> was called. Phase 72 computed the same idea from
    /// <c>ClaimedAtUtc</c>, which was then the column <c>BeginRun</c> wrote — the same instant under a
    /// name that belonged to an earlier one. Before that it measured from the enqueue, so under a
    /// backlog it reported a "duration" that was mostly queue.
    /// </para>
    /// <para>
    /// A run with no <c>StartedAtUtc</c> never began, so it contributes nothing rather than a wrong
    /// figure; that includes every row predating this migration, which is why the filter is explicit
    /// here rather than left to <c>julianday(NULL)</c>.
    /// </para>
    /// <para>
    /// SQLite has no <c>PERCENTILE_CONT</c>, so the choice was this or three <c>ORDER BY … LIMIT 1
    /// OFFSET n</c> queries. One column of one index scan beats three scans of the same index, the
    /// result is exact rather than interpolated, and at the size this is asked about — a week of runs —
    /// the list is small. Revisit if that stops being true; <c>tools/benchmarks</c> is where.
    /// </para>
    /// </summary>
    private List<double> ReadProcessingTimes(
        DbConnection connection, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RunKind? runKind, string kindClause)
    {
        // The duration is worked out here, not in SQL: date arithmetic is the one thing the three engines spell three ways, and a window's runs are few enough to read.
        using var cmd = database.Command(SyncStateModule.Name, connection, $$"""
            SELECT began_at, ended_at
            FROM {table:run_detail}
            WHERE task_name = $taskName AND enqueued_at >= $from AND enqueued_at < $to
              AND ended_at IS NOT NULL AND began_at IS NOT NULL{{kindClause}};
            """);
        Bind(cmd, taskName, fromUtc, toUtc, runKind);

        using var reader = cmd.ExecuteReader();
        var processing = new List<double>();
        while (reader.Read())
            processing.Add(Math.Max(0, (reader.Time(1) - reader.Time(0)).TotalMilliseconds));
        processing.Sort();
        return processing;
    }

    private DateTimeOffset? ReadLastCompletedPass(
        DbConnection connection, string taskName, RunKind? runKind, string kindClause)
    {
        // No window: "nothing has completed in 24 hours" is the answer this is for, and a query that could only look inside the window could never give it.
        using var cmd = database.Command(SyncStateModule.Name, connection, $$"""
            SELECT MAX(ended_at) FROM {table:run_detail}
            WHERE task_name = $taskName AND status = 'Succeeded' AND ended_at IS NOT NULL{{kindClause}};
            """);
        cmd.Bind(database, "taskName", taskName);
        if (runKind is not null)
            cmd.Bind(database, "runKind", runKind.Value.ToString());

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : DbDataKit.State.StateValues.ToTime(result);
    }

    private IReadOnlyList<RunMetricsBucket> ReadBuckets(
        DbConnection connection, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RunKind? runKind, string kindClause, int buckets)
    {
        buckets = Math.Clamp(buckets, 1, 200);
        var width = (toUtc - fromUtc) / buckets;

        var counts = new int[buckets];
        var failures = new int[buckets];
        var written = new long[buckets];

        using (var cmd = database.Command(SyncStateModule.Name, connection, $$"""
            SELECT enqueued_at, status, rows_written FROM {table:run_detail}
            WHERE task_name = $taskName AND enqueued_at >= $from AND enqueued_at < $to{{kindClause}};
            """))
        {
            Bind(cmd, taskName, fromUtc, toUtc, runKind);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var enqueuedAt = reader.Time(0);

                // The right edge is exclusive in the WHERE above, so this cannot be `buckets`; the clamp is for a zero-width window, where every run belongs to the only bucket.
                var index = Math.Clamp((int)((enqueuedAt - fromUtc) / width), 0, buckets - 1);
                counts[index]++;
                if (reader.GetString(1) == nameof(RunStatus.Failed))
                    failures[index]++;
                written[index] += reader.Int64(2);
            }
        }

        return [.. Enumerable.Range(0, buckets)
            .Select(i => new RunMetricsBucket(fromUtc + width * i, counts[i], failures[i], written[i]))];
    }

    private static double? Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
            return null;
        var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private void Bind(
        DbCommand cmd, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc, RunKind? runKind)
    {
        cmd.Bind(database, "taskName", taskName);
        cmd.Bind(database, "from", fromUtc);
        cmd.Bind(database, "to", toUtc);
        if (runKind is not null)
            cmd.Bind(database, "runKind", runKind.Value.ToString());
    }
}
