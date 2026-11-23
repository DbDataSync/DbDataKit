using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Applies a staged change set to the target. Implementations may be target-specific (MERGE, bulk
/// operations) or generic (ordered insert/update/delete statements).
/// </summary>
public interface IChangeWriter
{
    /// <summary>Identifier matched against <see cref="WriterConfig.Kind"/>, e.g. "MsSqlMerge".</summary>
    string Kind { get; }

    /// <inheritdoc cref="IChangeReader.Parameters"/>
    IReadOnlyList<ParameterDescriptor> Parameters => [];

    /// <summary>
    /// Whether this writer makes the target *match* the change set within the scope it was given —
    /// i.e. rows present in the target's scope but absent from the change set are removed. False for
    /// upsert-only writers, which can add and update rows but never notice an absence.
    /// <para>
    /// Declared here rather than inferred from <see cref="Kind"/> so capability discovery (the SPA's
    /// writer pickers, config validation) stays engine-neutral: a future driver's writers advertise
    /// the same property instead of every caller learning a new set of magic Kind strings.
    /// </para>
    /// </summary>
    bool SupportsReconciliation { get; }

    /// <summary>
    /// Whether this writer commits the entry in one transaction and calls <see cref="PositionLogScope.BeforeCommitAsync"/> before that commit, so the replication's state row
    /// (plan 28c E17, <c>tracking: target</c>) lands with the data or not at all. False for a writer that commits in pieces or not through a transaction of its own; a replication
    /// that keeps its state on the target is refused with a writer that does not.
    /// </summary>
    bool SupportsPositionLog => false;

    /// <param name="mappingName">The table mapping this write belongs to — see
    /// <see cref="IChangeReader.ReadChangesAsync"/>'s identical parameter.</param>
    /// <param name="targetColumns">The target's cached shape — see
    /// <see cref="IStagingProvider.StageAsync"/>'s identical parameter. Every writer here resolves it
    /// through <c>TargetShape</c>/<c>MsSqlTargetShape</c> rather than reading it directly.</param>
    Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
