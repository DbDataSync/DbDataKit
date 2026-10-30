using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    /// <summary>
    /// Query parameters (plan 19, J25): a value node <c>{"param": "name"}</c> stands for a value, or a list of them, bound in the request's <c>parameters</c> object, and <c>parameter_definitions</c> gives a name its
    /// <c>for</c> (the attribute), <c>type</c>, <c>many</c>, <c>default</c> and <c>required</c>. Parameters are resolved once, before the request is validated, hashed or planned, so everything after sees the literal
    /// request and the hash, the cache key and a replay are those of the literal form. A parameter stands for a value, never for a name.
    /// </summary>
    private (JsonObject Doc, List<QisError>? Errors) ResolveParameters(JsonObject doc)
    {
        var hasParameters = doc["parameters"] is not null || doc["parameter_definitions"] is not null;
        if (!hasParameters && !ContainsParameter(doc)) return (doc, null);

        var errors = new List<QisError>();
        var bound = doc["parameters"] as JsonObject ?? new JsonObject();
        var definitions = doc["parameter_definitions"] as JsonObject ?? new JsonObject();
        if (doc["parameters"] is not null and not JsonObject) errors.Add(Err("param_invalid", "$.parameters", "parameters is an object of names and values."));
        if (doc["parameter_definitions"] is not null and not JsonObject) errors.Add(Err("param_invalid", "$.parameter_definitions", "parameter_definitions is an object of names and definitions."));

        var used = new HashSet<string>(StringComparer.Ordinal);
        _noneLeaves = [];
        var copy = (JsonObject)doc.DeepClone();
        copy.Remove("parameters");
        copy.Remove("parameter_definitions");
        Replace(copy, "$", null, bound, definitions, used, errors);

        // a filter whose list parameter is empty and defined `empty: matches_none` matches nothing: an attribute that is set and not set at once
        foreach (var leaf in _noneLeaves.Distinct())
        {
            var attribute = leaf["attribute"]?.GetValue<string>();
            leaf.Clear();
            if (attribute is null) { errors.Add(Err("param_empty_list", "$", "An empty list that matches none needs a filter on an attribute.")); continue; }
            leaf["and"] = new JsonArray(new JsonObject { ["attribute"] = attribute, ["operator"] = "set" }, new JsonObject { ["attribute"] = attribute, ["operator"] = "not_set" });
        }
        foreach (var (name, _) in bound)
            if (!used.Contains(name) && !definitions.ContainsKey(name)) errors.Add(Err("param_unknown", $"$.parameters.{name}", $"'{name}' is bound but no value in the request is that parameter."));
        return (copy, errors.Count > 0 ? errors : null);
    }

    private List<JsonObject> _noneLeaves = [];

    private static bool ContainsParameter(JsonNode? node) => node switch
    {
        JsonObject o => o.ContainsKey("param") && o.Count == 1 || o.Any(p => ContainsParameter(p.Value)),
        JsonArray a => a.Any(ContainsParameter),
        _ => false,
    };

    private static string? ParamName(JsonNode? node) => node is JsonObject { Count: 1 } o && o["param"] is JsonValue v && v.TryGetValue<string>(out var name) ? name : null;

    /// <summary>Replaces the parameter nodes under <paramref name="node"/>. <paramref name="attribute"/> is the attribute of the filter being read, which a parameter in its values is for.</summary>
    private void Replace(JsonNode node, string path, string? attribute, JsonObject bound, JsonObject definitions, HashSet<string> used, List<QisError> errors)
    {
        switch (node)
        {
            case JsonObject o:
                var filterAttribute = o["attribute"] is JsonValue av && av.TryGetValue<string>(out var a) ? a : attribute;
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var child = o[key];
                    var here = $"{path}.{key}";
                    if (ParamName(child) is { } whole)
                    {
                        // the whole of a filter's values is the parameter: a list or one value
                        var resolved = Bind(whole, here, key == "values" ? filterAttribute : null, many: key == "values", bound, definitions, used, errors);
                        if (resolved is not null) o[key] = key == "values" ? (resolved is JsonArray ? resolved : new JsonArray(resolved)) : resolved;
                        if (resolved is JsonArray { Count: 0 }) _noneLeaves.Add(o);
                    }
                    else if (key == "values" && child is JsonArray values)
                    {
                        var items = new List<JsonNode?>();
                        for (int i = 0; i < values.Count; i++)
                        {
                            if (ParamName(values[i]) is { } name)
                            {
                                var resolved = Bind(name, $"{here}[{i}]", filterAttribute, many: true, bound, definitions, used, errors);
                                if (resolved is JsonArray list) items.AddRange(list.Select(x => x?.DeepClone()));
                                else if (resolved is not null) items.Add(resolved);
                            }
                            else items.Add(values[i]?.DeepClone());
                        }
                        o[key] = new JsonArray([.. items]);
                        if (items.Count == 0 && values.Count > 0) _noneLeaves.Add(o);
                    }
                    else if (child is not null) Replace(child, here, filterAttribute, bound, definitions, used, errors);
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (ParamName(arr[i]) is { } name)
                    {
                        var resolved = Bind(name, $"{path}[{i}]", null, many: false, bound, definitions, used, errors);
                        if (resolved is not null) arr[i] = resolved;
                    }
                    else if (arr[i] is { } item) Replace(item, $"{path}[{i}]", attribute, bound, definitions, used, errors);
                }
                break;
        }
    }

    private JsonNode? Bind(string name, string path, string? attribute, bool many, JsonObject bound, JsonObject definitions, HashSet<string> used, List<QisError> errors)
    {
        used.Add(name);
        var definition = definitions[name] as JsonObject;
        var declaredMany = definition?["many"]?.GetValue<bool>() ?? false;
        var declaredFor = definition?["for"]?.GetValue<string>();
        var declaredType = definition?["type"]?.GetValue<string>();

        if (declaredFor is not null && attribute is not null && !string.Equals(declaredFor, attribute, StringComparison.Ordinal))
        {
            errors.Add(Err("param_for_mismatch", path, $"Parameter '{name}' is for {declaredFor}, and is used for {attribute}."));
            return null;
        }
        var target = attribute ?? declaredFor;

        JsonNode? value;
        if (bound.TryGetPropertyValue(name, out var given) && given is not null) value = given.DeepClone();
        else if (definition?["default"] is { } fallback) value = fallback.DeepClone();
        else if (definition?["required"]?.GetValue<bool>() == false) { errors.Add(Err("param_missing", path, $"Parameter '{name}' has no value and no default.")); return null; }
        else { errors.Add(Err("param_missing", path, $"Parameter '{name}' has no value.")); return null; }

        if (value is JsonArray list)
        {
            if (!many) { errors.Add(Err("param_type_mismatch", path, $"Parameter '{name}' is a list, and this place takes one value.")); return null; }
            if (!declaredMany && definition is not null) { errors.Add(Err("param_type_mismatch", path, $"Parameter '{name}' is bound to a list but is not defined with many: true.")); return null; }
            if (list.Count == 0 && definition?["empty"]?.GetValue<string>() == "matches_none") return list;
            if (list.Count == 0) { errors.Add(Err("param_empty_list", path, $"Parameter '{name}' is an empty list; an empty list is refused so that no values never silently means no filter.")); return null; }
            foreach (var item in list)
                if (ValueProblem(name, item, declaredType, target) is { } bad) { errors.Add(Err(bad.Code, path, bad.Message)); return null; }
            return list;
        }
        if (value is JsonObject) { errors.Add(Err("param_type_mismatch", path, $"Parameter '{name}' is an object; a parameter is a value or a list of values.")); return null; }
        if (declaredMany && many) value = new JsonArray(value);
        if (ValueProblem(name, value, declaredType, target) is { } problem) { errors.Add(Err(problem.Code, path, problem.Message)); return null; }
        return value;
    }

    /// <summary>Why a bound value does not fit the type the definition gives, or the type of the attribute it is for, and for a catalog-member attribute why it is not a member.</summary>
    private (string Code, string Message)? ValueProblem(string name, JsonNode? value, string? declaredType, string? attribute)
    {
        var kind = value is JsonValue v ? v.GetValueKind() : System.Text.Json.JsonValueKind.Null;
        var actual = kind switch
        {
            System.Text.Json.JsonValueKind.String => "string",
            System.Text.Json.JsonValueKind.Number => value!.ToJsonString().Contains('.') || value.ToJsonString().Contains('e') ? "number" : "integer",
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
            _ => "null",
        };
        string? expected = declaredType;
        if (expected is null && attribute is not null && cat.Doc.Attributes.ContainsKey(attribute)) expected = cat.TypeOf(attribute);
        if (expected is not null)
        {
            var fits = expected switch
            {
                "string" or "date" or "datetime" => actual == "string",
                "integer" => actual == "integer",
                "number" => actual is "integer" or "number",
                "boolean" => actual == "boolean",
                _ => true,
            };
            if (!fits) return ("param_type_mismatch", $"Parameter '{name}' is {actual}, and {(declaredType is not null ? "its definition" : attribute)} takes {expected}.");
        }
        if (attribute is not null && actual == "string" && cat.Doc.Attributes.TryGetValue(attribute, out var def) && (def.MemberList is not null || def.MembersFrom is not null))
        {
            var members = AttributeMembers(attribute);
            if (!members.Contains(value!.GetValue<string>())) return ("param_unknown_member", $"Parameter '{name}' is {value.GetValue<string>()}, which isn't a member of {attribute}.");
        }
        return null;
    }
}
