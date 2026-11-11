using DbDataKit.Monitoring;
using Xunit;

namespace DbDataKit.Monitoring.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public class HealthTests
{
    private const string Job = "refresh:nightly";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private readonly TestClock _clock = new();

    private HealthEvaluator Evaluator => new(_clock);

    private RunRecord Run(TimeSpan startedAgo, TimeSpan? took = null, RunOutcome outcome = RunOutcome.Succeeded, string? failedItem = null, RunFailure? failure = null)
    {
        var started = _clock.Now - startedAgo;
        return new RunRecord(Job, Guid.NewGuid().ToString("N"), started, outcome == RunOutcome.Running ? null : started + (took ?? TimeSpan.FromMinutes(1)), outcome, FailedItem: failedItem, Failure: failure);
    }

    // ---- overdue ----

    [Fact]
    public void A_job_with_nothing_expected_of_it_is_never_unhealthy_for_being_late()
    {
        Assert.True(Evaluator.Evaluate(new JobExpectation(Job), null).Healthy);
        Assert.True(Evaluator.Evaluate(new JobExpectation(Job), Run(TimeSpan.FromDays(30))).Healthy);
    }

    [Fact]
    public void A_job_that_never_ran_is_overdue()
    {
        var health = Evaluator.Evaluate(new JobExpectation(Job, ExpectedEvery: Hour), null);
        var finding = Assert.Single(health.Findings);
        Assert.Equal(HealthRule.Overdue, finding.Rule);
        Assert.Contains("has never run", finding.Message);
        Assert.Contains("60 min", finding.Message);
    }

    [Fact]
    public void A_run_exactly_as_old_as_the_interval_is_on_time_and_one_second_older_is_overdue()
    {
        var expectation = new JobExpectation(Job, ExpectedEvery: Hour);
        Assert.True(Evaluator.Evaluate(expectation, Run(Hour)).Healthy);
        var late = Evaluator.Evaluate(expectation, Run(Hour + TimeSpan.FromSeconds(1)));
        Assert.True(late.Has(HealthRule.Overdue));
        Assert.Contains("began 60 min ago", late.Findings[0].Message);
    }

    [Fact]
    public void A_run_still_going_makes_the_job_overdue_only_once_it_has_been_going_that_long()
    {
        var expectation = new JobExpectation(Job, ExpectedEvery: Hour);
        Assert.True(Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(30), outcome: RunOutcome.Running)).Healthy);
        Assert.True(Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(61), outcome: RunOutcome.Running)).Has(HealthRule.Overdue));
    }

    // ---- slow ----

    [Fact]
    public void A_finished_run_longer_than_the_interval_is_slow_and_a_running_one_is_not_yet()
    {
        var expectation = new JobExpectation(Job, ExpectedEvery: Hour);
        var slow = Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(10), took: TimeSpan.FromMinutes(90)));
        Assert.True(slow.Has(HealthRule.Slow));
        Assert.Contains("took 90 min", slow.Findings.Single(f => f.Rule == HealthRule.Slow).Message);

        Assert.False(Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(10), took: Hour)).Has(HealthRule.Slow));                 // exactly the interval is not slow
        Assert.False(Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(10), outcome: RunOutcome.Running)).Has(HealthRule.Slow));
    }

    [Fact]
    public void A_run_that_is_both_late_and_slow_reports_both()
    {
        var health = Evaluator.Evaluate(new JobExpectation(Job, ExpectedEvery: Hour), Run(TimeSpan.FromHours(5), took: TimeSpan.FromHours(2)));
        Assert.True(health.Has(HealthRule.Overdue));
        Assert.True(health.Has(HealthRule.Slow));
    }

    // ---- running too long ----

    [Fact]
    public void A_run_past_its_maximum_runtime_is_an_error_and_a_finished_one_is_not_judged_by_it()
    {
        var expectation = new JobExpectation(Job, MaxRuntime: TimeSpan.FromMinutes(30));
        var stuck = Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(31), outcome: RunOutcome.Running));
        Assert.Equal(Severity.Error, stuck.Findings.Single(f => f.Rule == HealthRule.RunningTooLong).Severity);
        Assert.True(Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(30), outcome: RunOutcome.Running)).Healthy);               // exactly the maximum is allowed
        Assert.True(Evaluator.Evaluate(expectation, Run(TimeSpan.FromHours(3), took: TimeSpan.FromHours(2))).Healthy);
    }

    // ---- failed ----

    [Theory]
    [InlineData(RunOutcome.Failed, Severity.Error)]
    [InlineData(RunOutcome.Interrupted, Severity.Error)]
    [InlineData(RunOutcome.Stopped, Severity.Warning)]
    [InlineData(RunOutcome.CompletedWithFailures, Severity.Warning)]
    public void A_last_run_that_did_not_succeed_is_a_failed_finding_with_the_right_severity(RunOutcome outcome, Severity severity)
    {
        var health = Evaluator.Evaluate(new JobExpectation(Job), Run(TimeSpan.FromMinutes(5), outcome: outcome));
        var finding = Assert.Single(health.Findings);
        Assert.Equal(HealthRule.Failed, finding.Rule);
        Assert.Equal(severity, finding.Severity);
    }

    [Fact]
    public void A_failure_names_the_item_and_the_classified_reason()
    {
        var run = Run(TimeSpan.FromMinutes(5), outcome: RunOutcome.Failed, failedItem: "marts.fct_orders", failure: new RunFailure("login-failed", "the login was refused"));
        var message = Evaluator.Evaluate(new JobExpectation(Job), run).Findings.Single().Message;
        Assert.Contains("marts.fct_orders", message);
        Assert.Contains("login-failed: the login was refused", message);
    }

    [Fact]
    public void Successful_and_running_runs_are_not_failures()
    {
        Assert.True(Evaluator.Evaluate(new JobExpectation(Job), Run(TimeSpan.FromMinutes(5))).Healthy);
        Assert.True(Evaluator.Evaluate(new JobExpectation(Job), Run(TimeSpan.FromMinutes(5), outcome: RunOutcome.Running)).Healthy);
    }

    // ---- lag ----

    [Fact]
    public void A_job_further_behind_than_allowed_is_lagging_and_one_at_the_limit_is_not()
    {
        var expectation = new JobExpectation(Job, MaxLag: TimeSpan.FromMinutes(10));
        var behind = Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(1)), new LagReading(TimeSpan.FromMinutes(25), _clock.Now));
        Assert.Contains("25 min behind its source", behind.Findings.Single(f => f.Rule == HealthRule.Lagging).Message);
        Assert.True(Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(1)), new LagReading(TimeSpan.FromMinutes(10), _clock.Now)).Healthy);
        Assert.True(Evaluator.Evaluate(expectation, Run(TimeSpan.FromMinutes(1)), null).Healthy);                                      // no reading, nothing to say
        Assert.True(Evaluator.Evaluate(new JobExpectation(Job), Run(TimeSpan.FromMinutes(1)), new LagReading(TimeSpan.FromDays(3), _clock.Now)).Healthy);   // no limit set
    }

    // ---- together ----

    [Fact]
    public void The_worst_findings_come_first_and_the_worst_severity_is_reported()
    {
        var expectation = new JobExpectation(Job, ExpectedEvery: Hour, MaxLag: TimeSpan.FromMinutes(1));
        var health = Evaluator.Evaluate(expectation, Run(TimeSpan.FromHours(5), took: TimeSpan.FromHours(2), outcome: RunOutcome.Failed), new LagReading(TimeSpan.FromHours(1), _clock.Now));
        Assert.Equal(Severity.Error, health.Worst);
        Assert.Equal(Severity.Error, health.Findings[0].Severity);
        Assert.Equal([HealthRule.Overdue, HealthRule.Slow, HealthRule.Lagging], health.Findings.Where(f => f.Severity == Severity.Warning).Select(f => f.Rule).ToArray());
        Assert.Null(Evaluator.Evaluate(new JobExpectation(Job), null).Worst);
    }

    [Fact]
    public void The_same_evaluation_a_minute_later_changes_when_the_clock_does()
    {
        var expectation = new JobExpectation(Job, ExpectedEvery: Hour);
        var run = Run(TimeSpan.FromMinutes(59));
        Assert.True(Evaluator.Evaluate(expectation, run).Healthy);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Evaluator.Evaluate(expectation, run).Has(HealthRule.Overdue));
    }

    [Theory]
    [InlineData(5, "5 s")]
    [InlineData(119, "119 s")]
    [InlineData(120, "2 min")]
    [InlineData(7199, "119 min")]
    [InlineData(7200, "2 h")]
    [InlineData(172799, "47 h")]
    [InlineData(172800, "2 d")]
    public void A_span_is_written_in_the_unit_a_person_reads(int seconds, string expected) =>
        Assert.Equal(expected, HealthEvaluator.Span(TimeSpan.FromSeconds(seconds)));

    // ---- many jobs from a history ----

    private sealed class History(Dictionary<string, RunRecord?> runs, Dictionary<string, LagReading?> lags) : IRunHistory
    {
        public List<string> LagAsked { get; } = [];
        public Task<RunRecord?> LastRunAsync(string jobId, CancellationToken ct = default) => Task.FromResult(runs.GetValueOrDefault(jobId));
        public Task<LagReading?> LagAsync(string jobId, CancellationToken ct = default) { LagAsked.Add(jobId); return Task.FromResult(lags.GetValueOrDefault(jobId)); }
    }

    [Fact]
    public async Task Every_job_is_evaluated_from_its_history_and_lag_is_only_asked_for_jobs_that_limit_it()
    {
        var history = new History(
            new() { ["a"] = Run(TimeSpan.FromMinutes(5)) with { JobId = "a" }, ["b"] = null },
            new() { ["c"] = new LagReading(TimeSpan.FromHours(2), _clock.Now) });
        var result = await Evaluator.EvaluateAsync(
        [
            new JobExpectation("a", ExpectedEvery: Hour),
            new JobExpectation("b", ExpectedEvery: Hour),
            new JobExpectation("c", MaxLag: TimeSpan.FromMinutes(5)),
        ], history);

        Assert.True(result.Single(h => h.JobId == "a").Healthy);
        Assert.True(result.Single(h => h.JobId == "b").Has(HealthRule.Overdue));
        Assert.True(result.Single(h => h.JobId == "c").Has(HealthRule.Lagging));
        Assert.Equal(["c"], history.LagAsked);
    }
}
