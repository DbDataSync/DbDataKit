using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Knowledge;

/// <summary>Where generated knowledge came from: the published definition, its version and channel, who owns it, and what it reads from and what reads it (names, as the lineage graph names them).</summary>
/// <param name="Kind">The definition kind (<c>semantic-model</c>).</param>
/// <param name="Name">The definition's name.</param>
/// <param name="Version">The published version (a number or a short hash).</param>
/// <param name="Channel">The channel it was staged or activated in.</param>
/// <param name="Owner">The definition's owner (a group of the identity home), or null.</param>
/// <param name="Upstream">Direct upstream: what this definition reads.</param>
/// <param name="Downstream">Direct downstream: what reads this definition.</param>
public sealed record GeneratedSource(string Kind, string Name, string Version, string Channel, string? Owner = null, IReadOnlyList<string>? Upstream = null, IReadOnlyList<string>? Downstream = null)
{
    /// <summary>The key a definition's items are kept and retired under.</summary>
    public string Key => $"{Kind}:{Name}";
}

/// <summary>What a definition has that nobody described: members with no <c>description</c>, for "12 measures in Sales have no description".</summary>
public sealed record UndescribedReport(string Model, IReadOnlyList<(string Kind, string Name)> Missing)
{
    public int Count(string kind) => Missing.Count(m => m.Kind == kind);

    public string Sentence()
    {
        var parts = new[] { "measure", "attribute", "dimension" }.Select(k => (Kind: k, N: Count(k))).Where(p => p.N > 0).Select(p => $"{p.N} {p.Kind}{(p.N == 1 ? "" : "s")}").ToList();
        return parts.Count == 0 ? $"Everything in {Model} has a description." : $"{(parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1])} in {Model} {(Missing.Count == 1 ? "has" : "have")} no description.";
    }
}

/// <summary>
/// Generated knowledge (plan 27a KN12, plan 27 section 2.7): what a published definition says about itself, rendered as small knowledge items, one per subject, so nothing is typed twice and the index can answer "what is revenue" from the model's own words. This is
/// the renderer, a function of the definition and where it came from: the same definition always gives the same items, so a version that changed one measure changes one item's text and only that one is indexed again. Rendered for a <b>semantic model</b>:
/// the model, each dimension, each attribute, each measure, each hierarchy and each saved query. Items have the id <c>generated:semantic-model:Sales/measure:revenue</c>, role reference, are about the subject they describe (so who may see an item is who may read its
/// subject, KN09), and name their provenance (the definition; the version and channel are kept beside the item by the index, not in its text, so a new version that did not change an item does not change the item) and the direct upstream and downstream. <b>Volatile facts are never rendered</b>: no row counts, no run times, no state of a deployment (that is what <c>lineage</c> is for, as a tool).
/// </summary>
public static class GeneratedKnowledge
{
    public const string IdPrefix = "generated:";

    private static string Q(string text) => JsonSerializer.Serialize(text);

    private static string? Text(JsonNode? node) => node switch { JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => s, _ => null };

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Entries(JsonNode? node) => node is JsonObject o ? o : [];

    private static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray a ? a.Select(Text).OfType<string>() : [];

    /// <summary>Renders a semantic model's items, in a fixed order. <paramref name="catalog"/> is the catalog document as JSON (the published content).</summary>
    public static IReadOnlyList<KnowledgeItem> RenderSemanticModel(JsonNode catalog, GeneratedSource source)
    {
        var model = Text(catalog["name"]) ?? source.Name;
        var items = new List<KnowledgeItem>();

        void Add(string idTail, string title, IEnumerable<(string Key, string Value)> about, string body)
        {
            var id = $"{IdPrefix}semantic-model:{model}{idTail}";
            var front = new StringBuilder("---\nknowledge: 1\n");
            front.Append($"title: {Q(title)}\nrole: reference\nabout:\n");
            var first = true;
            foreach (var (key, value) in about) { front.Append(first ? "  - " : "    ").Append(key).Append(": ").Append(Q(value)).Append('\n'); first = false; }
            if (source.Owner is { Length: > 0 }) front.Append($"owner: {Q(source.Owner)}\n");
            front.Append("---\n").Append(body.TrimEnd()).Append("\n\n").Append(Provenance(source, id)).Append('\n');
            var result = KnowledgeItemLoader.Load(front.ToString(), id, id);
            items.Add(result.Item ?? throw new InvalidOperationException($"The generated item {id} does not read: {string.Join("; ", result.Diagnostics)}"));
        }

        (string, string) ModelSubject() => ("semantic-model", model);

        // the model
        var dimensions = Entries(catalog["dimensions"]).ToList();
        var measures = Entries(catalog["measures"]).ToList();
        var queries = Entries(catalog["queries"]).ToList();
        var hierarchies = Entries(catalog["hierarchies"]).ToList();
        var body = new StringBuilder($"# Semantic model {model}\n\n");
        if (Text(catalog["description"]) is { } description) body.Append(description).Append("\n\n");
        if (catalog["ai_context"] is { } context) body.Append("How to use it: ").Append(Text(context) ?? context.ToJsonString()).Append("\n\n");
        body.Append($"It has {Count(dimensions.Count, "dimension")}, {Count(measures.Count, "measure")}, {Count(hierarchies.Count, "hierarchy", "hierarchies")} and {Count(queries.Count, "saved query", "saved queries")}.\n");
        if (measures.Count > 0) body.Append("\nMeasures: ").Append(string.Join(", ", measures.Select(m => m.Key))).Append(".\n");
        if (dimensions.Count > 0) body.Append("\nDimensions: ").Append(string.Join(", ", dimensions.Select(d => d.Key))).Append(".\n");
        Add("", $"Semantic model {model}", [ModelSubject()], body.ToString());

        foreach (var (name, node) in dimensions)
        {
            var attributes = Entries(node?["attributes"]).ToList();
            var text = new StringBuilder($"# Dimension {name} of {model}\n\n");
            if (Text(node?["label"]) is { } label) text.Append("Label: ").Append(label).Append("\n\n");
            if (Text(node?["description"]) is { } description2) text.Append(description2).Append("\n\n");
            if (Text(node?["default_role"]) is { } role) text.Append($"A fact table that reaches it in more than one role reads it as {role} unless a measure says otherwise.\n\n");
            if (attributes.Count > 0) text.Append("Attributes: ").Append(string.Join(", ", attributes.Select(a => a.Key))).Append(".\n");
            var inHierarchies = hierarchies.Where(h => Strings(h.Value?["levels"]).Any(l => l.StartsWith(name + ".", StringComparison.Ordinal))).Select(h => h.Key).ToList();
            if (inHierarchies.Count > 0) text.Append("\nIt is a level of the hierarchy ").Append(string.Join(", ", inHierarchies)).Append(".\n");
            Add($"/dimension:{name}", $"Dimension {name} of {model}", [("semantic-model", model), ("dimension", name)], text.ToString());

            foreach (var (attribute, attributeNode) in attributes)
            {
                var attr = new StringBuilder($"# Attribute {name}.{attribute} of {model}\n\n");
                if (Text(attributeNode?["label"]) is { } aLabel) attr.Append("Label: ").Append(aLabel).Append("\n\n");
                if (Text(attributeNode?["description"]) is { } aDescription) attr.Append(aDescription).Append("\n\n");
                if (Text(attributeNode?["type"]) is { } type) attr.Append("Type: ").Append(type).Append(".\n");
                var synonyms = Strings(attributeNode?["synonyms"]).ToList();
                if (synonyms.Count > 0) attr.Append("Also called: ").Append(string.Join(", ", synonyms)).Append(".\n");
                var members = Strings(attributeNode?["members"]).ToList();
                if (members.Count > 0 && members.Count <= 20) attr.Append("Its members: ").Append(string.Join(", ", members)).Append(".\n");
                Add($"/attribute:{name}.{attribute}", $"Attribute {name}.{attribute} of {model}", [("semantic-model", model), ("dimension", name)], attr.ToString());
            }
        }

        foreach (var (name, node) in measures)
        {
            var text = new StringBuilder($"# Measure {name} of {model}\n\n");
            if (Text(node?["label"]) is { } label) text.Append("Label: ").Append(label).Append("\n\n");
            if (Text(node?["description"]) is { } description3) text.Append(description3).Append("\n\n");
            if (Text(node?["format"]) is { } format) text.Append("Shown as: ").Append(format).Append(".\n");
            var synonyms = Strings(node?["synonyms"]).ToList();
            if (synonyms.Count > 0) text.Append("Also called: ").Append(string.Join(", ", synonyms)).Append(".\n");
            Add($"/measure:{name}", $"Measure {name} of {model}", [("semantic-model", model), ("measure", name)], text.ToString());
        }

        foreach (var (name, node) in hierarchies)
        {
            var levels = Strings(node?["levels"]).ToList();
            Add($"/hierarchy:{name}", $"Hierarchy {name} of {model}", [ModelSubject()], $"# Hierarchy {name} of {model}\n\nFrom the top level to the bottom: {string.Join(" > ", levels)}.\n");
        }

        foreach (var (name, node) in queries)
        {
            var text = new StringBuilder($"# Saved query {name} of {model}\n\n");
            if (Text(node?["description"]) is { } description4) text.Append(description4).Append("\n\n");
            if (Entries(node?["parameters"]).ToList() is { Count: > 0 } parameters)
                text.Append("Parameters: ").Append(string.Join("; ", parameters.Select(p => $"{p.Key}{(Text(p.Value?["for"]) is { } f ? $" (for {f})" : "")}{(p.Value?["default"] is { } d ? $", default {Text(d) ?? d.ToJsonString()}" : "")}"))).Append(".\n\n");
            if (Text(node?["sql"]) is { } sql) text.Append("```sql\n").Append(sql.Trim()).Append("\n```\n");
            else if (node?["request"] is { } request) text.Append("```json\n").Append(request.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).Append("\n```\n");
            Add($"/query:{name}", $"Saved query {name} of {model}", [ModelSubject()], text.ToString());
        }
        return items;
    }

    private static string Count(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    internal static string Provenance(GeneratedSource source, string id)
    {
        // the version and channel are not in the text: they change with every publish and would make every item look changed. The index keeps them beside the item (source and version), and a hit cites them.
        var text = new StringBuilder($"---\nGenerated from the published {source.Kind} {source.Name}. This is rendered from the definition and changes with it; edit the definition, not this text.");
        if (source.Upstream is { Count: > 0 }) text.Append("\nReads from: ").Append(string.Join(", ", source.Upstream)).Append('.');
        if (source.Downstream is { Count: > 0 }) text.Append("\nRead by: ").Append(string.Join(", ", source.Downstream)).Append('.');
        return text.ToString();
    }

    /// <summary>Which members of a semantic model nobody described: the measures, dimensions and attributes with no <c>description</c>, in the model's order.</summary>
    public static UndescribedReport Undescribed(JsonNode catalog)
    {
        var missing = new List<(string, string)>();
        foreach (var (name, node) in Entries(catalog["measures"])) if (Text(node?["description"]) is null) missing.Add(("measure", name));
        foreach (var (name, node) in Entries(catalog["dimensions"]))
        {
            if (Text(node?["description"]) is null) missing.Add(("dimension", name));
            foreach (var (attribute, attributeNode) in Entries(node?["attributes"])) if (Text(attributeNode?["description"]) is null) missing.Add(("attribute", $"{name}.{attribute}"));
        }
        return new UndescribedReport(Text(catalog["name"]) ?? "the model", missing);
    }
}
