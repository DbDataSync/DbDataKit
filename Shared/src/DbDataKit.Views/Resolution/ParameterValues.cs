using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DbDataKit.Views;

/// <summary>
/// The values of a document's parameters for one resolution (plan 32 section 3.5, J25): what the viewer gave, coerced to each parameter's type (a URL carries text; a <c>many</c> parameter is always a list), else the
/// default, else nothing. A value that is unset (<c>null</c> or an empty list) is a viewer who chose nothing: a filter on it drops out of the request ("no region chosen means all regions") instead of reaching the engine,
/// which refuses an empty list (<c>param_empty_list</c>) so that "no values" never silently means "no filter" by accident.
/// </summary>
internal sealed partial class ParameterValues
{
    private readonly Dictionary<string, JsonNode?> values = [];
    private readonly Dictionary<string, ViewParameter> definitions;

    public ParameterValues(ViewDocument document, IReadOnlyDictionary<string, JsonNode?>? given)
    {
        definitions = document.Parameters.ToDictionary(p => p.Name);
        foreach (var name in given?.Keys ?? [])
            if (!definitions.ContainsKey(name))
                throw new ViewResolutionException(ResolveCodes.BadParameterValue, $"The document has no parameter `{name}` (it has {(definitions.Count == 0 ? "none" : string.Join(", ", definitions.Keys.Order().Select(k => $"`{k}`")))}).");
        foreach (var p in document.Parameters)
        {
            if (given is not null && given.TryGetValue(p.Name, out var value)) values[p.Name] = Coerce(p, value);
            else values[p.Name] = p.Default is { } d ? Coerce(p, d.DeepClone()) : null;
        }
    }

    public ViewParameter? Definition(string name) => definitions.GetValueOrDefault(name);

    public bool Declares(string name) => definitions.ContainsKey(name);

    /// <summary>The value, or null when the viewer chose nothing (null or an empty list).</summary>
    public JsonNode? Get(string name) => values.TryGetValue(name, out var v) && !IsEmpty(v) ? v : null;

    public bool IsSet(string name) => Get(name) is not null;

    /// <summary>The value as the text a person reads: a list joined by commas, nothing as empty (the same as the renderers' <c>parameterText</c>).</summary>
    public string Text(string name) => Get(name) switch
    {
        null => "",
        JsonArray list => string.Join(", ", list.Select(Scalar)),
        var scalar => Scalar(scalar),
    };

    private static bool IsEmpty(JsonNode? v) => v is null || v is JsonArray { Count: 0 };

    private static string Scalar(JsonNode? n) => n is JsonValue v ? Format(v) : "";

    /// <summary>A plain value as the text a person reads. (A node may be backed by any CLR number type, so kinds are read from the JSON it stands for.)</summary>
    internal static string Format(JsonValue v) => v.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => v.ToJsonString(),
    };

    internal static bool TryLong(JsonValue v, out long value)
    {
        value = 0;
        return v.GetValueKind() == System.Text.Json.JsonValueKind.Number && long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    internal static bool TryDouble(JsonValue v, out double value)
    {
        value = 0;
        return v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static JsonNode? Coerce(ViewParameter p, JsonNode? value)
    {
        if (value is null) return null;
        if (p.Many)
        {
            var items = value is JsonArray a ? a.ToList() : [value];
            return new JsonArray(items.Select(i => One(p, i)).ToArray());
        }
        if (value is JsonArray) throw new ViewResolutionException(ResolveCodes.BadParameterValue, $"The parameter `{p.Name}` takes one value; make it `many: true` to take a list.");
        return One(p, value);
    }

    private static JsonNode One(ViewParameter p, JsonNode? value)
    {
        string Bad(string wants) => $"`{value?.ToJsonString()}` is not {wants} (the parameter `{p.Name}` is a {p.Type}).";
        if (value is not JsonValue v) throw new ViewResolutionException(ResolveCodes.BadParameterValue, Bad("a plain value"));
        var inv = CultureInfo.InvariantCulture;
        var kind = v.GetValueKind();
        var text = kind == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;
        switch (p.Type)
        {
            case "integer":
                if (TryLong(v, out var l)) return JsonValue.Create(l);
                if (TryDouble(v, out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15) return JsonValue.Create((long)d);
                if (text is not null && long.TryParse(text, NumberStyles.AllowLeadingSign, inv, out var parsed)) return JsonValue.Create(parsed);
                throw new ViewResolutionException(ResolveCodes.BadParameterValue, Bad("a whole number"));
            case "number":
                if (TryLong(v, out var ln)) return JsonValue.Create(ln);
                if (TryDouble(v, out var n)) return JsonValue.Create(n);
                if (text is not null && double.TryParse(text, NumberStyles.Float, inv, out var pn) && double.IsFinite(pn)) return JsonValue.Create(pn);
                throw new ViewResolutionException(ResolveCodes.BadParameterValue, Bad("a number"));
            case "boolean":
                if (kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) return JsonValue.Create(kind == System.Text.Json.JsonValueKind.True);
                if (text is "true" or "false") return JsonValue.Create(text == "true");
                throw new ViewResolutionException(ResolveCodes.BadParameterValue, Bad("true or false"));
            default:
                return JsonValue.Create(Format(v));
        }
    }

    // ---- which parameters a widget uses ----

    [GeneratedRegex(@"\{\{\s*params\.([A-Za-z0-9_-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Replaces <c>{{params.name}}</c> with the parameter's value as text (never markup: the renderer escapes it again).</summary>
    public string Substitute(string text) => Placeholder().Replace(text, m => Text(m.Groups[1].Value));

    /// <summary>
    /// The document parameters a widget reads: a control's own, the ones a request's <c>{"param": name}</c> markers name (through the query's <c>params</c> map), a saved query's arguments and the placeholders of a text. A parameter
    /// change resolves again only the widgets that use it (DBR08).
    /// </summary>
    public static IReadOnlySet<string> Used(ViewWidget widget)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var raw = widget.Raw;
        if (raw["parameter"] is JsonValue pv && pv.TryGetValue<string>(out var control)) used.Add(control);
        if (raw["text"] is JsonValue tv && tv.TryGetValue<string>(out var text)) foreach (Match m in Placeholder().Matches(text)) used.Add(m.Groups[1].Value);
        if (raw["query"] is JsonObject query)
        {
            var map = query["params"] as JsonObject;
            string Document(string requestName) => map?[requestName] is JsonValue mv && mv.TryGetValue<string>(out var mapped) ? mapped : requestName;
            void Walk(JsonNode? node)
            {
                switch (node)
                {
                    case JsonObject { Count: 1 } o when o["param"] is JsonValue v && v.TryGetValue<string>(out var name): used.Add(Document(name)); break;
                    case JsonObject o: foreach (var (_, child) in o) Walk(child); break;
                    case JsonArray a: foreach (var child in a) Walk(child); break;
                }
            }
            foreach (var (key, child) in query) if (key != "params") Walk(child);
            // a saved query's arguments, and the markers of a written semantic SQL (`:name`), are named by the map and by the text
            if (query["saved"] is not null && map is not null) foreach (var (_, target) in map) if (target is JsonValue t && t.TryGetValue<string>(out var doc)) used.Add(doc);
            if (query["semantic"]?["sql"] is JsonValue sql && sql.TryGetValue<string>(out var sqlText)) foreach (var marker in SqlMarkerNames(sqlText)) used.Add(Document(marker));
        }
        return used;
    }

    /// <summary>The names of the <c>:name</c> and <c>$name</c> markers of semantic SQL, outside quotes and comments (a <c>::</c> is a cast, a <c>?</c> is positional and has no name).</summary>
    public static IReadOnlyList<string> SqlMarkerNames(string sql)
    {
        var names = new List<string>();
        char? quote = null;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (c == ':' && i + 1 < sql.Length && sql[i + 1] == ':') { i++; continue; }
            if (c is ':' or '$' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                var j = i + 1;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                var name = sql[(i + 1)..j];
                if (!names.Contains(name)) names.Add(name);
                i = j - 1;
            }
        }
        return names;
    }
}
