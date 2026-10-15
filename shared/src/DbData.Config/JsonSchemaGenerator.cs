using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbData.Config;

/// <summary>Generates a JSON Schema (draft 2020-12) from the schema attributes, for editor aids. Never hand-written.</summary>
public static class JsonSchemaGenerator
{
    public static JsonObject Generate(Type type, string? id = null)
    {
        var schema = SchemaModel.For(type);
        var root = Node(schema, topLevel: true);
        var result = new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema" };
        if (id != null) result["$id"] = id;
        foreach (var (k, v) in root) result[k] = v?.DeepClone();
        return result;
    }

    public static JsonObject Generate<T>(string? id = null) => Generate(typeof(T), id);

    public static string ToJson(Type type, string? id = null) =>
        Generate(type, id).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonObject Node(TypeSchema schema, bool topLevel = false)
    {
        switch (schema)
        {
            case ScalarSchema s: return Scalar(s);
            case ListSchema l: return new JsonObject { ["type"] = "array", ["items"] = Node(l.Item) };
            case MapSchema m: return new JsonObject { ["type"] = "object", ["additionalProperties"] = Node(m.Value) };
            case UnionSchema u:
            {
                var one = new JsonArray();
                foreach (var (name, c) in u.Cases)
                {
                    var o = Object(c, false, u.Discriminator, name);
                    one.Add(o);
                }
                return new JsonObject { ["oneOf"] = one };
            }
            case ObjectSchema o: return Object(o, topLevel, null, null);
            default: return [];
        }
    }

    private static JsonObject Object(ObjectSchema o, bool topLevel, string? discriminator, string? caseName)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        if (discriminator != null) { props[discriminator] = new JsonObject { ["const"] = caseName }; required.Add(discriminator); }
        if (topLevel && o.HasVersion)
            props[o.VersionKey] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = o.Version, ["default"] = 1, ["description"] = "Schema version of this file (1 when absent)." };
        if (topLevel && o.AcceptsHeader)
            props[SchemaHeader.Key] = new JsonObject { ["type"] = "string", ["pattern"] = o.Kind is null ? "^[^/]+/[1-9][0-9]*$" : $"^{System.Text.RegularExpressions.Regex.Escape(o.Kind)}/[1-9][0-9]*$", ["description"] = "Alternative to the version key: `<kind>/<version>`." };
        foreach (var p in o.Properties)
        {
            var n = Node(p.Type);
            if (p.Description != null) n["description"] = p.Description;
            if (p.Min != null) n[p.Type is ScalarSchema { Underlying: var u } && u == typeof(TimeSpan) ? "x-minSeconds" : "minimum"] = Num(p.Min.Value);
            if (p.Max != null) n[p.Type is ScalarSchema { Underlying: var u2 } && u2 == typeof(TimeSpan) ? "x-maxSeconds" : "maximum"] = Num(p.Max.Value);
            if (p.Pattern != null) n["pattern"] = "^(?:" + p.Pattern + ")$";
            if (p.Default != null) n["default"] = DefaultJson(p.Default);
            if (p.Secret) { n["x-secret"] = true; n["writeOnly"] = true; }
            if (p.DeprecatedMessage != null) { n["deprecated"] = true; n["x-deprecatedMessage"] = p.DeprecatedMessage; }
            props[p.Key] = n;
            if (p.Required) required.Add(p.Key);
        }
        var result = new JsonObject { ["type"] = "object" };
        if (o.Description != null) result["description"] = o.Description;
        result["properties"] = props;
        if (required.Count > 0) result["required"] = required;
        result["additionalProperties"] = o.UnknownKeys == UnknownKeyPolicy.Preserve;
        return result;
    }

    private static JsonNode Num(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);

    private static JsonNode? DefaultJson(object d) => d switch
    {
        string s => s, bool b => b, int i => i, long l => l, double x => x, float f => f, decimal m => m, Enum e => SchemaModel.ApplyNaming(e.ToString(), KeyNaming.CamelCase),
        _ => d.ToString(),
    };

    private static JsonObject Scalar(ScalarSchema s)
    {
        var t = s.Underlying;
        JsonObject o;
        if (t == typeof(bool)) o = new() { ["type"] = "boolean" };
        else if (t.IsEnum) o = new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode?)JsonValue.Create(SchemaModel.ApplyNaming(n, KeyNaming.CamelCase))).ToArray()) };
        else if (t == typeof(string)) o = new() { ["type"] = "string" };
        else if (t == typeof(TimeSpan)) o = new() { ["type"] = "string", ["description"] = "Duration: 30s, 5m, 2h, 1d, 250ms or hh:mm:ss." };
        else if (t == typeof(Guid)) o = new() { ["type"] = "string", ["format"] = "uuid" };
        else if (t == typeof(Uri)) o = new() { ["type"] = "string", ["format"] = "uri-reference" };
        else if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) o = new() { ["type"] = "string", ["format"] = "date-time" };
        else if (t == typeof(char)) o = new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1 };
        else if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) o = new() { ["type"] = "number" };
        else o = new() { ["type"] = "integer" };
        if (s.IsNullable && t != typeof(string) && !t.IsEnum) o["type"] = new JsonArray((JsonNode?)o["type"]!.DeepClone(), "null");
        return o;
    }
}
