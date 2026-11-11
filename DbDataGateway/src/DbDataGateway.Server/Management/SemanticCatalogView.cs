using System.Text.Json.Nodes;
using DbDataKit.Semantic;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The content of a model's catalog for a person who may query it (plan 14a SL12, D19): dimensions, attributes with their type, synonyms and physical binding by catalog source, measures, hierarchies, calendars,
/// segments and saved queries. It is built from the loaded catalog and says what the catalog says about names: no data source, no connection string and no secret reference of the mapping is in it (those are
/// the publisher's, read by the management route).
/// </summary>
public static class SemanticCatalogView
{
    public static JsonObject Build(Catalog catalog, string name, int? version, bool published)
    {
        var doc = catalog.Doc;
        var dimensions = new JsonArray();
        foreach (var (dimension, def) in doc.Dimensions.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var attributes = new JsonArray();
            foreach (var (key, attribute) in doc.Attributes.Where(a => a.Key.StartsWith(dimension + ".", StringComparison.Ordinal)).OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                var bindings = new JsonArray();
                foreach (var (source, binding) in attribute.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
                    bindings.Add(new JsonObject { ["source"] = source, ["join"] = binding.Join, ["column"] = binding.Column, ["dax"] = binding.Dax });
                attributes.Add(new JsonObject
                {
                    ["name"] = key, ["type"] = attribute.Type, ["synonyms"] = Strings(attribute.Synonyms), ["bindings"] = bindings,
                    ["sort_key"] = attribute.SortKey, ["time"] = attribute.IsTime ? true : null,
                });
            }
            dimensions.Add(new JsonObject { ["name"] = dimension, ["label"] = def.Label, ["default_role"] = def.DefaultRole, ["attributes"] = attributes });
        }

        var measures = new JsonArray();
        foreach (var (measureName, m) in doc.Measures.OrderBy(m => m.Key, StringComparer.Ordinal))
            measures.Add(new JsonObject
            {
                ["name"] = measureName, ["type"] = m.Type, ["source"] = m.Derived is null ? m.Source : null, ["aggregation"] = m.Derived is null ? m.Agg : null,
                ["column"] = m.Derived is null ? m.Column : null, ["derived"] = m.Derived?.Op, ["format"] = m.Format?.DeepClone(), ["synonyms"] = Strings(m.Synonyms),
                ["roles"] = new JsonObject(m.Roles.Select(r => new KeyValuePair<string, JsonNode?>(r.Key, r.Value))), ["filtered"] = m.Filters.Count > 0 ? true : null,
            });

        var hierarchies = new JsonArray([.. doc.Hierarchies.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => (JsonNode)new JsonObject { ["name"] = h.Key, ["levels"] = Strings(h.Value.Levels) })]);
        var calendars = new JsonArray([.. doc.Calendars.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => (JsonNode)new JsonObject
        {
            ["name"] = c.Key, ["date"] = c.Value.Date, ["implicit"] = c.Value.Implicit ? true : null,
            ["levels"] = new JsonObject(c.Value.Levels.Select(l => new KeyValuePair<string, JsonNode?>(l.Key, l.Value))),
        })]);
        var segments = Strings(doc.Segments.Keys.Order(StringComparer.Ordinal));
        var queries = new JsonArray([.. doc.Queries.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => (JsonNode)new JsonObject
        {
            ["name"] = q.Key, ["description"] = q.Value.Description, ["sql"] = q.Value.Sql, ["request"] = q.Value.Request?.DeepClone(), ["parameters"] = q.Value.Parameters.DeepClone(),
        })]);
        // the physical source of each catalog source by kind and table only: where the fact rows come from is part of the model, which data source serves it is not
        var sources = new JsonArray([.. doc.Sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.Key, ["kind"] = s.Value.Kind, ["fact_table"] = s.Value.Fact.Table, ["joins"] = Strings(s.Value.Joins.Select(j => j.Value.Table)),
        })]);

        return new JsonObject
        {
            ["name"] = name, ["version"] = version, ["published"] = published, ["timezone"] = doc.Timezone, ["dimensions"] = dimensions, ["measures"] = measures,
            ["hierarchies"] = hierarchies, ["calendars"] = calendars, ["segments"] = segments, ["queries"] = queries, ["sources"] = sources,
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);
}
