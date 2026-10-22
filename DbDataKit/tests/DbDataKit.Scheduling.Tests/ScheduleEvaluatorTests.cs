using DbDataKit.Scheduling;
using Xunit;

namespace DbDataKit.Scheduling.Tests;

public class ScheduleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    // ---- the cases Sync's SchedulingEvaluator already had, unchanged ----

    [Fact]
    public void A_job_that_never_ran_is_due_immediately()
    {
        Assert.True(ScheduleEvaluator.IsDue(Schedule.Interval(TimeSpan.FromHours(1)), null, Now));
        Assert.True(ScheduleEvaluator.IsDue(Schedule.OnCron("0 0 * * *"), null, Now));
    }

    [Theory]
    [InlineData(30, 29, false)]
    [InlineData(30, 30, true)]
    [InlineData(30, 31, true)]
    public void An_interval_is_due_once_the_time_since_the_last_start_reaches_it(int everySeconds, int elapsedSeconds, bool due) =>
        Assert.Equal(due, ScheduleEvaluator.IsDue(Schedule.Interval(TimeSpan.FromSeconds(everySeconds)), Now.AddSeconds(-elapsedSeconds), Now));

    [Fact]
    public void An_interval_of_zero_is_always_due()
    {
        Assert.True(ScheduleEvaluator.IsDue(Schedule.Interval(TimeSpan.Zero), Now, Now));
        Assert.True(ScheduleEvaluator.IsDue(Schedule.Interval(TimeSpan.Zero), Now.AddSeconds(-1), Now));
    }

    [Fact]
    public void A_cron_is_due_when_its_next_time_after_the_last_start_has_passed_and_not_before()
    {
        var daily = Schedule.OnCron("0 0 * * *");
        Assert.True(ScheduleEvaluator.IsDue(daily, Now.AddDays(-2), Now));
        Assert.False(ScheduleEvaluator.IsDue(daily, Now, Now));
    }

    [Fact]
    public void A_schedule_with_neither_an_interval_nor_a_cron_is_never_due_by_the_clock()
    {
        Assert.False(new Schedule().HasSchedule);
        Assert.False(ScheduleEvaluator.IsDue(new Schedule(), Now.AddDays(-30), Now));
        Assert.False(ScheduleEvaluator.IsDue(new Schedule(), null, Now));
        Assert.Null(ScheduleEvaluator.NextAfter(new Schedule(), Now));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void The_gate_requires_enabled_and_not_paused(bool enabled, bool paused, bool expected) =>
        Assert.Equal(expected, RunGate.ShouldRun(enabled, paused));

    // ---- next time ----

    [Fact]
    public void The_next_time_is_given_for_both_kinds()
    {
        Assert.Equal(Now.AddMinutes(10), ScheduleEvaluator.NextAfter(Schedule.Interval(TimeSpan.FromMinutes(10)), Now));
        Assert.Equal(Utc(2026, 8, 26), ScheduleEvaluator.NextAfter(Schedule.OnCron("0 0 * * *"), Now));
        Assert.Equal(Utc(2026, 8, 25, 13), ScheduleEvaluator.NextAfter(Schedule.OnCron("0 * * * *"), Now));            // strictly after: 12:00 itself is not "next"
    }

    [Fact]
    public void A_decision_carries_the_next_time_and_what_to_record()
    {
        var notDue = ScheduleEvaluator.Evaluate(Schedule.Interval(TimeSpan.FromMinutes(10)), Now.AddMinutes(-4), Now);
        Assert.False(notDue.Due);
        Assert.Equal(Now.AddMinutes(6), notDue.NextDue);
        Assert.Equal(Now.AddMinutes(-4), notDue.HandledUpTo);                  // nothing changes

        var due = ScheduleEvaluator.Evaluate(Schedule.Interval(TimeSpan.FromMinutes(10)), Now.AddMinutes(-10), Now);
        Assert.True(due.Due);
        Assert.Equal(Now.AddMinutes(10), due.NextDue);
        Assert.Equal(Now, due.HandledUpTo);                                    // the run that starts now
    }

    // ---- time zones ----

    [Fact]
    public void A_cron_is_read_in_its_time_zone()
    {
        // 09:00 every day in Berlin (CEST, UTC+2, in August) is 07:00 UTC
        var berlin = Schedule.OnCron("0 9 * * *", "Europe/Berlin");
        Assert.Equal(Utc(2026, 8, 26, 7), ScheduleEvaluator.NextAfter(berlin, Now));
        Assert.Equal(Utc(2026, 8, 25, 7), ScheduleEvaluator.NextAfter(berlin, Utc(2026, 8, 24, 8)));
        // and 09:00 in winter (CET, UTC+1) is 08:00 UTC
        Assert.Equal(Utc(2026, 12, 1, 8), ScheduleEvaluator.NextAfter(berlin, Utc(2026, 11, 30, 12)));
    }

    // ---- daylight saving (values checked against what Cronos does at the transitions) ----

    [Fact]
    public void A_daily_time_inside_the_spring_forward_gap_runs_right_after_the_gap_and_the_day_after_is_back_to_normal()
    {
        // New York, Sunday 8 March 2026: 02:00 EST becomes 03:00 EDT, so 02:30 does not exist that day
        var daily = Schedule.OnCron("30 2 * * *", "America/New_York");
        Assert.Equal(Utc(2026, 3, 8, 7, 0), ScheduleEvaluator.NextAfter(daily, Utc(2026, 3, 7, 12)));          // 03:00 EDT
        Assert.Equal(Utc(2026, 3, 9, 6, 30), ScheduleEvaluator.NextAfter(daily, Utc(2026, 3, 8, 8)));           // 02:30 EDT
        Assert.Equal(Utc(2026, 3, 7, 7, 30), ScheduleEvaluator.NextAfter(daily, Utc(2026, 3, 6, 12)));          // 02:30 EST the day before
    }

    [Fact]
    public void A_daily_time_in_the_repeated_hour_runs_once_and_an_hourly_cron_runs_in_both_passes()
    {
        // New York, Sunday 1 November 2026: 02:00 EDT becomes 01:00 EST, so 01:00 to 02:00 happens twice
        var fixedTime = Schedule.OnCron("30 1 * * *", "America/New_York");
        var first = ScheduleEvaluator.NextAfter(fixedTime, Utc(2026, 10, 31, 12));
        Assert.Equal(Utc(2026, 11, 1, 5, 30), first);                                                           // 01:30 EDT, the first one
        Assert.Equal(Utc(2026, 11, 2, 6, 30), ScheduleEvaluator.NextAfter(fixedTime, first!.Value));            // not again at 01:30 EST: the next is the next day

        var hourly = Schedule.OnCron("0 * * * *", "America/New_York");
        var runs = new List<DateTimeOffset>();
        for (var t = ScheduleEvaluator.NextAfter(hourly, Utc(2026, 11, 1, 3, 30)); t is not null && t <= Utc(2026, 11, 1, 7); t = ScheduleEvaluator.NextAfter(hourly, t.Value)) runs.Add(t.Value);
        Assert.Equal([Utc(2026, 11, 1, 4), Utc(2026, 11, 1, 5), Utc(2026, 11, 1, 6), Utc(2026, 11, 1, 7)], runs);   // 00:00, 01:00 EDT, 01:00 EST, 02:00 EST
    }

    [Fact]
    public void The_same_holds_in_Europe()
    {
        var berlin = Schedule.OnCron("30 2 * * *", "Europe/Berlin");
        Assert.Equal(Utc(2026, 3, 29, 1, 0), ScheduleEvaluator.NextAfter(berlin, Utc(2026, 3, 28, 12)));        // 03:00 CEST after the gap
        Assert.Equal(Utc(2026, 10, 25, 0, 30), ScheduleEvaluator.NextAfter(berlin, Utc(2026, 10, 24, 12)));     // 02:30 CEST, once
        Assert.Equal(Utc(2026, 10, 26, 1, 30), ScheduleEvaluator.NextAfter(berlin, Utc(2026, 10, 25, 12)));     // then 02:30 CET
    }

    [Fact]
    public void A_job_that_ran_at_the_gap_time_is_not_due_again_until_the_next_day()
    {
        var daily = Schedule.OnCron("30 2 * * *", "America/New_York");
        var ranAt = Utc(2026, 3, 8, 7, 1);                                                                      // started at 03:01 EDT, the replacement for 02:30
        Assert.False(ScheduleEvaluator.IsDue(daily, ranAt, Utc(2026, 3, 8, 20)));
        Assert.True(ScheduleEvaluator.IsDue(daily, ranAt, Utc(2026, 3, 9, 6, 31)));
    }

    // ---- missed runs ----

    [Fact]
    public void Several_missed_times_are_one_run_by_default_and_the_decision_says_how_many_were_missed()
    {
        // down for three days: 00:00 on the 23rd, 24th and 25th passed
        var decision = ScheduleEvaluator.Evaluate(Schedule.OnCron("0 0 * * *"), Utc(2026, 8, 22, 6), Now);
        Assert.True(decision.Due);
        Assert.Equal(2, decision.Missed);                                  // three passed; this run stands for one of them
        Assert.Equal(Now, decision.HandledUpTo);
        Assert.Equal(Utc(2026, 8, 26), decision.NextDue);
    }

    [Fact]
    public void An_interval_missed_many_times_over_is_one_run()
    {
        var decision = ScheduleEvaluator.Evaluate(Schedule.Interval(TimeSpan.FromHours(1)), Now.AddHours(-5).AddMinutes(-30), Now);
        Assert.True(decision.Due);
        Assert.Equal(4, decision.Missed);                                  // five whole hours passed since the last start; one run covers one of them
        Assert.Equal(Now.AddHours(1), decision.NextDue);
    }

    [Fact]
    public void With_skip_a_time_that_is_too_late_is_let_pass_and_remembered_so_it_is_not_judged_again()
    {
        var daily = Schedule.OnCron("0 0 * * *", misfire: MisfirePolicy.Skip) with { MisfireGrace = TimeSpan.FromMinutes(5) };
        var late = ScheduleEvaluator.Evaluate(daily, Utc(2026, 8, 22, 6), Now);                                  // the latest time, 00:00 today, was 12 hours ago
        Assert.False(late.Due);
        Assert.Equal(3, late.Missed);
        Assert.Equal(Utc(2026, 8, 25), late.HandledUpTo);                                                        // store this, and the next tick starts from it
        Assert.Equal(Utc(2026, 8, 26), late.NextDue);

        var nextTick = ScheduleEvaluator.Evaluate(daily, late.HandledUpTo, Now.AddMinutes(1));
        Assert.False(nextTick.Due);
        Assert.Equal(0, nextTick.Missed);
    }

    [Fact]
    public void With_skip_a_time_within_the_grace_still_runs()
    {
        var daily = Schedule.OnCron("0 0 * * *", misfire: MisfirePolicy.Skip) with { MisfireGrace = TimeSpan.FromMinutes(5) };
        var decision = ScheduleEvaluator.Evaluate(daily, Utc(2026, 8, 24, 0, 0), Utc(2026, 8, 25, 0, 4));
        Assert.True(decision.Due);
        Assert.Equal(Utc(2026, 8, 25, 0, 4), decision.HandledUpTo);
        Assert.False(ScheduleEvaluator.Evaluate(daily, Utc(2026, 8, 24, 0, 0), Utc(2026, 8, 25, 0, 6)).Due);
    }

    [Fact]
    public void With_skip_an_interval_waits_for_the_next_slot_of_its_grid()
    {
        var schedule = Schedule.Interval(TimeSpan.FromHours(1), MisfirePolicy.Skip) with { MisfireGrace = TimeSpan.FromMinutes(5) };
        var last = Utc(2026, 8, 25, 6);
        var decision = ScheduleEvaluator.Evaluate(schedule, last, Utc(2026, 8, 25, 9, 20));                      // slots at 07:00, 08:00, 09:00 passed; 09:00 was 20 minutes ago
        Assert.False(decision.Due);
        Assert.Equal(3, decision.Missed);
        Assert.Equal(Utc(2026, 8, 25, 9), decision.HandledUpTo);
        Assert.Equal(Utc(2026, 8, 25, 10), decision.NextDue);
        Assert.True(ScheduleEvaluator.Evaluate(schedule, last, Utc(2026, 8, 25, 9, 3)).Due);
    }

    [Fact]
    public void The_count_of_missed_times_stops_at_a_limit_instead_of_counting_for_ever()
    {
        var everyMinute = Schedule.OnCron("* * * * *");
        var decision = ScheduleEvaluator.Evaluate(everyMinute, Now.AddYears(-1), Now);
        Assert.True(decision.Due);
        Assert.Equal(ScheduleEvaluator.MissedCountLimit - 1, decision.Missed);
    }

    // ---- validation ----

    [Fact]
    public void A_schedule_that_cannot_be_used_says_why_and_the_evaluator_refuses_it()
    {
        Assert.Null(Schedule.OnCron("0 0 * * *", "Europe/Berlin").Validate());
        Assert.Contains("is not a cron expression", Schedule.OnCron("nonsense").Validate());
        Assert.Contains("not a time zone", Schedule.OnCron("0 0 * * *", "Mars/Olympus").Validate());
        Assert.Contains("not both", (new Schedule { Every = TimeSpan.FromHours(1), Cron = "0 0 * * *" }).Validate());
        Assert.Contains("grace", (new Schedule { MisfireGrace = TimeSpan.FromSeconds(-1) }).Validate());
        Assert.Throws<ArgumentException>(() => ScheduleEvaluator.Evaluate(Schedule.OnCron("nonsense"), null, Now));
        Assert.Throws<ArgumentException>(() => ScheduleEvaluator.NextAfter(Schedule.OnCron("0 0 * * *", "Mars/Olympus"), Now));
    }

    [Fact]
    public void A_job_asked_about_before_its_last_start_is_not_due()
    {
        // a clock that went back a little: the last start is "in the future"
        Assert.False(ScheduleEvaluator.IsDue(Schedule.Interval(TimeSpan.FromMinutes(5)), Now.AddMinutes(1), Now));
        Assert.False(ScheduleEvaluator.IsDue(Schedule.OnCron("0 0 * * *"), Now.AddMinutes(1), Now));
    }
}
