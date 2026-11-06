namespace DbDataKit.State;

/// <summary>
/// How long one table of a module keeps its rows (plan 29 section 3.3). A table that holds history (runs, logs, notifications, settled dead letters, expired idempotency keys)
/// names its time column and the default age; a table that holds current state has no rule, and its rows go when what they describe goes. The audit has no default age at all
/// (<paramref name="DefaultDays"/> null): it is kept until an administrator sets a limit.
/// </summary>
/// <param name="Table">The table's name within its module, as written in <c>{table:name}</c>.</param>
/// <param name="TimeColumn">The UTC time column the age is measured on.</param>
/// <param name="DefaultDays">Days and fractions of days (<c>0.25</c> is six hours); null keeps rows until an administrator sets a limit.</param>
/// <param name="Where">An optional condition that must also hold for a row to go (a settled dead letter, an expired key), written with column names only.</param>
public sealed record RetentionRule(string Table, string TimeColumn, double? DefaultDays = RetentionRule.StandardDays, string? Where = null)
{
    /// <summary>The suite's default for everything except the audit (owner, 2026-11-05).</summary>
    public const double StandardDays = 30;
}
