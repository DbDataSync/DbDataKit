using System.Text.Json.Nodes;

namespace DbDataKit.Views;

/// <summary>One line of the confirmation: a name (a field or a key part) and the value as plain text.</summary>
public sealed record ActionLine(string Name, string Value);

/// <summary>
/// What the confirmation popup of an action link says for one clicked row, and the request it sends (plan 32 DBR53): the declaration's words with the row's values in them as plain text, everything that will be
/// written listed verbatim, and what the row did not give (<see cref="Missing"/>: an action with any of these is not sent). The same derivation as <c>resolveAction</c> in <c>@dbdatakit/visuals</c>
/// (<c>conformance/views/actions.json</c> runs one set of cases through both); the server derives the change from the published document with it, so a widget cannot dress one request as another.
/// </summary>
public sealed record ResolvedAction(string Link, string Kind, string Operation, string Title, string Button, bool Destructive, string Target, IReadOnlyList<ActionLine> Shows, IReadOnlyList<ActionLine> Writes,
    IReadOnlyList<ActionLine> Key, string? Version, IReadOnlyList<string> Missing, IReadOnlyList<ActionAsk> Asks)
{
    /// <summary>The model and entity of a change, or the package and task.</summary>
    public string? Model { get; init; }
    public string? Entity { get; init; }
    public string? Package { get; init; }
    public string? Task { get; init; }

    /// <summary>The golden's JSON: the keys <c>resolveAction</c> of the TypeScript package has.</summary>
    public JsonObject ToJson()
    {
        static JsonArray Lines(IEnumerable<ActionLine> l, string first) => new([.. l.Select(x => (JsonNode)new JsonObject { [first] = x.Name, ["value"] = x.Value })]);
        return new JsonObject
        {
            ["link"] = Link, ["kind"] = Kind, ["operation"] = Operation, ["title"] = Title, ["button"] = Button, ["destructive"] = Destructive, ["target"] = Target,
            ["shows"] = Lines(Shows, "field"), ["writes"] = Lines(Writes, "name"), ["key"] = Lines(Key, "name"), ["version"] = Version, ["missing"] = new JsonArray([.. Missing.Select(m => (JsonNode)m)]),
            ["asks"] = new JsonArray([.. Asks.Select(a =>
            {
                var o = new JsonObject { ["name"] = a.Name, ["label"] = a.Label, ["type"] = a.Type, ["choices"] = new JsonArray([.. a.Choices.Select(c => (JsonNode)c)]), ["required"] = a.Required };
                if (a.Default is not null) o["default"] = a.Default;
                return (JsonNode)o;
            })]),
        };
    }
}

public static class ActionResolution
{
    /// <summary>One value as the popup lists it: plain text, never markup.</summary>
    public static string Plain(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => node.ToJsonString(),
    };

    public static string DefaultTitle(ActionDefinition a, string? label) =>
        label ?? a.Operation switch
        {
            "insert" => $"Add a row to {a.Entity}",
            "edit" => $"Change a row of {a.Entity}",
            "delete" => $"Delete a row of {a.Entity}",
            _ => $"Run {a.Package}.{a.Task}",
        };

    public static string DefaultButton(string operation) => operation switch { "insert" => "Add the row", "edit" => "Apply the change", "delete" => "Delete the row", _ => "Run the task" };

    private static JsonNode? FromRow(string name, JsonObject row) =>
        row.TryGetPropertyValue(name, out var v) ? v : row.TryGetPropertyValue(name[(name.LastIndexOf('.') + 1)..], out var tail) ? tail : null;

    /// <summary>One value of the action for a row: the literal, the row's field, the parameter or what was typed; null when there is none.</summary>
    public static JsonNode? ValueOf(LinkValue v, JsonObject row, JsonObject parameters, IReadOnlyDictionary<string, string?> answers) => v.Kind switch
    {
        LinkValueKind.Literal => v.LiteralType switch
        {
            "boolean" => JsonValue.Create(v.Text == "true"),
            "integer" => long.TryParse(v.Text, out var l) ? JsonValue.Create(l) : JsonValue.Create(v.Text),
            "number" => double.TryParse(v.Text, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(v.Text),
            _ => JsonValue.Create(v.Text),
        },
        LinkValueKind.Param => parameters.TryGetPropertyValue(v.Text, out var p) ? p : null,
        LinkValueKind.Ask => answers.TryGetValue(v.Text, out var a) && a is not null ? JsonValue.Create(a) : null,
        _ => FromRow(v.Text, row),
    };

    private static string TextOf(string name, JsonObject row, IReadOnlyDictionary<string, string?> answers) =>
        name.StartsWith("ask.", StringComparison.Ordinal) ? answers.TryGetValue(name[4..], out var a) ? a ?? "" : "" : Plain(FromRow(name, row));

    /// <summary>The derivation: see <see cref="ResolvedAction"/>. <paramref name="link"/> must be an action link.</summary>
    public static ResolvedAction Resolve(ViewLink link, JsonObject? row = null, JsonObject? parameters = null, IReadOnlyDictionary<string, string?>? answers = null)
    {
        var a = link.Action ?? throw new ArgumentException($"The link '{link.Name}' is not an action.", nameof(link));
        row ??= []; parameters ??= []; answers ??= new Dictionary<string, string?>();
        var missing = new List<string>();

        string? Value(string label, LinkValue v)
        {
            var raw = ValueOf(v, row, parameters, answers);
            if (raw is null || (raw is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0)) { if (v.Kind != LinkValueKind.Ask) { missing.Add(label); return null; } return ""; }
            if (raw is JsonArray list) return string.Join(", ", list.Select(Plain));
            return Plain(raw);
        }

        var key = new List<ActionLine>();
        foreach (var (k, v) in a.Key) if (Value(k == "key" ? "the row key" : $"key {k}", v) is { } t) key.Add(new ActionLine(k, t));
        string? version = null;
        if (a.Version is { } av && ValueOf(av, row, parameters, answers) is { } vraw) version = Plain(vraw);
        var writes = new List<ActionLine>();
        foreach (var (k, v) in a.Set) if (Value(k, v) is { } t) writes.Add(new ActionLine(k, t));
        var shows = a.Confirm.Show.Select(f => new ActionLine(f, TextOf(f, row, answers))).ToList();
        var title = a.Confirm.Title is { } tt ? ViewsLoader.TitlePlaceholder.Replace(tt, m => TextOf(m.Groups[1].Value, row, answers)) : DefaultTitle(a, link.Label);
        return new ResolvedAction(link.Name, a.Kind, a.Operation, title, a.Confirm.Button ?? DefaultButton(a.Operation), a.Operation == "delete",
            a.Kind == "change" ? $"{a.Model}.{a.Entity}" : $"{a.Package}.{a.Task}", shows, writes, key, version, missing, [.. a.Ask.Values])
        { Model = a.Model, Entity = a.Entity, Package = a.Package, Task = a.Task };
    }
}
