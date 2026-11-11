using Cronos;

namespace DbDataKit.Scheduling;

/// <summary>
/// The answer for one job at one moment.
/// </summary>
/// <param name="Due">Start a run now.</param>
/// <param name="NextDue">The next scheduled time after now (null for a schedule that runs only when asked).</param>
/// <param name="Missed">How many scheduled times passed without a run, not counting the one a due run stands for.</param>
/// <param name="HandledUpTo">Scheduled times up to here are dealt with: store it as the job's "since" after acting. When a run starts it is now; when late times were let pass (<see cref="MisfirePolicy.Skip"/>) it is the latest of them, so they are not judged again at the next tick.</param>
public sealed record ScheduleDecision(bool Due, DateTimeOffset? NextDue, int Missed, DateTimeOffset? HandledUpTo);

/// <summary>
/// Pure due-ness: a schedule, when the job was last handled (the start of its last run, or the latest time let pass), and the clock. Nothing here reads a clock or a store, so a scheduler can
/// call it on every tick and a test can pose any moment, daylight-saving transitions included.
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>A cron that fires every minute, missed for years, would be counted forever; the count stops here.</summary>
    public const int MissedCountLimit = 100_000;

    public static bool IsDue(Schedule schedule, DateTimeOffset? since, DateTimeOffset now) => Evaluate(schedule, since, now).Due;

    public static ScheduleDecision Evaluate(Schedule schedule, DateTimeOffset? since, DateTimeOffset now)
    {
        if (schedule.Validate() is { } problem) throw new ArgumentException(problem, nameof(schedule));
        if (!schedule.HasSchedule) return new ScheduleDecision(false, null, 0, since);

        return schedule.Every is { } every ? Interval(schedule, every, since, now) : Cron(schedule, since, now);
    }

    /// <summary>The next scheduled time strictly after <paramref name="after"/>, or null for a schedule that runs only when asked.</summary>
    public static DateTimeOffset? NextAfter(Schedule schedule, DateTimeOffset after)
    {
        if (schedule.Validate() is { } problem) throw new ArgumentException(problem, nameof(schedule));
        if (schedule.Every is { } every) return after + (every > TimeSpan.Zero ? every : TimeSpan.Zero);
        return string.IsNullOrWhiteSpace(schedule.Cron) ? null : NextCron(schedule, after);
    }

    private static ScheduleDecision Interval(Schedule schedule, TimeSpan every, DateTimeOffset? since, DateTimeOffset now)
    {
        if (since is null) return new ScheduleDecision(true, now + Positive(every), 0, now);
        if (every <= TimeSpan.Zero) return new ScheduleDecision(now >= since, now, 0, now);

        var elapsed = now - since.Value;
        if (elapsed < every) return new ScheduleDecision(false, since + every, 0, since);

        var slots = (long)(elapsed.Ticks / every.Ticks);                                // whole intervals since the last start
        var latest = since.Value + TimeSpan.FromTicks(every.Ticks * slots);             // the latest time a run was meant to start
        var missed = (int)Math.Min(slots - 1, MissedCountLimit);
        if (schedule.Misfire == MisfirePolicy.Skip && now - latest > schedule.MisfireGrace)
            return new ScheduleDecision(false, latest + every, (int)Math.Min(slots, MissedCountLimit), latest);
        return new ScheduleDecision(true, now + every, missed, now);
    }

    private static ScheduleDecision Cron(Schedule schedule, DateTimeOffset? since, DateTimeOffset now)
    {
        var next = NextCron(schedule, now);
        if (since is null) return new ScheduleDecision(true, next, 0, now);

        // every scheduled time in (since, now]
        var count = 0;
        DateTimeOffset? latest = null;
        for (var occurrence = NextCron(schedule, since.Value); occurrence is not null && occurrence <= now && count < MissedCountLimit; occurrence = NextCron(schedule, occurrence.Value))
        {
            count++;
            latest = occurrence;
        }
        if (count == 0) return new ScheduleDecision(false, next, 0, since);
        if (schedule.Misfire == MisfirePolicy.Skip && now - latest!.Value > schedule.MisfireGrace)
            return new ScheduleDecision(false, next, count, latest);
        return new ScheduleDecision(true, next, count - 1, now);
    }

    private static DateTimeOffset? NextCron(Schedule schedule, DateTimeOffset after)
    {
        var expression = CronExpression.Parse(schedule.Cron!);
        var next = expression.GetNextOccurrence(after.UtcDateTime, schedule.Zone()!, inclusive: false);
        return next is null ? null : new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc));
    }

    private static TimeSpan Positive(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.Zero;
}
