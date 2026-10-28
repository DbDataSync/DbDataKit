using System.Data.Common;
using DbDataKit.Transfer;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Replaces a segment's worth of target rows wholesale: delete everything in scope, insert the staged
/// change set.
/// <para>
/// **The portable writer.** Reconciling, but by replacement rather than by matching — so it needs no
/// primary key, joins nothing row by row, and is expressible identically on every engine in scope. An
/// upsert is where engines diverge hardest (<c>MERGE</c>, <c>ON CONFLICT</c>, <c>ON DUPLICATE KEY</c>,
/// and Oracle's own <c>MERGE</c> spelling); delete-then-insert is where they do not. That is what makes
/// this the writer a new driver gets for free.
/// </para>
/// <para>
/// It is also what makes watermark mode useful on an engine with no change data capture at all: a
/// watermark reader cannot see deletes, but a segmented reload through this writer converges the
/// target anyway.
/// </para>
/// <para>
/// Both statements run inside one transaction, so no concurrent reader ever observes the segment
/// empty: a read during the write sees either the old contents or the new ones, never neither.
/// </para>
/// <para>
/// **That is why this writer's chunking stops short of what <see cref="ApplyBatch"/> buys elsewhere.**
/// The refill is issued a chunk at a time — smaller statements, so a huge reload no longer builds one
/// enormous insert — but every chunk stays inside the single transaction the delete opened. Committing
/// per chunk would publish a half-refilled segment to anyone reading the target, which for a writer
/// whose entire contract is "replace this scope atomically" is not a tuning knob, it is a different
/// writer. An upsert writer has no such invariant to protect and does commit per chunk; see
/// <c>MsSqlMergeWriter</c>.
/// </para>
/// </summary>
public sealed class DeleteInsertWriter(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeWriter, IStatementPreview
{
    public string Kind => GenericDriverKinds.DeleteInsert;

    public bool SupportsReconciliation => true;

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
        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var shape = TargetShape.FromCachedColumns(dialect, mappingName, targetColumns, target, columnMappings);
        var scope = SegmentScope.Resolve(SegmentSerializer.ReadOptional(options), shape.Columns, columnMappings);
        var batchSize = ApplyBatch.Read(options);

        // Both statements come from the shared builder (plan 13, A16): it owns their text and says which parameters they
        // refer to; this writer binds them, typed the way its engine needs, and owns the transaction.
        var delete = DeleteInsertStatement.BuildDelete(dialect, shape.QuotedTarget, scope.Filter);

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var deleteCmd = targetConnection.CreateTimedCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = delete.Text;
                SegmentScope.AddTo(deleteCmd, dialect, binder, delete.Slots, scope.Column);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var rowsInserted = await dialect.WriteWithGeneratedColumnOverrideAsync(
                targetConnection, transaction, shape.QuotedTarget, shape.RequiresGeneratedColumnOverride,
                async () =>
                {
                    var inserted = 0;
                    // The text is the same for every chunk, so it is built once; each chunk binds its own ordinal bounds.
                    BuiltStatement? insert = null;
                    foreach (var (after, upTo) in ApplyBatch.Ranges(staged.RowCount, batchSize))
                    {
                        insert ??= DeleteInsertStatement.BuildInsert(
                            dialect, shape.QuotedTarget, shape.MappedTargetColumns, staged.StagingLocation,
                            shape.RequiresGeneratedColumnOverride, chunked: batchSize is not null);
                        using var insertCmd = targetConnection.CreateTimedCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = insert.Text;
                        SegmentScope.AddTo(insertCmd, dialect, binder, insert.Slots, column: null, after, upTo);

                        inserted += await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    return inserted;
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // RowsWritten counts the insert only. Folding the delete count in would double-count every
            // row that was simply replaced, which on a typical reload is most of them.
            return new WriteResult(rowsInserted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Both halves, in one transaction: the scope is emptied and then refilled, which is what
    /// makes this writer reconciling and what makes the delete's predicate the thing to read closely.</summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Target.Database, cancellationToken);

        var shape = await TargetShape.LoadAsync(
            dialect, catalog, request.Connection, request.Target, request.ColumnMappings, cancellationToken);
        var segment = SegmentSerializer.ReadOptional(request.Options);
        var scope = SegmentScope.Resolve(segment, shape.Columns, request.ColumnMappings);

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Empty the scope",
                DeleteInsertStatement.BuildDelete(dialect, shape.QuotedTarget, scope.Filter).Text, PreviewOrigin.BuiltIn,
                segment is null
                    ? "Unsegmented, that scope is the whole table."
                    : $"Scoped to {segment.Describe()}; rows outside it are untouched."),

            new PreviewStatement(
                PreviewStages.Write, "Refill it from the staged rows",
                DeleteInsertStatement.BuildInsert(
                    dialect, shape.QuotedTarget, shape.MappedTargetColumns, "<staging>",
                    shape.RequiresGeneratedColumnOverride, chunked: ApplyBatch.Read(request.Options) is not null).Text,
                PreviewOrigin.BuiltIn,
                ApplyBatch.Read(request.Options) is { } size
                    ? $"Issued once per {size} staged rows — but all of them inside the one transaction " +
                      "the delete opened, so the scope is never observed half-refilled."
                    : "Both statements run in one transaction."),
        ];
    }
}

/// <summary>
/// The two statements of <see cref="DeleteInsertWriter"/>, built by the shared scope-shaped builders
/// (<see cref="ScopedStatements"/>) with this dialect's quoting and placeholder text, and separated so the text can be
/// asserted without a live server. Each comes back with the parameter slots the writer binds.
/// </summary>
public static class DeleteInsertStatement
{
    private static ScopedApplySpec Spec(
        string quotedTarget, IReadOnlyList<string> columns, string stagingLocation, ScopeFilter scope,
        bool overrideGenerated, bool chunked) => new()
    {
        Target = SqlRelation.Raw(quotedTarget),
        Stage = SqlRelation.Raw(stagingLocation),
        Columns = columns,
        Scope = scope,
        Marker = new OperationMarker(BatchInsertStagingProvider.OperationColumn),
        Ordinal = chunked ? new OrdinalRange(0, 0, BatchInsertStagingProvider.OrdinalColumn) : null,
        // The dialect decides what the override is: a clause in this statement (PostgreSQL), or nothing here because
        // its hook brackets the write instead (SQL Server) or because the engine needs nothing (MySQL).
        Override = overrideGenerated ? GeneratedColumnOverride.Clause : GeneratedColumnOverride.None,
    };

    /// <summary><c>DELETE FROM target WHERE scope</c>: the scope is emptied, whatever the staged rows say.</summary>
    public static BuiltStatement BuildDelete(SqlDialect dialect, string quotedTarget, ScopeFilter scope) =>
        ScopedStatements.ScopeDelete(
            SharedStatementSyntax.For(dialect), Spec(quotedTarget, [], "", scope, overrideGenerated: false, chunked: false));

    /// <summary>
    /// Deletes in the change set are dropped rather than applied: the delete has already removed
    /// everything in scope, so a 'D' row would only be re-adding a row in order to say it isn't there.
    /// </summary>
    /// <param name="chunked">
    /// Adds a range over the staging table's ordinal, so one call refills part of the scope rather
    /// than all of it. The ordinal is the staging table's key, which is what keeps a chunk a seek
    /// instead of another scan of everything staged. The statement's slots say where the bounds go;
    /// the text is the same for every chunk.
    /// </param>
    public static BuiltStatement BuildInsert(
        SqlDialect dialect, string quotedTarget, IReadOnlyList<string> columns, string stagingLocation,
        bool overrideGenerated = false, bool chunked = false) =>
        ScopedStatements.Refill(
            SharedStatementSyntax.For(dialect),
            Spec(quotedTarget, columns, stagingLocation, ScopeFilter.All, overrideGenerated, chunked));
}
