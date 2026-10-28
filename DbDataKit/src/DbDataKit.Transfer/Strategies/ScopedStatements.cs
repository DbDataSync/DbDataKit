using System.Text;
using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>A scope rendered as predicate text plus the parameters it refers to.</summary>
public sealed record RenderedScope(string Predicate, IReadOnlyList<ParameterSlot> Slots);

/// <summary>
/// Collects the parameter slots of one statement. Named styles reuse a slot when a name is referenced twice; positional style binds every occurrence.
/// The same rule the dialect-bound <see cref="StatementBuilder"/> applies.
/// </summary>
internal sealed class SlotCollector(StatementSyntax syntax)
{
    private readonly List<ParameterSlot> _slots = [];
    private readonly Dictionary<string, ParameterSlot> _byName = new(StringComparer.Ordinal);

    public IReadOnlyList<ParameterSlot> Slots => _slots;

    public string Add(string name, ParameterRole role, string? column, object? value)
    {
        if (syntax.ParameterStyle != ParameterStyle.Positional && _byName.TryGetValue(name, out var existing)) return existing.Marker;
        var index = syntax.ParameterStyle is ParameterStyle.Positional or ParameterStyle.Numbered ? _slots.Count : _byName.Count;
        var slot = new ParameterSlot(_slots.Count, name, syntax.ParameterReference(index, name), role, column, value);
        _slots.Add(slot);
        _byName[name] = slot;
        return slot.Marker;
    }
}

/// <summary>
/// The statement shapes of a staged apply that Sync's writers use and the key-based strategies do not have (plan 13, A16a): the scope delete, the
/// refill that leaves out delete-marked rows and can take one ordinal slice, and the MERGE that honours the marker, takes an ordinal slice and can
/// delete what the stage does not mention within a scope. Dialect-free: quoting and placeholders come from the caller's <see cref="StatementSyntax"/>,
/// values never reach the text, and each statement comes back with the <see cref="ParameterSlot"/>s the caller binds.
/// </summary>
public static class ScopedStatements
{
    public const string AfterOrdinalName = "afterOrdinal";
    public const string UpToOrdinalName = "upToOrdinal";
    public const string RangeMinName = "segMin";
    public const string RangeMaxName = "segMax";
    public static string ListValueName(int index) => $"seg{index}";

    // Every generated parameter name starts with a letter, not an underscore: Oracle's bind-variable grammar rejects a leading underscore (ORA-00911).

    /// <summary>
    /// Renders a scope. <paramref name="columnReference"/> says how the scope column is written in the statement (the quoted identifier by default); a
    /// reader that joins the table under an alias resolves its own reference.
    /// </summary>
    public static RenderedScope RenderScope(StatementSyntax syntax, ScopeFilter scope, Func<string, string>? columnReference = null)
    {
        var slots = new SlotCollector(syntax);
        return new RenderedScope(Predicate(syntax, scope, slots, columnReference), slots.Slots);
    }

    private static string Predicate(StatementSyntax syntax, ScopeFilter scope, SlotCollector slots, Func<string, string>? columnReference)
    {
        var reference = columnReference ?? syntax.QuoteIdentifier;
        switch (scope)
        {
            case ScopeFilter.AllRows:
                return "1 = 1";
            case ScopeFilter.ValueList list:
            {
                if (list.Values.Count == 0)
                    throw new InvalidOperationException(
                        $"List segment on '{list.Column}' has no values. An empty list matches nothing, which " +
                        "for a reconciling writer would delete the target's entire scope — reject it rather " +
                        "than render 'IN ()' (which isn't valid SQL anyway).");
                var placeholders = string.Join(", ", list.Values.Select((v, i) => slots.Add(ListValueName(i), ParameterRole.ScopeValue, list.Column, v)));
                return $"{reference(list.Column)} IN ({placeholders})";
            }
            case ScopeFilter.ValueRange range:
            {
                var column = reference(range.Column);
                var min = slots.Add(RangeMinName, ParameterRole.ScopeMin, range.Column, range.Min);
                var max = slots.Add(RangeMaxName, ParameterRole.ScopeMax, range.Column, range.Max);
                return $"{column} >= {min} AND {column} < {max}";
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "unknown scope");
        }
    }

    private static string Terminator(StatementSyntax syntax) => syntax.TransactSql ? ";" : "";

    private static string Bookkeeping(StatementSyntax syntax, string column) => syntax.QuoteBookkeepingColumns ? syntax.QuoteIdentifier(column) : column;

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ColumnList(StatementSyntax syntax, ScopedApplySpec s)
    {
        if (s.Columns.Count == 0) throw new ArgumentException("the apply writes no columns");
        return string.Join(", ", s.Columns.Select(syntax.QuoteIdentifier));
    }

    /// <summary><c>DELETE FROM target WHERE scope</c>: empties the scope. Keyless: nothing is joined row by row.</summary>
    public static BuiltStatement ScopeDelete(StatementSyntax syntax, ScopedApplySpec s)
    {
        var slots = new SlotCollector(syntax);
        var predicate = Predicate(syntax, s.Scope, slots, null);
        return new BuiltStatement($"DELETE FROM {s.Target.Render(syntax)} WHERE {predicate}{Terminator(syntax)}", slots.Slots);
    }

    /// <summary>
    /// <c>INSERT INTO target (cols) SELECT cols FROM stage</c>, without delete-marked rows when there is a marker, and limited to one ordinal slice when
    /// there is one. A marker row inside the slice is still not inserted: the slice narrows which stage rows the statement carries, not what they mean.
    /// </summary>
    public static BuiltStatement Refill(StatementSyntax syntax, ScopedApplySpec s)
    {
        var slots = new SlotCollector(syntax);
        var cols = ColumnList(syntax, s);
        var target = s.Target.Render(syntax);
        var overrideGenerated = s.Override == GeneratedColumnOverride.Clause;
        if (overrideGenerated && syntax.InsertInto is null)
            throw new ArgumentException("a clause override needs the caller to render the INSERT head (StatementSyntax.InsertInto)");
        var head = syntax.InsertInto is { } insertInto ? insertInto(target, cols, overrideGenerated) : $"INSERT INTO {target} ({cols})";

        var text = new StringBuilder();
        text.Append(head).Append("\nSELECT ").Append(cols).Append(" FROM ").Append(s.Stage.Render(syntax));
        if (s.Marker is { } m)
            text.Append("\nWHERE ").Append(Bookkeeping(syntax, m.Column)).Append(" <> ").Append(Literal(m.DeleteValue));
        if (s.Ordinal is { } o)
        {
            var ordinal = Bookkeeping(syntax, o.Column);
            var after = slots.Add(AfterOrdinalName, ParameterRole.OrdinalAfter, null, o.After);
            var upTo = slots.Add(UpToOrdinalName, ParameterRole.OrdinalUpTo, null, o.UpTo);
            text.Append('\n').Append(s.Marker is null ? "WHERE" : "  AND").Append(' ').Append(ordinal).Append(" > ").Append(after);
            text.Append("\n  AND ").Append(ordinal).Append(" <= ").Append(upTo);
        }
        text.Append(Terminator(syntax));
        return new BuiltStatement(text.ToString(), slots.Slots, s.Override == GeneratedColumnOverride.Bracketed);
    }

    /// <summary>
    /// A MERGE of the stage into the target by key. A delete-marked stage row deletes its target row; other matched rows update the non-key columns; stage
    /// rows without a target row insert (unless delete-marked). With <see cref="ScopedApplySpec.DeleteUnmatchedInScope"/> the target is the scope as a CTE
    /// (filtering <i>before</i> the MERGE matches, so the engine neither scans nor locks rows outside the scope) and target rows the stage does not
    /// mention are deleted: a reload converges, which a reader that only reports rows that exist cannot do any other way.
    /// </summary>
    public static BuiltStatement Merge(StatementSyntax syntax, ScopedApplySpec s)
    {
        if (s.KeyColumns.Count == 0) throw new ArgumentException("a merge needs at least one key column");
        if (s.Override == GeneratedColumnOverride.Clause)
            throw new ArgumentException("a MERGE takes a bracketed generated-column override, not a clause");
        if (s.DeleteUnmatchedInScope && !syntax.TransactSql)
            throw new ArgumentException("deleting what the stage does not mention within a scope needs a MERGE into a CTE with WHEN NOT MATCHED BY SOURCE (Transact-SQL)");

        var slots = new SlotCollector(syntax);
        var q = syntax.QuoteIdentifier;
        var scoped = s.DeleteUnmatchedInScope;
        var nonKeys = s.Columns.Where(c => !s.KeyColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var op = s.Marker is { } m ? Bookkeeping(syntax, m.Column) : null;
        var deleted = s.Marker is { } mk ? Literal(mk.DeleteValue) : null;

        var lines = new List<string>();
        if (scoped)
        {
            var predicate = Predicate(syntax, s.Scope, slots, null);
            lines.Add($";WITH TargetScope AS (\n    SELECT * FROM {s.Target.Render(syntax)} WHERE {predicate}\n)\nMERGE INTO TargetScope AS tgt");
        }
        else
        {
            lines.Add($"MERGE INTO {s.Target.Render(syntax)} AS tgt");
        }

        var stage = s.Stage.Render(syntax);
        if (s.Ordinal is { } o)
        {
            var ordinal = Bookkeeping(syntax, o.Column);
            var after = slots.Add(AfterOrdinalName, ParameterRole.OrdinalAfter, null, o.After);
            var upTo = slots.Add(UpToOrdinalName, ParameterRole.OrdinalUpTo, null, o.UpTo);
            lines.Add($"USING (SELECT * FROM {stage}\n     WHERE {ordinal} > {after}\n       AND {ordinal} <= {upTo}) AS src");
        }
        else
        {
            lines.Add($"USING {stage} AS src");
        }

        lines.Add("ON " + string.Join(" AND ", s.KeyColumns.Select(k => $"tgt.{q(k)} = src.{q(k)}")));
        if (op is not null) lines.Add($"WHEN MATCHED AND src.{op} = {deleted} THEN DELETE");

        var set = string.Join(", ", nonKeys.Select(c => $"{(syntax.TransactSql ? "tgt." : "")}{q(c)} = src.{q(c)}"));
        if (nonKeys.Count > 0)
            lines.Add($"WHEN MATCHED{(op is null ? "" : $" AND src.{op} <> {deleted}")} THEN UPDATE SET {set}");
        else if (op is not null)
            lines.Add("");   // the statement has always carried a blank line where an empty update clause would be

        var notMatched = syntax.TransactSql ? "WHEN NOT MATCHED BY TARGET" : "WHEN NOT MATCHED";
        var cols = ColumnList(syntax, s);
        var values = string.Join(", ", s.Columns.Select(c => $"src.{q(c)}"));
        lines.Add(op is null
            ? $"{notMatched} THEN INSERT ({cols}) VALUES ({values})"
            : $"{notMatched} AND src.{op} <> {deleted}\n    THEN INSERT ({cols}) VALUES ({values})");
        if (scoped) lines.Add("WHEN NOT MATCHED BY SOURCE THEN DELETE");

        return new BuiltStatement(string.Join("\n", lines) + Terminator(syntax), slots.Slots, s.Override == GeneratedColumnOverride.Bracketed);
    }
}
