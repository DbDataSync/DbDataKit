using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Presentation;

/// <summary>
/// A chart is a restricted Vega-Lite spec: marks, channels and encodings, no data and nothing that computes a value. The renderer
/// supplies the data from the result's detail rows (total rows are left out) and resolves each field to the result's column.
/// </summary>
public static class ChartView
{
    private static readonly HashSet<string> Marks = ["bar", "line", "area", "point", "rule", "text"];
    private static readonly HashSet<string> Channels = ["x", "y", "color", "column", "row", "tooltip"];
    private static readonly HashSet<string> EncodingProperties = ["field", "type", "sort", "stack", "title"];
    private static readonly HashSet<string> Types = ["quantitative", "ordinal", "nominal", "temporal"];
    private static readonly HashSet<string> TopLevel = ["mark", "encoding", "title", "width", "height", "$schema", "description"];
    private static readonly HashSet<string> Computing = ["data", "transform", "aggregate", "bin", "timeUnit", "url", "datasets", "layer", "facet", "repeat", "concat", "params", "selection"];

    public static (JsonObject? View, List<QisError> Errors) Render(JsonObject spec, ResultTable table, string path)
    {
        var errors = new List<QisError>();
        QisError Err(string code, string where, string message, List<string>? suggestions = null) =>
            new("presentation", code, where, message, suggestions is { Count: > 0 } ? suggestions : null);

        var vega = spec["vega_lite"]!.AsObject();
        var vegaPath = path + ".vega_lite";

        // Anything that computes a value or fetches data is refused wherever it appears.
        void Scan(JsonNode? node, string where)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, child) in o)
                    {
                        if (Computing.Contains(key))
                            errors.Add(Err("vega_disallowed", $"{where}.{key}", $"'{key}' isn't allowed in a chart spec: it fetches data or computes values, and the renderer supplies the data from the result."));
                        else Scan(child, $"{where}.{key}");
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) Scan(a[i], $"{where}[{i}]");
                    break;
            }
        }
        Scan(vega, vegaPath);
        foreach (var key in vega.Select(p => p.Key).Where(k => !TopLevel.Contains(k) && !Computing.Contains(k)))
            errors.Add(Err("vega_disallowed", $"{vegaPath}.{key}", $"'{key}' isn't in the allowed Vega-Lite subset (mark, encoding, title)."));

        var mark = vega["mark"] is JsonValue m ? m.GetValue<string>() : vega["mark"]?["type"]?.GetValue<string>();
        if (mark is null) errors.Add(Err("vega_disallowed", $"{vegaPath}.mark", "A chart needs a mark."));
        else if (!Marks.Contains(mark))
            errors.Add(Err("vega_disallowed", $"{vegaPath}.mark", $"Mark '{mark}' isn't allowed; use one of {string.Join(", ", Marks.Order())}.", [.. Marks.Order()]));

        var encoding = vega["encoding"] as JsonObject ?? [];
        var resolved = new Dictionary<string, ResultField>();
        foreach (var (channel, definition) in encoding)
        {
            var where = $"{vegaPath}.encoding.{channel}";
            if (!Channels.Contains(channel))
            {
                errors.Add(Err("vega_disallowed", where, $"Channel '{channel}' isn't allowed; use one of {string.Join(", ", Channels.Order())}."));
                continue;
            }
            if (definition is not JsonObject def) continue;
            foreach (var key in def.Select(p => p.Key).Where(k => !EncodingProperties.Contains(k) && !Computing.Contains(k)))
                errors.Add(Err("vega_disallowed", $"{where}.{key}", $"Encoding property '{key}' isn't allowed; use field, type, sort, stack or title."));
            if (def["type"]?.GetValue<string>() is { } type && !Types.Contains(type))
                errors.Add(Err("vega_disallowed", $"{where}.type", $"Type '{type}' isn't one of {string.Join(", ", Types.Order())}."));
            if (def["field"]?.GetValue<string>() is { } reference)
            {
                var field = table.Resolve(reference);
                if (field is null) errors.Add(Err("unknown_field", $"{where}.field", $"{reference} isn't a field of the result.", table.Suggest(reference)));
                else resolved[channel] = field;
            }
        }

        // A stack adds the series' values, so it needs a measure that can be added. Bars and areas with a colour stack unless told not to.
        if (resolved.TryGetValue("y", out var y) && y.Kind == "measure" && !y.Additive)
        {
            var stackSpec = encoding["y"]?["stack"];
            var explicitStack = stackSpec is not null && !(stackSpec is JsonValue sv && (sv.GetValueKind() == System.Text.Json.JsonValueKind.False));
            var implicitStack = mark is "bar" or "area" && encoding.ContainsKey("color") && encoding["y"] is JsonObject { } yd && !yd.ContainsKey("stack");
            if (explicitStack || implicitStack)
                errors.Add(Err("stack_not_additive", $"{vegaPath}.encoding.y",
                    $"{y.Binding ?? y.Name} can't be added up, so its series can't be stacked. Set \"stack\": null for a grouped chart, or use a measure that adds."));
        }
        if (errors.Count > 0) return (null, errors);

        // The spec the renderer hands to Vega-Lite: fields as the result's columns, sort_key as the members' order, and the data.
        var detail = table.Rows.Where(table.IsDetail).ToList();
        var output = vega.DeepClone().AsObject();
        output["$schema"] = "https://vega.github.io/schema/vega-lite/v5.json";
        foreach (var (channel, field) in resolved)
        {
            var def = output["encoding"]![channel]!.AsObject();
            def["field"] = field.Name;
            if (def["sort"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && def["sort"]!.GetValue<string>() == "sort_key")
                def["sort"] = new JsonArray(detail.Select(r => r[field.Name]).Select(v => v?.DeepClone()).Distinct(new JsonNodeComparer()).ToArray());
        }
        var skip = table.LevelField;
        output["data"] = new JsonObject
        {
            ["values"] = new JsonArray(detail.Select(r => (JsonNode)new JsonObject(r.Where(p => p.Key != skip).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())))).ToArray()),
        };
        return (new JsonObject { ["id"] = spec["id"]!.GetValue<string>(), ["type"] = "chart", ["vega_lite"] = output }, errors);
    }

    private sealed class JsonNodeComparer : IEqualityComparer<JsonNode?>
    {
        public bool Equals(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
        public int GetHashCode(JsonNode? n) => n?.ToJsonString().GetHashCode() ?? 0;
    }
}
