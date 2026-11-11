using System.Text.Json.Nodes;
using DbDataKit.DataModel;

namespace DbDataKit.Store;

/// <summary>
/// A model's schema on the wire (plan 19 Z08: <c>GET /api/v1/models/&lt;model&gt;/schema</c>): entities, keys, attributes with their canonical kind and limits. The same for a DbDataStore server and for the apps host's data route (plan 26, AP25),
/// so the shared controls read one shape.
/// </summary>
public static class ModelSchemaJson
{
    public static JsonObject Of(DbDataKit.DataModel.DataModel model) => new()
    {
        ["name"] = model.Name,
        ["title"] = model.Title,
        ["approval"] = model.Approval.ToString().ToLowerInvariant(),
        ["instances"] = model.Instances.ToString().ToLowerInvariant(),
        ["entities"] = new JsonArray(model.Entities.Select(e => (JsonNode)new JsonObject
        {
            ["name"] = e.Name,
            ["title"] = e.Title,
            ["key"] = new JsonArray(e.Key.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
            ["history"] = e.History.ToString().ToLowerInvariant(),
            ["role"] = e.Role.ToString().ToLowerInvariant(),
            ["attributes"] = new JsonArray(e.Attributes.Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Name,
                ["title"] = a.Title,
                ["type"] = a.Type,
                ["kind"] = a.Canonical.Kind.ToString().ToLowerInvariant(),
                ["length"] = a.Canonical.Length,
                ["precision"] = a.Canonical.Precision,
                ["scale"] = a.Canonical.Scale,
                ["required"] = a.Required,
                ["unique"] = a.Unique,
                ["default"] = a.Default,
                ["pattern"] = a.Pattern,
                ["min"] = a.Min,
                ["max"] = a.Max,
                ["allowed"] = a.Allowed is null ? null : new JsonArray(a.Allowed.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["references"] = a.References is null ? null : new JsonObject { ["model"] = a.References.Model, ["entity"] = a.References.Entity, ["attribute"] = a.References.Attribute },
            }).ToArray()),
            ["rules"] = new JsonArray((e.Rules ?? []).Select(r => (JsonNode)new JsonObject { ["kind"] = r.Kind.ToString(), ["attribute"] = r.Attribute, ["group_by"] = new JsonArray(r.GroupBy.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()) }).ToArray()),
        }).ToArray()),
    };
}
