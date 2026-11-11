using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>Maps the scope-shaped builders' output onto gate statements: slots become typed <see cref="SqlParameter"/>s (the scope's type, else the schema's type of the scope column, else text; a 64-bit integer for an ordinal).</summary>
internal static class ScopedStrategyHelpers
{
    public static (StatementSyntax Syntax, ScopedApplySpec Spec) Spec(ApplyRequest r, ISqlDialect d, bool needsKeys, string strategy)
    {
        var o = r.Scoped ?? new ScopedOptions();
        StrategyHelpers.RequireColumns(r);
        var spec = new ScopedApplySpec
        {
            Target = r.Target,
            Stage = r.Stage,
            Columns = [.. r.Schema.Columns.Select(c => c.Name)],
            KeyColumns = needsKeys ? [.. StrategyHelpers.Keys(r, strategy).Select(k => k.Name)] : [],
            Scope = o.Scope,
            Marker = o.Marker,
            Ordinal = o.Ordinal,
            Override = o.Override,
            DeleteUnmatchedInScope = o.DeleteUnmatchedInScope,
            SequenceColumn = o.SequenceColumn,
        };
        return (o.Syntax ?? StatementSyntax.From(d), spec);
    }

    private static CanonicalType? ScopeType(ScopeFilter scope) => scope switch
    {
        ScopeFilter.ValueList l => l.Type,
        ScopeFilter.ValueRange g => g.Type,
        _ => null,
    };

    public static SqlStatement ToStatement(BuiltStatement built, string id, WriteIntent intent, ApplyRequest r)
    {
        var scopeType = ScopeType((r.Scoped ?? new ScopedOptions()).Scope);
        CanonicalType Type(ParameterSlot p)
        {
            if (p.Role is ParameterRole.OrdinalAfter or ParameterRole.OrdinalUpTo) return CanonicalType.Of(CanonicalKind.Int64);
            if (scopeType is { } t) return t;
            var i = p.Column is null ? -1 : r.Schema.IndexOf(p.Column);
            return i >= 0 ? r.Schema[i].Type : CanonicalType.String();
        }
        return new SqlStatement(id, built.Text, [.. built.Slots.Select(p => new SqlParameter(p.Marker, p.Name, Type(p), p.Value))], intent);
    }
}

/// <summary>
/// Replaces what lies in a scope (all rows, a list of values or a half-open range of one column) with the stage: delete the scope, then insert the stage
/// rows, leaving out delete-marked ones. Keyless: nothing is joined row by row, so it runs the same on every engine. The refill can be one ordinal slice
/// of the stage (see <see cref="ScopedStatements"/> for a caller that chunks).
/// </summary>
public sealed class DeleteInsertByScopeStrategy : IApplyStrategy
{
    public string Name => StrategyNames.DeleteInsertByScope;
    public StrategyCaps Needs => StrategyCaps.Transaction;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var (syntax, spec) = ScopedStrategyHelpers.Spec(r, d, needsKeys: false, Name);
        var notes = new List<string>();
        if (spec.Override == GeneratedColumnOverride.Bracketed) notes.Add("the caller brackets the insert with its engine's generated-column override");
        return new StatementSet(Name,
        [
            ScopedStrategyHelpers.ToStatement(ScopedStatements.ScopeDelete(syntax, spec), $"{Name}:delete", StrategyHelpers.Intent(Name, "delete", WriteKind.Delete, r, WriteRisk.Destructive), r),
            ScopedStrategyHelpers.ToStatement(ScopedStatements.Refill(syntax, spec), $"{Name}:insert", StrategyHelpers.Intent(Name, "insert", WriteKind.Insert, r), r),
        ], true, Notes: notes);
    }
}

/// <summary>
/// Merges the stage into the target by key, the way a change feed needs it: a delete-marked stage row deletes its target row, an ordinal slice limits the
/// stage rows, and (SQL Server) target rows in a scope that the stage does not mention can be deleted so a reload converges. One MERGE statement.
/// </summary>
public sealed class MergeStagedChangesStrategy : IApplyStrategy
{
    public string Name => StrategyNames.MergeStagedChanges;
    public StrategyCaps Needs => StrategyCaps.Transaction | StrategyCaps.KeyColumns | StrategyCaps.MergeOrUpsert;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var (syntax, spec) = ScopedStrategyHelpers.Spec(r, d, needsKeys: true, Name);
        if (spec.DeleteUnmatchedInScope && !syntax.TransactSql)
            throw new StrategyNotSupportedException(Name, d.Id.Value, "deleting what the stage does not mention within a scope needs MERGE into a CTE with WHEN NOT MATCHED BY SOURCE (SQL Server)");
        var risk = spec.DeleteUnmatchedInScope ? WriteRisk.Destructive : spec.Marker is null ? WriteRisk.Safe : WriteRisk.Risky;
        var kind = WriteKind.Insert | WriteKind.Update | (spec.Marker is null && !spec.DeleteUnmatchedInScope ? 0 : WriteKind.Delete);
        var notes = new List<string> { "stage keys must be unique" };
        if (spec.Override == GeneratedColumnOverride.Bracketed) notes.Add("the caller brackets the merge with its engine's generated-column override");
        return new StatementSet(Name,
            [ScopedStrategyHelpers.ToStatement(ScopedStatements.Merge(syntax, spec), $"{Name}:merge", StrategyHelpers.Intent(Name, "merge", kind, r, risk), r)],
            true, Notes: notes);
    }
}

/// <summary>
/// Applies the stage as a change set by key with no MERGE: the stage is collapsed to its last row per key (arrival order), then three statements run in one
/// transaction. Delete-marked keys are deleted from the target, the other keys are updated where the target holds them and inserted where it does not.
/// Replaying the same stage gives the same target, and any engine can run it (the UPDATE's join is spelled per engine, see <see cref="UpdateStyle"/>). The key must
/// be a primary key or at least free of NULLs and unique in the target: a row without a complete key matches nothing. A key change reaches the stage as a delete of
/// the old key and an insert of the new one (the readers say so); the DELETE runs first so a unique value that moves between rows never collides.
/// </summary>
public sealed class ApplyStagedChangesStrategy : IApplyStrategy
{
    public string Name => StrategyNames.ApplyStagedChanges;
    public StrategyCaps Needs => StrategyCaps.Transaction | StrategyCaps.KeyColumns;

    public StatementSet Build(ApplyRequest r, ISqlDialect d)
    {
        var (syntax, spec) = ScopedStrategyHelpers.Spec(r, d, needsKeys: true, Name);
        // a change set always says what each row is for: without a marker of its own the stage's `__Operation` column (`D` deletes) is meant
        spec = spec with { Marker = spec.Marker ?? new OperationMarker() };
        var statements = new List<SqlStatement>
        {
            ScopedStrategyHelpers.ToStatement(ScopedStatements.CollapseStage(syntax, spec), $"{Name}:collapse",
                new WriteIntent($"{Name}:collapse", WriteKind.Delete, r.Stage.ToString()), r),
            ScopedStrategyHelpers.ToStatement(ScopedStatements.StagedDelete(syntax, spec), $"{Name}:delete",
                StrategyHelpers.Intent(Name, "delete", WriteKind.Delete, r, WriteRisk.Risky), r),
        };
        if (ScopedStatements.StagedUpdate(syntax, spec) is { } update)
            statements.Add(ScopedStrategyHelpers.ToStatement(update, $"{Name}:update", StrategyHelpers.Intent(Name, "update", WriteKind.Update, r), r));
        statements.Add(ScopedStrategyHelpers.ToStatement(ScopedStatements.StagedInsert(syntax, spec), $"{Name}:insert", StrategyHelpers.Intent(Name, "insert", WriteKind.Insert, r), r));
        var notes = new List<string> { "stage rows are collapsed to the last per key first; the key must be NOT NULL and unique in the target" };
        if (spec.Override == GeneratedColumnOverride.Bracketed) notes.Add("the caller brackets the insert with its engine's generated-column override");
        return new StatementSet(Name, statements, true, Notes: notes);
    }
}
