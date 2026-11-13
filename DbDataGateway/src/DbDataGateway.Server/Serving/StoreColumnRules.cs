using System.Text.Json.Nodes;
using DbDataKit.Access;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The column rules of an entity model's <c>access</c> section applied to what the group's Store answers (plan 22a GP13, GP14): the entity is the dataset and its attribute the column, as <see cref="DbDataKit.Serving.ColumnShapedEntities"/> says for an Arrow answer,
/// here on the JSON of a row. A hidden attribute is not in the row (and not in the model's schema for the caller), a nulled one is null, a masked one is the fixed mask or the keyed hash of its value (a hash with no key to make it with hides the attribute instead: a mask is
/// never made with a key that is not the group's). The treatments are those of the caller's own names and groups, so a caller no rule names sees every attribute as it is.
/// </summary>
public static class StoreColumnRules
{
    /// <summary>The row with the caller's treatments applied (the row itself is changed and returned).</summary>
    public static JsonObject Row(JsonObject row, string entity, ColumnTreatments treatments, Func<byte[]>? maskKey)
    {
        if (treatments.IsEmpty || row["values"] is not JsonObject values) return row;
        foreach (var name in values.Select(p => p.Key).ToList())
        {
            if (treatments.Of(entity, name) is not { } treatment) continue;
            switch (treatment.Treatment)
            {
                case ColumnTreatment.Hide:
                    values.Remove(name);
                    break;
                case ColumnTreatment.Mask when treatment.Mask == MaskKind.Fixed:
                    values[name] = ColumnMasks.FixedValue;
                    break;
                case ColumnTreatment.Mask when maskKey is not null:
                    values[name] = values[name] is JsonValue v && v.TryGetValue<string>(out var text) ? ColumnMasks.Hash(maskKey(), entity, name, text) : null;
                    break;
                case ColumnTreatment.Mask:
                    values.Remove(name);
                    break;
                default:
                    values[name] = null;
                    break;
            }
        }

        return row;
    }

    /// <summary>The schema of a model without the attributes the caller's rules hide (an entity with every attribute hidden keeps its key, which a rule cannot hide).</summary>
    public static JsonObject Schema(JsonObject schema, Func<string, ColumnTreatments> treatments)
    {
        if (schema["entities"] is not JsonArray entities) return schema;
        foreach (var entity in entities.OfType<JsonObject>())
        {
            var name = (string?)entity["name"];
            if (name is null || entity["attributes"] is not JsonArray attributes) continue;
            var resolved = treatments(name);
            if (resolved.IsEmpty) continue;
            foreach (var attribute in attributes.OfType<JsonObject>().ToList())
                if ((string?)attribute["name"] is { } column && resolved.Of(name, column) is { } t)
                {
                    if (t.Treatment == ColumnTreatment.Hide) attributes.Remove(attribute);
                    else attribute["access"] = t.ToString();
                }
        }

        return schema;
    }
}
