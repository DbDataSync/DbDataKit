using System.Security.Cryptography;
using DbDataKit.Monitoring;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit.Testing.Tests;

/// <summary>Plan 28a R03c: the simulator, each injector and each reporter, and the example of plan 28b section 10 as a test of the package.</summary>
public sealed class RunSimulatorTests
{
    private static readonly DateOnly Day = new(2026, 11, 7);

    private static (RunSimulator<DailyDrop> Sim, Drop Drop) DailyDropSim(bool archiveThrows = false, int rows = 1_240)
    {
        var drop = new Drop();
        drop.Files[Drop.Path(Day)] = Drop.Csv(rows);
        return (new RunSimulator<DailyDrop>(input: new DailyDropInput(Day, archiveThrows), slots: drop.Opener), drop);
    }

    // ---- the example of plan 28b section 10

    [Fact]
    public async Task The_plan_example_a_crash_after_the_download_body_is_recovered_on_the_retry()
    {
        var (sim, _) = DailyDropSim();
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Download, times: 1);                    // the in-doubt case: typed, so a renamed step breaks the test at compile time
        await sim.RunAsync();                                                    // first attempt fails
        await sim.RunAsync();                                                    // retry: the recovery runs
        Assert.Equal(1, sim.Executions(DailyDropSteps.Download));                // the body ran once
        Assert.Equal(StepStatus.Recovered, sim.StatusOf(DailyDropSteps.Download));
        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(1_240, sim.Counted(DailyDropCounters.Rows));
    }

    // ---- the injectors

    [Fact]
    public async Task FailAfterBody_crashes_between_the_body_and_its_commit_and_the_run_stays_interrupted()
    {
        var (sim, drop) = DailyDropSim();
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Download);

        var first = await sim.RunAsync();

        Assert.True(first.Crashed);
        Assert.Null(first.Result);
        Assert.Equal(RunOutcome.Interrupted, sim.Outcome);
        Assert.Equal(1, drop.Deletes);                                           // the body's effect happened
        Assert.Equal(StepState.InDoubt, sim.StoredSteps().Single().State);       // what the runner does about a vanished process
        Assert.Null(sim.StatusOf(DailyDropSteps.Download));                      // the attempt never finished the step
    }

    [Fact]
    public async Task FailAfterBody_of_a_value_step_leaves_it_in_doubt_and_its_check_finds_the_receipt()
    {
        var (sim, drop) = DailyDropSim();
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Archive);

        await sim.RunAsync();
        var retry = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(1, sim.Executions(DailyDropSteps.Archive));
        Assert.Equal(1, drop.Archives);
        Assert.Equal(StepStatus.Recovered, sim.StatusOf(DailyDropSteps.Archive));
        Assert.Equal(StepStatus.Reused, sim.StatusOf(DailyDropSteps.Download));  // completed in the first attempt
    }

    [Fact]
    public async Task An_injection_fires_the_given_number_of_times_in_all_and_then_stays_quiet()
    {
        var (sim, _) = DailyDropSim();
        using var _ = sim;
        sim.FailBeforeBody(DailyDropSteps.Archive, times: 2);                    // the check finds nothing, so each retry reaches the body again

        var results = new[] { await sim.RunAsync(), await sim.RunAsync(), await sim.RunAsync() };

        Assert.Equal([true, true, false], results.Select(r => r.Crashed).ToArray());
        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(3, sim.Attempts.Count);
    }

    [Fact]
    public async Task FailAfterBody_with_a_key_hits_only_that_key()
    {
        var (sim, _) = DailyDropSim();
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Archive, key: "2020-01-01");           // not this run's step

        var first = await sim.RunAsync();

        Assert.False(first.Crashed);
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);
    }

    [Fact]
    public async Task FailBeforeBody_leaves_the_step_in_doubt_with_the_body_not_run_and_the_check_finds_nothing()
    {
        var (sim, drop) = DailyDropSim();
        using var _ = sim;
        sim.FailBeforeBody(DailyDropSteps.Archive);

        var first = await sim.RunAsync();
        Assert.True(first.Crashed);
        Assert.Equal(0, sim.Executions(DailyDropSteps.Archive));                 // it never started its body
        Assert.Equal(0, drop.Archives);

        var retry = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(1, sim.Executions(DailyDropSteps.Archive));                 // the check found nothing, so the body ran
        Assert.Equal(StepStatus.Ran, sim.StatusOf(DailyDropSteps.Archive));
    }

    [Fact]
    public async Task FailMidStream_leaves_an_incomplete_payload_that_is_not_adopted()
    {
        var (sim, drop) = DailyDropSim();
        using var _ = sim;
        sim.FailMidStream(DailyDropSteps.Download);

        var first = await sim.RunAsync();
        Assert.True(first.Crashed);
        Assert.Empty(sim.Payloads(DailyDropSteps.Download));                     // nothing was stored
        drop.Files[Drop.Path(Day)] = Drop.Csv(1_240);                            // the injected crash is after the body, which had deleted the file: put it back

        var retry = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, sim.Executions(DailyDropSteps.Download));                // the recovery (Repeatable) ran the body again
        Assert.Equal(StepStatus.Ran, sim.StatusOf(DailyDropSteps.Download));
    }

    [Fact]
    public async Task CancelAt_a_step_stops_the_run_and_the_retry_runs_the_step()
    {
        var (sim, _) = DailyDropSim();
        using var _ = sim;
        sim.CancelAt(DailyDropSteps.Download);

        var first = await sim.RunAsync();

        Assert.False(first.Crashed);
        Assert.Equal(RunOutcome.Stopped, sim.Outcome);
        Assert.Equal(0, sim.Executions(DailyDropSteps.Download));
        Assert.Equal(StepState.Started, sim.StoredSteps().Single().State);      // cancelled in the step: in doubt next time

        var retry = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(StepStatus.Ran, sim.StatusOf(DailyDropSteps.Download));
    }

    [Fact]
    public async Task CancelAt_a_unit_stops_the_run_leaves_the_unit_started_and_the_retry_skips_the_finished_ones()
    {
        using var sim = new RunSimulator<Fanout>(maxParallelism: 1);
        sim.CancelAt(FanoutUnits.Customer, key: "b");

        await sim.RunAsync();

        Assert.Equal(RunOutcome.Stopped, sim.Outcome);
        var units = sim.Units(FanoutUnits.Customer);
        Assert.Equal(["a"], units.Keys(UnitState.Completed));
        Assert.Equal(["b"], units.Keys(UnitState.Started));
        Assert.Null(units.Of("c"));                                              // never begun

        var retry = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(3, sim.Units(FanoutUnits.Customer).Completed);
        Assert.Equal("reused", retry.Result!.Units.Single(u => u.Key == "a").State);
        Assert.Equal(30, sim.Counted(FanoutCounters.Rows));                      // a once, b and c once each: no unit ran twice
    }

    [Fact]
    public async Task ConflictOnSave_stops_the_run_at_that_save_and_leaves_what_the_earlier_one_stored()
    {
        using var sim = new RunSimulator<Fanout>(maxParallelism: 1);
        sim.ConflictOnSave(2);

        await sim.RunAsync();

        Assert.Equal(RunOutcome.Stopped, sim.Outcome);
        var stored = sim.Progress<FanoutProgress>();
        Assert.Equal(1, stored.Position);                                        // the first save went through
        Assert.Null(stored.Note);                                                // the second wrote nothing

        var retry = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, sim.Progress<FanoutProgress>().Position);
        Assert.Equal("second", sim.Progress<FanoutProgress>().Note);
    }

    [Fact]
    public async Task FailUnit_fails_that_unit_the_others_go_on_and_the_retry_runs_only_it()
    {
        using var sim = new RunSimulator<Fanout>(maxParallelism: 1);
        sim.FailUnit(FanoutUnits.Customer, "b");

        var first = await sim.RunAsync();

        Assert.Equal(RunOutcome.CompletedWithFailures, first.Outcome);
        var units = sim.Units(FanoutUnits.Customer);
        Assert.Equal((3, 2, 1), (units.Total, units.Completed, units.Failed));
        Assert.Equal(["b"], units.Keys(UnitState.Failed));
        Assert.Equal("b", first.Result!.End.FirstFailedItem);
        Assert.Contains("Injected failure", units.Of("b")!.Error, StringComparison.Ordinal);
        Assert.Equal(20, sim.Counted(FanoutCounters.Rows));

        var retry = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(3, sim.Units(FanoutUnits.Customer).Completed);
        Assert.Equal(30, sim.Counted(FanoutCounters.Rows));
        Assert.Equal(10, sim.CountedIn(FanoutCounters.Rows, 2));                 // only b ran in the retry
    }

    [Fact]
    public async Task FailUnit_by_item_uses_the_units_key_function()
    {
        using var sim = new RunSimulator<Fanout>(maxParallelism: 1);
        sim.FailUnit(FanoutUnits.Customer, "c");

        await sim.RunAsync();

        Assert.Equal(["c"], sim.Units(FanoutUnits.Customer).Keys(UnitState.Failed));
    }

    // ---- the reporters

    [Fact]
    public async Task StatusOf_says_ran_reused_recovered_and_failed()
    {
        var (sim, _) = DailyDropSim();
        using var _ = sim;
        await sim.RunAsync();
        Assert.Equal(StepStatus.Ran, sim.StatusOf(DailyDropSteps.Download));
        Assert.Equal(StepStatus.Ran, sim.StatusOf(DailyDropSteps.Archive));

        await sim.RunAsync();                                                    // the chain is complete: every step answers from memory
        Assert.Equal(StepStatus.Reused, sim.StatusOf(DailyDropSteps.Download));
        Assert.Equal(StepStatus.Reused, sim.StatusOf(DailyDropSteps.Archive));
        Assert.Equal(StepStatus.Ran, sim.StatusOf(DailyDropSteps.Download, attempt: 1));      // and the first attempt is still what it was
        Assert.Equal(1, sim.Executions(DailyDropSteps.Download));

        var (failing, _) = DailyDropSim(archiveThrows: true);
        using (failing)
        {
            await failing.RunAsync();
            Assert.Equal(StepStatus.Failed, failing.StatusOf(DailyDropSteps.Archive));
            Assert.Equal(RunOutcome.Failed, failing.Outcome);
            Assert.Contains("archive is full", failing.Failure!.Message, StringComparison.Ordinal);
            Assert.Equal(StepState.Failed, failing.StoredSteps().Single(s => s.Name == "archive").State);
        }
    }

    [Fact]
    public async Task Recovered_is_reported_for_a_check_that_found_the_result()
    {
        var (sim, _) = DailyDropSim();
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Archive);
        await sim.RunAsync();
        await sim.RunAsync();

        var report = Assert.Single(sim.StepReports(DailyDropSteps.Archive));
        Assert.Equal((StepStatus.Recovered, "2026-11-07"), (report.Status, report.Key));
    }

    [Fact]
    public async Task Counted_totals_all_attempts_and_CountedIn_asks_for_one()
    {
        var (sim, _) = DailyDropSim(rows: 250);
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Archive);                               // the first attempt counts all 250 rows, then crashes

        await sim.RunAsync();
        await sim.RunAsync();                                                    // the retry loads the stored bytes again and counts them again

        Assert.Equal(250, sim.CountedIn(DailyDropCounters.Rows, 1));
        Assert.Equal(250, sim.CountedIn(DailyDropCounters.Rows, 2));
        Assert.Equal(500, sim.Counted(DailyDropCounters.Rows));
        Assert.Equal(250, sim.Attempts[0].Counters["rows"]);
        Assert.Equal(250, sim.Attempts[1].Result!.Counters["rows"]);
    }

    [Fact]
    public async Task Progress_is_the_stored_progress_after_each_attempt()
    {
        using var sim = new RunSimulator<Fanout>(maxParallelism: 1);
        sim.ConflictOnSave(2);
        await sim.RunAsync();
        await sim.RunAsync();

        Assert.Equal(1, sim.Progress<FanoutProgress>(1).Position);
        Assert.Null(sim.Progress<FanoutProgress>(1).Note);
        Assert.Equal(2, sim.Progress<FanoutProgress>(2).Position);
        Assert.Equal(2, sim.Progress<FanoutProgress>().Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Progress<FanoutProgress>(3));
    }

    [Fact]
    public async Task Units_summarises_the_stored_units_by_state()
    {
        using var sim = new RunSimulator<Fanout>(maxParallelism: 1);
        await sim.RunAsync();

        var units = sim.Units(FanoutUnits.Customer);

        Assert.Equal("customer", units.Name);
        Assert.Equal((3, 3, 0, 0, 0), (units.Total, units.Completed, units.Failed, units.Skipped, units.Started));
        Assert.Equal(["a", "b", "c"], units.Keys(UnitState.Completed));
        Assert.Equal(10, units.Of("a")!.Counts["rows"]);                         // counted against the unit
    }

    [Fact]
    public async Task Payloads_of_a_stream_step_are_readable_and_their_hash_matches()
    {
        var (sim, _) = DailyDropSim(rows: 50);
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Download);
        await sim.RunAsync();
        await sim.RunAsync();

        var payload = Assert.Single(sim.Payloads(DailyDropSteps.Download));

        var expected = Drop.Csv(50);
        Assert.Equal("2026-11-07", payload.Key);
        Assert.Equal("text/csv", payload.MediaType);
        Assert.Equal(expected.Length, payload.Size);
        Assert.Equal(expected, payload.ReadAllBytes());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), payload.Sha256);   // the adopted payload is the one the dead attempt wrote
        Assert.Empty(sim.Payloads(DailyDropSteps.Archive));                      // a value step has none
    }

    [Fact]
    public async Task The_run_log_has_the_lines_of_every_attempt()
    {
        var (sim, _) = DailyDropSim();
        using var _ = sim;
        sim.FailAfterBody(DailyDropSteps.Archive);
        await sim.RunAsync();
        await sim.RunAsync();

        var log = sim.Log;

        Assert.Contains(log, l => l.Message.StartsWith("archived ", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Message.StartsWith("Run ended Succeeded", StringComparison.Ordinal));
        Assert.All(log, l => Assert.Contains(l.RunId, sim.Attempts.Select(a => a.RunId)));
        Assert.NotEqual(sim.Attempts[0].RunId, sim.Attempts[1].RunId);
    }

    [Fact]
    public void Reporting_before_a_run_says_to_run_first()
    {
        using var sim = new RunSimulator<Fanout>();

        Assert.Throws<InvalidOperationException>(() => sim.Outcome);
        Assert.Empty(sim.Attempts);
        Assert.Equal("fanout", sim.TaskName);
    }
}
