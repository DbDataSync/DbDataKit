using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;

namespace DbDataKit.Semantic.Authoring;

/// <summary>
/// The JSON Schema of a catalog <b>written by hand without its physical layer</b> (what a DbDataBuild project keeps in <c>semantic/</c>): the catalog schema with
/// <c>tables</c> removed, <c>sources</c> reduced to the settings a person may give a source (<c>totals</c>, <c>datetime_zone</c>; the kind comes from the connection) and
/// <c>is_time</c> allowed on a binding. It is derived from the catalog schema, never written twice, so the two cannot drift; products that publish the schema to
/// editors and agents write <see cref="Text"/> to a file and a test of theirs compares it.
/// </summary>
public static class AuthoringSchema
{
    /// <summary>The <c>$id</c> of the schema.</summary>
    public const string Id = "https://example.internal/qis/semantic.schema.json";

    private static readonly Lazy<string> Generated = new(Build);
    private static readonly Lazy<JsonSchema> Parsed = new(() =>
    {
        _ = SchemaStore.Get("catalog");   // loads and registers the embedded schemas, which this one refers to by their ids
        return JsonSchema.FromText(Generated.Value);
    });

    /// <summary>The schema as indented JSON text with a final line feed.</summary>
    public static string Text => Generated.Value;

    /// <summary>The schema, ready to evaluate a document with.</summary>
    public static JsonSchema Schema => Parsed.Value;

    private static string Build()
    {
        var assembly = typeof(AuthoringSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream("DbDataKit.Semantic.schemas.catalog.schema.json")
            ?? throw new InvalidOperationException("The catalog schema is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        var root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();

        root["$id"] = Id;
        root["title"] = "Semantic model (authoring format)";
        root["description"] = "A semantic model as a person writes it in a DbDataBuild project: dimensions, measures, hierarchies, calendars and segments. The physical layer (tables, columns, keys, sources) is not written: " +
            "a binding's or measure's `table` names a model of the project, a source is a connection, and the tool generates what the catalog format calls tables from the models' definitions. " +
            "Relationships are written between models, by their columns.";

        var required = root["required"]!.AsArray();
        foreach (var gone in new[] { "sources", "tables" })
            for (var i = required.Count - 1; i >= 0; i--)
                if (required[i]!.GetValue<string>() == gone) required.RemoveAt(i);

        var properties = root["properties"]!.AsObject();
        properties.Remove("tables");
        properties["sources"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Settings of the connections the model reads, by connection name. Optional: a connection the model uses needs no entry. The kind of a source comes from the connection's engine.",
            ["additionalProperties"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["datetime_zone"] = root["properties"]!["sources"]!["additionalProperties"]!["properties"]!["datetime_zone"]!.DeepClone(),
                    ["totals"] = root["properties"]!["sources"]!["additionalProperties"]!["properties"]!["totals"]!.DeepClone(),
                },
            },
        };
        properties["relationships"]!["description"] = "Many-to-one relationships between models: `from` is the model that holds the key, `to` the model whose grain the key is. They are instantiated on every connection both models are built on.";

        var defs = root["$defs"]!.AsObject();
        var binding = defs["binding"]!["properties"]!.AsObject();
        binding["is_time"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "The bound column holds periods (month labels, fiscal years) that sort in time order, though its type is not a date: the same as marking the column is_time in a physical layer.",
        };
        var relationship = defs["relationship"]!.AsObject();
        relationship["description"] = "A many-to-one relationship between two models of the project.";

        // definitions only the physical layer used go with it, one at a time because each can be the last reference to the next
        foreach (var name in new[] { "table", "column", "expression" })
            if (!root.ToJsonString().Contains("#/$defs/" + name, StringComparison.Ordinal)) defs.Remove(name);

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
    }
}
