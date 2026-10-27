using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace DbDataKit.Semantic;

/// <summary>A catalog read from an Ossie document, with what the import could not carry over.</summary>
public sealed record PlainImport(JsonObject Catalog, List<string> Notes);

public static partial class CatalogImport
{
    [GeneratedRegex(@"^\s*(SUM|AVG|MIN|MAX|COUNT)\s*\(\s*(DISTINCT\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SimpleAggregate();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotName();

    /// <summary>A logical name part: lower case, letters digits and underscores, starting with a letter.</summary>
    private static string Part(string text, params string[] strip)
    {
        var name = text.ToLowerInvariant();
        foreach (var prefix in strip)
            if (name.StartsWith(prefix) && name.Length > prefix.Length) { name = name[prefix.Length..]; break; }
        name = NotName().Replace(name, "_").Trim('_');
        return name.Length == 0 || !char.IsAsciiLetter(name[0]) ? "x_" + name : name;
    }

    private static string LogicalType(string? datatype) => datatype switch
    {
        "Integer" => "integer", "Decimal" or "Float" => "number", "Boolean" => "boolean",
        "Date" => "date", "DateTime" or "DateTimeTz" => "datetime", _ => "string",
    };

    /// <summary>
    /// Reads a plain Ossie document, one with no QIS extension, as a catalog over one source. What it can say about the
    /// logical layer it infers, and lists what it left out:
    /// - Every dataset and relationship comes across as they are. A dataset that is only ever on the many side of a
    ///   relationship is a fact; one on the one side is a dimension table.
    /// - A field becomes a dimension when it is marked as one or is text, a date or a boolean, and is not a key. It is named
    ///   dataset.field, without a dim_ or fact_ prefix. Where a fact reaches a dataset by several relationships, each
    ///   relationship is a role: the dimension reads the first role by default, and each role is also a dimension (role.field).
    /// - A dataset metric becomes a measure when its SQL is one aggregate over one of its fields (SUM, AVG, MIN, MAX, COUNT,
    ///   COUNT DISTINCT). Its rollup follows the aggregate: sum and count add, min and max repeat, anything else is
    ///   recomputed at the source (none). It is marked inferred, not governed.
    /// - Metrics that are anything else (CASE, ratios, joins across datasets) and model-level metrics are not imported.
    /// </summary>
    public static PlainImport FromPlainOssie(JsonObject ossie, string source, string kind = "sqlserver")
    {
        var notes = new List<string>();
        var datasetNodes = ossie["datasets"]?.AsArray().Select(d => d!.AsObject()).ToList() ?? [];
        var relationshipNodes = ossie["relationships"]?.AsArray().Select(r => r!.AsObject()).ToList() ?? [];
        string Str(JsonNode? n) => n!.GetValue<string>();

        var root = new JsonObject
        {
            ["version"] = "0.3", ["name"] = Part(Str(ossie["name"] ?? "ossie_model")),
            ["sources"] = new JsonObject { [source] = new JsonObject { ["kind"] = kind } },
        };
        if (ossie["description"] is { } description) root["description"] = description.DeepClone();

        // Roles: a fact table that reaches one dataset by several relationships needs each to say which it means.
        var roles = new Dictionary<string, string>();
        foreach (var group in relationshipNodes.GroupBy(r => (Str(r["from"]), Str(r["to"]))).Where(g => g.Count() > 1))
            foreach (var r in group) roles[Str(r["name"])] = Part(Str(r["name"]));

        var tables = new JsonArray();
        foreach (var d in datasetNodes)
        {
            var copy = OssieShape.ToTable(d);
            copy.Remove("metrics");
            copy.Remove("custom_extensions");
            copy["x_source"] = source;
            tables.Add(copy);
        }
        root["tables"] = tables;
        var relationships = new JsonArray();
        foreach (var r in relationshipNodes)
        {
            var copy = (JsonObject)r.DeepClone();
            copy.Remove("custom_extensions");
            if (roles.TryGetValue(Str(r["name"]), out var role)) copy["x_role"] = role;
            relationships.Add(copy);
        }
        if (relationships.Count > 0) root["relationships"] = relationships;

        var many = relationshipNodes.Select(r => Str(r["from"])).ToHashSet();
        var one = relationshipNodes.Select(r => Str(r["to"])).ToHashSet();
        var prefixes = new Dictionary<string, string>();
        var used = new HashSet<string>();
        foreach (var d in datasetNodes)
        {
            var prefix = Part(Str(d["name"]), "dim_", "fact_", "dimension_", "fct_");
            var unique = prefix;
            for (int n = 2; !used.Add(unique); n++) unique = $"{prefix}_{n}";
            prefixes[Str(d["name"])] = unique;
        }

        // The columns that join datasets are keys, not dimensions.
        var keys = new Dictionary<string, HashSet<string>>();
        foreach (var d in datasetNodes)
        {
            var set = new HashSet<string>(d["primary_key"]?.AsArray().Select(k => Str(k)) ?? []);
            foreach (var u in d["unique_keys"]?.AsArray() ?? []) foreach (var k in u!.AsArray()) set.Add(Str(k));
            keys[Str(d["name"])] = set;
        }
        foreach (var r in relationshipNodes)
        {
            foreach (var c in r["from_columns"]!.AsArray()) keys[Str(r["from"])].Add(Str(c));
            foreach (var c in r["to_columns"]!.AsArray()) keys[Str(r["to"])].Add(Str(c));
        }

        var dimensions = new JsonObject();
        foreach (var d in datasetNodes)
        {
            var dataset = Str(d["name"]);
            var datasetRoles = relationshipNodes.Where(r => Str(r["to"]) == dataset && roles.ContainsKey(Str(r["name"])))
                .Select(r => roles[Str(r["name"])]).Distinct().ToList();
            foreach (var field in d["fields"]?.AsArray().Select(f => f!.AsObject()) ?? [])
            {
                var name = Str(field["name"]);
                var datatype = field["datatype"]?.GetValue<string>();
                var marked = field["dimension"] is not null;
                if (keys[dataset].Contains(name) && !marked) continue;
                if (!marked && datatype is not (null or "String" or "Date" or "DateTime" or "DateTimeTz" or "Boolean")) continue;
                var binding = new JsonObject { ["table"] = dataset, ["column"] = name };
                var attribute = new JsonObject
                {
                    ["type"] = LogicalType(datatype),
                    ["bindings"] = new JsonObject { [source] = binding },
                };
                if (field["label"] is { } label) attribute["label"] = label.DeepClone();
                if (field["description"] is { } text) attribute["description"] = text.DeepClone();
                // One dimension per dataset and one attribute per field. A dataset reached in several roles reads the first by default, and the catalog loader makes each role usable by name too.
                if (dimensions[prefixes[dataset]] is not JsonObject dimension)
                {
                    dimension = new JsonObject { ["attributes"] = new JsonObject() };
                    if (datasetRoles.Count > 0) dimension["default_role"] = datasetRoles[0];
                    dimensions[prefixes[dataset]] = dimension;
                }
                dimension["attributes"]![Part(name)] = attribute;
            }
        }
        root["dimensions"] = dimensions;

        var measures = new JsonObject();
        foreach (var d in datasetNodes)
        {
            var dataset = Str(d["name"]);
            var fields = d["fields"]?.AsArray().Select(f => Str(f!["name"])).ToHashSet() ?? [];
            foreach (var metric in d["metrics"]?.AsArray().Select(m => m!.AsObject()) ?? [])
            {
                var metricName = Str(metric["name"]);
                var sql = metric["expression"]?["dialects"]?.AsArray()
                    .Where(x => Str(x!["dialect"]) == "ANSI_SQL").Select(x => Str(x!["expression"])).FirstOrDefault();
                var match = sql is null ? null : SimpleAggregate().Match(sql);
                if (match is null || !match.Success || !fields.Contains(match.Groups[3].Value))
                {
                    notes.Add($"{dataset}.{metricName}: not imported; its SQL isn't one aggregate over one field of the dataset ({sql ?? "no ANSI_SQL expression"}).");
                    continue;
                }
                var fn = match.Groups[1].Value.ToUpperInvariant();
                var distinct = match.Groups[2].Success;
                var agg = fn == "COUNT" ? (distinct ? "count_distinct" : "count") : fn.ToLowerInvariant();
                if (distinct && fn != "COUNT") { notes.Add($"{dataset}.{metricName}: not imported; {fn}(DISTINCT ...) isn't an aggregate the catalog has."); continue; }
                var rule = agg switch { "sum" or "count" => "sum", "min" => "min", "max" => "max", _ => "none" };
                var logical = $"{prefixes[dataset]}.{Part(metricName)}";
                var measure = new JsonObject
                {
                    ["type"] = agg is "count" or "count_distinct" ? "integer" : "number",
                    ["source"] = source, ["agg"] = agg,
                    ["of"] = new JsonObject { ["table"] = dataset, ["column"] = match.Groups[3].Value },
                    ["semantics"] = new JsonObject
                    {
                        ["rollup"] = new JsonObject { ["default"] = rule }, ["spread"] = "blank", ["status"] = "inferred",
                    },
                };
                if (metric["description"] is { } text) measure["description"] = text.DeepClone();
                if (metric["expression"]!["dialects"]!.AsArray().FirstOrDefault(x => Str(x!["dialect"]) == "DAX") is { } dax)
                    measure["native"] = new JsonObject { ["DAX"] = dax["expression"]!.DeepClone() };
                if (!measures.TryAdd(logical, measure)) notes.Add($"{dataset}.{metricName}: not imported; another metric already has the name {logical}.");
            }
        }
        foreach (var model in ossie["metrics"]?.AsArray() ?? [])
            notes.Add($"{model!["name"]}: not imported; model-level metrics can span datasets and the catalog's measures are about one.");
        root["measures"] = measures;
        if (measures.Count == 0) notes.Add("The document has no importable metrics, so the catalog has no measures.");
        return new PlainImport(root, notes);
    }
}
