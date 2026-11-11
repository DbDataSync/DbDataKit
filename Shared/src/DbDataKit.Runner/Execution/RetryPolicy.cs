using DbDataKit.Connections;

namespace DbDataKit.Runner.Execution;

/// <summary>What a task's retry policy falls back to for the values its manifest leaves out (plan 28a R08). A manifest that names no <c>attempts</c> gets one run, not a default of several: a retry is the author's choice.</summary>
/// <param name="Backoff">The wait before the second attempt. Default 30 seconds.</param>
/// <param name="MaxBackoff">The ceiling of the wait. Default 10 minutes.</param>
/// <param name="Jitter">How far the wait may move either way, as a fraction of it (0.2: plus or minus a fifth), so tasks that failed together do not retry together. Default 0.2.</param>
public sealed record RetryDefaults(TimeSpan Backoff, TimeSpan MaxBackoff, double Jitter = 0.2)
{
    public static RetryDefaults Standard { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
}

/// <summary>
/// A task's retry policy, from the manifest's <see cref="TaskLimits"/> (plan 28a DR8): <see cref="Attempts"/> counts the first run too; the wait before attempt <c>n + 1</c> is <see cref="Backoff"/> times
/// <see cref="Factor"/> to the power of <c>n - 1</c>, moved by up to <see cref="Jitter"/> either way and never above <see cref="MaxBackoff"/> (the ceiling holds after the jitter).
/// </summary>
public sealed record RetryPolicy
{
    public RetryPolicy(int attempts, TimeSpan backoff, TimeSpan maxBackoff, double jitter = 0.2, double factor = 2.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        if (backoff < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(backoff), backoff, "The back-off cannot be negative.");
        if (maxBackoff < backoff) throw new ArgumentOutOfRangeException(nameof(maxBackoff), maxBackoff, "The ceiling of the back-off cannot be below its base.");
        if (jitter is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(jitter), jitter, "The jitter is a fraction between 0 and 1.");
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor), factor, "The back-off grows or stays; a factor below 1 would shrink it.");
        Attempts = attempts;
        Backoff = backoff;
        MaxBackoff = maxBackoff;
        Jitter = jitter;
        Factor = factor;
    }

    /// <summary>How many runs one work item may have, the first included. 1: no retry.</summary>
    public int Attempts { get; }

    public TimeSpan Backoff { get; }

    public TimeSpan MaxBackoff { get; }

    public double Jitter { get; }

    public double Factor { get; }

    /// <summary>A task with no retry: one run.</summary>
    public static RetryPolicy None { get; } = new(1, TimeSpan.Zero, TimeSpan.Zero, 0);

    /// <summary>The policy of a task's limits; what they leave out comes from <paramref name="defaults"/>. A ceiling the manifest sets below its own base is raised to the base.</summary>
    public static RetryPolicy From(TaskLimits limits, RetryDefaults? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        defaults ??= RetryDefaults.Standard;
        var backoff = limits.Backoff ?? defaults.Backoff;
        var ceiling = limits.MaxBackoff ?? defaults.MaxBackoff;
        return new RetryPolicy(limits.Attempts ?? 1, backoff, ceiling < backoff ? backoff : ceiling, defaults.Jitter);
    }

    /// <summary>
    /// The wait after attempt <paramref name="failedAttempt"/> (1 for the first run) failed. <paramref name="sample"/> is a number in [0, 1) that places the jitter: 0.5 is none, 0 the shortest wait the jitter allows, near 1 the longest.
    /// </summary>
    public TimeSpan DelayAfter(int failedAttempt, double sample = 0.5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);
        var seconds = Backoff.TotalSeconds * Math.Pow(Factor, failedAttempt - 1);
        var capped = double.IsFinite(seconds) ? Math.Min(seconds, MaxBackoff.TotalSeconds) : MaxBackoff.TotalSeconds;
        var jittered = capped * (1 + Jitter * (2 * Math.Clamp(sample, 0, 1) - 1));
        return TimeSpan.FromSeconds(Math.Clamp(jittered, 0, MaxBackoff.TotalSeconds));
    }
}

/// <summary>Another attempt: when it may start, the wait, its number (the second run is attempt 2) and the failure that caused it.</summary>
public sealed record RetryPlan(DateTimeOffset At, TimeSpan Delay, int NextAttempt, TaskFailure Failure);

/// <summary>
/// Decides whether a finished run is tried again, and when (plan 28a R08, DR8). It never runs anything and holds no state: <see cref="Plan(TaskEnd, int, RetryPolicy, TimeProvider, Func{double}?)"/> answers from the end, the attempt
/// number, the policy and the clock. A retry happens only for a failed run whose failure is retryable (<see cref="TaskEndMapping.ShouldRetry"/>: temporary, timed out, process gone), while attempts remain; a permanent failure, a cancel, a
/// protocol violation, a limit and a run that completed with failed items are never retried. A failure that carries a connection classification in its <see cref="TaskFailure.Code"/> follows the classification: a failure the connection called
/// permanent is not retried even if its kind says otherwise.
/// </summary>
public static class RetryPlanner
{
    public static RetryPlan? Plan(TaskRunResult result, int attempt, RetryPolicy policy, TimeProvider clock, Func<double>? jitter = null) =>
        Plan((result ?? throw new ArgumentNullException(nameof(result))).End, attempt, policy, clock, jitter);

    public static RetryPlan? Plan(TaskEnd end, int attempt, RetryPolicy policy, TimeProvider clock, Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (!TaskEndMapping.ShouldRetry(end, attempt, policy.Attempts)) return null;
        var failure = end.Failure!;
        if (ClassifiedPermanent(failure)) return null;

        var delay = policy.DelayAfter(attempt, (jitter ?? Random.Shared.NextDouble)());
        return new RetryPlan(clock.GetUtcNow() + delay, delay, attempt + 1, failure);
    }

    /// <summary>True when the failure names a connection classification (its code) that is permanent.</summary>
    private static bool ClassifiedPermanent(TaskFailure failure) =>
        failure.Code is { } code && Enum.TryParse<ConnectionFailureKind>(code, out var kind) && Enum.IsDefined(kind) && TaskFailure.KindOf(kind) == TaskFailureKind.Permanent;
}
