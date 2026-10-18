using DbDataBuild.Models;

namespace DbDataBuild.Define;

public sealed record TypeChange(ColumnDefinition Declared, InferredColumn Inferred, string ResolvedType);

/// <summary>A declared column that disappeared and a new output column of the same type at the same position: possibly a rename.</summary>
public sealed record RenameCandidate(ColumnDefinition Removed, InferredColumn Added, int Position);

/// <summary>
/// How a model's declared columns differ from what its query returns (DESIGN.md 6.5). Columns match by name, case-insensitively, and
/// declared order is not compared. Declared nullability is human knowledge, so a difference there is a note, never a change:
/// lineage can propose nullability for a new column but cannot disprove a declared NOT NULL.
/// </summary>
public sealed record ColumnDiff(
    IReadOnlyList<InferredColumn> Added,
    IReadOnlyList<ColumnDefinition> Removed,
    IReadOnlyList<TypeChange> TypeChanged,
    IReadOnlyList<RenameCandidate> RenameCandidates,
    IReadOnlyList<string> Notes)
{
    public bool InSync => Added.Count == 0 && Removed.Count == 0 && TypeChanged.Count == 0;

    public static ColumnDiff Compute(IReadOnlyList<ColumnDefinition> declared, Inference inferred)
    {
        var outputByName = inferred.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var declaredByName = declared.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var removed = declared.Where(d => !outputByName.ContainsKey(d.Name)).ToList();
        var added = inferred.Columns.Where(c => !declaredByName.ContainsKey(c.Name)).ToList();

        var changed = new List<TypeChange>();
        var notes = new List<string>();
        foreach (var d in declared.Where(d => outputByName.ContainsKey(d.Name)))
        {
            var i = outputByName[d.Name];
            var resolved = i.Type.LogicalType ?? i.DuckDbType;
            if (!LogicalTypes.Equivalent(d.Type, resolved)) changed.Add(new TypeChange(d, i, resolved));
            if (!d.Nullable && i.Nullability.Nullable == true)
                notes.Add($"Column `{d.Name}` is declared NOT NULL, but the query's lineage says it can be NULL ({i.Nullability.Reason}). The declaration is kept: it is yours to decide.");
        }

        // a rename needs a removed and an added column of the same type at the same ordinal position
        var candidates = new List<RenameCandidate>();
        foreach (var r in removed)
        {
            var position = IndexOf(declared, r);
            if (position < inferred.Columns.Count && inferred.Columns[position] is { } at && added.Contains(at) &&
                LogicalTypes.Equivalent(r.Type, at.Type.LogicalType ?? at.DuckDbType) && !candidates.Any(c => c.Added == at))
                candidates.Add(new RenameCandidate(r, at, position));
        }
        return new ColumnDiff(added, removed, changed, candidates, notes);
    }

    private static int IndexOf(IReadOnlyList<ColumnDefinition> list, ColumnDefinition item)
    {
        for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }
}
