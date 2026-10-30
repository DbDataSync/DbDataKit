using System.Text.Json.Nodes;

namespace DbDataKit.Semantic;

/// <summary>What a request leaves to be bound (plan 19, J25): one parameter's name, the attribute it is a value of, its type and whether it takes a list.</summary>
/// <param name="Name">The name in <c>{"param": name}</c>; a positional semantic SQL marker is "1", "2", ....</param>
/// <param name="For">The attribute whose values it stands for (a filter on that attribute), or null.</param>
/// <param name="Type"><c>string</c>, <c>integer</c>, <c>number</c> or <c>boolean</c>; null when nothing says (the value is checked when it is bound).</param>
/// <param name="Many">Whether it may be a list (a filter's <c>equals</c> or <c>not_equals</c>, or the whole of its values).</param>
public sealed record ParameterShape(string Name, string? For, string? Type, bool Many);

public static class QueryParameters
{
    /// <summary>The parameters of a request in the order they first appear, with what the request and its <c>parameter_definitions</c> say of each.</summary>
    public static List<ParameterShape> Describe(JsonObject request, Catalog? catalog)
    {
        var found = new Dictionary<string, ParameterShape>(StringComparer.Ordinal);
        void Add(string name, string? attribute, string? type, bool many)
        {
            if (!found.TryGetValue(name, out var known)) found[name] = new ParameterShape(name, attribute, type, many);
            else found[name] = known with { For = known.For ?? attribute, Type = known.Type ?? type, Many = known.Many || many };
        }
        Walk(request["query"], catalog, Add, "$.query");
        if (request["parameter_definitions"] is JsonObject definitions)
            foreach (var (name, node) in definitions)
                if (node is JsonObject d && found.TryGetValue(name, out var shape))
                    found[name] = shape with { For = d["for"]?.GetValue<string>() ?? shape.For, Type = d["type"]?.GetValue<string>() ?? shape.Type, Many = d["many"]?.GetValue<bool>() ?? shape.Many };
        return [.. found.Values];
    }

    private static string? ParamName(JsonNode? node) => node is JsonObject { Count: 1 } o && o["param"] is JsonValue v && v.TryGetValue<string>(out var name) ? name : null;

    private static void Walk(JsonNode? node, Catalog? catalog, Action<string, string?, string?, bool> add, string path)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["attribute"] is JsonValue av && av.TryGetValue<string>(out var attribute) && o["values"] is { } values)
                {
                    var type = catalog is not null && catalog.Doc.Attributes.ContainsKey(attribute) ? TypeName(catalog.TypeOf(attribute)) : null;
                                        if (ParamName(values) is { } whole) add(whole, attribute, type, true);
                    else if (values is JsonArray list)
                        foreach (var item in list)
                            if (ParamName(item) is { } name) add(name, attribute, type, false);
                    return;
                }
                foreach (var (key, child) in o)
                {
                    if (ParamName(child) is { } name) add(name, null, key == "limit" ? "integer" : null, false);
                    else Walk(child, catalog, add, $"{path}.{key}");
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (ParamName(item) is { } name) add(name, null, null, false);
                    else Walk(item, catalog, add, path);
                break;
        }
    }

    private static string TypeName(string catalogType) => catalogType switch
    {
        "integer" => "integer",
        "number" => "number",
        "boolean" => "boolean",
        _ => "string",   // string, date and datetime values travel as text
    };
}
