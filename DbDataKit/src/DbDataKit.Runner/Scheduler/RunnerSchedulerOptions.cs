using DbDataKit.State;
using DbDataKit.Runner.Execution;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Scheduler;

/// <summary>What a <see cref="RunnerScheduler"/> serves and how it keeps time. Every interval is a setting; the defaults are the owner's (plan 28a QR3: a 30 second heartbeat and a 90 second lease).</summary>
public sealed class RunnerSchedulerOptions
{
    public static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(90);

    /// <summary>The channels this loop serves: it reads the projection rows of these channels and no others (DR11). Required, not empty.</summary>
    public required IReadOnlyCollection<string> Channels { get; init; }

    /// <summary>Only these packages of the channels; null serves every package.</summary>
    public IReadOnlyCollection<string>? Packages { get; init; }

    /// <summary>
    /// The service the queue, the job records and the run records are kept under (<c>work_item.service</c>, <c>run.service</c>): it has to be the executor's <see cref="TaskExecutorOptions.Service"/> for the status to find a task's last run.
    /// Default <c>runner</c>.
    /// </summary>
    public string Service { get; init; } = "runner";

    /// <summary>How often the loop looks at the projection and the queue. Default 5 seconds. A due time is acted on at the next tick, so it is also how late a start can be.</summary>
    public TimeSpan Tick { get; init; } = DefaultTick;

    /// <summary>How often a running task's lease is renewed. Default 30 seconds. Must be well below <see cref="Lease"/>; three beats fit in a lease with the defaults.</summary>
    public TimeSpan Heartbeat { get; init; } = DefaultHeartbeat;

    /// <summary>How long a claimed item stays claimed without a heartbeat before another loop may take it. Default 90 seconds.</summary>
    public TimeSpan Lease { get; init; } = DefaultLease;

    /// <summary>How many tasks this loop runs at once. Default 4. (The pooled limits of a package and of a connection are separate and apply across loops.)</summary>
    public int MaxConcurrentRuns { get; init; } = 4;

    /// <summary>How long a cancelled task has to stop before its process is killed, for a graceful stop and for a cancel by hand. Default 30 seconds (<see cref="TaskRunRequest.CancellationGrace"/>).</summary>
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>What a manifest's silence about back-off falls back to.</summary>
    public RetryDefaults RetryDefaults { get; init; } = RetryDefaults.Standard;

    /// <summary>How many times an item whose version could not be fetched or materialized (a temporary failure) is tried in all. Default 3. A manifest's own <c>attempts</c> applies once its manifest could be read.</summary>
    public int StartFailureAttempts { get; init; } = 3;

    /// <summary>Names this loop's claims; unique per loop instance. Default host, process and a random part.</summary>
    public string WorkerId { get; init; } = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <summary>The jitter of the back-off, a number in [0, 1); null is random. A test passes a constant (0.5 is no jitter).</summary>
    public Func<double>? Jitter { get; init; }

    /// <summary>Where the loop reports what it skipped and why.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Checks the values and says what is wrong in words; throws <see cref="ArgumentException"/>.</summary>
    internal void Validate()
    {
        if (Channels is null || Channels.Count == 0) throw new ArgumentException("A scheduler serves at least one channel.", nameof(Channels));
        if (Tick <= TimeSpan.Zero) throw new ArgumentException("The tick is above zero.", nameof(Tick));
        if (Heartbeat <= TimeSpan.Zero) throw new ArgumentException("The heartbeat is above zero.", nameof(Heartbeat));
        if (Lease <= Heartbeat) throw new ArgumentException($"The lease ({Lease}) has to be longer than the heartbeat ({Heartbeat}), or a healthy task loses its item between beats.", nameof(Lease));
        if (MaxConcurrentRuns < 1) throw new ArgumentException("A scheduler runs at least one task at a time.", nameof(MaxConcurrentRuns));
        if (StartFailureAttempts < 1) throw new ArgumentException("At least one try.", nameof(StartFailureAttempts));
        if (string.IsNullOrWhiteSpace(Service)) throw new ArgumentException("A service name is needed.", nameof(Service));
    }
}

/// <summary>Something the loop skipped or could not do, and why. <see cref="Subject"/> is a task (<c>package/channel/task</c>) or a work item.</summary>
public sealed record SchedulerProblem(string Subject, string Message, DateTimeOffset At);

/// <summary>One task as the loop sees it.</summary>
/// <param name="Task">Which task.</param>
/// <param name="Version">The version the projection says is active.</param>
/// <param name="NextDue">When the schedule next says to start it; null for a task with no schedule, or a broken row.</param>
/// <param name="LastRun">Its latest run record.</param>
/// <param name="Running">Items of the task claimed by some loop (running now).</param>
/// <param name="Queued">Items waiting to be claimed, including a retry waiting for its time.</param>
/// <param name="NextAttemptAt">The earliest time a waiting item may be claimed (a retry's back-off), or null.</param>
/// <param name="Paused">Whether its schedule is paused.</param>
/// <param name="PausedBy">Who paused it.</param>
/// <param name="Problem">Why its projection row is skipped, or null.</param>
public sealed record ScheduledTaskStatus(
    TaskRef Task, string Version, DateTimeOffset? NextDue, StoredRun? LastRun, int Running, int Queued, DateTimeOffset? NextAttemptAt, bool Paused, string? PausedBy, string? Problem);

/// <summary>A snapshot of the loop.</summary>
/// <param name="WorkerId">This loop's claim name.</param>
/// <param name="At">When the snapshot was taken (the state database's clock).</param>
/// <param name="Tasks">Every task of the served channels, with a broken row's problem.</param>
/// <param name="Problems">Rows skipped, items dropped and runs that could not start, newest last (the latest 50 of the last two kinds).</param>
/// <param name="RunningHere">Runs this loop has going.</param>
/// <param name="MaxConcurrentRuns">The loop's bound.</param>
public sealed record SchedulerStatus(string WorkerId, DateTimeOffset At, IReadOnlyList<ScheduledTaskStatus> Tasks, IReadOnlyList<SchedulerProblem> Problems, int RunningHere, int MaxConcurrentRuns);

/// <summary>What one tick did.</summary>
/// <param name="Enqueued">Tasks put on the queue because they were due.</param>
/// <param name="OverlapRefused">Tasks that were due while an earlier run of them was still queued or running, so none was queued.</param>
/// <param name="Paused">Tasks that were due while paused: the time is handled and not run.</param>
/// <param name="Claimed">Items this loop took and started.</param>
/// <param name="SkippedRows">Projection rows that could not be read; they are in <see cref="RunnerScheduler.Status"/>.</param>
/// <param name="ExpiredLeases">Items whose lease ran out and went back to the queue.</param>
public sealed record TickReport(IReadOnlyList<TaskRef> Enqueued, int OverlapRefused, int Paused, int Claimed, int SkippedRows, int ExpiredLeases);
