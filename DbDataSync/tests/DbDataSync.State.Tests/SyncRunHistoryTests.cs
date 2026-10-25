using DbDataKit.Monitoring;

namespace DbDataSync.State.Tests;

/// <summary>Sync's runs as the shared monitoring model (plan 13, M06): the last ordinary pass of a replication, mapped to a shared run, with the shared health rules over it.</summary>
public sealed class SyncRunHistoryTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;
    private readonly WorkQueueStore _queue;
    private readonly TaskRunStore _runs;

    public SyncRunHistoryTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _queue = new WorkQueueStore(database);
        _runs = new TaskRunStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private Guid Run(string mapping, RunStatus? end, RunKind kind = RunKind.Primary, string? failureKind = null, string? error = null)
    {
        var id = _queue.Enqueue("crm", kind, mapping);
        _runs.BeginRun(id, 1);
        if (end is { } status) _runs.CompleteRun(id, status, 10, 8, error, failureKind);
        // the next enqueue of the same mapping needs this one out of the queue
        var item = _queue.TryClaimNext("crm", "w", runId: id);
        if (item is not null) _queue.MarkDone(item.Id);
        return id;
    }

    [Fact]
    public async Task A_replication_that_never_ran_has_no_last_run()
    {
        Assert.Null(await new SyncRunHistory(_runs).LastRunAsync("crm"));
    }

    [Fact]
    public async Task The_last_run_is_the_latest_ordinary_pass_and_reloads_do_not_count()
    {
        Run("orders", RunStatus.Succeeded, RunKind.BulkLoad);
        Assert.Null(await new SyncRunHistory(_runs).LastRunAsync("crm"));

        Run("orders", RunStatus.Succeeded);

        var last = await new SyncRunHistory(_runs).LastRunAsync("crm");

        Assert.NotNull(last);
        Assert.Equal("crm", last!.JobId);
        Assert.Equal(RunOutcome.Succeeded, last.Outcome);
        Assert.Equal(8, last.Rows);
        Assert.NotNull(last.Finished);
    }

    [Fact]
    public async Task A_failed_run_carries_the_mapping_and_a_classified_failure()
    {
        Run("orders", RunStatus.Failed, failureKind: RunFailureKinds.PositionExpired, error: "history discarded");

        var last = (await new SyncRunHistory(_runs).LastRunAsync("crm"))!;

        Assert.Equal(RunOutcome.Failed, last.Outcome);
        Assert.Equal("orders", last.FailedItem);
        Assert.Equal(new RunFailure(RunFailureKinds.PositionExpired, "history discarded"), last.Failure);
    }

    [Fact]
    public async Task A_run_still_going_is_running()
    {
        Run("orders", null);
        Assert.Equal(RunOutcome.Running, (await new SyncRunHistory(_runs).LastRunAsync("crm"))!.Outcome);
    }

    [Fact]
    public async Task A_cancelled_run_is_stopped()
    {
        Run("orders", RunStatus.Cancelled);
        Assert.Equal(RunOutcome.Stopped, (await new SyncRunHistory(_runs).LastRunAsync("crm"))!.Outcome);
    }

    [Fact]
    public async Task The_shared_rules_run_over_it_including_lag_from_the_supplied_source()
    {
        Run("orders", RunStatus.Succeeded);
        var history = new SyncRunHistory(_runs, job => job == "crm" ? new LagReading(TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow) : null);

        var health = await new HealthEvaluator().EvaluateAsync([new JobExpectation("crm", ExpectedEvery: TimeSpan.FromHours(1), MaxLag: TimeSpan.FromMinutes(10))], history);

        Assert.Equal([HealthRule.Lagging], health.Single().Findings.Select(f => f.Rule));
    }
}
