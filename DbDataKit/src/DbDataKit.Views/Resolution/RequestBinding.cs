using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Serving;

namespace DbDataKit.Views;

/// <summary>
/// Binds the viewer's parameter values to a request (plan 32 DBR04, on the bindings <see cref="DerivedRequest.Bindings"/> lists): the request keeps its <c>{"param": name}</c> markers, the values travel beside it as the
/// request's <c>parameters</c> with the document's definitions as <c>parameter_definitions</c> (<c>docs/semantic/query.md</c>, Parameters), so the request hash, the cache key and a replay are those of the literal form.
/// A filter whose parameter the viewer left unset drops out first, so the engine never sees an empty list.
/// </summary>
internal static class RequestBinding
{
    /// <summary>The request-level name of a marker and the document parameter that supplies it.</summary>
    private static string DocumentName(JsonObject? map, string requestName) => map?[requestName] is JsonValue v && v.TryGetValue<string>(out var mapped) ? mapped : requestName;

    /// <summary>Binds a semantic request (the envelope, its <c>query</c> inside) and returns it; the markers found are bound to the values of <paramref name="values"/>.</summary>
    public static JsonObject Bind(JsonObject envelope, JsonObject? map, ParameterValues values)
    {
        var request = (JsonObject)envelope.DeepClone();
        if (request["query"] is JsonObject query) Prune(query, map, values);

        var parameters = new JsonObject();
        var definitions = new JsonObject();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject { Count: 1 } o when o["param"] is JsonValue v && v.TryGetValue<string>(out var name):
                    if (parameters.ContainsKey(name)) break;
                    var doc = DocumentName(map, name);
                    if (values.Get(doc) is not { } value) break;
                    parameters[name] = value.DeepClone();
                    if (values.Definition(doc) is { } d)
                    {
                        var def = new JsonObject { ["type"] = d.Type };
                        if (d.For is not null) def["for"] = d.For;
                        if (d.Many) def["many"] = true;
                        definitions[name] = def;
                    }
                    break;
                case JsonObject o: foreach (var (_, child) in o) Walk(child); break;
                case JsonArray a: foreach (var child in a) Walk(child); break;
            }
        }
        Walk(request["query"]);
        if (parameters.Count > 0) { request["parameters"] = parameters; request["parameter_definitions"] = definitions; }
        return request;
    }

    /// <summary>A filter on an unset parameter is not a filter: it is removed (a list loses the unset entries; a filter left with no values goes).</summary>
    private static void Prune(JsonNode? node, JsonObject? map, ParameterValues values)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["filters"] is JsonArray filters)
                    for (var i = filters.Count - 1; i >= 0; i--)
                        if (filters[i] is JsonObject filter && DropsOut(filter, map, values)) filters.RemoveAt(i);
                if (o["filters"] is JsonArray { Count: 0 }) o.Remove("filters");
                foreach (var (_, child) in o.ToList()) Prune(child, map, values);
                break;
            case JsonArray a:
                foreach (var child in a.ToList()) Prune(child, map, values);
                break;
        }
    }

    private static bool Unset(JsonNode? marker, JsonObject? map, ParameterValues values) =>
        marker is JsonObject { Count: 1 } o && o["param"] is JsonValue v && v.TryGetValue<string>(out var name) && values.Declares(DocumentName(map, name)) && !values.IsSet(DocumentName(map, name));

    private static bool DropsOut(JsonObject filter, JsonObject? map, ParameterValues values)
    {
        if (filter["values"] is JsonNode vals)
        {
            if (Unset(vals, map, values)) return true;
            if (vals is JsonArray list)
            {
                var before = list.Count;
                for (var i = list.Count - 1; i >= 0; i--) if (Unset(list[i], map, values)) list.RemoveAt(i);
                if (list.Count == 0 && before > 0) return true;
            }
        }
        if (filter["value"] is JsonNode single && Unset(single, map, values)) return true;
        return false;
    }

    // ---- semantic SQL ----

    /// <summary>
    /// The values of the markers of a written semantic SQL, in the form every protocol binds (<see cref="ServingParameter"/>, by name): a marker is named by the text, mapped by the query's <c>params</c> to a document
    /// parameter. A marker with no value is left unbound: the engine says <c>param_missing</c> and the widget shows it.
    /// </summary>
    public static IReadOnlyList<ServingParameter> SqlParameters(IEnumerable<string> markerNames, JsonObject? map, ParameterValues values)
    {
        var result = new List<ServingParameter>();
        foreach (var name in markerNames)
            if (values.Get(DocumentName(map, name)) is { } value) result.Add(Parameter(name, value));
        return result;
    }

    /// <summary>A saved query's call arguments: the request's name (a key of <c>params</c>) bound to the value of the document parameter it maps to.</summary>
    public static IReadOnlyList<(string Name, ServingParameter Value)> SavedArguments(JsonObject map, ParameterValues values)
    {
        var result = new List<(string, ServingParameter)>();
        foreach (var (name, target) in map)
            if (target is JsonValue t && t.TryGetValue<string>(out var doc) && values.Get(doc) is { } value) result.Add((name, Parameter(name, value)));
        return result;
    }

    private static ServingParameter Parameter(string name, JsonNode value)
    {
        var inv = CultureInfo.InvariantCulture;
        if (value is JsonArray) return new ServingParameter(name, "list", value.ToJsonString());
        var v = (JsonValue)value;
        if (v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) return new ServingParameter(name, "bool", v.GetValueKind() == System.Text.Json.JsonValueKind.True ? "true" : "false");
        if (ParameterValues.TryLong(v, out var l)) return new ServingParameter(name, "int64", l.ToString(inv));
        if (ParameterValues.TryDouble(v, out var d)) return new ServingParameter(name, "double", d.ToString("R", inv));
        return new ServingParameter(name, "string", ParameterValues.Format(v));
    }
}
