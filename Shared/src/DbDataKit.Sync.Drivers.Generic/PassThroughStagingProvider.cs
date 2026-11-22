using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// The cache that is not one: no staging table, no second copy of the data. The reader's rows go to the
/// writer as they arrive, so a large first load is read once and written once instead of read, written
/// to tempdb, and written again.
/// <para>
/// Only for a writer that declares <see cref="IPassThroughWriter"/>. The price of skipping staging is
/// that nothing exists to re-read or merge from, so the writer has to be one that inserts rows as they
/// stream (and a hook's <c>{{staging}}</c> has no table to name).
/// </para>
/// </summary>
public sealed class PassThroughStagingProvider : IStagingProvider
{
    /// <summary>What <c>{{staging}}</c> expands to for a pass that staged nothing.</summary>
    public const string Location = "(pass-through: not staged)";

    public string Kind => GenericDriverKinds.PassThrough;

    public Task<StagedChangeSet> StageAsync(
        DbConnection targetConnection,
        TableRef target,
        IAsyncEnumerable<ChangeRow> rows,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken) =>
        Task.FromResult(new StagedChangeSet(Location, 0, Stream: new PassThroughRows(rows)));

    public Task CleanupAsync(DbConnection targetConnection, StagedChangeSet staged, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
