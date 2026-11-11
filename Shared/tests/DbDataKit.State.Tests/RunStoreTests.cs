using DbDataKit.Monitoring;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q12: the one run model, on every engine.</summary>
public sealed class RunStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-06T08:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([RunsModule.Module], time: _clock);

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(RunsModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_run_is_recorded_from_its_start_to_its_end_with_the_failure_classified(string engine)
    {
        var runs = new RunStore(Open(engine), "dbdatasync");

        var id = runs.Start("orders-sync");
        Assert.Equal(RunOutcome.Running, runs.Get(id)!.Run.Outcome);
        Assert.Null(runs.Get(id)!.Run.Finished);

        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(runs.Finish(id, RunOutcome.Failed, rows: 1200, failedItem: "dbo.Orders", failure: new RunFailure("Timeout", "the source did not answer")));

        var run = runs.Get(id)!;
        Assert.Equal("dbdatasync", run.Service);
        Assert.Equal(("orders-sync", id.ToString("D"), RunOutcome.Failed, 1200L, "dbo.Orders"), (run.Run.JobId, run.Run.RunId, run.Run.Outcome, run.Run.Rows, run.Run.FailedItem));
        Assert.Equal(new RunFailure("Timeout", "the source did not answer"), run.Run.Failure);
        Assert.Equal(TimeSpan.FromMinutes(3), run.Run.Duration);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_run_ends_once_and_takes_the_queues_run_id_when_it_has_one(string engine)
    {
        var runs = new RunStore(Open(engine), "dbdatabuild");
        var fromQueue = Guid.NewGuid();

        Assert.Equal(fromQueue, runs.Start("refresh", fromQueue));
        Assert.True(runs.Finish(fromQueue, RunOutcome.Succeeded));
        Assert.False(runs.Finish(fromQueue, RunOutcome.Failed));       // already ended: the first word stands
        Assert.False(runs.Finish(Guid.NewGuid(), RunOutcome.Succeeded)); // no such run
        Assert.Equal(RunOutcome.Succeeded, runs.Get(fromQueue)!.Run.Outcome);
        Assert.Throws<ArgumentException>(() => runs.Finish(fromQueue, RunOutcome.Running));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_build_run_and_a_sync_run_read_back_through_one_query_and_each_service_can_narrow_it(string engine)
    {
        var database = Open(engine);
        var sync = new RunStore(database, "dbdatasync");
        var build = new RunStore(database, "dbdatabuild");
        var a = sync.Start("orders-sync"); _clock.Advance(TimeSpan.FromMinutes(1));
        var b = build.Start("nightly-refresh"); _clock.Advance(TimeSpan.FromMinutes(1));
        var c = sync.Start("orders-sync"); _clock.Advance(TimeSpan.FromMinutes(1));
        sync.Finish(a, RunOutcome.Succeeded);
        build.Finish(b, RunOutcome.CompletedWithFailures);

        Assert.Equal([c, b, a], sync.Read().Select(r => r.Id));                                              // everyone's, newest first
        Assert.Equal([c, a], sync.Read(new RunQuery(Service: "dbdatasync")).Select(r => r.Id));
        Assert.Equal([b], sync.Read(new RunQuery(JobId: "nightly-refresh")).Select(r => r.Id));
        Assert.Equal([c], sync.Read(new RunQuery(Outcome: RunOutcome.Running)).Select(r => r.Id));
        Assert.Equal([c, b], sync.Read(new RunQuery(StartedSince: Start.AddMinutes(1))).Select(r => r.Id));
        Assert.Equal([a], sync.Read(new RunQuery(StartedBefore: Start.AddMinutes(1))).Select(r => r.Id));
        Assert.Equal(c, sync.LastRun("orders-sync")!.Id);
        Assert.Null(sync.LastRun("nightly-refresh"));    // another service's job
        Assert.Equal(b, build.LastRun("nightly-refresh")!.Id);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_runs_log_is_read_in_order_a_page_at_a_time(string engine)
    {
        var runs = new RunStore(Open(engine), "dbdatasync");
        var run = runs.Start("orders-sync");
        var other = runs.Start("customers-sync");
        for (var i = 1; i <= 5; i++) runs.Log(run, i == 5 ? "Error" : "Info", $"line {i}", item: i == 5 ? "dbo.Orders" : null);
        runs.Log(other, "Info", "somebody else's line");

        var first = runs.Logs(run, limit: 2);
        var second = runs.Logs(run, afterId: first[^1].Id, limit: 2);
        var last = runs.Logs(run, afterId: second[^1].Id, limit: 2);

        Assert.Equal(["line 1", "line 2"], first.Select(l => l.Message));
        Assert.Equal(["line 3", "line 4"], second.Select(l => l.Message));
        Assert.Equal(["line 5"], last.Select(l => l.Message));
        Assert.Equal(("Error", "dbo.Orders"), (last[0].Level, last[0].Item));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_run_recorded_in_a_transaction_stands_or_falls_with_it(string engine)
    {
        var database = Open(engine);
        var runs = new RunStore(database, "dbdatasync");
        Guid id;
        using (var connection = database.OpenConnection())
        {
            using var transaction = connection.BeginTransaction();
            id = runs.Start("orders-sync", connection: connection, transaction: transaction);
            transaction.Rollback();
        }

        Assert.Null(runs.Get(id));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Runs_left_running_by_a_process_that_went_away_are_marked_interrupted(string engine)
    {
        var database = Open(engine);
        var runs = new RunStore(database, "dbdatasync");
        var old = runs.Start("a");
        _clock.Advance(TimeSpan.FromHours(2));
        var recent = runs.Start("b");
        var otherService = new RunStore(database, "dbdatabuild").Start("c", at: Start);

        Assert.Equal(1, runs.InterruptRunning(Start.AddHours(1)));

        Assert.Equal(RunOutcome.Interrupted, runs.Get(old)!.Run.Outcome);
        Assert.Equal(RunOutcome.Running, runs.Get(recent)!.Run.Outcome);
        Assert.Equal(RunOutcome.Running, runs.Get(otherService)!.Run.Outcome);   // only this service's own runs
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_retention_job_removes_old_runs_with_their_logs_and_keeps_recent_ones(string engine)
    {
        var database = Open(engine);
        var runs = new RunStore(database, "dbdatasync");
        var old = runs.Start("a");
        runs.Log(old, "Info", "old line");
        _clock.Advance(TimeSpan.FromDays(40));
        var recent = runs.Start("a");
        runs.Log(recent, "Info", "new line");

        var result = new RetentionJob(database).Run();

        Assert.Equal(1, result.Removed["runs.run"]);
        Assert.Null(runs.Get(old));
        Assert.Empty(runs.Logs(old));
        Assert.NotNull(runs.Get(recent));
        Assert.Single(runs.Logs(recent));
    }
}
