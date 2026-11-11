using Cronos;

namespace DbDataKit.Scheduling;

/// <summary>What to do when a scheduled time passed while nothing was running (the server was down, a run overran).</summary>
public enum MisfirePolicy
{
    /// <summary>Run once, now, however many times were missed (Sync's behaviour).</summary>
    RunOnce,
    /// <summary>Run only if the latest scheduled time is recent (within <see cref="Schedule.MisfireGrace"/>); otherwise let it pass and wait for the next.</summary>
    Skip,
}

/// <summary>
/// When a job should start: every so often (<see cref="Every"/>, counted from the start of its last run), or on a cron schedule (<see cref="Cron"/>, five fields, evaluated in <see cref="TimeZoneId"/>).
/// With neither, the job has no schedule: it runs only when asked.
/// </summary>
public sealed record Schedule
{
    /// <summary>The interval between starts. Zero or less means "as soon as the last run is over".</summary>
    public TimeSpan? Every { get; init; }

    /// <summary>A five-field cron expression (minute hour day-of-month month day-of-week).</summary>
    public string? Cron { get; init; }

    /// <summary>The time zone the cron expression is read in: an IANA id (<c>Europe/Berlin</c>) or a Windows one; <c>UTC</c> by default. Daylight saving follows the zone.</summary>
    public string TimeZoneId { get; init; } = "UTC";

    public MisfirePolicy Misfire { get; init; } = MisfirePolicy.RunOnce;

    /// <summary>With <see cref="MisfirePolicy.Skip"/>: how late a scheduled time may still be run.</summary>
    public TimeSpan MisfireGrace { get; init; } = TimeSpan.FromMinutes(1);

    public static Schedule Interval(TimeSpan every, MisfirePolicy misfire = MisfirePolicy.RunOnce) => new() { Every = every, Misfire = misfire };

    public static Schedule OnCron(string cron, string timeZoneId = "UTC", MisfirePolicy misfire = MisfirePolicy.RunOnce) => new() { Cron = cron, TimeZoneId = timeZoneId, Misfire = misfire };

    public bool HasSchedule => Every is not null || !string.IsNullOrWhiteSpace(Cron);

    /// <summary>What is wrong with this schedule, in words that name the field, or null when it is usable.</summary>
    public string? Validate()
    {
        if (Every is not null && !string.IsNullOrWhiteSpace(Cron)) return "A schedule is either an interval or a cron expression, not both.";
        if (MisfireGrace < TimeSpan.Zero) return "The misfire grace cannot be negative.";
        if (!string.IsNullOrWhiteSpace(Cron))
        {
            try { CronExpression.Parse(Cron); }
            catch (CronFormatException ex) { return $"'{Cron}' is not a cron expression: {ex.Message}"; }
            if (Zone() is null) return $"'{TimeZoneId}' is not a time zone this machine knows.";
        }
        return null;
    }

    internal TimeZoneInfo? Zone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}

/// <summary>The gate that decides whether a job may run at all, from the two independent things that decide it: its durable intent (enabled, kept in configuration) and a temporary hold (paused, never committed).</summary>
public static class RunGate
{
    public static bool ShouldRun(bool enabled, bool paused) => enabled && !paused;
}
