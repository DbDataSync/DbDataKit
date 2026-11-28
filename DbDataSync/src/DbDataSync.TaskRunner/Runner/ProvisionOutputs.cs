using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Provisioning;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>The renames an apply on a runner caught the target up with (plan 28c E21): a run output, since the published definition cannot be changed (ED8).</summary>
public sealed record AppliedRenamesReport(string Replication, string Mapping, List<AppliedRename> Renames);

/// <summary>One rename of a mapping's column that the apply ran on the target: the column of the mapping and the step, as the definition names them.</summary>
public sealed record AppliedRename(string Column, string From, string To);

/// <summary>
/// What the manual provisioning task records of an apply (plan 28c E21, Sync half step b): nothing is written into the definition, the read-only
/// published package (ED8). The target's columns read back after the apply are the same <see cref="ProvisionedColumnsReport"/> a drain pass reports
/// (<see cref="DrainSteps.ProvisionedColumns"/>), so the Sync screens read both the same way; the renames done are a report of their own. The two
/// recorders are the run's steps in the task (the job passes them), plain callbacks in a test.
/// </summary>
public sealed class RunOutputMappingAfterApply(Action<ProvisionedColumnsReport> columns, Action<AppliedRenamesReport> renames) : IMappingAfterApply
{
    public void RenamesApplied(string replicationName, TableMappingConfig mapping, IReadOnlyList<RenameStep> applied)
    {
        // the column each step belongs to, found by reference: the steps are the mapping's own objects, already marked applied by the provisioner
        var steps = mapping.ColumnMappings
            .SelectMany(c => c.Renames.Where(r => applied.Any(a => ReferenceEquals(a, r))).Select(r => new AppliedRename(c.TargetColumn, r.From, r.To)))
            .ToList();
        if (steps.Count > 0)
            renames(new AppliedRenamesReport(replicationName, mapping.Name, steps));
    }

    /// <summary>Reported every time, not only when it differs from the definition's cache: the back end compares when it takes the report in, as it does for a drain's.</summary>
    public void TargetColumnsRead(string replicationName, string mappingName, IReadOnlyList<CachedColumn> read) =>
        columns(new ProvisionedColumnsReport(replicationName, mappingName, [.. read]));
}
