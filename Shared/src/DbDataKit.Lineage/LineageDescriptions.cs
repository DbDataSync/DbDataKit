namespace DbDataKit.Lineage;

/// <summary>
/// The names a node's <see cref="LineageNode.Properties"/> uses for the text a person typed once in a definition (plan 22a GP23): <c>description</c> and <c>owner</c>. Every emitter writes them through <see cref="With"/> so the rule is one: <b>a node without a description says so
/// (<see cref="LineageNode.Description"/> is null), never an empty string</b>, and the text on the node is the definition's own, so nothing needs typing twice. A catalog reads the nodes through <see cref="LineageNode.Description"/> and <see cref="Undescribed"/>.
/// </summary>
public static class LineageDescriptions
{
    public const string DescriptionKey = "description";

    public const string OwnerKey = "owner";

    /// <summary>The longest description kept on a node; a longer one is cut at a word with an ellipsis (a node is a row of a graph, not a document).</summary>
    public const int MaxLength = 1000;

    /// <summary>
    /// <paramref name="properties"/> with the description and owner added. Blank or missing text adds no key (never an empty string); the text is trimmed, its line breaks and runs of spaces become single spaces, and it is cut at <see cref="MaxLength"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string>? properties, string? description, string? owner = null)
    {
        var map = properties is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(properties, StringComparer.Ordinal);
        if (Clean(description, MaxLength) is { } d) map[DescriptionKey] = d;
        if (Clean(owner, 200) is { } o) map[OwnerKey] = o;
        return map;
    }

    internal static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (one.Length <= max) return one;
        var cut = one[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut) + "…";
    }

    /// <summary>The nodes of the graph that have no description, of the given kinds when named (a catalog's "what still needs one").</summary>
    public static IReadOnlyList<LineageNode> Undescribed(this LineageGraph graph, params NodeKind[] kinds) =>
        [.. graph.Nodes.Where(n => n.Description is null && (kinds.Length == 0 || kinds.Contains(n.Kind)))];
}
