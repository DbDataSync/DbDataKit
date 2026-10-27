using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Emit;

namespace DbDataKit.Semantic;

/// <summary>One thing a hierarchy check found: the pair of levels, what kind of finding, and some members that show it.</summary>
public sealed record HierarchyFinding(string Hierarchy, string Parent, string Child, string Kind, List<string> Examples);

public sealed partial class QisEngine
{
    /// <summary>
    /// A hierarchy says each level is many-to-one onto the one above, and the engine relies on it for share_of_parent and
    /// for rolling up. This checks the claim against the data: for each pair of adjacent levels, every child member must
    /// have exactly one parent. A finding is <c>many_to_one_violated</c> (with examples), or <c>unchecked</c> when no source
    /// binds both levels. The pairs are read from the dimension table when both levels are columns of one table, so a member
    /// with no fact rows is checked too; otherwise from the fact rows.
    /// </summary>
    public List<HierarchyFinding> CheckHierarchies()
    {
        var findings = new List<HierarchyFinding>();
        foreach (var (name, hierarchy) in cat.Doc.Hierarchies)
            for (int i = 0; i + 1 < hierarchy.Levels.Count; i++)
            {
                var (parent, child) = (hierarchy.Levels[i], hierarchy.Levels[i + 1]);
                var source = cat.Doc.Sources.Where(s => cat.Binding(parent, s.Key) is not null && cat.Binding(child, s.Key) is not null)
                    .OrderBy(s => s.Value.Kind == "ssas" ? 1 : 0).Select(s => s.Key).FirstOrDefault();
                if (source is null) { findings.Add(new HierarchyFinding(name, parent, child, "unchecked", [])); continue; }

                var (pb, cb) = (cat.Binding(parent, source)!, cat.Binding(child, source)!);
                List<Dictionary<string, object?>> rows;
                if (pb.Join is not null && pb.Join == cb.Join && CanReadDimension(cat.Doc.Sources[source], pb, cb))
                {
                    rows = ReadDimension(source, SqlEmitter.BuildPairs(cat, source, parent, child), () => DaxEmitter.BuildPairs(cat, source, parent, child))
                        .Select(r => new Dictionary<string, object?> { ["p"] = r[Catalog.ColName(parent)], ["c"] = r[Catalog.ColName(child)] }).ToList();
                }
                else
                {
                    var pairs = Scan(source, [parent, child], []);
                    rows = pairs.Select(r => new Dictionary<string, object?> { ["p"] = r[Catalog.ColName(parent)], ["c"] = r[Catalog.ColName(child)] }).ToList();
                }
                var bad = rows.Where(r => r["c"] is not null).GroupBy(r => r["c"]!.ToString()!)
                    .Select(g => (Child: g.Key, Parents: g.Select(r => r["p"]?.ToString() ?? "(none)").Distinct().Order().ToList()))
                    .Where(g => g.Parents.Count > 1).OrderBy(g => g.Child, StringComparer.Ordinal).ToList();
                if (bad.Count > 0)
                    findings.Add(new HierarchyFinding(name, parent, child, "many_to_one_violated",
                        bad.Take(5).Select(g => $"{g.Child} -> {string.Join(", ", g.Parents)}").ToList()));
            }
        return findings;
    }
}
