using System.Globalization;
using System.Text.Json.Nodes;

namespace DbDataKit.Apps;

/// <summary>Reads the values an address gives a custom view against the manifest entry's declaration (plan 32 DBR33): the apps host's page route and the views module's export route read a page's parameters the same way.</summary>
public static class CustomViewParameters
{
    /// <summary>
    /// The address's parameters read against the entry's declaration: each declared name by its type (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>; a <c>many</c> parameter takes every value given, any other takes one). A name the entry does not
    /// declare is not read: it reaches nothing. A value of the wrong type, two values for a single parameter and a required parameter with no value are a problem in words, naming the parameter, and no values.
    /// </summary>
    public static (IReadOnlyDictionary<string, JsonNode?>? Values, string? Problem) Read(CustomView view, IEnumerable<KeyValuePair<string, string[]>> query)
    {
        var given = query.GroupBy(q => q.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.SelectMany(q => q.Value).ToArray(), StringComparer.Ordinal);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var p in view.Params)
        {
            var texts = given.GetValueOrDefault(p.Name)?.Where(t => t.Length > 0).ToArray() ?? [];
            if (texts.Length == 0)
            {
                if (p.Required) return (null, $"The page '{view.Name}' needs the parameter `{p.Name}`: add `?{p.Name}=...` to the address.");
                continue;
            }
            if (!p.Many && texts.Length > 1) return (null, $"The parameter `{p.Name}` of the page '{view.Name}' takes one value, not {texts.Length}.");
            var nodes = new List<JsonNode?>();
            foreach (var text in texts)
            {
                var node = Coerce(p.Type, text);
                if (node is null) return (null, $"The parameter `{p.Name}` of the page '{view.Name}' is {(p.Type == "integer" ? "an integer" : p.Type == "number" ? "a number" : p.Type == "boolean" ? "true or false" : "text")}, not '{text}'.");
                nodes.Add(node);
            }
            values[p.Name] = p.Many ? new JsonArray([.. nodes]) : nodes[0];
        }
        return (values, null);
    }

    private static JsonNode? Coerce(string type, string text) => type switch
    {
        "integer" => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : null,
        "number" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? JsonValue.Create(d) : null,
        "boolean" => text switch { "true" => JsonValue.Create(true), "false" => JsonValue.Create(false), _ => null },
        _ => JsonValue.Create(text),
    };
}
