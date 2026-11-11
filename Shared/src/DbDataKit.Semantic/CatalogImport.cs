using System.Text.Json.Nodes;

namespace DbDataKit.Semantic;

/// <summary>
/// Reads the Ossie documents <see cref="CatalogExport"/> writes (one per source) back into a catalog. The physical model comes
/// from the Ossie datasets and relationships; everything else comes from each document's QIS extension. Nothing is lost:
/// importing an export gives back the catalog it came from.
/// </summary>
public static partial class CatalogImport
{
    public static JsonObject FromOssie(IEnumerable<JsonObject> documents)
    {
        var docs = documents.ToList();
        var extensions = docs.Select(Extension).ToList();
        var first = extensions[0];

        var root = new JsonObject
        {
            ["version"] = first["version"]!.DeepClone(),
            ["name"] = first["catalog"]!.DeepClone(),
        };
        if (docs[0]["description"] is JsonNode description) root["description"] = description.DeepClone();

        var sources = new JsonObject();
        var tables = new JsonArray();
        var relationships = new JsonArray();
        var dimensions = new JsonObject();
        var measures = new JsonObject();
        for (int i = 0; i < docs.Count; i++)
        {
            var source = extensions[i]["source"]!.GetValue<string>();
            sources[source] = extensions[i]["source_def"]!.DeepClone();

            foreach (var d in docs[i]["datasets"]!.AsArray())
            {
                var copy = OssieShape.ToTable(d!.AsObject());
                copy.Remove("metrics");                    // the structured measures come from the extension
                copy["x_source"] = source;
                tables.Add(copy);
            }
            var roles = extensions[i]["relationship_roles"]!.AsObject();
            foreach (var r in docs[i]["relationships"]?.AsArray() ?? [])
            {
                var copy = (JsonObject)r!.DeepClone();
                if (roles[copy["name"]!.GetValue<string>()] is JsonNode role) copy["x_role"] = role.DeepClone();
                relationships.Add(copy);
            }
            // An attribute shared by several sources is in each document with that source's binding: put them back together.
            foreach (var (name, dimension) in extensions[i]["dimensions"]!.AsObject())
            {
                if (dimensions[name] is not JsonObject existing) { dimensions[name] = dimension!.DeepClone(); continue; }
                foreach (var (attributeName, attribute) in dimension!["attributes"]!.AsObject())
                {
                    if (existing["attributes"]![attributeName] is JsonObject have)
                        foreach (var (src, binding) in attribute!["bindings"]!.AsObject()) have["bindings"]![src] = binding!.DeepClone();
                    else existing["attributes"]![attributeName] = attribute!.DeepClone();
                }
            }
            foreach (var (name, m) in extensions[i]["measures"]!.AsObject()) measures[name] = m!.DeepClone();
        }
        foreach (var (name, m) in first["derived_measures"]!.AsObject()) measures[name] = m!.DeepClone();

        root["sources"] = sources;
        root["tables"] = tables;
        if (relationships.Count > 0) root["relationships"] = relationships;
        root["dimensions"] = dimensions;
        root["measures"] = measures;
        if (first["hierarchies"] is JsonObject { Count: > 0 } hierarchies) root["hierarchies"] = hierarchies.DeepClone();
        if (first["calendars"] is JsonObject { Count: > 0 } calendars) root["calendars"] = calendars.DeepClone();
        if (first["segments"] is JsonObject { Count: > 0 } segments) root["segments"] = segments.DeepClone();
        if (first["timezone"] is JsonValue zone) root["timezone"] = zone.DeepClone();
        return root;
    }

    private static JsonObject Extension(JsonObject doc)
    {
        var ext = doc["custom_extensions"]?.AsArray().FirstOrDefault(e => e!["vendor_name"]!.GetValue<string>() == "COMMON");
        return ext is null
            ? throw new InvalidOperationException($"{doc["name"]} has no QIS extension, so it can't be imported as a catalog.")
            : JsonNode.Parse(ext["data"]!.GetValue<string>())!["qis"]!.AsObject();
    }
}
