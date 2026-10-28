using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

public static class StrategyNames
{
    public const string FullReplace = "full_replace";
    public const string DeleteInsertByKey = "delete_insert_by_key";
    public const string MergeByKey = "merge_by_key";
    public const string DeleteInsertByRange = "delete_insert_by_range";
    public const string WatermarkAppend = "watermark_append";
    public const string KeyReconcileDelete = "key_reconcile_delete";
    public const string DeleteInsertByScope = "delete_insert_by_scope";
    public const string MergeStagedChanges = "merge_staged_changes";
}

/// <summary>The built-in strategies by shared name. Names from the products resolve through <see cref="StrategyMatrix.Resolve"/>.</summary>
public static class StrategyCatalog
{
    private static readonly IApplyStrategy[] All =
    [
        new FullReplaceStrategy(), new DeleteInsertByKeyStrategy(), new MergeByKeyStrategy(),
        new DeleteInsertByRangeStrategy(), new WatermarkAppendStrategy(), new KeyReconcileDeleteStrategy(),
        new DeleteInsertByScopeStrategy(), new MergeStagedChangesStrategy(),
    ];

    public static IReadOnlyList<IApplyStrategy> Strategies => All;

    public static IApplyStrategy Get(string name) =>
        All.FirstOrDefault(s => s.Name == (StrategyMatrix.Builtin.Resolve(name) ?? name)) ?? throw new ArgumentException($"unknown strategy `{name}`");

    /// <summary>Validates the combination against the matrix and builds the statements.</summary>
    public static StatementSet Build(string strategy, ApplyRequest request, ISqlDialect dialect, bool requireVerified = false)
    {
        var s = Get(strategy);
        StrategyMatrix.Builtin.Require(s.Name, dialect.Id, request.EngineVersion, requireVerified);
        return s.Build(request, dialect);
    }
}

/// <summary>Empties the target and refills it from the stage. DELETE by default (transactional everywhere); TRUNCATE on request where the dialect has it.</summary>
public sealed class FullReplaceStrategy : IApplyStrategy
{
    public string Name => StrategyNames.FullReplace;
    public StrategyCaps Needs => StrategyCaps.Transaction;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        StrategyHelpers.RequireColumns(r);
        var notes = new List<string>();
        var transactional = true;
        var del = new StatementBuilder(d);
        WriteKind kind;
        if (r.Opts.UseTruncate)
        {
            if (!d.Caps.HasFlag(EngineCaps.TruncateTable)) throw new StrategyNotSupportedException(Name, d.Id.Value, "the engine has no TRUNCATE");
            del.Append($"TRUNCATE TABLE {del.T(r.Target)}");
            kind = WriteKind.Truncate;
            if (!d.Caps.HasFlag(EngineCaps.TransactionalDdl))
            {
                transactional = false;
                notes.Add("TRUNCATE commits implicitly on this engine: the replace is not atomic");
            }
        }
        else
        {
            del.Append($"DELETE FROM {del.T(r.Target)}");
            kind = WriteKind.Delete;
        }
        var ins = new StatementBuilder(d);
        StrategyHelpers.InsertSelect(ins, r);
        return new StatementSet(Name,
        [
            del.Build($"{Name}:clear", StrategyHelpers.Intent(Name, "clear", kind, r, WriteRisk.Destructive)),
            ins.Build($"{Name}:insert", StrategyHelpers.Intent(Name, "insert", WriteKind.Insert, r)),
        ], transactional, Notes: notes);
    }
}

/// <summary>Deletes target rows whose key is in the stage, then inserts the whole stage. NULL keys never match, so such rows are not deleted. Rows absent from the stage are untouched.</summary>
public sealed class DeleteInsertByKeyStrategy : IApplyStrategy
{
    public string Name => StrategyNames.DeleteInsertByKey;
    public StrategyCaps Needs => StrategyCaps.Transaction | StrategyCaps.KeyColumns;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var keys = StrategyHelpers.Keys(r, Name);
        var del = new StatementBuilder(d);
        // the target is referenced by its own qualified name so the correlated predicate resolves on every engine
        var target = del.T(r.Target);
        del.Append($"DELETE FROM {target} WHERE EXISTS (SELECT 1 FROM {del.T(r.Stage)} s WHERE {string.Join(" AND ", keys.Select(k => $"s.{del.Q(k.Name)} = {target}.{del.Q(k.Name)}"))})");
        return Finish(r, d, del);
    }

    private StatementSet Finish(ApplyRequest r, ISqlDialect d, StatementBuilder del)
    {
        var ins = new StatementBuilder(d);
        StrategyHelpers.InsertSelect(ins, r);
        return new StatementSet(Name,
        [
            del.Build($"{Name}:delete", StrategyHelpers.Intent(Name, "delete", WriteKind.Delete, r, WriteRisk.Destructive)),
            ins.Build($"{Name}:insert", StrategyHelpers.Intent(Name, "insert", WriteKind.Insert, r)),
        ], true);
    }
}

/// <summary>Updates matching keys and inserts the rest: MERGE, ON CONFLICT or ON DUPLICATE KEY by capability. Never deletes. Stage keys must be unique.</summary>
public sealed class MergeByKeyStrategy : IApplyStrategy
{
    public string Name => StrategyNames.MergeByKey;
    public StrategyCaps Needs => StrategyCaps.Transaction | StrategyCaps.KeyColumns | StrategyCaps.MergeOrUpsert;

    /// <summary>
    /// Auto: engines with the MERGE capability use MERGE; PostgreSQL 15 and later uses MERGE (Build parity); otherwise ON CONFLICT
    /// (capability) and finally ON DUPLICATE KEY (MySQL).
    /// </summary>
    public static MergeStyle Resolve(ISqlDialect d, EngineVersion? version, MergeStyle requested)
    {
        if (requested != MergeStyle.Auto) return requested;
        if (d.Caps.HasFlag(EngineCaps.Merge)) return MergeStyle.Merge;
        if (d.Id == EngineId.Postgres && version is { Major: >= 15 }) return MergeStyle.Merge;
        if (d.Caps.HasFlag(EngineCaps.UpsertOnConflict)) return d.Id == EngineId.MySql ? MergeStyle.OnDuplicateKey : MergeStyle.OnConflict;
        throw new StrategyNotSupportedException(Name0, d.Id.Value, "the engine has neither MERGE nor an upsert form");
    }

    private const string Name0 = StrategyNames.MergeByKey;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var keys = StrategyHelpers.Keys(r, Name);
        var nonKeys = r.Schema.NonKeys.ToList();
        var style = Resolve(d, r.EngineVersion, r.Opts.MergeStyle);
        var b = new StatementBuilder(d);
        var cols = b.QList(r.Schema.Columns.Select(c => c.Name));
        var notes = new List<string>();
        switch (style)
        {
            case MergeStyle.Merge:
            {
                b.Append($"MERGE INTO {b.T(r.Target)} tgt USING {b.T(r.Stage)} src ON {StrategyHelpers.KeyJoin(b, keys, "tgt", "src")}");
                if (nonKeys.Count > 0)
                    b.Append(" WHEN MATCHED THEN UPDATE SET " + string.Join(", ", nonKeys.Select(c => $"{b.Q(c.Name)} = {b.Qualified("src", c.Name)}")));
                b.Append($" WHEN NOT MATCHED THEN INSERT ({cols}) VALUES ({string.Join(", ", r.Schema.Columns.Select(c => b.Qualified("src", c.Name)))})");
                if (d.Id == EngineId.SqlServer || d.Id == EngineId.Fabric) b.Append(";");
                break;
            }
            case MergeStyle.OnConflict:
            {
                b.Append($"INSERT INTO {b.T(r.Target)} ({cols}) SELECT {cols} FROM {b.T(r.Stage)} ON CONFLICT ({b.QList(keys.Select(k => k.Name))}) ");
                b.Append(nonKeys.Count == 0
                    ? "DO NOTHING"
                    : "DO UPDATE SET " + string.Join(", ", nonKeys.Select(c => $"{b.Q(c.Name)} = EXCLUDED.{b.Q(c.Name)}")));
                notes.Add("requires a PRIMARY KEY or UNIQUE constraint on the key columns of the target");
                break;
            }
            case MergeStyle.OnDuplicateKey:
            {
                var sel = string.Join(", ", r.Schema.Columns.Select(c => b.Qualified("s", c.Name)));
                b.Append($"INSERT INTO {b.T(r.Target)} ({cols}) SELECT {sel} FROM {b.T(r.Stage)} s ON DUPLICATE KEY UPDATE ");
                var upd = nonKeys.Count == 0 ? keys.Take(1).ToList() : nonKeys;
                b.Append(string.Join(", ", upd.Select(c => $"{b.Q(c.Name)} = {b.Qualified("s", c.Name)}")));
                notes.Add("requires a PRIMARY KEY or UNIQUE index on the key columns of the target");
                break;
            }
        }
        notes.Add($"style: {style}; stage keys must be unique");
        return new StatementSet(Name, [b.Build($"{Name}:merge", StrategyHelpers.Intent(Name, "merge", WriteKind.Insert | WriteKind.Update, r))], true, Notes: notes);
    }
}

/// <summary>Replaces the rows whose range column lies in [start, end): delete the window, insert the stage rows of the window. Bounds are bound parameters.</summary>
public sealed class DeleteInsertByRangeStrategy : IApplyStrategy
{
    public string Name => StrategyNames.DeleteInsertByRange;
    public StrategyCaps Needs => StrategyCaps.Transaction | StrategyCaps.RangeColumn;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var col = StrategyHelpers.Column(r, r.RangeColumn, "range column");
        if (r.RangeStart is null || r.RangeEnd is null) throw new ArgumentException("range start and end are required");
        var del = new StatementBuilder(d);
        del.Append($"DELETE FROM {del.T(r.Target)} WHERE {del.Q(col.Name)} >= {del.Param("range_start", col.Type, r.RangeStart)} AND {del.Q(col.Name)} < {del.Param("range_end", col.Type, r.RangeEnd)}");
        var ins = new StatementBuilder(d);
        StrategyHelpers.InsertSelect(ins, r, b => b.Append($"{b.Q(col.Name)} >= {b.Param("range_start", col.Type, r.RangeStart)} AND {b.Q(col.Name)} < {b.Param("range_end", col.Type, r.RangeEnd)}"));
        return new StatementSet(Name,
        [
            del.Build($"{Name}:delete", StrategyHelpers.Intent(Name, "delete", WriteKind.Delete, r, WriteRisk.Destructive)),
            ins.Build($"{Name}:insert", StrategyHelpers.Intent(Name, "insert", WriteKind.Insert, r)),
        ], true);
    }
}

/// <summary>
/// Appends stage rows newer than the watermark. With <see cref="ApplyRequest.WatermarkInclusive"/> (a lookback window) the window from the
/// watermark is replaced, never duplicated. The host resolves the watermark (see <see cref="ResolverStatement"/>) and subtracts any lookback itself.
/// </summary>
public sealed class WatermarkAppendStrategy : IApplyStrategy
{
    public string Name => StrategyNames.WatermarkAppend;
    public StrategyCaps Needs => StrategyCaps.Transaction | StrategyCaps.WatermarkColumn;

    /// <summary><c>SELECT MAX(col) FROM target</c> as a scalar statement for the host to run before building the set.</summary>
    public static SqlStatement ResolverStatement(ApplyRequest r, ISqlDialect d)
    {
        var col = StrategyHelpers.Column(r, r.WatermarkColumn, "watermark column");
        var b = new StatementBuilder(d);
        b.Append($"SELECT MAX({b.Q(col.Name)}) FROM {b.T(r.Target)}");
        return b.Build($"{StrategyNames.WatermarkAppend}:resolve", new WriteIntent($"{StrategyNames.WatermarkAppend}:resolve", WriteKind.None, r.Target.ToString()), StatementRole.Scalar);
    }

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var col = StrategyHelpers.Column(r, r.WatermarkColumn, "watermark column");
        var statements = new List<SqlStatement>();
        var op = r.WatermarkInclusive ? ">=" : ">";
        if (r.Watermark is not null && r.WatermarkInclusive)
        {
            var del = new StatementBuilder(d);
            del.Append($"DELETE FROM {del.T(r.Target)} WHERE {del.Q(col.Name)} >= {del.Param("watermark", col.Type, r.Watermark)}");
            statements.Add(del.Build($"{Name}:delete-window", StrategyHelpers.Intent(Name, "delete-window", WriteKind.Delete, r, WriteRisk.Risky)));
        }
        var ins = new StatementBuilder(d);
        StrategyHelpers.InsertSelect(ins, r, r.Watermark is null ? null : b => b.Append($"{b.Q(col.Name)} {op} {b.Param("watermark", col.Type, r.Watermark)}"));
        statements.Add(ins.Build($"{Name}:insert", StrategyHelpers.Intent(Name, "insert", WriteKind.Insert, r)));
        return new StatementSet(Name, statements, true,
            Notes: r.WatermarkInclusive ? ["lookback window is replaced"] : ["append only: idempotent because the watermark moves after the first run"]);
    }
}

/// <summary>
/// Deletes target rows (within an optional scope) whose key is not in the stage. The count statement and the delete statement are paired by
/// the <see cref="DeleteGuard"/>: a breach rolls the transaction back (Sync's guard, ratio 0.5 by default).
/// </summary>
public sealed class KeyReconcileDeleteStrategy : IApplyStrategy
{
    public string Name => StrategyNames.KeyReconcileDelete;
    public StrategyCaps Needs => StrategyCaps.Transaction | StrategyCaps.KeyColumns;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var keys = StrategyHelpers.Keys(r, Name);
        TransferColumn? scope = r.ScopeColumn is null ? null : StrategyHelpers.Column(r, r.ScopeColumn, "scope column");
        if (scope is not null && (r.RangeStart is null || r.RangeEnd is null)) throw new ArgumentException("a scope column needs range start and end");

        void Scope(StatementBuilder b, string alias)
        {
            if (scope is null) return;
            var c = alias + b.Q(scope.Name);
            b.Append($"{c} >= {b.Param("range_start", scope.Type, r.RangeStart)} AND {c} < {b.Param("range_end", scope.Type, r.RangeEnd)}");
        }

        var count = new StatementBuilder(d);
        count.Append($"SELECT COUNT(*) FROM {count.T(r.Target)}");
        if (scope is not null) { count.Append(" WHERE "); Scope(count, ""); }

        var del = new StatementBuilder(d);
        var target = del.T(r.Target);
        del.Append($"DELETE FROM {target} WHERE ");
        if (scope is not null) { Scope(del, ""); del.Append(" AND "); }
        del.Append($"NOT EXISTS (SELECT 1 FROM {del.T(r.Stage)} s WHERE {string.Join(" AND ", keys.Select(k => $"s.{del.Q(k.Name)} = {target}.{del.Q(k.Name)}"))})");

        return new StatementSet(Name,
        [
            count.Build($"{Name}:count", new WriteIntent($"{Name}:count", WriteKind.None, r.Target.ToString()), StatementRole.CountScope),
            del.Build($"{Name}:delete", StrategyHelpers.Intent(Name, "delete", WriteKind.Delete, r, WriteRisk.Destructive)),
        ], true, Guard: r.Opts.Guard, Notes: ["rows with a NULL key in the target are deleted only if the scope covers them and no stage key equals NULL (NULL never matches)"]);
    }
}
