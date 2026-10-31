namespace DbDataSync.State.Tests;

/// <summary>The upgrade to the shared work queue (plan 13, M03): a database made before it opens, its queued runs are cancelled with a reason, and the new queue works.</summary>
public sealed class QueueUpgradeTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void ADatabaseFromBeforeTheSharedQueue_Opens_WithItsQueuedRunsCancelledAndAWorkingQueue()
    {
        var path = Path.Combine(_tempDir, "state.db");
        var before = new DbDataKit.State.StateDatabase(path, new DbDataKit.State.StateSchema(Migrations.Templates[..^2]));   // without the shared queue (the one before the last), and without the account keys appended after it (U16)
        var queued = Guid.NewGuid();
        var running = Guid.NewGuid();
        using (var connection = before.OpenConnection())
        {
            Execute(connection, before, $"""
                INSERT INTO TaskRuns (RunId, TaskName, Status, RunKind, MappingName, StartedAtUtc, EnqueuedAtUtc, RowsRead, RowsWritten)
                VALUES ('{queued}', 'crm-sync', 'Queued', 'BulkLoad', 'orders', '2026-10-24T10:00:00Z', '2026-10-24T10:00:00Z', 0, 0),
                       ('{running}', 'crm-sync', 'Running', 'Primary', 'orders', '2026-10-24T10:00:00Z', '2026-10-24T10:00:00Z', 0, 0);
                """);
            Execute(connection, before, $"""
                INSERT INTO WorkQueue (TaskName, RunKind, MappingName, SegmentLabel, RunId, Status, EnqueuedAtUtc, AvailableAtUtc)
                VALUES ('crm-sync', 'BulkLoad', 'orders', 'seg-1', '{queued}', 'Pending', '2026-10-24T10:00:00Z', '2026-10-24T10:00:00Z');
                """);
        }

        var database = new StateDatabase(path);
        var runs = new TaskRunStore(database);

        var cancelled = runs.GetRun(queued)!;
        Assert.Equal(RunStatus.Cancelled, cancelled.Status);
        Assert.Contains("work queue was upgraded", cancelled.ErrorSummary);
        Assert.Equal(RunStatus.Running, runs.GetRun(running)!.Status);

        var queue = new WorkQueueStore(database);
        var runId = queue.Enqueue("crm-sync", RunKind.BulkLoad, "orders", segmentLabel: "seg-1");
        Assert.Equal(runId, queue.TryClaimNext("crm-sync", "worker-1")!.RunId);
    }

    private static void Execute(System.Data.Common.DbConnection connection, DbDataKit.State.StateDatabase database, string sql)
    {
        using var cmd = database.Command(connection, sql);
        cmd.ExecuteNonQuery();
    }
}
