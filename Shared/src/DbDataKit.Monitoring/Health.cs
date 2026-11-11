namespace DbDataKit.Monitoring;

public enum HealthRule
{
    /// <summary>It has not started within its expected interval, or has never run.</summary>
    Overdue,
    /// <summary>Its last run finished, but took longer than the interval it should repeat in.</summary>
    Slow,
    /// <summary>A run has been going longer than its maximum runtime.</summary>
    RunningTooLong,
    /// <summary>Its last run failed, stopped, was interrupted or finished with failures.</summary>
    Failed,
    /// <summary>It is further behind its source than allowed.</summary>
    Lagging,
}

public enum Severity { Warning, Error }

/// <summary>One thing wrong with a job, in words a person can act on, with the numbers behind it.</summary>
public sealed record HealthFinding(string JobId, HealthRule Rule, Severity Severity, string Message, TimeSpan? Measured = null, TimeSpan? Limit = null);

public sealed record JobHealth(string JobId, IReadOnlyList<HealthFinding> Findings)
{
    public bool Healthy => Findings.Count == 0;

    /// <summary>The worst finding's severity, or null when there is none.</summary>
    public Severity? Worst => Findings.Count == 0 ? null : Findings.Max(f => f.Severity);

    public bool Has(HealthRule rule) => Findings.Any(f => f.Rule == rule);
}
