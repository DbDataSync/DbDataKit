using System.Data.Common;
using DbDataKit.Transfer;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// **The change writer of every engine** (plan 28c, CW01): applies the staged changes of an incremental pass to the target with a DELETE, an UPDATE and an
/// INSERT, with no <c>MERGE</c>, <c>ON CONFLICT</c> or <c>ON DUPLICATE KEY</c>. An upsert is where engines diverge hardest; three plain statements over the
/// staging table are the same idea everywhere, and only the UPDATE's join is spelled per engine (<see cref="SharedStatementSyntax.UpdateStyleOf"/>).
/// <para>
/// <b>What the staging table must hold.</b> The key columns, the written columns, the operation marker (<c>I</c>, <c>U</c> or <c>D</c>) and the per-pass ordinal,
/// which every staging provider fills in arrival order. A reader hands changes over in source order, and several changes to one key can sit in one pass
/// (logical decoding, CDC's all-changes mode). The first statement keeps the highest ordinal per key and drops the rest from the stage, so insert then update is
/// an insert of the final values, insert then delete is a delete of a row that is not there, update then delete is a delete, and delete then insert is an
/// update to the final values. Change Tracking, net-changes CDC, the trigger audit and a watermark scan hand over one row per key already; the collapse costs
/// them one grouped delete that removes nothing.
/// </para>
/// <para>
/// <b>Replay.</b> The statements converge on the final state and do not count: a DELETE of a key that is gone deletes nothing, the UPDATE sets final values, the
/// INSERT only adds keys the target lacks. A pass applied twice (a retry, a crash after the target commit and before the position was saved) leaves the same
/// target. Everything runs in one transaction on the target connection, so a pass is applied whole or not at all; <see cref="ApplyBatch"/> still splits each
/// statement into ordinal slices to keep statements small, inside that one transaction (unlike <c>MsSqlMerge</c>, which commits per slice: a half-applied pass here
/// would be visible and is not needed, as the statements are cheap).
/// </para>
/// <para>
/// <b>Keys.</b> The target's primary key, which the mapping must write: a table without one is refused with a message that says so (matching rows needs a key; a
/// reload reader with <c>DeleteInsert</c> is the keyless path). A key change reaches the stage as a delete of the old key and an insert of the new one (Change
/// Tracking, CDC and the logical-slot reader express it so); the DELETE runs first, so a unique value that moves from one row to another never collides.
/// Generated (identity) non-key columns are written on insert and never updated.
/// </para>
/// <para>
/// Not reconciling: a target row the stage does not mention is left alone, which is exactly right for a change feed.
/// </para>
/// </summary>
public sealed class ChangeApplyWriter(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeWriter, IStatementPreview
{
    public string Kind => GenericDriverKinds.ChangeApply;

    public bool SupportsPositionLog => true;

    public bool SupportsReconciliation => false;

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
        if (staged.Stream is not null)
            throw new InvalidOperationException(
                $"Table mapping '{mappingName}' pairs the '{Kind}' writer with a cache that stages nothing. This writer applies the staged rows with " +
                "statements against the staging table, so it needs a staging cache such as StagingTable.");

        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var shape = TargetShape.FromCachedColumns(dialect, mappingName, targetColumns, target, columnMappings);
        var key = RequireKey(shape, mappingName);
        var batchSize = ApplyBatch.Read(options);
        var statements = Statements.Build(dialect, shape, key, staged.StagingLocation, chunked: batchSize is not null);

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var collapse = targetConnection.CreateTimedCommand())
            {
                collapse.Transaction = transaction;
                collapse.CommandText = statements.Collapse.Text;
                await collapse.ExecuteNonQueryAsync(cancellationToken);
            }

            long affected = 0;
            var ranges = ApplyBatch.Ranges(staged.RowCount, batchSize).ToList();

            // All the DELETEs, then all the UPDATEs, then all the INSERTs, across every slice: after the collapse a key sits in exactly one slice, and this order is
            // what lets a unique value move from a deleted row to an inserted one wherever the two rows fall.
            affected += await RunAsync(targetConnection, transaction, statements.Delete, ranges, cancellationToken);
            if (statements.Update is not null)
                affected += await RunAsync(targetConnection, transaction, statements.Update, ranges, cancellationToken);

            affected += await dialect.WriteWithGeneratedColumnOverrideAsync(
                targetConnection, transaction, shape.QuotedTarget, shape.RequiresGeneratedColumnOverride,
                () => RunAsync(targetConnection, transaction, statements.Insert, ranges, cancellationToken),
                cancellationToken);

            await PositionLogScope.BeforeCommitAsync(targetConnection, transaction, cancellationToken);   // plan 28c E17: the entry's state row, same transaction
            await transaction.CommitAsync(cancellationToken);
            return new WriteResult(affected);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<long> RunAsync(
        DbConnection connection, DbTransaction transaction, BuiltStatement statement, IReadOnlyList<(long After, long UpTo)> ranges,
        CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (var (after, upTo) in ranges)
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = statement.Text;
            SegmentScope.AddTo(cmd, dialect, binder, statement.Slots, column: null, after, upTo);
            var changed = await cmd.ExecuteNonQueryAsync(cancellationToken);
            rows += Math.Max(0, changed);
        }
        return rows;
    }

    /// <summary>The target's primary key, which the mapping must write: rows are matched on it, and the message says what to do when it is not there.</summary>
    public static IReadOnlyList<string> RequireKey(TargetShape shape, string mappingName)
    {
        if (shape.PrimaryKeyColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table mapping '{mappingName}': target table '{shape.Target.Schema}.{shape.Target.Table}' has no primary key, and the '{GenericDriverKinds.ChangeApply}' " +
                "writer matches rows on it. Add a primary key to the target (then Refresh metadata on the mapping), or read the whole table each pass " +
                "with BatchReload and write with DeleteInsert, which needs no key.");

        var missing = shape.PrimaryKeyColumns.Where(pk => !shape.MappedTargetColumns.Contains(pk, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Table mapping '{mappingName}': the column mappings must include the target primary key column(s) {string.Join(", ", missing)}; " +
                $"the '{GenericDriverKinds.ChangeApply}' writer matches rows on them.");

        return shape.PrimaryKeyColumns;
    }

    /// <summary>The four statements, built once per apply (the text is the same for every slice; each slice binds its own ordinal bounds).</summary>
    public sealed record Statements(BuiltStatement Collapse, BuiltStatement Delete, BuiltStatement? Update, BuiltStatement Insert)
    {
        public static Statements Build(SqlDialect dialect, TargetShape shape, IReadOnlyList<string> key, string stage, bool chunked)
        {
            var syntax = SharedStatementSyntax.For(dialect);
            var spec = new ScopedApplySpec
            {
                Target = SqlRelation.Raw(shape.QuotedTarget),
                Stage = SqlRelation.Raw(stage),
                Columns = shape.MappedTargetColumns,
                KeyColumns = key,
                Marker = new OperationMarker(BatchInsertStagingProvider.OperationColumn),
                SequenceColumn = BatchInsertStagingProvider.OrdinalColumn,
                Ordinal = chunked ? new OrdinalRange(0, 0, BatchInsertStagingProvider.OrdinalColumn) : null,
                Override = shape.RequiresGeneratedColumnOverride ? GeneratedColumnOverride.Clause : GeneratedColumnOverride.None,
            };

            // A generated column is written on insert and never updated: the engine refuses to update one (SQL Server, PostgreSQL GENERATED ALWAYS).
            var generated = shape.Columns.Where(c => c.IsIdentity).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updateColumns = shape.MappedTargetColumns.Where(c => !generated.Contains(c) || key.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

            return new Statements(
                ScopedStatements.CollapseStage(syntax, spec),
                ScopedStatements.StagedDelete(syntax, spec),
                ScopedStatements.StagedUpdate(syntax, spec with { Columns = updateColumns }),
                ScopedStatements.StagedInsert(syntax, spec));
        }
    }

    /// <summary>The four statements, in the order they run, each with what it does for the key.</summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Target.Database, cancellationToken);

        var shape = await TargetShape.LoadAsync(
            dialect, catalog, request.Connection, request.Target, request.ColumnMappings, cancellationToken);
        if (shape.PrimaryKeyColumns.Count == 0)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.Write, "Apply the staged changes", null, PreviewOrigin.BuiltIn,
                    $"'{request.Target.Schema}.{request.Target.Table}' has no primary key, so this pass would fail here: the '{Kind}' writer matches rows on it."),
            ];
        }

        var batchSize = ApplyBatch.Read(request.Options);
        var s = Statements.Build(dialect, shape, shape.PrimaryKeyColumns, "<staging>", chunked: batchSize is not null);
        var slice = batchSize is { } size
            ? $" Issued once per {size} staged rows, all inside the one transaction."
            : "";

        var statements = new List<PreviewStatement>
        {
            new(PreviewStages.Write, "Keep the last change per key", s.Collapse.Text, PreviewOrigin.BuiltIn,
                "Several changes to one key in a pass (a logical slot, CDC's all-changes mode) collapse to the one that came last; the others are dropped from the staging table."),
            new(PreviewStages.Write, "Delete the keys the pass deleted", s.Delete.Text, PreviewOrigin.BuiltIn,
                "A key the target does not hold deletes nothing, so a replayed pass is harmless." + slice),
        };
        if (s.Update is not null)
            statements.Add(new(PreviewStages.Write, "Update the keys the target holds", s.Update.Text, PreviewOrigin.BuiltIn,
                "The engine's own join form; every written non-key column is set to its final value." + slice));
        statements.Add(new(PreviewStages.Write, "Insert the keys the target lacks", s.Insert.Text, PreviewOrigin.BuiltIn,
            "An anti-join on the key, so a replayed pass inserts nothing twice." + slice));
        return statements;
    }
}
