using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Knowledge;

/// <summary>
/// Generated knowledge for what Build builds (plan 27a KN13, plan 27 section 2.7): one small item per model and one per column, from the metadata documents Build already writes for each model of any kind (title, description, tags, properties, model kind, columns and
/// types, what it reads), published per project and connection as the definition <c>build-metadata</c>. Ids are stable (<c>generated:build-model:sales/dim_customer</c>, <c>.../column:region</c>), every item is a reference about the model (so who may see it is who may see the project's model, KN09), names its owner and
/// provenance, and says what reads from it and what it reads from, direct only. Like the semantic renderer it is a function of the document: the same document gives the same items, so a publish that changed one column's description changes one item. A model that is no longer in the
/// document has no item at the next publish (the index's sync retires what is not rendered).
/// </summary>
public static class GeneratedBuildKnowledge
{
    /// <summary>The key a project's items are kept under: the definition <c>build-metadata</c> named <c>project.connection</c>.</summary>
    public static string DefinitionName(string project, string connection) => $"{project}.{connection}";

    private static string Q(string text) => JsonSerializer.Serialize(text);

    private static string? Text(JsonNode? node) => node switch { JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => s, _ => null };

    private static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray a ? a.Select(Text).OfType<string>() : [];

    /// <summary>
    /// Renders a published <c>build-metadata</c> document: <c>{ "project": ..., "connection": ..., "models": [ the metadata document of each model ] }</c>, in the order of the models.
    /// </summary>
    public static IReadOnlyList<KnowledgeItem> Render(JsonNode document, GeneratedSource source)
    {
        var project = Text(document["project"]) ?? throw new ArgumentException("The build-metadata document has no `project`.");
        var connection = Text(document["connection"]);
        var models = (document["models"] as JsonArray ?? []).OfType<JsonObject>().Where(m => Text(m["name"]) is not null).ToList();
        var names = models.Select(m => Text(m["name"])!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // who reads each model, from what the models say they read (direct only)
        var readBy = models.SelectMany(m => (m["upstream"] as JsonArray ?? []).Select(u => (Reader: Text(m["name"])!, Read: Text(u?["name"]))))
            .Where(p => p.Read is not null && names.Contains(p.Read!)).GroupBy(p => p.Read!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Reader).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        var items = new List<KnowledgeItem>();

        foreach (var model in models)
        {
            var name = Text(model["name"])!;
            var reads = (model["upstream"] as JsonArray ?? []).Select(u => Text(u?["name"])).OfType<string>().ToList();
            var perModel = source with { Upstream = reads, Downstream = readBy.GetValueOrDefault(name) };
            var columns = (model["columns"] as JsonArray ?? []).OfType<JsonObject>().Where(c => Text(c["name"]) is not null).ToList();

            void Add(string idTail, string title, string body)
            {
                var id = $"{GeneratedKnowledge.IdPrefix}build-model:{project}/{name}{idTail}";
                var front = new StringBuilder("---\nknowledge: 1\n");
                front.Append($"title: {Q(title)}\nrole: reference\nabout:\n  - build-project: {Q(project)}\n    model: {Q(name)}\n");
                if (source.Owner is { Length: > 0 }) front.Append($"owner: {Q(source.Owner)}\n");
                front.Append("---\n").Append(body.TrimEnd()).Append("\n\n").Append(GeneratedKnowledge.Provenance(perModel, id)).Append('\n');
                var result = KnowledgeItemLoader.Load(front.ToString(), id, id);
                items.Add(result.Item ?? throw new InvalidOperationException($"The generated item {id} does not read: {string.Join("; ", result.Diagnostics)}"));
            }

            var text = new StringBuilder($"# Model {name} of the Build project {project}\n\n");
            if (Text(model["title"]) is { } title) text.Append(title).Append("\n\n");
            if (Text(model["description"]) is { } description) text.Append(description).Append("\n\n");
            text.Append($"It is a {Text(model["kind"]) ?? "model"}{(connection is null ? "" : $" built on the connection {connection}")}.\n");
            var tags = Strings(model["tags"]).ToList();
            if (tags.Count > 0) text.Append("Tags: ").Append(string.Join(", ", tags)).Append(".\n");
            if (model["properties"] is JsonObject properties && properties.Count > 0) text.Append("Properties: ").Append(string.Join("; ", properties.Select(p => $"{p.Key} = {Text(p.Value) ?? p.Value?.ToJsonString()}"))).Append(".\n");
            if (columns.Count > 0) text.Append($"\nColumns ({columns.Count}): ").Append(string.Join(", ", columns.Select(c => $"{Text(c["name"])} ({Text(c["logical_type"]) ?? "type not stated"})"))).Append(".\n");
            Add("", $"Model {name} of {project}", text.ToString());

            foreach (var column in columns)
            {
                var columnName = Text(column["name"])!;
                var c = new StringBuilder($"# Column {name}.{columnName} of the Build project {project}\n\n");
                if (Text(column["title"]) is { } columnTitle) c.Append(columnTitle).Append("\n\n");
                if (Text(column["description"]) is { } columnDescription) c.Append(columnDescription).Append("\n\n");
                c.Append($"Type: {Text(column["logical_type"]) ?? "not stated"}{(column["nullable"] is JsonValue n && n.TryGetValue<bool>(out var nullable) ? (nullable ? ", may be empty" : ", never empty") : "")}.\n");
                var columnTags = Strings(column["tags"]).ToList();
                if (columnTags.Count > 0) c.Append("Tags: ").Append(string.Join(", ", columnTags)).Append(".\n");
                Add($"/column:{columnName}", $"Column {name}.{columnName} of {project}", c.ToString());
            }
        }
        return items;
    }

    /// <summary>The models of a document that carry no description, as <see cref="GeneratedKnowledge.Undescribed"/> says it for a semantic model: for the list "12 columns of dim_customer have no description".</summary>
    public static IReadOnlyList<(string Model, string Column)> UndescribedColumns(JsonNode document) =>
        [.. (document["models"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(m => (m["columns"] as JsonArray ?? []).OfType<JsonObject>().Where(c => Text(c["description"]) is null).Select(c => (Text(m["name"]) ?? "", Text(c["name"]) ?? "")))];
}
