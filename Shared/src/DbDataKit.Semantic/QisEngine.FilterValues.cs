using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    /// <summary>
    /// A filter that asks for a member a attribute doesn't have matches nothing, and the agent should hear that rather than get
    /// an empty answer. Only attributes whose catalog lists their members are checked (<c>members</c>), so a free-text
    /// attribute never costs a query. The error carries the closest members.
    /// </summary>
    private List<QisError> CheckFilterValues(JsonObject doc)
    {
        var errors = new List<QisError>();
        var filters = new SemanticRequest(doc).Filters;
        var known = new Dictionary<string, List<string>>();
        foreach (var (leaf, path) in Validator.Leaves(filters, "$.query.filters"))
        {
            if (leaf.Operator is not ("equals" or "not_equals") || !cat.Doc.Attributes.TryGetValue(leaf.Name, out var def)) continue;
            if (def.MemberList is null && def.MembersFrom is null) continue;
            if (!known.TryGetValue(leaf.Name, out var members)) known[leaf.Name] = members = AttributeMembers(leaf.Name);
            for (int i = 0; i < leaf.Values.Count; i++)
            {
                if (leaf.Values[i].GetValueKind() != System.Text.Json.JsonValueKind.String) continue;
                var value = leaf.Values[i].GetValue<string>();
                if (members.Contains(value)) continue;
                var close = members.Select(m => (Member: m, Score: Fuzzy.JaroWinkler(Fuzzy.Normalize(value), Fuzzy.Normalize(m))))
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Member, StringComparer.Ordinal).Take(3).Select(x => x.Member).ToList();
                errors.Add(new QisError("native_query", "invalid_filter_value", $"{path}.values[{i}]",
                    $"{value} isn't a member of {leaf.Name}.", close));
            }
        }
        return errors;
    }
}
