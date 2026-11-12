using System.Text.Json.Nodes;

namespace DbDataKit.Knowledge;

/// <summary>A member of a semantic model a request names: <see cref="Kind"/> is <c>measure</c> or <c>attribute</c> (an attribute is written <c>dimension.attribute</c>).</summary>
public sealed record RequestedMember(string Kind, string Name);

/// <summary>Validates a request against a catalog the way the semantic engine would (the host supplies <c>DbDataKit.Semantic</c>'s validator); the messages say what no longer parses. Empty: it parses.</summary>
public delegate IReadOnlyList<string> RequestValidator(JsonObject request, JsonNode catalog);

/// <summary>
/// Re-verification of a verified query against a new version of its model (plan 27a KN22): <b>still parses</b> (the host's validator, and the shape of the request), <b>its members exist</b> in the new catalog (the measures,
/// the attributes of its filters and its attributes; a saved query is still one of the model's), and <b>the result has the same columns</b> (each requested member keeps its type: a column that changed type is a different
/// column). The checks are a function of the request and the two catalogs; what to do with the answer (mark the query, tell its owner) is the index's (<c>VerifiedRechecker</c>).
/// </summary>
public static class VerifiedRecheck
{
    /// <summary>The members a request names: its measures (a plain name, or the <c>measure</c> of an override), its attributes, and the attributes and measures its filters name, in order, each once.</summary>
    public static IReadOnlyList<RequestedMember> Members(JsonObject request)
    {
        var members = new List<RequestedMember>();
        void Add(string kind, string? name) { if (!string.IsNullOrEmpty(name) && !members.Any(m => m.Kind == kind && m.Name == name)) members.Add(new RequestedMember(kind, name)); }
        var query = request["query"] as JsonObject;
        if (query?["measures"] is JsonArray measures)
            foreach (var entry in measures) Add("measure", entry is JsonValue v && v.TryGetValue<string>(out var s) ? s : (entry as JsonObject)?["measure"]?.GetValue<string>());
        if (query?["attributes"] is JsonArray attributes)
            foreach (var entry in attributes) Add("attribute", entry is JsonValue v && v.TryGetValue<string>(out var s) ? s : null);
        if (query?["filters"] is JsonArray filters) Filters(filters, Add);
        return members;
    }

    // a filter names an attribute (or a measure, for a filter on a measure); the grouping filters hold more of them
    private static void Filters(JsonNode? node, Action<string, string?> add)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var child in array) Filters(child, add);
                break;
            case JsonObject o:
                if (o["attribute"] is JsonValue a && a.TryGetValue<string>(out var attribute)) add("attribute", attribute);
                if (o["measure"] is JsonValue m && m.TryGetValue<string>(out var measure)) add("measure", measure);
                foreach (var (key, child) in o)
                    if (child is JsonArray or JsonObject && key is not "values") Filters(child, add);
                break;
        }
    }

    /// <summary>
    /// What no longer holds for a verified query against the model as <paramref name="catalog"/> now says it; empty when it all holds. <paramref name="previous"/> is the catalog it was verified against, when it is known (it
    /// makes a changed type visible). Never throws: a problem with the check itself is a problem of the query.
    /// </summary>
    public static IReadOnlyList<string> Problems(string? requestJson, string? savedQuery, JsonNode catalog, JsonNode? previous = null, RequestValidator? validate = null)
    {
        var problems = new List<string>();
        var model = Text(catalog["name"]) ?? "the model";
        if (savedQuery is not null)
        {
            if (catalog["queries"] is not JsonObject queries || !queries.ContainsKey(savedQuery)) problems.Add($"the saved query {savedQuery} is no longer one of {model}'s");
            return problems;
        }
        if (requestJson is null) return ["it has no request to check"];
        JsonObject request;
        try { request = JsonNode.Parse(requestJson) as JsonObject ?? throw new FormatException(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException) { return ["its request is not a document"]; }
        if (request["query"] is not JsonObject query || query["measures"] is not JsonArray { Count: > 0 }) return ["its request is not a query with measures"];

        foreach (var member in Members(request))
        {
            var found = Find(catalog, member);
            if (found is null) { problems.Add($"the {member.Kind} {member.Name} is no longer in {model}"); continue; }
            if (previous is not null && Find(previous, member) is { } before && Type(before) is { } was && Type(found) is { } now && was != now)
                problems.Add($"the {member.Kind} {member.Name} changed from {was} to {now}, so its column is not the same");
        }
        if (problems.Count == 0 && validate is not null)
            foreach (var message in validate(request, catalog)) problems.Add($"it no longer parses: {message}");
        return problems;
    }

    private static JsonNode? Find(JsonNode catalog, RequestedMember member)
    {
        if (member.Kind == "measure")
        {
            if (catalog["measures"] is not JsonObject measures) return null;
            if (measures[member.Name] is { } exact) return exact;
            // a measure may be written with the source it comes from in front (orders.net_revenue)
            var dot = member.Name.LastIndexOf('.');
            return dot > 0 ? measures[member.Name[(dot + 1)..]] : null;
        }
        var parts = member.Name.Split('.', 2);
        return parts.Length == 2 && catalog["dimensions"]?[parts[0]]?["attributes"]?[parts[1]] is { } attribute ? attribute : null;
    }

    private static string? Type(JsonNode node) => Text(node["type"]) ?? Text(node["format"]);

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
