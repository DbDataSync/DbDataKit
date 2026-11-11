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
    /// <summary>A check on a dataset's data failed (or could not run): the data is not what its owner said it should be (plan 23a FQ16).</summary>
    CheckFailed,
}

public enum Severity { Warning, Error }

/// <summary>One thing wrong with a job, in words a person can act on, with the numbers behind it.</summary>
public sealed record HealthFinding(string JobId, HealthRule Rule, Severity Severity, string Message, TimeSpan? Measured = null, TimeSpan? Limit = null);

/// <summary>
/// The latest result of one data check, as the monitoring rule reads it (plan 23a FQ16): <paramref name="Status"/> is <c>pass</c>, <c>fail</c> or <c>error</c>, <paramref name="Blocking"/> says the check holds back what depends on the dataset now,
/// <paramref name="Measured"/> and <paramref name="Threshold"/> are the numbers behind a failure. The dataset is named by its lineage identity, so the finding lines up with the node.
/// </summary>
public sealed record CheckReading(string Dataset, string CheckId, string Status, bool Blocking, decimal? Measured = null, decimal? Threshold = null);

public sealed record JobHealth(string JobId, IReadOnlyList<HealthFinding> Findings)
{
    public bool Healthy => Findings.Count == 0;

    /// <summary>The worst finding's severity, or null when there is none.</summary>
    public Severity? Worst => Findings.Count == 0 ? null : Findings.Max(f => f.Severity);

    public bool Has(HealthRule rule) => Findings.Any(f => f.Rule == rule);
}
