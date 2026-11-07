namespace DbDataKit.Runner.Limits;

/// <summary>What a process may use. A null value is no limit.</summary>
/// <param name="MemoryBytes">Resident memory of the process, in bytes.</param>
/// <param name="CpuPercent">Percent of the machine's whole CPU capacity (the job object's meaning), 1 to 100.</param>
public sealed record LimitSpec(long? MemoryBytes = null, int? CpuPercent = null);

/// <summary>How a limit is held.</summary>
public enum LimitEnforcement
{
    /// <summary>Nothing is limiting (none was asked for, or this machine cannot).</summary>
    None,

    /// <summary>The operating system holds the limit (job object, cgroup).</summary>
    Enforced,

    /// <summary>A watchdog samples and kills the process over the limit: the limit can be overshot between samples.</summary>
    Watched,
}

/// <summary>A limit the process went over: what, the limit, and the value seen.</summary>
public sealed record LimitExceeded(string Limit, long LimitValue, long ObservedValue, string Reason);

/// <summary>Options for <see cref="ProcessLimits"/>; the defaults are the real machine, the rest exists for tests.</summary>
public sealed record LimitOptions
{
    /// <summary>How often the watchdog samples.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Never use a cgroup, even where one is possible (what a runner does when told to watch).</summary>
    public bool DisableCgroup { get; init; }

    /// <summary>The cgroup v2 directory the runner may create children in; null finds it (the cgroup2 mount plus the runner's own cgroup from <c>/proc/self/cgroup</c>).</summary>
    public string? CgroupBase { get; init; }

    /// <summary>Never use a job object (tests on Windows).</summary>
    public bool DisableJobObject { get; init; }
}

/// <summary>
/// The limits on one process. Dispose when the process is done (it also kills what is still running under a job object or a cgroup, and removes the cgroup); the watchdog stops
/// with the process or with Dispose.
/// </summary>
public abstract class LimitHandle : IDisposable
{
    private volatile LimitExceeded? _exceeded;

    /// <summary>How the memory limit is held (<see cref="LimitEnforcement.None"/> when none was asked for).</summary>
    public LimitEnforcement Enforcement { get; protected init; }

    /// <summary>How the CPU limit is held.</summary>
    public LimitEnforcement CpuEnforcement { get; protected init; }

    /// <summary>"job object", "cgroup v2", "watchdog" or "none".</summary>
    public string Mechanism { get; protected init; } = "none";

    /// <summary>True once the process went over a limit (and, for a watchdog, was killed).</summary>
    public bool Exceeded => _exceeded is not null || CheckExceeded();

    /// <summary>Which limit and by how much, or null.</summary>
    public LimitExceeded? ExceededInfo => _exceeded ?? (CheckExceeded() ? _exceeded : null);

    /// <summary>Human-readable reason when <see cref="Exceeded"/>, else null.</summary>
    public string? ExceededReason => ExceededInfo?.Reason;

    protected void SetExceeded(LimitExceeded exceeded) => _exceeded ??= exceeded;

    /// <summary>For handles whose evidence is read on demand (a cgroup's oom counter): sets the exceeded state and says whether there is one.</summary>
    protected virtual bool CheckExceeded() => false;

    public abstract void Dispose();
}
