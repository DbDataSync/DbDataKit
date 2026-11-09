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

    private sealed class FakeRunnerHistory(bool owns, RunRecord? last, bool down = false) : IRunnerRunHistory
    {
        public int Asked { get; private set; }

        public bool Owns(string jobId) => owns;

        public Task<RunRecord?> LastRunAsync(string jobId, CancellationToken ct)
        {
            Asked++;
            return down ? throw new InvalidOperationException("The runner could not be reached.") : Task.FromResult(last);
        }
    }

    private static RunRecord OnRunner(DateTimeOffset started, RunOutcome outcome = RunOutcome.Succeeded) =>
        new("crm", Guid.NewGuid().ToString(), started, started.AddSeconds(2), outcome, 3, outcome == RunOutcome.Failed ? "orders" : null, outcome == RunOutcome.Failed ? new RunFailure(RunFailureKinds.PositionExpired, "history discarded") : null);

    [Fact]
    public async Task For_a_replication_a_runner_owns_the_last_run_is_the_later_of_this_servers_and_the_runners()
    {
        var own = Run("orders", RunStatus.Succeeded);
        var ownRecord = (await new SyncRunHistory(_runs).LastRunAsync("crm"))!;

        var later = OnRunner(ownRecord.Started.AddMinutes(5), RunOutcome.Failed);
        var earlier = OnRunner(ownRecord.Started.AddMinutes(-5));

        // the runner's run is later: it is the last, with its mapping and the failure Sync classified
        var last = (await new SyncRunHistory(_runs, runner: new FakeRunnerHistory(true, later)).LastRunAsync("crm"))!;
        Assert.Equal((later.RunId, RunOutcome.Failed, "orders"), (last.RunId, last.Outcome, last.FailedItem));
        Assert.Equal(RunFailureKinds.PositionExpired, last.Failure!.Kind);
        // from before the move, this server's own is the last
        Assert.Equal(own.ToString(), (await new SyncRunHistory(_runs, runner: new FakeRunnerHistory(true, earlier)).LastRunAsync("crm"))!.RunId);
        // nothing there yet: what this server has
        Assert.Equal(own.ToString(), (await new SyncRunHistory(_runs, runner: new FakeRunnerHistory(true, null)).LastRunAsync("crm"))!.RunId);
        // a replication this server owns never asks the runner
        var asked = new FakeRunnerHistory(false, later);
        Assert.Equal(own.ToString(), (await new SyncRunHistory(_runs, runner: asked).LastRunAsync("crm"))!.RunId);
        Assert.Equal(0, asked.Asked);
    }

    [Fact]
    public async Task A_runner_that_cannot_be_asked_is_an_error_and_not_the_older_run_of_this_server()
    {
        Run("orders", RunStatus.Succeeded);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SyncRunHistory(_runs, runner: new FakeRunnerHistory(true, null, down: true)).LastRunAsync("crm"));

        Assert.Contains("could not be reached", ex.Message);
    }
}
