using System.Data.Common;
using DbDataKit.Transfer;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// The target-side facts every writer in this driver needs — mapped columns, primary key, identity —
/// resolved from the catalog once, plus the SQL fragments built from them. Shared by
/// <see cref="MsSqlMergeWriter"/>, <see cref="MsSqlMergeReconcileWriter"/> and
/// <see cref="MsSqlDeleteInsertWriter"/> so the column-list/join-clause construction exists once rather
/// than three near-identical times.
/// <para>
/// Assumes the caller has already put the connection into the target's database (the same contract
/// <see cref="MsSqlSchemaQueries"/> documents).
/// </para>
/// </summary>
internal sealed class MsSqlTargetShape
{
    public const string OperationColumn = "__Operation";

    /// <summary>The staging table's per-pass row ordinal, which a chunked apply ranges over. Same
    /// column, same purpose and same name as <see cref="Generic.BatchInsertStagingProvider.OrdinalColumn"/>
    /// — restated here because this driver stages into its own temp table rather than through the
    /// generic provider.</summary>
    public const string OrdinalColumn = "__Ordinal";

    private MsSqlTargetShape(
        TableRef target,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<string> mappedTargetColumns,
        IReadOnlyList<string> primaryKeyColumns,
        bool requiresIdentityInsert)
    {
        QuotedTarget = $"{SqlIdentifier.Quote(target.Schema)}.{SqlIdentifier.Quote(target.Table)}";
        Target = target;
        Columns = columns;
        MappedTargetColumns = mappedTargetColumns;
        PrimaryKeyColumns = primaryKeyColumns;
        RequiresIdentityInsert = requiresIdentityInsert;
    }

    public TableRef Target { get; }
    public string QuotedTarget { get; }
    public IReadOnlyList<ColumnMetadata> Columns { get; }
    public IReadOnlyList<string> MappedTargetColumns { get; }
    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    /// <summary>True when a mapped column is an IDENTITY column, so an INSERT supplying it explicitly
    /// has to be bracketed with SET IDENTITY_INSERT. Detected from the catalog rather than declared in
    /// config — the failure mode otherwise is a run that errors out at apply time on a table the
    /// operator had no reason to think was special.</summary>
    public bool RequiresIdentityInsert { get; }


    public static async Task<MsSqlTargetShape> LoadAsync(
        DbConnection targetConnection,
        TableRef target,
        IReadOnlyList<ColumnMapping> columnMappings,
        CancellationToken cancellationToken)
    {
        var columns = await MsSqlSchemaQueries.GetColumnsAsync(targetConnection, target.Schema, target.Table, cancellationToken);
        var byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var mappedTargetColumns = columnMappings.Select(m => m.TargetColumn).Distinct().ToList();
        var unknown = mappedTargetColumns.Where(c => !byName.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Target column(s) {string.Join(", ", unknown)} were not found on '{target.Schema}.{target.Table}'.");

        return new MsSqlTargetShape(
            target,
            columns,
            mappedTargetColumns,
            columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList(),
            mappedTargetColumns.Any(c => byName[c].IsIdentity));
    }

    /// <summary>The cache-only path every MERGE/delete-insert writer's real <c>ApplyAsync</c> uses as of
    /// phase 91 — see <see cref="DbDataSync.Drivers.Generic.TargetShape.FromCachedColumns"/>, which this
    /// mirrors exactly for the reason the two types exist side by side in the first place.</summary>
    public static MsSqlTargetShape FromCachedColumns(
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
        TableRef target,
        IReadOnlyList<ColumnMapping> columnMappings)
    {
        var columns = targetColumns.RequireAll(mappingName, "target");
        var byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var mappedTargetColumns = columnMappings.Select(m => m.TargetColumn).Distinct().ToList();
        foreach (var column in mappedTargetColumns)
            if (!byName.ContainsKey(column))
                throw new MetadataNotCachedException(mappingName, "target", column);

        return new MsSqlTargetShape(
            target,
            columns,
            mappedTargetColumns,
            columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList(),
            mappedTargetColumns.Any(c => byName[c].IsIdentity));
    }

    /// <summary>The target's primary key, which both MERGE-based writers match rows on. The requirement
    /// (a PK, and column mappings that carry it) is enforced here once with one message.</summary>
    public IReadOnlyList<string> RequireMergeKey()
    {
        if (PrimaryKeyColumns.Count == 0)
            throw new InvalidOperationException(
                $"Target table '{Target.Schema}.{Target.Table}' has no primary key; MERGE requires one.");

        var missing = PrimaryKeyColumns
            .Where(pk => !MappedTargetColumns.Contains(pk, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Column mappings must include the target primary key column(s): {string.Join(", ", missing)}.");

        return PrimaryKeyColumns;
    }

    /// <summary>
    /// What the shared statement builders (<see cref="ScopedStatements"/>) need to write this target from
    /// <paramref name="stage"/>: the mapped columns, the staged operation marker, and the identity override as
    /// a bracketed write (<see cref="MsSqlIdentityInsert"/> wraps it, since SQL Server wants a session flag around
    /// the statement rather than a clause in it).
    /// </summary>
    public ScopedApplySpec ToSpec(string stage) => new()
    {
        Target = SqlRelation.Raw(QuotedTarget),
        Stage = SqlRelation.Raw(stage),
        Columns = MappedTargetColumns,
        Marker = new OperationMarker(OperationColumn),
        Override = RequiresIdentityInsert ? GeneratedColumnOverride.Bracketed : GeneratedColumnOverride.None,
    };

    /// <summary>
    /// Brackets, <c>@name</c>, a trailing <c>;</c>, Transact-SQL's MERGE spelling and the staging table's bookkeeping columns
    /// written bare: the text these writers have always sent.
    /// </summary>
    public static StatementSyntax Syntax { get; } =
        SharedStatementSyntax.For(MsSqlDialect.Instance) with { TransactSql = true, QuoteBookkeepingColumns = false };
}
