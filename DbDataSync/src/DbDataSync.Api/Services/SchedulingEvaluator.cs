using DbDataKit.Scheduling;
using DbDataSync.Core.Config;

namespace DbDataSync.Api.Services;

/// <summary>Pure due-ness calculation, kept separate from SchedulerService's polling loop so it's
/// directly unit-testable without a background service or real clock. The calculation itself is
/// <see cref="ScheduleEvaluator"/> in DbDataKit.Scheduling (plan 13, M01); this maps Sync's
/// <see cref="SchedulingConfig"/> onto it and keeps Sync's behaviour exactly: a replication that has
/// never run is due, whatever its mode; a periodic one without a cron expression is never due; times are UTC.</summary>
public static class SchedulingEvaluator
{
    public static bool IsDue(SchedulingConfig scheduling, DateTimeOffset? lastRunStartedAtUtc, DateTimeOffset nowUtc)
    {
        if (lastRunStartedAtUtc is null)
            return true; // never run before — due immediately

        try
        {
            return scheduling.Mode switch
            {
                ScheduleMode.Continuous => ScheduleEvaluator.IsDue(Schedule.Interval(TimeSpan.FromSeconds(scheduling.FrequencySeconds ?? 0)), lastRunStartedAtUtc, nowUtc),
                ScheduleMode.Periodic => !string.IsNullOrWhiteSpace(scheduling.CronExpression)
                    && ScheduleEvaluator.IsDue(Schedule.OnCron(scheduling.CronExpression), lastRunStartedAtUtc, nowUtc),
                _ => false,
            };
        }
        catch (ArgumentException ex)
        {
            // an invalid cron expression was a FormatException from Cronos; callers that handle that still do
            throw new FormatException(ex.Message, ex);
        }
    }
}
