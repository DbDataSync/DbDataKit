using DbDataKit.Config.Git;
using DbDataKit.Sync.Core.Config;

namespace DbDataKit.Sync.Provisioning;

/// <summary>
/// What an apply records about a mapping once its DDL ran (plan 28c E21, Sync half step b): the renames the target has caught up with and the columns
/// the target now has. On the Sync server that is a commit to the mapping (<see cref="SavedMappingAfterApply"/>); on a runner the definition is
/// read-only (ED8), so the provisioning task reports the same facts as outputs of its run, which the Sync screens read as they read the drain's
/// <c>provisioned-columns</c>.
/// </summary>
public interface IMappingAfterApply
{
    /// <summary>Every step of the apply succeeded, so the mapping's pending renames (<paramref name="renames"/>, already marked applied on <paramref name="mapping"/>) are done on the target.</summary>
    void RenamesApplied(string replicationName, TableMappingConfig mapping, IReadOnlyList<RenameStep> renames);

    /// <summary>The target was read back from its catalog after the apply: <paramref name="columns"/> is its shape now.</summary>
    void TargetColumnsRead(string replicationName, string mappingName, IReadOnlyList<CachedColumn> columns);
}

/// <summary>
/// The Sync server's way: both facts are saved into the mapping file and committed as <paramref name="author"/> (the signed-in person).
/// </summary>
public sealed class SavedMappingAfterApply(ConfigRepository configRepository, Func<GitAuthor> author) : IMappingAfterApply
{
    public void RenamesApplied(string replicationName, TableMappingConfig mapping, IReadOnlyList<RenameStep> renames) =>
        configRepository.SaveTableMapping(replicationName, mapping, author());

    /// <summary>
    /// Re-loads the mapping rather than writing back the copy the apply opened with: that copy is as old as the start of the apply, and
    /// <see cref="RenamesApplied"/> may already have saved it. This write is about one field, so it is applied to one field of the current file.
    /// </summary>
    public void TargetColumnsRead(string replicationName, string mappingName, IReadOnlyList<CachedColumn> columns)
    {
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        if (CachedColumn.SameShape(mapping.TargetColumns, columns))
            return;

        mapping.TargetColumns = [.. columns];
        // Stamped for the reason a Refresh stamps it: the target really was read just now, and the age on screen is what an operator decides staleness from.
        mapping.ColumnsCapturedUtc = DateTime.UtcNow;
        configRepository.SaveTableMapping(replicationName, mapping, author());
    }
}
