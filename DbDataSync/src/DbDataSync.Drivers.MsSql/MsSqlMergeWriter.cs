using System.Data.Common;
using DbDataKit.Transfer;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Applies a staged change set via a single MERGE statement (architecture/detailed-design.md §3.5's
/// primary writer path). Requires the target's primary key column(s) to be included in
/// <see cref="ColumnMapping"/> — used both as the MERGE join key and to distinguish which mapped
/// columns are safe to include in the UPDATE SET list.
/// <para>
/// Upsert-only: it inserts, updates and applies the change set's explicit deletes, but a target row
/// the change set simply doesn't mention is left alone. That's exactly right for an incremental feed
/// (where "not mentioned" means "unchanged"), and exactly wrong for a reload (where it means "gone
/// from the source") — hence <see cref="SupportsReconciliation"/> being false, and
/// <see cref="MsSqlMergeReconcileWriter"/> existing.
/// </para>
/// </summary>
public sealed class MsSqlMergeWriter : IChangeWriter, IStatementPreview
{
    public string Kind => MsSqlDriverKinds.Merge;

    public bool SupportsReconciliation => false;

    /// <summary>Only the batch size: everything else this writer does is derived from the target.</summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [ApplyBatch.Descriptor];

    public async Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        targetConnection.ChangeDatabase(target.Database);

        var shape = MsSqlTargetShape.FromCachedColumns(mappingName, targetColumns, target, columnMappings);
        var batchSize = ApplyBatch.Read(options);

        long rowsAffected = 0;
        // Each chunk is its own statement and therefore its own implicit transaction, which is the
        // whole point: the target's locks are taken and released a chunk at a time instead of being
        // held across the entire staged set. An interrupted apply leaves earlier chunks committed —
        // safe here because a MERGE of the same staged rows is idempotent and the watermark has not
        // advanced, so the next attempt replays the pass and converges. See ApplyBatch.
        await MsSqlIdentityInsert.RunAsync(
            targetConnection, transaction: null, shape.QuotedTarget, shape.RequiresIdentityInsert,
            async () =>
            {
                // The text is the same for every chunk, so it is built once (on the first, as before: a staged set with
                // no rows never needs a primary key); each chunk binds its own ordinal bounds.
                BuiltStatement? merge = null;
                foreach (var (after, upTo) in ApplyBatch.Ranges(staged.RowCount, batchSize))
                {
                    merge ??= BuildMerge(shape, staged.StagingLocation, chunked: batchSize is not null);
                    using var cmd = targetConnection.CreateTimedCommand();
                    cmd.CommandText = merge.Text;
                    SegmentScope.AddTo(cmd, MsSqlDialect.Instance, MsSqlValueBinding.Instance, merge.Slots, column: null, after, upTo);

                    rowsAffected += await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                return rowsAffected;
            },
            cancellationToken);

        return new WriteResult(rowsAffected);
    }

    /// <summary>The statement itself (built by the shared <see cref="ScopedStatements.Merge"/>), so the
    /// preview shows what runs rather than a reconstruction of it.</summary>
    /// <param name="chunked">
    /// When set, the staged set is read through a bounded subquery rather than directly. The bound is
    /// a range over the staging table's clustered ordinal, so each chunk seeks its own slice instead
    /// of re-scanning the whole staged set — without which chunking would be quadratic in the number
    /// of chunks. The statement's slots say where the bounds go; the text is the same for every chunk.
    /// </param>
    internal static BuiltStatement BuildMerge(MsSqlTargetShape shape, string stagingLocation, bool chunked = false) =>
        ScopedStatements.Merge(MsSqlTargetShape.Syntax, shape.ToSpec(stagingLocation) with
        {
            KeyColumns = shape.RequireMergeKey(),
            Ordinal = chunked ? new OrdinalRange(0, 0, MsSqlTargetShape.OrdinalColumn) : null,
        });

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Target.Database);

        var shape = await MsSqlTargetShape.LoadAsync(
            request.Connection, request.Target, request.ColumnMappings, cancellationToken);

        var batchSize = ApplyBatch.Read(request.Options);
        var notes = new List<string>();
        if (batchSize is { } size)
            notes.Add($"Issued once per {size}-row chunk of the staged set, each its own transaction, " +
                      "so the target's locks are held for one chunk rather than the whole apply.");
        if (shape.RequiresIdentityInsert)
            notes.Add("Wrapped in SET IDENTITY_INSERT ON/OFF — a mapped identity column means the " +
                      "source's own values are written rather than the target generating new ones.");

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Merge the staged rows into the target",
                BuildMerge(shape, "#Staging_<per pass>", chunked: batchSize is not null).Text, PreviewOrigin.BuiltIn,
                notes.Count == 0 ? null : string.Join(" ", notes)),
        ];
    }
}
