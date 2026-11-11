using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.DataModel;

namespace DbDataKit.Store;

/// <summary>
/// The JSON of rows, changes and refusals on the wire (plan 19 Z09, plan 20 O08): the same for a DbDataStore server and the serving layer's <c>store</c> module, so a client (an app, the console, an agent) is written
/// once. A row is <c>{"values": {...}, "version": n, "valid_from", "valid_to"}</c>; a change is <c>{"action": "insert|update|delete", "entity", "key", "expected_version", "values", "effective_from"}</c>.
/// </summary>
public static class RowJson
{
    public static JsonNode? Value(object? value) => value switch
    {
        null => null,
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd")),
        TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss")),
        DateTimeOffset d => JsonValue.Create(d.ToString("O")),
        DateTime d => JsonValue.Create(d.ToString("O")),
        decimal d => JsonValue.Create(d),
        byte[] b => JsonValue.Create(Convert.ToBase64String(b)),
        string or bool or int or long or short or double => JsonSerializer.SerializeToNode(value),
        _ => JsonValue.Create(value.ToString()),
    };

    public static JsonObject Row(StoredRow row)
    {
        var values = new JsonObject();
        foreach (var (key, value) in row.Values) values[key] = Value(value);
        return new JsonObject { ["values"] = values, ["version"] = row.Version, ["valid_from"] = row.ValidFrom?.ToString("O"), ["valid_to"] = row.ValidTo?.ToString("O") };
    }

    public static JsonObject Key(IReadOnlyDictionary<string, object?> key)
    {
        var node = new JsonObject();
        foreach (var (name, value) in key) node[name] = Value(value);
        return node;
    }

    public static Dictionary<string, object?> ObjectOf(JsonNode? node) =>
        node is JsonObject o ? o.ToDictionary(p => p.Key, p => p.Value is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(p.Value.ToJsonString())) : [];

    /// <summary>One change as the wire has it (the inverse of <see cref="Changes"/>).</summary>
    public static JsonObject Change(RowChange change)
    {
        var node = new JsonObject { ["action"] = change.Action.ToString().ToLowerInvariant(), ["entity"] = change.Entity };
        if (change.Action != RowAction.Insert) { node["key"] = Key(change.Key); node["expected_version"] = change.ExpectedVersion; }
        if (change.Action != RowAction.Delete)
        {
            var values = new JsonObject();
            foreach (var (name, value) in change.Values) values[name] = Value(value);
            node["values"] = values;
        }
        if (change.EffectiveFrom is { } from) node["effective_from"] = from.ToString("O");
        return node;
    }

    /// <summary>The changes of a body's <c>changes</c> array, or the sentence that says what is wrong with it.</summary>
    public static (List<RowChange>? Changes, string? Problem) Changes(JsonArray list)
    {
        var changes = new List<RowChange>();
        foreach (var item in list)
        {
            if (item is not JsonObject c || (string?)c["entity"] is not { } entity || !Enum.TryParse<RowAction>((string?)c["action"], true, out var action))
                return (null, "Each change needs an \"entity\" and an \"action\" of insert, update or delete.");
            DateTimeOffset? from = c["effective_from"] is { } f && DateTimeOffset.TryParse((string?)f, out var parsed) ? parsed : null;
            var values = ObjectOf(c["values"]);
            var key = ObjectOf(c["key"]);
            long? version = c["expected_version"] is { } v ? (long)v : null;
            if (action != RowAction.Insert && version is null) return (null, "An update or delete needs \"expected_version\".");
            changes.Add(new RowChange(action, entity, action == RowAction.Insert ? new Dictionary<string, object?>() : key, version, values) { EffectiveFrom = from });
        }
        return (changes, null);
    }

    /// <summary>The body of a refused write: every broken rule, each with the rule's name.</summary>
    public static JsonObject Refusal(WriteRefusedException refused) => new()
    {
        ["error"] = "The change breaks the model's rules, so nothing was written.",
        ["violations"] = new JsonArray([.. refused.Violations.Select(v => (JsonNode)new JsonObject
        {
            ["rule"] = v.Rule, ["entity"] = v.Entity, ["attribute"] = v.Attribute, ["key"] = Key(v.Key), ["message"] = v.Message,
        })]),
    };
}

/// <summary>Whether a write waits for a person, and who decides (plan 15 section 3, Z07), whoever serves the model.</summary>
public static class ChangeApproval
{
    /// <summary>True when a write by this caller waits: an agent's, unless the model lets agents write directly; anyone's, when the model asks for approval of changes.</summary>
    public static bool Needs(DataModel.DataModel model, bool byAgent) =>
        byAgent ? model.Agents == AgentWrites.Request : model.Approval != ApprovalLevel.None;

    /// <summary>Whether only an administrator decides a request on this model (an owner's approval); otherwise an author does, or an administrator.</summary>
    public static bool AdministratorDecides(DataModel.DataModel model) => model.Approval == ApprovalLevel.Owner;
}
