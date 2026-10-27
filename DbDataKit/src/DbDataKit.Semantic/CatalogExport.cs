using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>
/// Exports the catalog as Apache Ossie documents, one per source (Ossie describes one model per document). The physical
/// model goes across in Ossie's words (<see cref="OssieShape"/>: a table is a dataset, a column is a field). Each structured
/// measure becomes a metric scoped to its table, with an ANSI_SQL expression (and a DAX one if the model names it), where it
/// can be written as scalar SQL over that table. Everything Ossie has no place for (dimensions and attributes, rollup and
/// spread semantics, hierarchies, measures that need another table) travels in one custom extension as a JSON string.
/// </summary>
public static class CatalogExport
{
    public const string OssieVersion = "0.2.0.dev0";

    public static Dictionary<string, JsonObject> ToOssie(Catalog cat)
    {
        var src = cat.Source;
        var result = new Dictionary<string, JsonObject>();
        foreach (var (source, def) in src["sources"]!.AsObject())
        {
            var tables = src["tables"]!.AsArray().Select(d => d!.AsObject()).Where(d => d["x_source"]!.GetValue<string>() == source).ToList();
            var names = tables.Select(d => d["name"]!.GetValue<string>()).ToHashSet();
            var measures = src["measures"]!.AsObject().Where(m => m.Value!["source"]?.GetValue<string>() == source).ToList();

            var exported = new JsonArray();
            var inExtension = new JsonObject();
            foreach (var d in tables)
            {
                var copy = OssieShape.ToDataset(d);
                copy.Remove("x_source");
                var metrics = new JsonArray();
                foreach (var (name, m) in measures.Where(m => m.Value!["of"]!["table"]!.GetValue<string>() == d["name"]!.GetValue<string>()))
                {
                    var metric = Metric(cat, source, d, name, m!.AsObject());
                    if (metric is null) inExtension[name] = "not expressible as a metric of its own table";
                    else metrics.Add(metric);
                }
                if (metrics.Count > 0) copy["metrics"] = metrics;
                exported.Add(copy);
            }

            var doc = new JsonObject
            {
                ["version"] = OssieVersion,
                ["name"] = $"{cat.Name}_{source}",
                ["datasets"] = exported,
            };
            if (src["description"] is JsonNode description) doc["description"] = description.DeepClone();
            var relationships = new JsonArray(src["relationships"]?.AsArray()
                .Where(r => names.Contains(r!["from"]!.GetValue<string>()) && names.Contains(r["to"]!.GetValue<string>()))
                .Select(r => { var c = (JsonObject)r!.DeepClone(); c.Remove("x_role"); return (JsonNode)c; }).ToArray() ?? []);
            if (relationships.Count > 0) doc["relationships"] = relationships;

            // The logical layer for this source: its dimensions' bindings, and its measures' meaning.
            var dimensions = new JsonObject();
            foreach (var (name, dimension) in src["dimensions"]!.AsObject())
            {
                var attributes = new JsonObject();
                foreach (var (attributeName, attribute) in dimension!["attributes"]!.AsObject())
                    if (attribute!["bindings"]![source] is JsonNode binding)
                    {
                        var a = (JsonObject)attribute.DeepClone();
                        a["bindings"] = new JsonObject { [source] = binding.DeepClone() };
                        attributes[attributeName] = a;
                    }
                if (attributes.Count == 0) continue;
                var d = (JsonObject)dimension.DeepClone();
                d["attributes"] = attributes;
                dimensions[name] = d;
            }
            var qis = new JsonObject
            {
                ["catalog"] = cat.Name, ["version"] = src["version"]!.DeepClone(), ["source"] = source,
                ["kind"] = def!["kind"]!.DeepClone(), ["source_def"] = def.DeepClone(),
                ["relationship_roles"] = new JsonObject(src["relationships"]?.AsArray()
                    .Where(r => r!["x_role"] is not null && names.Contains(r["from"]!.GetValue<string>()) && names.Contains(r["to"]!.GetValue<string>()))
                    .Select(r => KeyValuePair.Create<string, JsonNode?>(r!["name"]!.GetValue<string>(), r["x_role"]!.DeepClone())) ?? []),
                ["calendars"] = src["calendars"]?.DeepClone() ?? new JsonObject(),
                ["segments"] = src["segments"]?.DeepClone() ?? new JsonObject(),
                ["timezone"] = src["timezone"]?.DeepClone(),
                ["dimensions"] = dimensions,
                ["measures"] = new JsonObject(measures.Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key, m.Value!.DeepClone()))),
                // Derived measures read measures from any source, so they belong to the logical layer of every document.
                ["derived_measures"] = new JsonObject(src["measures"]!.AsObject().Where(m => m.Value!["compute"] is not null)
                    .Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key, m.Value!.DeepClone()))),
                ["hierarchies"] = src["hierarchies"]?.DeepClone() ?? new JsonObject(),
            };
            if (inExtension.Count > 0) qis["metrics_not_exported"] = inExtension;
            doc["custom_extensions"] = new JsonArray(new JsonObject
            {
                ["vendor_name"] = "COMMON",
                ["data"] = new JsonObject { ["qis"] = qis }.ToJsonString(),
            });
            result[source] = doc;
        }
        return result;
    }

    /// <summary>A measure as an Ossie dataset-scoped metric, or null when its filters can't be written over the table's own columns.</summary>
    private static JsonObject? Metric(Catalog cat, string source, JsonObject table, string name, JsonObject measure)
    {
        var column = measure["of"]!["column"]!.GetValue<string>();
        var value = column;
        if (measure["filters"] is JsonArray filters)
        {
            var conditions = new List<string>();
            foreach (var f in filters.Select(f => Filter.Parse(f!)))
            {
                var sql = FilterSql(cat, source, table["name"]!.GetValue<string>(), f);
                if (sql is null) return null;
                conditions.Add(sql);
            }
            value = $"CASE WHEN {string.Join(" AND ", conditions)} THEN {column} END";
        }
        var agg = measure["agg"]!.GetValue<string>();
        var sqlExpr = agg == "count_distinct" ? $"COUNT(DISTINCT {value})" : $"{agg.ToUpperInvariant()}({value})";
        var dialects = new JsonArray(new JsonObject { ["dialect"] = "ANSI_SQL", ["expression"] = sqlExpr });
        if (measure["native"]?["DAX"] is JsonNode dax) dialects.Add(new JsonObject { ["dialect"] = "DAX", ["expression"] = dax.DeepClone() });
        var metric = new JsonObject
        {
            ["name"] = name.Replace('.', '_'),
            ["expression"] = new JsonObject { ["dialects"] = dialects },
            ["datatype"] = measure["type"]!.GetValue<string>() == "integer" ? "Integer" : "Decimal",
        };
        if (measure["description"] is JsonNode d) metric["description"] = d.DeepClone();
        return metric;
    }

    /// <summary>A filter over columns of one table as scalar SQL, or null if a member isn't a column of that table.</summary>
    private static string? FilterSql(Catalog cat, string source, string table, Filter f)
    {
        switch (f)
        {
            case AndFilter a: return Join(a.Items.Select(i => FilterSql(cat, source, table, i)), " AND ");
            case OrFilter o: return Join(o.Items.Select(i => FilterSql(cat, source, table, i)), " OR ");
            case LeafFilter l:
                var binding = cat.Binding(l.Name, source);
                if (binding is null || (binding.Join ?? cat.Doc.Sources[source].Fact.Table) is null) return null;
                // Only a fact column (no join) is a column of the fact table.
                if (binding.Join is not null) return null;
                var col = binding.Column;
                string Lit(JsonNode v) => v.GetValueKind() == JsonValueKind.String
                    ? "'" + v.GetValue<string>().Replace("'", "''") + "'" : v.GetValue<decimal>().ToString(CultureInfo.InvariantCulture);
                return l.Operator switch
                {
                    "equals" => l.Values.Count == 1 ? $"{col} = {Lit(l.Values[0])}" : $"{col} IN ({string.Join(", ", l.Values.Select(Lit))})",
                    "not_equals" => l.Values.Count == 1 ? $"{col} <> {Lit(l.Values[0])}" : $"{col} NOT IN ({string.Join(", ", l.Values.Select(Lit))})",
                    "between" => $"{col} BETWEEN {Lit(l.Values[0])} AND {Lit(l.Values[1])}",
                    "gt" => $"{col} > {Lit(l.Values[0])}", "gte" => $"{col} >= {Lit(l.Values[0])}",
                    "lt" => $"{col} < {Lit(l.Values[0])}", "lte" => $"{col} <= {Lit(l.Values[0])}",
                    "set" => $"{col} IS NOT NULL", "not_set" => $"{col} IS NULL",
                    _ => null,
                };
            default: return null;
        }
    }

    private static string? Join(IEnumerable<string?> parts, string sep)
    {
        var list = parts.ToList();
        return list.Any(p => p is null) ? null : "(" + string.Join(sep, list) + ")";
    }
}
