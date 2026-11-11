using System.Text.Json.Nodes;

namespace DbDataKit.Cli;

/// <summary>
/// Builders for the JSON Schema of a verb's <c>data</c>. The shape is closed (no property that is not listed), and a property is present when the command got far enough to produce it,
/// as in DbDataBuild's documents; <see cref="Required"/> marks the ones that are always there.
/// </summary>
public static class DataSchemas
{
    /// <summary>A verb that finds nothing to report beyond its text: <c>data</c> is an empty object.</summary>
    public const string None = """{ "type": "object", "additionalProperties": false }""";

    public const string String = """{ "type": "string" }""";
    public const string Boolean = """{ "type": "boolean" }""";
    public const string Integer = """{ "type": "integer" }""";
    public const string StringList = """{ "type": "array", "items": { "type": "string" } }""";

    /// <summary>A closed object with these properties (name to schema fragment); <paramref name="required"/> names the ones that are always present.</summary>
    public static string Object(IEnumerable<(string Name, string Schema)> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = JsonNode.Parse(schema);
        var node = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = props };
        if (required.Length > 0) node["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return node.ToJsonString();
    }

    /// <summary>An array of closed objects.</summary>
    public static string ListOf(string itemSchema) => $$"""{ "type": "array", "items": {{itemSchema}} }""";

    public static string[] Required(params string[] names) => names;
}
