using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>A registered ad hoc table: rows and a descriptor under a name, one version per registration.</summary>
public sealed class RegisteredTable
{
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required string Scope { get; init; }
    public required JsonObject Descriptor { get; init; }
    public required List<Dictionary<string, object?>> Rows { get; init; }
    public string Ref => $"{Name}@v{Version}";
}

/// <summary>Similarity and normalization for fuzzy value maps. Deterministic and dependency-free.</summary>
public static class Fuzzy
{
    /// <summary>Trim, case-fold, strip accents and punctuation, collapse spaces.</summary>
    public static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c) || c == '-' || c == '_') sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static double Jaro(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = Math.Max(0, i - window); j <= Math.Min(b.Length - 1, i + window); j++)
            {
                if (bMatched[j] || a[i] != b[j]) continue;
                aMatched[i] = bMatched[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;
        var transpositions = 0;
        for (int i = 0, k = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        var m = (double)matches;
        return (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3;
    }

    /// <summary>Jaro-Winkler with the usual 0.1 prefix weight, a prefix of at most 4, and a boost only above 0.7.</summary>
    public static double JaroWinkler(string a, string b)
    {
        var jaro = Jaro(a, b);
        if (jaro <= 0.7) return jaro;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}

/// <summary>
/// Ad hoc tables, column bindings and value maps (the mapping spec), and the bridges they produce.
/// One registry belongs to one session and registers its dimensions on that session's catalog copy.
/// </summary>
public sealed class TableRegistry(Catalog cat, Func<string, List<string>> members)
{
    public const string Unmapped = "Unmapped";

    private readonly Dictionary<string, List<RegisteredTable>> _tables = new();
    private readonly Dictionary<string, JsonObject> _maps = new();     // id -> value map document
    private readonly Dictionary<string, List<string>> _memberCache = new();

    private static QisError Err(string code, string path, string message, List<string>? suggestions = null) =>
        new("mapping", code, path, message, suggestions is { Count: > 0 } ? suggestions : null);

    private List<string> Members(string attribute) =>
        _memberCache.TryGetValue(attribute, out var m) ? m : _memberCache[attribute] = members(attribute);

    // ---------- tables ----------

    public List<QisError> RegisterTable(JsonObject doc, out RegisteredTable? registered)
    {
        registered = null;
        var name = doc["name"]!.GetValue<string>();
        if (cat.Exists(name))
            return [Err("name_collision", "$.name", $"{name} is already a catalog member.")];
        var descriptor = doc["descriptor"]!.AsObject();
        var fields = descriptor["schema"]!["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToList();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var r in doc["rows"]!.AsArray())
        {
            var row = new Dictionary<string, object?>();
            foreach (var f in fields)
                row[f] = r![f] is JsonValue v ? Plain(v) : null;
            rows.Add(row);
        }
        var version = _tables.TryGetValue(name, out var existing) ? existing.Count + 1 : 1;
        registered = new RegisteredTable
        {
            Name = name, Version = version, Scope = doc["scope"]?.GetValue<string>() ?? "session",
            Descriptor = descriptor.DeepClone().AsObject(), Rows = rows,
        };
        if (!_tables.TryGetValue(name, out var list)) _tables[name] = list = [];
        list.Add(registered);
        return [];
    }

    private static object? Plain(JsonValue v) => v.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        System.Text.Json.JsonValueKind.Number => v.TryGetValue<long>(out var l) ? l : v.GetValue<decimal>(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null,
    };

    public RegisteredTable? Find(string reference)
    {
        var parts = reference.Split("@v");
        if (!_tables.TryGetValue(parts[0], out var list)) return null;
        return parts.Length == 1 ? list[^1] : list.FirstOrDefault(t => t.Version.ToString() == parts[1]);
    }

    // ---------- bindings ----------

    public List<QisError> ApplyBindings(JsonObject doc, out RegisteredTable? updated)
    {
        updated = null;
        var tableRef = doc["table"]!.GetValue<string>();
        var table = Find(tableRef);
        if (table is null) return [Err("unknown_table", "$.table", $"{tableRef} isn't a registered table.")];

        var errors = new List<QisError>();
        var bindings = doc["bindings"]!.AsArray().Select(b => b!.AsObject()).ToList();
        var tableFields = table.Descriptor["schema"]!["fields"]!.AsArray().ToDictionary(f => f!["name"]!.GetValue<string>(), f => f!);

        string? keyField = null, keyAttr = null, mapId = null, weightField = null;
        var groups = new List<(string Field, string Name)>();
        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            var p = $"$.bindings[{i}]";
            var field = b["field"]!.GetValue<string>();
            if (!tableFields.ContainsKey(field))
            {
                errors.Add(Err("unknown_field", p + ".field", $"{field} isn't a column of {table.Ref}.", tableFields.Keys.ToList()));
                continue;
            }
            if (b["role"]!.GetValue<string>() == "weight")
            {
                if (tableFields[field]["type"]!.GetValue<string>() is not ("number" or "integer"))
                    errors.Add(Err("weight_not_numeric", p + ".field", $"{field} holds weights, so it must be a number."));
                else if (weightField is not null)
                    errors.Add(Err("multiple_weights", p + ".field", "A bindings document can name one weight column."));
                else weightField = field;
                continue;
            }
            if (b["as"] is JsonNode asNode)
            {
                var asName = asNode.GetValue<string>();
                if (cat.Doc.Attributes.TryGetValue(asName, out var clash) && clash.Relation is null || cat.Doc.Measures.ContainsKey(asName))
                    errors.Add(Err("name_collision", p + ".as", $"{asName} is already a catalog member."));
                else groups.Add((field, asName));
            }
            if (b["binds_to"] is JsonNode bt)
            {
                var target = bt.GetValue<string>();
                if (!cat.IsAttribute(target) || cat.Doc.Attributes[target].Relation is not null)
                {
                    errors.Add(Err("unknown_target", p + ".binds_to", $"{target} isn't a catalog attribute.", cat.Suggestions(target, "attribute")));
                    continue;
                }
                if (keyField is not null)
                {
                    errors.Add(Err("multiple_keys_unsupported", p + ".binds_to", "A bindings document can bind one column to the catalog."));
                    continue;
                }
                keyField = field; keyAttr = target;
                mapId = b["via_map"]?.GetValue<string>();
                if (mapId is not null && !_maps.ContainsKey(mapId))
                    errors.Add(Err("unknown_map", p + ".via_map", $"{mapId} isn't a defined value map."));
            }
        }
        if (groups.Count > 0 && keyField is null && errors.Count == 0)
            errors.Add(Err("adhoc_attribute_unrelated", "$.bindings",
                "A attribute registered with 'as' needs a column bound to a catalog attribute with 'binds_to', or measures can't be related to it."));
        if (errors.Count > 0) return errors;

        if (doc["grain"] is JsonArray grain)
        {
            var grainFields = grain.Select(g =>
            {
                var name = g!.GetValue<string>();
                return bindings.FirstOrDefault(b => b["as"]?.GetValue<string>() == name || b["binds_to"]?.GetValue<string>() == name)?["field"]?.GetValue<string>();
            }).ToList();
            if (grainFields.Any(f => f is null))
                errors.Add(Err("grain_unverified", "$.grain", "A grain member isn't bound to any column."));
            else
            {
                var seen = new HashSet<string>();
                foreach (var row in table.Rows)
                    if (!seen.Add(string.Join("\u001f", grainFields.Select(f => row[f!]?.ToString() ?? ""))))
                    {
                        errors.Add(Err("grain_unverified", "$.grain", $"Rows aren't unique on {string.Join(", ", grain.Select(g => g!.GetValue<string>()))}."));
                        break;
                    }
            }
            if (errors.Count > 0) return errors;
        }

        // A binding produces a new version of the table's descriptor with roles and bindings filled in.
        var descriptor = table.Descriptor.DeepClone().AsObject();
        foreach (var f in descriptor["schema"]!["fields"]!.AsArray())
        {
            var b = bindings.FirstOrDefault(x => x["field"]!.GetValue<string>() == f!["name"]!.GetValue<string>());
            if (b is null) continue;
            f!["x_kind"] = b["role"]!.GetValue<string>();
            var bound = b["as"]?.GetValue<string>() ?? b["binds_to"]?.GetValue<string>();
            if (bound is not null) f["x_binding"] = bound;
        }
        var grainMembers = groups.Select(g => g.Name).Concat(keyAttr is null ? [] : [keyAttr]).ToList();
        descriptor["x_grain"] = new JsonArray(grainMembers.Select(g => (JsonNode)g).ToArray());
        var version = _tables[table.Name].Count + 1;
        updated = new RegisteredTable
        {
            Name = table.Name, Version = version, Scope = table.Scope, Descriptor = descriptor, Rows = table.Rows,
        };
        _tables[table.Name].Add(updated);

        var confirmed = doc["confirmed_by"] is not null;
        foreach (var (field, name) in groups)
            cat.Doc.Attributes[name] = new AttributeDef
            {
                Type = tableFields[field]["type"]!.GetValue<string>(),
                Relation = new AdHocRelation
                {
                    TableName = table.Name, TableVersion = version, GroupField = field, KeyField = keyField!,
                    KeyAttribute = keyAttr!, MapId = mapId, Confirmed = confirmed, WeightField = weightField,
                },
            };
        return [];
    }

    // ---------- value maps ----------

    public List<QisError> DefineMap(JsonObject doc, int level, out List<Dictionary<string, object?>> mappingRows)
    {
        mappingRows = [];
        var id = doc["id"]!.GetValue<string>();
        var mode = doc["mode"]!.GetValue<string>();
        var tableRef = doc["table"]!.GetValue<string>();
        var field = doc["field"]!.GetValue<string>();
        var target = doc["target"]!.GetValue<string>();

        var table = Find(tableRef);
        if (table is null) return [Err("unknown_table", "$.table", $"{tableRef} isn't a registered table.")];
        if (!table.Descriptor["schema"]!["fields"]!.AsArray().Any(f => f!["name"]!.GetValue<string>() == field))
            return [Err("unknown_field", "$.field", $"{field} isn't a column of {table.Ref}.")];
        if (!cat.IsAttribute(target) || cat.Doc.Attributes[target].Relation is not null)
            return [Err("unknown_target", "$.target", $"{target} isn't a catalog attribute.", cat.Suggestions(target, "attribute"))];
        if (mode != "catalog" && level < 4)
            return [Err("level_not_implemented", "$.mode", $"{mode} maps need engine level 4; catalog maps run at level 3.")];
        if (mode == "catalog" && (doc["scope"]?.GetValue<string>() ?? "session") is not ("shared" or "governed"))
            return [Err("catalog_map_scope_invalid", "$.scope", "A catalog map must be stored at shared or governed scope, where it has been reviewed.")];

        var errors = new List<QisError>();
        mappingRows = RunMap(doc, table, errors);
        if (errors.Count > 0) return errors;
        _maps[id] = doc.DeepClone().AsObject();
        return [];
    }

    /// <summary>Runs a value map over its table: one row per source value, from the table and from any explicit pairs.</summary>
    public List<Dictionary<string, object?>> RunMap(JsonObject map, RegisteredTable table, List<QisError> errors)
    {
        var mode = map["mode"]!.GetValue<string>();
        var field = map["field"]!.GetValue<string>();
        var target = map["target"]!.GetValue<string>();
        var canonical = Members(target);
        var values = table.Rows.Select(r => r[field]?.ToString()).Where(v => v is not null).Select(v => v!).Distinct().ToList();
        var rows = new List<Dictionary<string, object?>>();

        if (mode is "catalog" or "llm")
        {
            var pairs = new Dictionary<string, (string To, double? Confidence)>();
            var items = map["pairs"]!.AsArray();
            for (int i = 0; i < items.Count; i++)
            {
                var from = items[i]!["from"]!.GetValue<string>();
                var to = items[i]!["to"]!.GetValue<string>();
                if (!canonical.Contains(to))
                    errors.Add(Err("target_value_invalid", $"$.pairs[{i}].to", $"{to} isn't a member of {target}.", canonical));
                if (pairs.TryGetValue(from, out var prev) && prev.To != to)
                    errors.Add(Err("duplicate_from", $"$.pairs[{i}].from", $"{from} is mapped to more than one target."));
                pairs[from] = (to, items[i]!["confidence"]?.GetValue<double>());
            }
            foreach (var v in values.Concat(pairs.Keys).Distinct())
                rows.Add(pairs.TryGetValue(v, out var p)
                    ? MapRow(v, p.To, mode == "catalog" ? 1.0 : p.Confidence, mode)
                    : MapRow(v, null, null, mode));
            return rows;
        }

        // fuzzy: normalize, exact match, then similarity at or above the threshold; ties at the best score are unmatched
        var threshold = map["threshold"]?.GetValue<double>() ?? 0.92;
        var normalized = canonical.Select(c => (Member: c, Norm: Fuzzy.Normalize(c))).ToList();
        foreach (var v in values)
        {
            var nv = Fuzzy.Normalize(v);
            var exact = normalized.Where(c => c.Norm == nv).Select(c => c.Member).ToList();
            if (exact.Count == 1) { rows.Add(MapRow(v, exact[0], 1.0, "exact")); continue; }
            if (exact.Count > 1) { rows.Add(MapRow(v, null, null, "fuzzy")); continue; }
            var scored = normalized.Select(c => (c.Member, Score: Fuzzy.JaroWinkler(nv, c.Norm))).OrderByDescending(c => c.Score).ToList();
            var best = scored.Count > 0 ? scored[0] : default;
            var tied = scored.Count > 1 && Math.Abs(scored[1].Score - best.Score) < 1e-12;
            rows.Add(scored.Count > 0 && best.Score >= threshold && !tied
                ? MapRow(v, best.Member, Math.Round(best.Score, 4), "fuzzy")
                : MapRow(v, null, null, "fuzzy"));
        }
        return rows;
    }

    private static Dictionary<string, object?> MapRow(string from, string? to, double? confidence, string method) =>
        new() { ["from"] = from, ["to"] = to, ["confidence"] = confidence is null ? null : (decimal)confidence, ["method"] = method };

    public static JsonObject MappingDescriptor(string id, int rowCount, string tableRef) => new()
    {
        ["descriptor_version"] = "1.0", ["name"] = "map_" + id,
        ["schema"] = new JsonObject
        {
            ["fields"] = new JsonArray(
                new JsonObject { ["name"] = "from", ["type"] = "string", ["x_kind"] = "column" },
                new JsonObject { ["name"] = "to", ["type"] = "string", ["x_kind"] = "column" },
                new JsonObject { ["name"] = "confidence", ["type"] = "number", ["x_kind"] = "column" },
                new JsonObject { ["name"] = "method", ["type"] = "string", ["x_kind"] = "column" }),
            ["primaryKey"] = new JsonArray("from"),
        },
        ["x_grain"] = new JsonArray("from"), ["x_row_count"] = rowCount, ["x_truncated"] = false,
        ["x_provenance"] = new JsonObject { ["spec"] = "mapping", ["spec_version"] = "0.3", ["source"] = tableRef },
    };

    // ---------- bridges ----------

    /// <summary>
    /// The relation as a bridge table: canonical key to group, built from the registered table through the value map.
    /// A key that maps to more than one group is rejected: rolling up through it would count the key twice.
    /// </summary>
    public (List<QisError> Errors, List<Dictionary<string, object?>> Rows, string OnUnmatched) BuildBridge(AdHocRelation rel)
    {
        var table = Find($"{rel.TableName}@v{rel.TableVersion}")!;
        var errors = new List<QisError>();
        Dictionary<string, string?> keyMap;
        var onUnmatched = "keep";
        if (rel.MapId is not null)
        {
            var map = _maps[rel.MapId];
            // A reviewed catalog map for the same column and target replaces a fuzzy or llm map.
            var catalogMap = _maps.Values.FirstOrDefault(m => m["mode"]!.GetValue<string>() == "catalog"
                && m["field"]!.GetValue<string>() == rel.KeyField && m["target"]!.GetValue<string>() == rel.KeyAttribute
                && Find(m["table"]!.GetValue<string>())?.Name == table.Name);
            if (catalogMap is not null) map = catalogMap;
            onUnmatched = map["on_unmatched"]?.GetValue<string>() ?? "keep";
            var mapped = RunMap(map, table, errors);
            if (errors.Count > 0) return (errors, [], onUnmatched);
            keyMap = mapped.ToDictionary(r => (string)r["from"]!, r => (string?)r["to"]);
        }
        else
        {
            var canonical = Members(rel.KeyAttribute).ToHashSet();
            keyMap = table.Rows.Select(r => r[rel.KeyField]?.ToString()).Where(v => v is not null).Distinct()
                .ToDictionary(v => v!, v => canonical.Contains(v!) ? v : null);
        }

        var pairs = new List<(string Key, string Group, decimal Weight)>();
        foreach (var row in table.Rows)
        {
            var from = row[rel.KeyField]?.ToString();
            var group = row[rel.GroupField]?.ToString();
            if (from is null || group is null || keyMap.GetValueOrDefault(from) is not { } key) continue;
            var weight = rel.WeightField is null ? 1m : Convert.ToDecimal(row[rel.WeightField] ?? 0m);
            var at = pairs.FindIndex(p => p.Key == key && p.Group == group);
            if (at < 0) pairs.Add((key, group, weight));
            else pairs[at] = (key, group, pairs[at].Weight + weight);
        }

        if (rel.WeightField is null)
        {
            var fanout = pairs.GroupBy(p => p.Key).Where(g => g.Count() > 1).ToList();
            if (fanout.Count > 0)
                errors.Add(Err("bridge_not_many_to_one", "$.attributes",
                    $"{string.Join(", ", fanout.Select(g => $"{g.Key} (in {string.Join(" and ", g.Select(p => p.Group))})"))} maps to more than one {rel.GroupField}; " +
                    "a rollup through it would count the value twice. Bind a weight column to split its value instead."));
        }
        else
        {
            // The weights split each key's value across its groups; they have to account for all of it.
            var negative = pairs.Where(p => p.Weight < 0).Select(p => $"{p.Key} to {p.Group}").ToList();
            var off = pairs.GroupBy(p => p.Key).Where(g => Math.Abs(g.Sum(p => p.Weight) - 1m) > 0.0001m)
                .Select(g => $"{g.Key} sums to {g.Sum(p => p.Weight)}").ToList();
            if (negative.Count > 0 || off.Count > 0)
                errors.Add(Err("allocation_weights_invalid", "$.attributes",
                    "Weights must be non-negative and sum to 1 for each key, so the split accounts for the whole value: " +
                    string.Join("; ", negative.Select(n => $"{n} is negative").Concat(off)) + "."));
        }
        var rows = pairs.Select(p =>
        {
            var row = new Dictionary<string, object?> { [Catalog.ColName(rel.KeyAttribute)] = p.Key, ["grp"] = p.Group };
            if (rel.WeightField is not null) row["weight"] = p.Weight;
            return row;
        }).ToList();
        return (errors, rows, onUnmatched);
    }
}
