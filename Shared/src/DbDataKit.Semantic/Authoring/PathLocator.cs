using DbDataKit.Config.Yaml;

namespace DbDataKit.Semantic.Authoring;

/// <summary>
/// Finds the YAML node a validation path (<c>$.measures.orders.net_revenue.of</c>) names, so an error can say file, line and column. A name may itself contain dots
/// (a measure is <c>orders.net_revenue</c>) and the loader flattens a dimension's attributes (<c>dimensions.customers.region</c> for what the file writes as
/// <c>dimensions: customers: attributes: region:</c>), so a step is matched against the keys of the node it is at: the shortest run of path segments that is a key,
/// and a mapping that has an <c>attributes</c> block is entered through it when the segment is not a key of its own. When a step matches nothing, the deepest node found is the answer.
/// </summary>
internal static class PathLocator
{
    /// <summary>What a path names: the node, and the key it is the value of (null for the root and for an item of a list). A block or list is reported where its key is, a scalar where it is.</summary>
    public sealed record Found(YamlNode Node, YamlScalar? Key)
    {
        public int Line => Node is YamlScalar || Key is null ? Node.Line : Key.Line;
        public int Column => Node is YamlScalar || Key is null ? Node.Column : Key.Column;
    }

    public static Found Find(YamlNode root, string path)
    {
        var steps = path.Split(['$', '.', '/', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        var node = root;
        YamlScalar? key = null;
        for (var i = 0; i < steps.Length;)
        {
            YamlNode? next = null;
            YamlScalar? nextKey = null;
            var used = 0;
            switch (node)
            {
                case YamlMapping m:
                    for (var j = i; j < steps.Length && next is null; j++)
                        if (m.GetEntry(string.Join('.', steps[i..(j + 1)])) is { } hit) { next = hit.Value; nextKey = hit.Key; used = j - i + 1; }
                    if (next is null && m.Get("attributes") is YamlMapping attributes)
                    {
                        // a dimension: its attributes are keyed one level down
                        for (var j = i; j < steps.Length && next is null; j++)
                            if (attributes.GetEntry(string.Join('.', steps[i..(j + 1)])) is { } hit) { next = hit.Value; nextKey = hit.Key; used = j - i + 1; }
                    }
                    break;
                case YamlSequence q when int.TryParse(steps[i], out var index) && index >= 0 && index < q.Items.Count:
                    next = q.Items[index];
                    nextKey = null;
                    used = 1;
                    break;
            }
            if (next is null) break;
            node = next;
            key = nextKey;
            i += used;
        }
        return new Found(node, key);
    }
}
