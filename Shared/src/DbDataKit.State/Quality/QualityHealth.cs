using DbDataKit.Monitoring;

namespace DbDataKit.State;

/// <summary>
/// The <c>quality</c> module read as monitoring findings (plan 23a FQ16): the latest result of every check becomes a <see cref="CheckReading"/> and the shared <see cref="HealthEvaluator"/> says which datasets have a
/// <see cref="HealthRule.CheckFailed"/> finding. Nothing is evaluated here.
/// </summary>
public static class QualityHealth
{
    public static CheckReading Reading(CheckStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CheckReading(row.Dataset, row.CheckId, QualityStore.Name(row.Outcome), row.Blocking, row.Measured, row.Threshold);
    }

    /// <summary>One <see cref="JobHealth"/> per dataset with a failing or erroring check, the job id being the dataset's identity.</summary>
    public static IReadOnlyList<JobHealth> Findings(this QualityStore store, HealthEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        return (evaluator ?? new HealthEvaluator()).EvaluateChecks(store.All().Select(Reading));
    }
}
