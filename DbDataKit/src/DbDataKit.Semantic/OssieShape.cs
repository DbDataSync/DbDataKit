using System.Text.Json.Nodes;

namespace DbDataKit.Semantic;

/// <summary>
/// Where the catalog and Apache Ossie use different words for the same thing: the catalog's table is Ossie's dataset, a
/// column is a field, and a column that holds times says so with <c>is_time</c> where Ossie nests it in <c>dimension</c>.
/// </summary>
internal static class OssieShape
{
    /// <summary>A catalog table as an Ossie dataset.</summary>
    public static JsonObject ToDataset(JsonObject table)
    {
        var copy = (JsonObject)table.DeepClone();
        if (copy["columns"] is JsonArray columns)
        {
            copy.Remove("columns");
            foreach (var column in columns.Select(c => c!.AsObject()))
                if (column.Remove("is_time", out var isTime)) column["dimension"] = new JsonObject { ["is_time"] = isTime };
            copy["fields"] = columns;
        }
        return copy;
    }

    /// <summary>An Ossie dataset as a catalog table. Ossie's <c>dimension</c> marker on a field survives only as <c>is_time</c>.</summary>
    public static JsonObject ToTable(JsonObject dataset)
    {
        var copy = (JsonObject)dataset.DeepClone();
        if (copy["fields"] is JsonArray fields)
        {
            copy.Remove("fields");
            foreach (var field in fields.Select(f => f!.AsObject()))
                if (field.Remove("dimension", out var marker) && marker?["is_time"] is JsonNode isTime) field["is_time"] = isTime.DeepClone();
            copy["columns"] = fields;
        }
        return copy;
    }
}
