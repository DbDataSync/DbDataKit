using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Semantic.Sql;

/// <summary>
/// The canonical form of a semantic request, the one two requests that mean the same have in common: what semantic SQL cannot tell apart is made equal (docs/semantic/semantic-sql.md,
/// "Same request"). Top-level filters are a flat list with the conditions on measures after the conditions on attributes; a measure with nothing but its name is its name; a
/// number is a decimal; <c>grand_total</c> is explicit and <c>cube: false</c> is absent; an order entry names its direction.
/// </summary>
public static class RequestNormalizer
{
    /// <summary>A normalized copy of the request document.</summary>
    public static JsonObject Normalize(JsonObject request)
    {
        var copy = (JsonObject)Canonical(request)!;
        if (copy["query"] is not JsonObject q) return copy;

        if (q["measures"] is JsonArray measures)
            for (int i = 0; i < measures.Count; i++)
                if (measures[i] is JsonObject entry)
                {
                    if (entry["filters"] is JsonArray { Count: 0 }) entry.Remove("filters");
                    else if (entry["filters"] is JsonArray fs) entry["filters"] = FilterList(fs);
                    if (entry.Count == 1 && entry["measure"] is { } only) measures[i] = only.DeepClone();
                }
        // a list with nothing in it is the same as no list
        foreach (var key in new[] { "attributes", "filters", "post_filters", "order", "segments" })
            if (q[key] is JsonArray { Count: 0 }) q.Remove(key);
        if (q["filters"] is JsonArray filters) q["filters"] = FilterList(filters);
        if (q["post_filters"] is JsonArray post) q["post_filters"] = FilterList(post);
        if (q["totals"] is JsonObject totals)
        {
            totals["grand_total"] ??= false;
            if (totals["cube"] is JsonValue { } cube && cube.GetValueKind() == JsonValueKind.False) totals.Remove("cube");
        }
        if (q["order"] is JsonArray order)
            foreach (var item in order.OfType<JsonArray>())
                if (item.Count == 1) item.Add("asc");
        return copy;
    }

    private static JsonArray FilterList(JsonArray filters) => new(TopLevelFilters(filters).Select(f => Clean(f.DeepClone())).ToArray());

    // an operator that takes no values has none, whether the list is absent or empty
    private static JsonNode Clean(JsonNode filter)
    {
        if (filter is not JsonObject o) return filter;
        if (o["values"] is JsonArray { Count: 0 }) o.Remove("values");
        foreach (var key in new[] { "and", "or" })
            if (o[key] is JsonArray items) foreach (var item in items) Clean(item!);
        return o;
    }

    /// <summary>The top-level filters as the flat AND list: nested ANDs expanded, the conditions on attributes first and the conditions that read only measures after, each group in its order.</summary>
    public static List<JsonNode> TopLevelFilters(JsonArray? filters)
    {
        var flat = new List<JsonNode>();
        void Add(JsonNode f)
        {
            if (f is JsonObject o && o["and"] is JsonArray and && o.Count == 1) foreach (var child in and) Add(child!);
            else flat.Add(f);
        }
        foreach (var f in filters ?? []) Add(f!);
        return flat.Where(f => !ReadsOnlyMeasures(f)).Concat(flat.Where(ReadsOnlyMeasures)).ToList();
    }

    /// <summary>True for a leaf on a measure, and for an and/or of nothing else.</summary>
    public static bool ReadsOnlyMeasures(JsonNode filter) => filter is JsonObject o &&
        (o["measure"] is not null || (o["or"] ?? o["and"]) is JsonArray items && items.Count > 0 && items.All(i => i is not null && ReadsOnlyMeasures(i)));

    // a copy with every number as a decimal, so 1, 1.0 and 1e0 are one
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(Strip(Decimal(v))),
        JsonValue v => v.DeepClone(),
        _ => node.DeepClone(),
    };

    private static decimal Decimal(JsonValue v) =>
        v.TryGetValue<decimal>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static decimal Strip(decimal d) => d / 1.000000000000000000000000000000000m;
}
