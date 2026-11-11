using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Emit;

namespace DbDataKit.Semantic;

/// <summary>Ad hoc tables, bindings and value maps, and rolling result sets up through a bridge to an ad hoc attribute.</summary>
public sealed partial class QisEngine
{
    private TableRegistry? _registry;
    private TableRegistry Registry => _registry ??= new TableRegistry(cat, AttributeMembers);

    /// <summary>
    /// The canonical members of a catalog attribute: the catalog's list, or the values in the dimension table of the source
    /// the catalog names, or (when it says nothing) the distinct values in the first source that binds it, which are the
    /// ones that appear in its fact rows.
    /// </summary>
    private List<string> AttributeMembers(string attribute)
    {
        var def = cat.Doc.Attributes[attribute];
        if (def.MemberList is { } listed) return listed.Order().ToList();
        var source = def.MembersFrom ?? cat.Doc.Sources.OrderBy(s => s.Value.Kind == "ssas" ? 1 : 0)
            .First(s => cat.Binding(attribute, s.Key) is not null).Key;
        var table = def.MembersFrom is not null;
        var kind = cat.Doc.Sources[source].Kind;
        var native = kind switch
        {
            "ssas" => new NativeQuery("dax", DaxEmitter.BuildDax(cat, source, [attribute], [], [], [], null)),
            "postgres" => new NativeQuery("postgres", table ? SqlEmitter.BuildMembers(cat, source, attribute) : SqlEmitter.BuildSelect(cat, source, [attribute], [], [], [], null, EmitDialects.Postgres).Text),
            _ => new NativeQuery("tsql", table ? SqlEmitter.BuildMembers(cat, source, attribute) : SqlEmitter.BuildSelect(cat, source, [attribute], [], [], [], null, EmitDialects.TSql).Text),
        };
        var (duckSql, duckParams) = table ? (SqlEmitter.BuildMembers(cat, source, attribute), new List<object?>())
            : SqlEmitter.BuildSelect(cat, source, [attribute], [], [], [], null, EmitDialects.DuckDb);
        ParameterizedSql? postgres = null;
        if (kind != "ssas")
        {
            var (pgText, pgParams) = table ? (SqlEmitter.BuildMembers(cat, source, attribute), new List<object?>())
                : SqlEmitter.BuildSelect(cat, source, [attribute], [], [], [], null, EmitDialects.Postgres);
            postgres = new ParameterizedSql(pgText, pgParams);
        }
        var rows = executor.Execute(new CompiledQuery(source, native, duckSql, duckParams, null, postgres), rowCap + 1).Rows;
        return rows.Select(r => r[Catalog.ColName(attribute)]?.ToString()).Where(v => v is not null).Select(v => v!).Distinct().Order().ToList();
    }

    public const int MembersDefaultLimit = 100;
    public const int MembersMaxLimit = 10_000;

    /// <summary>
    /// The distinct values of an attribute, to choose a parameter's value from (plan 19, J25): the catalog's list, or the dimension table the catalog names, or the values in the first source that binds it, read through the executor
    /// (so through the caller's own access when the host runs the scans as the caller). <paramref name="search"/> keeps the members that contain it, in any case. Fails with <c>unknown_attribute</c> when the catalog has no such attribute.
    /// </summary>
    public (List<string> Members, bool Truncated, List<QisError>? Errors) Members(string attribute, string? search = null, int? limit = null)
    {
        if (!cat.Doc.Attributes.ContainsKey(attribute))
            return ([], false, [Err("unknown_attribute", "$.attribute", $"The catalog has no attribute '{attribute}'.", "core")]);
        var cap = Math.Clamp(limit ?? MembersDefaultLimit, 1, MembersMaxLimit);
        var all = AttributeMembers(attribute);
        var matching = string.IsNullOrWhiteSpace(search) ? all : all.Where(m => m.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        return (matching.Take(cap).ToList(), matching.Count > cap, null);
    }

    // ---------- registration (table, bindings, value_map) ----------

    private Result RunRegistration(JsonObject doc)
    {
        var h = QisJson.RequestHash(doc);
        var kind = doc["kind"]!.GetValue<string>();
        var errors = validator.SchemaErrors(doc, kind, "mapping");
        if (errors.Count > 0) return Result.Failed(level, h, errors);

        string id;
        JsonObject descriptor;
        List<Dictionary<string, object?>> rows = [];
        switch (kind)
        {
            case "table":
            {
                errors = Registry.RegisterTable(doc, out var table);
                if (errors.Count > 0) return Result.Failed(level, h, errors);
                (id, descriptor) = (table!.Ref, table.Descriptor);
                break;
            }
            case "bindings":
            {
                errors = Registry.ApplyBindings(doc, out var table);
                if (errors.Count > 0) return Result.Failed(level, h, errors);
                (id, descriptor) = (table!.Ref, table.Descriptor);
                break;
            }
            default:
            {
                errors = Registry.DefineMap(doc, level, out rows);
                if (errors.Count > 0) return Result.Failed(level, h, errors);
                var mapId = doc["id"]!.GetValue<string>();
                id = "map_" + mapId;
                descriptor = TableRegistry.MappingDescriptor(mapId, rows.Count, doc["table"]!.GetValue<string>());
                break;
            }
        }
        _registrations.Add(doc.DeepClone().AsObject());
        var resultSet = new ResultSet(id, null, [], null, descriptor, null, rows);
        return new Result { Status = "complete", RequestHash = h, EngineLevel = level, ResultSets = [resultSet], Remaining = [] };
    }

    // ---------- rollup through a bridge ----------

    /// <summary>
    /// Rolls each result set that holds a relation's catalog key up to the ad hoc attribute: join to the bridge, then
    /// aggregate by the measure's rollup rule for the key. A rule of none can't be rolled up this way, because the
    /// source can't be asked for a distinct count by a grouping it doesn't have.
    /// </summary>
    private (List<ResultSet> Effective, List<ResultSet> Derived, Result? Failure) RollThroughBridges(
        string hash, List<ResultSet> resultSets, List<(string Attr, AdHocRelation Rel)> adhoc, SemanticRequest req)
    {
        var derived = new List<ResultSet>();
        var bridges = new Dictionary<string, (ResultSet Table, string OnUnmatched)>();
        foreach (var (attr, rel) in adhoc)
        {
            var (errors, rows, onUnmatched) = Registry.BuildBridge(rel);
            if (errors.Count > 0) return ([], [], Result.Failed(level, hash, errors));
            var keyCol = Catalog.ColName(rel.KeyAttribute);
            var table = new ResultSet($"bridge_{Catalog.ColName(attr)}", null, [rel.KeyAttribute], null,
                new JsonObject
                {
                    ["descriptor_version"] = "1.0", ["name"] = $"bridge_{Catalog.ColName(attr)}",
                    ["schema"] = new JsonObject
                    {
                        ["fields"] = new JsonArray(
                            new JsonObject { ["name"] = keyCol, ["type"] = "string", ["x_kind"] = "attribute", ["x_binding"] = rel.KeyAttribute },
                            new JsonObject { ["name"] = "grp", ["type"] = "string", ["x_kind"] = "column" })
                            .Also(f => { if (rel.WeightField is not null) f.Add(new JsonObject { ["name"] = "weight", ["type"] = "number", ["x_kind"] = "column" }); }),
                        ["primaryKey"] = new JsonArray(keyCol),
                    },
                    ["x_grain"] = new JsonArray(rel.KeyAttribute), ["x_row_count"] = rows.Count, ["x_truncated"] = false,
                    ["x_provenance"] = new JsonObject
                    {
                        ["spec"] = "mapping", ["spec_version"] = SpecVersion, ["source"] = $"{rel.TableName}@v{rel.TableVersion}",
                    },
                }, null, rows);
            bridges[attr] = (table, onUnmatched);
            derived.Add(table);
        }

        var effective = new List<ResultSet>();
        foreach (var ds in resultSets)
        {
            var through = adhoc.Where(a => ds.Grain.Contains(a.Rel.KeyAttribute)).ToList();
            if (through.Count == 0) { effective.Add(ds); continue; }

            var outAttrs = req.Attributes.Where(d => ds.Grain.Contains(d) || through.Any(a => a.Attr == d)).ToList();
            var droppedKeys = through.Select(a => a.Rel.KeyAttribute).Distinct().Where(k => !req.Attributes.Contains(k)).ToList();
            var measures = ds.Covers!.Measures;

            // Rolling up adds values across the key, so the rule must allow it. A weighted relation also multiplies
            // each value by a share, which only makes sense for a value that adds up, even if the key stays in the result.
            var weighted = through.Any(a => a.Rel.WeightField is not null);
            var ruleKeys = weighted ? through.Select(a => a.Rel.KeyAttribute).Distinct().ToList() : droppedKeys;
            var aggs = new Dictionary<string, string>();
            foreach (var m in measures.Where(_ => ruleKeys.Count > 0))
            {
                var rules = ruleKeys.Select(k => cat.RollupRule(m, k)).Distinct().ToList();
                if (rules.Count != 1 || rules[0] is not ("sum" or "min" or "max" or "avg") || (weighted && rules[0] != "sum"))
                    return ([], [], Result.Failed(level, hash, [Err("non_additive_rollup", "$.query.measures",
                        $"{m} can't be rolled up through {string.Join(", ", through.Select(a => a.Attr))}: its rollup rule for " +
                        $"{string.Join(", ", ruleKeys)} is {string.Join(", ", rules)}. " + (weighted
                            ? "Only a value that adds up (rule sum) can be split across groups by weights."
                            : "A value that can't be added across the relation has to be computed at the source at the ad hoc grain, which the source doesn't have."), "mashup")]));
                if (droppedKeys.Count > 0) aggs[m] = rules[0].ToUpperInvariant();
            }

            var joins = new List<string>();
            var cols = new List<string>();
            for (int b = 0; b < through.Count; b++)
            {
                var (attr, rel) = through[b];
                var (table, onUnmatched) = bridges[attr];
                var key = $"\"{Catalog.ColName(rel.KeyAttribute)}\"";
                if (onUnmatched == "fail")
                {
                    var known = table.Rows.Select(r => r[Catalog.ColName(rel.KeyAttribute)]?.ToString()).ToHashSet();
                    var missing = ds.Rows.Select(r => r[Catalog.ColName(rel.KeyAttribute)]?.ToString())
                        .Where(v => v is not null && !known.Contains(v)).Distinct().Order().ToList();
                    if (missing.Count > 0)
                        return ([], [], Result.Failed(level, hash, [Err("unmapped_values", "$.attributes",
                            $"{string.Join(", ", missing)} have no {attr} and the map says unmatched values fail.", "mashup")]));
                }
                joins.Add($"{(onUnmatched == "drop" ? "JOIN" : "LEFT JOIN")} \"{table.Id}\" b{b} ON x.{key} = b{b}.{key}");
            }
            // A weighted relation splits each value across its groups: the measure is multiplied by the row's weight.
            var weight = string.Join(" * ", through.Select((a, b) => a.Rel.WeightField is null ? null : $"COALESCE(b{b}.\"weight\", 1)")
                .Where(w => w is not null));
            foreach (var d in outAttrs)
            {
                var b = through.FindIndex(a => a.Attr == d);
                cols.Add(b >= 0
                    ? $"{(bridges[d].OnUnmatched == "keep" ? $"COALESCE(b{b}.\"grp\", '{TableRegistry.Unmapped}')" : $"b{b}.\"grp\"")} AS \"{Catalog.ColName(d)}\""
                    : $"x.\"{Catalog.ColName(d)}\"");
            }
            cols.AddRange(measures.Select(m =>
            {
                var value = weight.Length > 0 ? $"x.\"{Catalog.ColName(m)}\" * {weight}" : $"x.\"{Catalog.ColName(m)}\"";
                return aggs.TryGetValue(m, out var agg) ? $"{agg}({value}) AS \"{Catalog.ColName(m)}\"" : $"{value} AS \"{Catalog.ColName(m)}\"";
            }));
            var sql = $"SELECT {string.Join(", ", cols)} FROM \"{ds.Id}\" x {string.Join(" ", joins)}";
            if (droppedKeys.Count > 0 && outAttrs.Count > 0)
                sql += $" GROUP BY {string.Join(", ", Enumerable.Range(1, outAttrs.Count))}";

            QueryRows result;
            try { result = Sandbox.Run(sql, resultSets.Concat(derived).ToList(), rowCap + 1); }
            catch (Exception e)
            { return ([], [], Result.Failed(level, hash, [Err("native_query_failed", "$.attributes", e.Message.Split('\n')[0], "mashup")])); }

            var descriptor = Descriptor($"{ds.Id}r", ds.Source ?? "", outAttrs, measures, result.Rows.Count, false, hash);
            descriptor["x_provenance"] = new JsonObject
            {
                ["spec"] = "mashup", ["spec_version"] = SpecVersion, ["mode"] = "engine", ["request_hash"] = hash,
                ["inputs"] = new JsonArray(ds.Id, string.Join(",", through.Select(a => bridges[a.Attr].Table.Id))), ["engine_level"] = level,
            };
            var rolled = new ResultSet($"{ds.Id}r", ds.Source, outAttrs, ds.Covers, descriptor, null, result.Rows);
            derived.Add(rolled);
            effective.Add(rolled);
        }
        return (effective, derived, null);
    }
}

internal static class JsonArrayExtensions
{
    /// <summary>Runs an action on the array and returns it, so a field can be added while the array is being built inline.</summary>
    public static JsonArray Also(this JsonArray array, Action<JsonArray> action) { action(array); return array; }
}
