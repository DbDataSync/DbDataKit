using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

/// <summary>A refresh plan keeping to its <c>expected_every</c> (plan 13, M09): the shared health rules over the last run the tracking tables report, on a clock the test moves.</summary>
public sealed class PlanHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly EveryDuration Hourly = new("1h", 3600);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AuditLog.PlanRun Run(TimeSpan startedAgo, TimeSpan took, string status = "complete") =>
        new("run-1", "nightly", status, (Now - startedAgo).UtcDateTime, (Now - startedAgo + took).UtcDateTime, null, 3, 3);

    private static RefreshViews.PlanHealth Health(EveryDuration? expected, AuditLog.PlanRun? last) => RefreshViews.Evaluate(expected, last, new Clock());

    [Fact]
    public void A_plan_with_no_expected_every_has_nothing_to_be_late_at()
    {
        Assert.Equal(RefreshViews.PlanHealth.None, Health(null, null));
        Assert.Equal(RefreshViews.PlanHealth.None, Health(null, Run(TimeSpan.FromDays(9), TimeSpan.FromHours(5))));
    }

    [Fact]
    public void A_plan_that_never_ran_is_overdue()
    {
        var health = Health(Hourly, null);

        Assert.True(health.Overdue);
        Assert.False(health.Slow);
        Assert.Equal("1h", health.ExpectedEvery);
    }

    [Fact]
    public void A_plan_that_ran_in_time_and_finished_quickly_is_healthy()
    {
        var health = Health(Hourly, Run(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(5)));

        Assert.False(health.Overdue);
        Assert.False(health.Slow);
        Assert.Equal(300, health.LastRunSeconds);
        Assert.Equal(1200, health.AgeSeconds);
    }

    [Fact]
    public void A_plan_whose_last_run_began_longer_ago_than_the_span_is_overdue()
    {
        var health = Health(Hourly, Run(TimeSpan.FromMinutes(61), TimeSpan.FromMinutes(1)));

        Assert.True(health.Overdue);
        Assert.False(health.Slow);
    }

    [Fact]
    public void A_run_that_took_longer_than_the_span_makes_the_plan_slow()
    {
        var health = Health(Hourly, Run(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(61)));

        Assert.True(health.Slow);
        Assert.False(health.Overdue);
    }

    [Fact]
    public void Exactly_at_the_limit_is_still_keeping_to_it()
    {
        Assert.False(Health(Hourly, Run(TimeSpan.FromHours(1), TimeSpan.FromHours(1))).Overdue);
        Assert.False(Health(Hourly, Run(TimeSpan.FromHours(2), TimeSpan.FromHours(1))).Slow);
    }

    [Fact]
    public void A_run_still_going_is_neither_slow_nor_overdue_until_it_has_gone_on_past_the_span()
    {
        var recent = Health(Hourly, Run(TimeSpan.FromMinutes(10), TimeSpan.Zero, "started"));
        Assert.False(recent.Overdue);
        Assert.False(recent.Slow);

        var long_ = Health(Hourly, Run(TimeSpan.FromMinutes(90), TimeSpan.Zero, "started"));
        Assert.True(long_.Overdue);
        Assert.False(long_.Slow);
    }
}
