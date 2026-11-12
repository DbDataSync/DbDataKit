using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Knowledge;

/// <summary>
/// Generated knowledge for the reports of a report catalog (plan 32 RC04, plan 27a KN12's shape): one small item per report or dashboard of a <c>report-catalog/1</c> snapshot (<c>DbDataKit.ReportCatalogs</c>), so a search for a report's title finds it beside a model's measures and a
/// person's own articles. The renderer reads the snapshot as JSON (<c>{ provider, items: [{ id, path, name, type, description, owner, parameters, exports }] }</c>, snake case as the snapshot writes it), so this library does not depend on the catalogs. Folders have no item (their
/// title would only add noise). Ids are stable (<c>generated:report-catalog:ssrs/&lt;id&gt;</c>), the role is reference, and the item is about the subject <c>report-catalog: &lt;catalog&gt;</c> narrowed to <c>item: &lt;id&gt;</c>, so who may see it is decided by the catalog's own
/// policies as RC03's filter says (the host's caller answers <c>MayRead</c> for that subject; <c>ReportCatalogKnowledge</c> in <c>DbDataKit.ReportCatalogs</c> does). The text names its catalog in the title and in the body, so a hit says where the report lives; nothing volatile is rendered (no read time).
/// </summary>
public static class GeneratedCatalogKnowledge
{
    /// <summary>The definition kind a snapshot is published as.</summary>
    public const string Kind = "report-catalog";

    private static string Q(string text) => JsonSerializer.Serialize(text);

    private static string? Text(JsonNode? node) => node switch { JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => s, _ => null };

    /// <summary>The key a catalog's items are kept and retired under (<see cref="GeneratedSource.Key"/> for the snapshot's source).</summary>
    public static GeneratedSource SourceOf(string provider, string version, string channel) => new(Kind, provider, version, channel);

    public static IReadOnlyList<KnowledgeItem> Render(JsonNode snapshot, GeneratedSource source)
    {
        var provider = Text(snapshot["provider"]) ?? throw new ArgumentException("The snapshot has no `provider`.");
        var items = new List<KnowledgeItem>();
        foreach (var node in (snapshot["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = Text(node["type"]);
            if (type is null or "folder" || Text(node["id"]) is not { } itemId || Text(node["name"]) is not { } name) continue;
            var id = $"{GeneratedKnowledge.IdPrefix}{Kind}:{provider}/{itemId}";
            var title = $"{name} ({provider})";
            var body = new StringBuilder($"# {type switch { "dashboard" => "Dashboard", "report" => "Report", _ => "Report" }} {name}\n\nIt is in the report catalog {provider}");
            if (Text(node["path"]) is { } path) body.Append(", at ").Append(path);
            body.Append(".\n");
            if (Text(node["description"]) is { } description) body.Append('\n').Append(description).Append('\n');
            if (Text(node["owner"]) is { } owner) body.Append("\nOwner on the report server: ").Append(owner).Append(".\n");
            var parameters = (node["parameters"] as JsonArray ?? []).OfType<JsonObject>().Select(p => Text(p["label"]) is { } label && Text(p["name"]) is { } pn ? $"{label} ({pn})" : Text(p["name"])).OfType<string>().ToList();
            if (parameters.Count > 0) body.Append("\nParameters: ").Append(string.Join(", ", parameters)).Append(".\n");
            var formats = (node["exports"] as JsonArray ?? []).OfType<JsonObject>().Where(e => e["available"] is not JsonValue a || !a.TryGetValue<bool>(out var ok) || ok).Select(e => Text(e["format"])).OfType<string>().ToList();
            if (formats.Count > 0) body.Append("\nIt can be taken as ").Append(string.Join(", ", formats)).Append(".\n");

            var front = new StringBuilder("---\nknowledge: 1\n");
            front.Append($"title: {Q(title)}\nrole: reference\nabout:\n  - report-catalog: {Q(provider)}\n    item: {Q(itemId)}\n");
            front.Append("---\n").Append(body.ToString().TrimEnd()).Append("\n\n").Append(GeneratedKnowledge.Provenance(source, id)).Append('\n');
            var result = KnowledgeItemLoader.Load(front.ToString(), id, id);
            items.Add(result.Item ?? throw new InvalidOperationException($"The generated item {id} does not read: {string.Join("; ", result.Diagnostics)}"));
        }
        return items;
    }
}
