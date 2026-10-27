using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Emit;

namespace DbDataKit.Semantic;

/// <summary>
/// Levels 0 to 5, plus agent completions and run records. The <c>level</c> setting gates what runs here:
/// level 0 wraps agent-written native queries, level 1 compiles single-source semantic requests, level 2 and up combine
/// sources, and transforms and overrides run from level 4 and 5. Whatever is above the level comes back as remaining steps.
/// </summary>
/// <summary>One agent completion compared with the engine's result for the same request.</summary>
public sealed record ShadowReport(string RequestHash, bool Match, string Detail);

public sealed partial class QisEngine(Catalog cat, ISourceExecutor executor, Validator validator, int level = 1, int rowCap = 1_000_000, string? runDir = null, bool shadow = false)
{
    public const string SpecVersion = "0.3";

    /// <summary>
    /// The default for where subtotals are computed: native asks the source (ROLLUP in SQL, ROLLUPADDISSUBTOTAL in DAX) for every
    /// measure whose rollup rules allow it, engine rolls detail rows up here and repeats the query for measures that can't be
    /// added, and auto asks the source only for the measures that would otherwise need a query of their own. A source's
    /// <c>totals</c> setting in the catalog overrides it, and a request's <c>totals.compute</c> overrides both.
    /// </summary>
    public string TotalsCompute { get; set; } = "native";

    /// <summary>The setting that applies to a result set: the request's hint, else its source's, else the engine's.</summary>
    private string ComputeModeFor(ResultSet ds, TotalsSpec? totals) =>
        totals?.Compute ?? (ds.Source is { } s && cat.Doc.Sources.TryGetValue(s, out var def) ? def.Totals : null) ?? TotalsCompute;
    private readonly Dictionary<string, (JsonObject Request, Result Partial, List<string> Grain)> _pending = new();
    private readonly Dictionary<string, Result> _shadowed = new();

    /// <summary>What shadow mode found: for each completion compared with the engine's own result, whether they agreed.</summary>
    public List<ShadowReport> ShadowReports { get; } = new();

    private QisError Err(string code, string path, string message, string spec = "native_query") => new(spec, code, path, message);

    /// <summary>Registrations (tables, bindings, value maps) in order, so a run record can register them again.</summary>
    private readonly List<JsonObject> _registrations = new();

    public Result Run(JsonObject doc) => doc["kind"]?.GetValue<string>() switch
    {
        "query" => RunInZone(doc, () => Recorded(doc, WithResolved(RunQuery(doc)))),
        "native_batch" => RunNativeBatch(doc),
        "table" or "bindings" or "value_map" => RunRegistration(doc),
        "clarify" => RunClarify(doc),
        "unsupported" => RunUnsupported(doc),
        var k => Result.Failed(level, null, [Err("schema_invalid", "$.kind", $"Unknown kind '{k}'.", "core")]),
    };

    /// <summary>Runs a query in the time zone it names, or the catalog's.</summary>
    private Result RunInZone(JsonObject doc, Func<Result> run)
    {
        using var zone = cat.WithZone(doc["query"]?["timezone"]?.GetValue<string>());
        return run();
    }

    /// <summary>
    /// A datetime dimension's raw values leave the engine as ISO 8601 UTC instants whatever the source returned: the wall-clock time of a
    /// source that stores local time is converted, and a bucket of a datetime (a label) is left alone.
    /// </summary>
    private void NormalizeDatetimes(string source, List<string> attrs, List<Dictionary<string, object?>> rows)
    {
        var stored = TimeZones.Find(cat.Doc.Sources[source].DatetimeZone) ?? TimeZoneInfo.Utc;
        foreach (var attr in attrs.Where(d => cat.TypeOf(d) == "datetime" && cat.Doc.Attributes[d].Bucket is null))
        {
            var column = Catalog.ColName(attr);
            foreach (var row in rows)
                row[column] = row[column] switch
                {
                    DateTime wall => TimeZones.Iso(TimeZones.FromWallClock(wall, stored)),
                    DateTimeOffset instant => TimeZones.Iso(instant.UtcDateTime),
                    var other => other,
                };
        }
    }

    /// <summary>
    /// A result the engine computed in full is recorded like an agent's completion is, with the request and the tables,
    /// bindings and maps it used, so it can be replayed. In shadow mode it is also kept, so an agent's completion for the
    /// same request can be compared with it.
    /// </summary>
    private Result Recorded(JsonObject request, Result result)
    {
        if (result.Status != "complete" || result.Combined is null) return result;
        SaveRun(request, null, result);
        if (shadow && result.RequestHash is { } hash) _shadowed[hash] = result;
        return result;
    }

    // ---------- clarify and unsupported ----------

    /// <summary>How many times each unsupported reason code has come back: the roadmap for the next version.</summary>
    public Dictionary<string, int> UnsupportedCounts { get; } = new();

    /// <summary>
    /// The agent asked a question instead of guessing. Valid options resolve to catalog members; the result is partial
    /// with reason clarification_needed, and the user's answer completes a new query.
    /// </summary>
    private Result RunClarify(JsonObject doc)
    {
        var h = QisJson.RequestHash(doc);
        var errors = validator.ValidateClarify(doc);
        return errors.Count > 0
            ? Result.Failed(level, h, errors)
            : new Result { Status = "partial", RequestHash = h, EngineLevel = level, ResultSets = [], Remaining = [], ReasonCode = "clarification_needed" };
    }

    /// <summary>The agent says the question can't be expressed. Counted by reason code, and reported as a failed result.</summary>
    private Result RunUnsupported(JsonObject doc)
    {
        var h = QisJson.RequestHash(doc);
        var errors = validator.SchemaErrors(doc, "unsupported", "native_query");
        if (errors.Count > 0) return Result.Failed(level, h, errors);
        var u = doc["unsupported"]!;
        var code = u["reason_code"]!.GetValue<string>();
        UnsupportedCounts[code] = UnsupportedCounts.GetValueOrDefault(code) + 1;
        return Result.Failed(level, h, [Err("unsupported", "$.unsupported.reason_code", $"{code}: {u["detail"]!.GetValue<string>()}")]);
    }

    // ---------- level 1 ----------

    private Result RunQuery(JsonObject doc)
    {
        var h = QisJson.RequestHash(doc);
        if (level < 1)
            return Result.Failed(level, h, [Err("level_not_implemented", "$", "This engine runs at level 0; send a native_batch.")]);
        var errors = validator.ValidateRequest(doc);
        if (errors.Count > 0) return Result.Failed(level, h, errors);

        var badValues = CheckFilterValues(doc);
        if (badValues.Count > 0) return Result.Failed(level, h, badValues);

        // Relative period operators are resolved to the calendar's members or dates first.
        _resolved = [];
        var (timed, timeFailure) = ExpandPeriods(doc, h);
        if (timeFailure is not null) return timeFailure;

        // A segment is a named set of filters from the catalog, added to the query's own.
        var req = new SemanticRequest(WithHaving(WithSegments(timed)));

        // What this engine can run: catalog measures, with an alias or spread override; filtered measures from level 2.
        // Everything else (rollup overrides, allocate, empty, ad hoc aggregates, transforms) comes back as remaining steps.
        // An allocation can take its weights from another period (a period offset, -1 for the previous one) if the calendar can move by that unit.
        bool PeriodOk(JsonNode? period) => period is null || (period["offset"]!.GetValue<int>() != 0 && cat.Doc.Shifts.ContainsKey(period["unit"]!.GetValue<string>()));
        // A rollup override averages (or sums, mins, maxes) the measure over one attribute that isn't in the query, from level 5.
        bool RollupOk(MeasureRef r) => level >= 5 && !r.HasAllocate && r.Rollup.Count >= 1
            && r.Rollup.All(x => x.Rule is "sum" or "avg" or "min" or "max" && !req.Attributes.Contains(x.Attribute));
        bool Runs(MeasureRef r) => (cat.Doc.Measures[r.Measure].Derived is null || level >= 4)
            && (!r.HasRollup || RollupOk(r)) && (!r.HasEmpty || r.HasRollup)
            && (!r.HasAllocate || (level >= 4 && PeriodOk(r.Allocate!["period"])))
            && (r.Filters.Count == 0 || level >= 2);
        // Transforms are computed here over the combined rows (window functions), from level 4; rank from level 5.
        // prior_period needs calendar relations and always comes back as a remaining step.
        bool TransformRuns(TransformRef t) => level >= (t.Type == "rank" ? 5 : 4)
            && (t.Type != "prior_period" || PeriodOk(t.Raw["period"]));
        var refs = req.Measures.Where(Runs).ToList();
        // A transform that reads another transform's output runs only if that one does (they come in request order, so it is already decided).
        var transforms = new List<TransformRef>();
        foreach (var t in req.Transforms)
            if (TransformRuns(t) && (transforms.Any(x => x.Alias == t.Measure) || t.Measure.Contains('.')))
                transforms.Add(t);
        var unsupported = req.MeasureEntries.Where(n => MeasureRef.IsRef(n) ? !Runs(MeasureRef.Parse(n))
            : !(TransformRef.IsRef(n) && transforms.Any(t => t.Alias == n["as"]!.GetValue<string>()))).ToList();

        // An alias or override is a request-scoped copy of the catalog measure, so every later step treats it like any other.
        var scoped = new Dictionary<string, MeasureDef>();
        foreach (var r in refs.Where(r => r.IsOverride))
        {
            var def = cat.Doc.Measures[r.Measure].Copy();
            def.Base = r.Measure;
            def.Filters = def.Filters.Concat(r.Filters).ToList();
            def.RequestFilters = r.Filters.Count;
            if (r.Spread is not null) def.Semantics["spread"] = r.Spread;
            if (r.Allocate is { } a)
                def.Allocate = new AllocateSpec(a["by"]!.GetValue<string>(), a["measure"]?.GetValue<string>(),
                    a["members"]?.GetValue<string>(), a["fallback"]?.GetValue<string>() ?? "fail", a["period"]?["unit"]?.GetValue<string>(), a["period"]?["offset"]?.GetValue<int>() ?? -1);
            if (r.HasRollup)
            {
                // Scan finer by the extra attributes, then combine across them by the rules, in the order given; the result has no rollup of its own.
                def.Via = new EntityRollup(r.Rollup.Select(x => new RollupStep(x.Attribute, x.Rule)).ToList(), r.Empty == "zero");
                def.Semantics["rollup"] = new Dictionary<object, object> { ["default"] = "none" };
            }
            scoped[r.Name] = def;
        }
        // per_entity is a measure aggregated per entity at the source, then rolled up here: a scoped measure with a via.
        foreach (var t in transforms.Where(t => t.Type == "per_entity"))
        {
            var def = cat.Doc.Measures[t.Measure].Copy();
            def.Base = t.Measure;
            def.Via = new EntityRollup(t.Str("entity")!, t.Str("agg")!);
            def.Semantics["rollup"] = new Dictionary<object, object> { ["default"] = "none" };   // an average of averages is wrong
            scoped[t.Alias] = def;
        }
        // prior_period compares with the same measure one unit earlier: a hidden copy that scans the shifted period.
        foreach (var t in transforms.Where(t => t.Type == "window"))
        {
            var def = cat.Doc.Measures[t.Measure].Copy();
            def.Base = t.Measure;
            int? offset = t.Raw["offset"] is JsonValue ov && ov.TryGetValue<int>(out var o) ? o : null;
            def.Window = new WindowSpec(t.Str("along")!, t.Raw["start"] is JsonObject ps ? ps["period_start"]!.GetValue<string>() : null,
                offset ?? (t.Raw["start"] is JsonValue sv && sv.TryGetValue<int>(out var so) ? so : 0),
                offset ?? (t.Raw["end"] is JsonValue ev && ev.TryGetValue<int>(out var eo) ? eo : 0));
            scoped[PriorName(t)] = def;
        }
        // An index divides by the measure at one base member: a hidden copy that scans that member for each combination of the other attributes.
        foreach (var t in transforms.Where(t => t.Type == "index"))
        {
            var def = cat.Doc.Measures[t.Measure].Copy();
            def.Base = t.Measure;
            var entry = req.MeasureEntries.Select((n, i) => (n, i)).First(x => x.n is JsonObject o && o["as"]?.GetValue<string>() == t.Alias).i;
            def.Index = new IndexSpec(t.Str("along")!, t.Str("base")!, $"$.query.measures[{entry}]");
            scoped[PriorName(t)] = def;
        }
        foreach (var t in transforms.Where(t => t.Type == "prior_period"))
        {
            var def = cat.Doc.Measures[t.Measure].Copy();
            def.Base = t.Measure;
            def.Shift = new PeriodShift(t.Raw["period"]!["unit"]!.GetValue<string>(), t.Raw["period"]!["offset"]!.GetValue<int>());
            scoped[PriorName(t)] = def;
        }
        using var scope = cat.ScopedMeasures(scoped);

        // The measures to compute: those asked for, then any a window transform needs that wasn't asked for.
        var window = transforms.Where(t => t.Type != "per_entity").ToList();
        var names = refs.Select(r => r.Name).Concat(transforms.Where(t => t.Type == "per_entity").Select(t => t.Alias)).ToList();
        names.AddRange(window.Select(t => t.Measure).Where(m => m.Contains('.') && !names.Contains(m)).Distinct());
        names.AddRange(window.Where(t => t.Type is "prior_period" or "window" or "index").Select(PriorName));
        var known = refs.Select(r => r.Name).Concat(transforms.Select(t => t.Alias)).ToHashSet();
        var outputs = req.OutputOrder.Where(known.Contains).ToList();
        // A derived measure is never scanned: the measures it reads are, and it is computed from them after rollup.
        names = Combine.ScanNames(cat, names);
        return PlanQuery(h, doc, req, names, unsupported, window, outputs);
    }

    /// <summary>The request with its segments' filters appended to its own. The original is kept for the hash and for replay.</summary>
    private Result WithResolved(Result result) => _resolved.Count == 0 ? result : result with { Resolved = _resolved };

    /// <summary>
    /// A filter on measures isn't a filter on rows: it applies to the answer, after aggregation (HAVING). The entries of
    /// <c>filters</c> whose members are all measure outputs move to <c>having</c>, where the sources and the combine find them.
    /// </summary>
    private static JsonObject WithHaving(JsonObject doc)
    {
        var query = doc["query"]!.AsObject();
        if (query["filters"] is not JsonArray filters || filters.Count == 0) return doc;
        var outputs = new SemanticRequest(doc).Measures.Select(r => r.Name).ToHashSet();
        outputs.UnionWith(doc["query"]!["measures"]!.AsArray().Where(n => n is JsonObject o && o["of"] is not null).Select(n => n!["as"]!.GetValue<string>()));
        bool OnMeasures(JsonNode f) => Filter.Parse(f).Names().All(outputs.Contains);
        if (!filters.Any(f => OnMeasures(f!))) return doc;
        var copy = doc.DeepClone().AsObject();
        var q = copy["query"]!.AsObject();
        var rows = new JsonArray();
        var having = new JsonArray();
        foreach (var f in q["filters"]!.AsArray())
            (OnMeasures(f!) ? having : rows).Add(f!.DeepClone());
        q["filters"] = rows;
        q["having"] = having;
        return copy;
    }

    private JsonObject WithSegments(JsonObject doc)
    {
        if (doc["query"]!["segments"] is not JsonArray names || names.Count == 0) return doc;
        var expanded = doc.DeepClone().AsObject();
        var query = expanded["query"]!.AsObject();
        var filters = query["filters"]?.AsArray() ?? [];
        query["filters"] = filters;
        foreach (var name in names)
            foreach (var f in cat.Doc.Segments[name!.GetValue<string>()])
                filters.Add(FilterJson(f));
        return expanded;
    }

    private static JsonNode FilterJson(Filter f) => f switch
    {
        AndFilter a => new JsonObject { ["and"] = new JsonArray(a.Items.Select(FilterJson).ToArray()) },
        OrFilter o => new JsonObject { ["or"] = new JsonArray(o.Items.Select(FilterJson).ToArray()) },
        LeafFilter l => LeafJson(l),
        _ => throw new NotSupportedException(),
    };

    private static JsonObject LeafJson(LeafFilter l)
    {
        var o = new JsonObject { [l.OnMeasure ? "measure" : "attribute"] = l.Name, ["operator"] = l.Operator };
        if (l.Count is { } count) o["count"] = count;
        if (l.Values.Count > 0 || l.Count is null) o["values"] = new JsonArray(l.Values.Select(v => v.DeepClone()).ToArray());
        return o;
    }

    /// <summary>The attributes a request reads (axes and filters), with the counterparts a period shift reads, which are read in the same role.</summary>
    private List<string> RequestMembers(SemanticRequest req)
    {
        var members = req.Attributes.Concat(req.Filters.SelectMany(f => f.Names())).ToList();
        members.AddRange(cat.Doc.Shifts.Values.SelectMany(o => o.Where(kv => members.Contains(kv.Key)).Select(kv => kv.Value)).ToList());
        return members;
    }

    private Result PlanQuery(string h, JsonObject doc, SemanticRequest req, List<string> measureNames, List<JsonNode> unsupported,
        List<TransformRef> windowTransforms, List<string> outputs)
    {
        // GroupBy keeps first-seen order. A per_entity measure is scanned at a finer grain and a prior-period one for another
        // period, so each forms its own group.
        // A measure that reads a role-playing dimension in another role than its default forms its own scan group too: its bare attribute is joined in that role.
        var requestMembers = RequestMembers(req);
        string RoleKey(string m) => string.Join("|", cat.RoleMap(m, requestMembers).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}>{kv.Value}"));
        var bySource = measureNames.GroupBy(m => (cat.Doc.Measures[m].Source, cat.Doc.Measures[m].Via, cat.Doc.Measures[m].Shift, cat.Doc.Measures[m].Window, cat.Doc.Measures[m].Index, Roles: RoleKey(m))).ToList();
        var derived0 = outputs.Where(o => cat.Doc.Measures.TryGetValue(o, out var d) && d.Derived is not null).ToList();
        var single = bySource.Count == 1 && unsupported.Count == 0 && windowTransforms.Count == 0 && derived0.Count == 0 && bySource[0].Key.Via is null;
        var vias = new Dictionary<string, EntityRollup>();
        var entityScans = new List<ResultSet>();

        var adhoc = req.Attributes.Where(d => cat.Doc.Attributes[d].Relation is not null)
            .Select(d => (Attr: d, Rel: cat.Doc.Attributes[d].Relation!)).ToList();
        var resultSets = new List<ResultSet>();
        var scans = new Dictionary<string, List<Filter>>();   // result set id -> filters applied, to repeat a scan at another grain
        var warnings = new List<Warning>();
        int i = 0;
        foreach (var group in bySource)
        {
            i++;
            var (source, via, shift, windowSpec, indexSpec, _) = group.Key;
            var measures = group.ToList();
            using var roles = cat.WithRoles(cat.RoleMap(measures[0], requestMembers));
            var attrs = req.Attributes.Where(d => cat.Binding(d, source) is not null).ToList();
            // An ad hoc attribute is reached through its key: the source is asked for the catalog attribute it maps to.
            attrs.AddRange(adhoc.Select(a => a.Rel.KeyAttribute).Distinct()
                .Where(k => cat.Binding(k, source) is not null && !attrs.Contains(k)));

            var applied = new List<Filter>();
            var notApplied = new List<FilterNotApplied>();
            foreach (var f in req.Filters)
            {
                if (f.Names().All(m => cat.Binding(m, source) is not null)) applied.Add(f);
                else
                {
                    notApplied.Add(new FilterNotApplied(f.Text(), "not_in_source"));
                    warnings.Add(new Warning("filter_not_applied", $"{f.Text()} couldn't be applied to {source}."));
                }
            }

            // The source applies order and limit only when its result set is the whole answer.
            var final = single && attrs.SequenceEqual(req.Attributes) && req.Totals is null && adhoc.Count == 0;
            var order = final
                ? req.Order.Where(o => attrs.Contains(o.Member) || measures.Contains(o.Member)).ToList()
                : new List<(string, string)>();
            int? limit = final ? req.Limit : null;

            if (shift is not null)
            {
                var (shifted, rawShift, shiftFailure) = ShiftedScan($"d{i}", source, attrs, measures, applied, shift.Unit, shift.Offset, h);
                if (shiftFailure is not null) return shiftFailure;
                entityScans.Add(rawShift!);
                resultSets.Add(shifted!);
                scans[shifted!.Id] = applied;
                continue;
            }

            if (windowSpec is not null)
            {
                var (accumulated, rawScans, windowFailure) = WindowScan($"d{i}", source, attrs, measures, applied, windowSpec, h);
                if (windowFailure is not null) return windowFailure;
                entityScans.AddRange(rawScans!);
                resultSets.Add(accumulated!);
                scans[accumulated!.Id] = applied;
                continue;
            }

            if (indexSpec is not null)
            {
                var (indexed, rawIndex, indexFailure) = IndexScan($"d{i}", source, attrs, measures, applied, indexSpec, h);
                if (indexFailure is not null) return indexFailure;
                entityScans.AddRange(rawIndex!);
                resultSets.Add(indexed!);
                scans[indexed!.Id] = applied;
                continue;
            }

            if (via is not null)
            {
                if (via.Entities.FirstOrDefault(e => cat.Binding(e, source) is null) is { } unavailable)
                    return Result.Failed(level, h, [Err("transform_entity_unavailable", "$.query.measures",
                        $"{unavailable} isn't available in {source}, so {string.Join(", ", measures)} can't be computed per {unavailable}.", "mashup")]);
                var (raw, rawFailure) = ScanSource($"d{i}e", source, attrs.Concat(via.Entities).ToList(), measures, applied, notApplied, [], null, h, $"$.result_sets[{i - 1}]");
                if (rawFailure is not null) return rawFailure;
                entityScans.Add(raw!);
                var rolledUp = RollEntity(raw!, attrs, measures, via, $"d{i}", h, applied);
                resultSets.Add(rolledUp);
                vias[rolledUp.Id] = via;
                scans[rolledUp.Id] = applied;
                continue;
            }

            var (ds, failure) = ScanSource($"d{i}", source, attrs, measures, applied, notApplied, order, limit, h, $"$.result_sets[{i - 1}]",
                having: final ? req.Having : null);
            if (failure is not null) return failure;
            resultSets.Add(ds!);
            scans[ds!.Id] = applied;
        }

        // One bare attribute read in different roles by different measures is allowed, but it is said, so a column isn't taken for one role when it is two.
        var readAs = new Dictionary<string, Dictionary<string, List<string>>>();
        foreach (var group in bySource)
            foreach (var m in group)
                foreach (var attr in requestMembers.Where(x => cat.Doc.Attributes.TryGetValue(x, out var d) && d.DefaultRole is not null)
                             .GroupBy(x => x.Split('.')[0]).Select(g => g.First()))
                {
                    var role = cat.RoleMap(m, requestMembers).TryGetValue(attr, out var instance) ? cat.Doc.Attributes[instance].Instance!.Value.Role : cat.Doc.Attributes[attr].DefaultRole!;
                    var byRole = readAs.TryGetValue(attr, out var r0) ? r0 : readAs[attr] = new();
                    (byRole.TryGetValue(role, out var list) ? list : byRole[role] = new()).Add(m);
                }
        foreach (var (attr, byRole) in readAs.Where(kv => kv.Value.Count > 1))
            warnings.Add(new Warning("role_differs_by_measure",
                $"{attr.Split('.')[0]} is read in different roles: " + string.Join("; ", byRole.Select(kv => $"{kv.Key} for {string.Join(", ", kv.Value)}")) + ". Name a role (for example ship_date." + attr[(attr.IndexOf('.') + 1)..] + ") to read one role for every measure."));

        var effective = resultSets;
        var derived = entityScans.ToList();
        if (adhoc.Count > 0 && level >= 3 && unsupported.Count == 0)
        {
            var (rolled, extra, failure) = RollThroughBridges(h, resultSets, adhoc, req);
            if (failure is not null) return failure;
            effective = rolled;
            derived = extra;
        }
        var remaining = Remaining(resultSets, req, unsupported, adhoc, windowTransforms, derived0);
        if ((remaining.Count > 0 || derived.Count > 0) && level >= (req.Totals is not null || adhoc.Count > 0 ? 3 : 2) && unsupported.Count == 0)
            return CombineResultSets(h, resultSets, effective, derived, scans, req, warnings, outputs, windowTransforms, vias);
        var result = new Result
        {
            Status = remaining.Count > 0 ? "partial" : "complete",
            RequestHash = h, EngineLevel = level, ResultSets = resultSets,
            Combined = remaining.Count > 0 ? null : resultSets[0],
            Remaining = remaining,
            ReasonCode = remaining.Count > 0 ? "level_not_implemented" : null,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
        if (remaining.Count > 0) _pending[h] = (doc, result, req.Attributes);
        return result;
    }

    /// <summary>Compiles one source query, runs it, and wraps the rows as a result set.</summary>
    private (ResultSet?, Result?) ScanSource(
        string id, string source, List<string> attrs, List<string> measures, List<Filter> applied,
        List<FilterNotApplied> notApplied, List<(string, string)> order, int? limit, string hash, string path,
        List<string>? rollup = null, JsonArray? levels = null, List<Filter>? having = null, bool cube = false)
    {
        var native = cat.Doc.Sources[source].Kind == "ssas"
            ? new NativeQuery("dax", DaxEmitter.BuildDax(cat, source, attrs, measures, applied, order, limit, rollup, having, cube))
            : new NativeQuery("tsql", SqlEmitter.BuildSelect(cat, source, attrs, measures, applied, order, limit, EmitDialects.TSql, rollup, having, cube).Text);

        var execLimit = limit is not null ? limit.Value + 1 : rowCap + 1;
        var (duckSql, duckParams) = SqlEmitter.BuildSelect(cat, source, attrs, measures, applied, order, execLimit, EmitDialects.DuckDb, rollup, having, cube);
        var execText = native.Dialect == "tsql"
            ? SqlEmitter.BuildSelect(cat, source, attrs, measures, applied, order, execLimit, EmitDialects.TSql, rollup, having, cube).Text : null;
        var compiled = new CompiledQuery(source, native, duckSql, duckParams, execText);
        // An executor that reads Arrow hands the rows over as columns; a column it cannot (a datetime among them) makes it answer in rows.
        var outcome = executor is IColumnarSourceExecutor columnarExecutor ? columnarExecutor.ExecuteColumnar(compiled, execLimit) : new ColumnarOutcome(null, executor.Execute(compiled, execLimit));
        var columnar = outcome.Columnar;
        var rows = outcome.Rows?.Rows;
        if (rows is not null) NormalizeDatetimes(source, attrs, rows);
        var count = columnar?.Count ?? rows!.Count;

        var truncated = false;
        if (limit is not null && count > limit)
        {
            if (columnar is not null) columnar = columnar with { Batch = columnar.Batch.Slice(0, limit.Value) };
            else rows = rows!.Take(limit.Value).ToList();
            count = limit.Value;
            truncated = true;
        }
        else if (limit is null && count > rowCap)
            return (null, Result.Failed(level, hash, [Err("row_cap_exceeded", path, $"{source} returned more than {rowCap} rows.")]));

        var descriptor = Descriptor(id, source, attrs, measures, count, truncated, hash);
        if (rollup is { Count: > 0 })
        {
            // A native subtotal result set: its rows include the subtotal rows, told apart by the level column.
            descriptor["schema"]!["fields"]!.AsArray().Add(new JsonObject
                { ["name"] = Combine.LevelColumn, ["type"] = "integer", ["x_kind"] = "column" });
            descriptor["x_totals"] = new JsonObject
            {
                ["level_field"] = Combine.LevelColumn, ["subtotal_by"] = new JsonArray(rollup.Select(r => (JsonNode)r).ToArray()),
                ["levels"] = levels?.DeepClone(),
            };
        }
        var covers = new Covers(measures, applied.Concat(having ?? []).Select(f => f.Text()).ToList(), notApplied);
        return (columnar is not null ? new ResultSet(id, source, attrs, covers, descriptor, native, columnar) : new ResultSet(id, source, attrs, covers, descriptor, native, rows!), null);
    }

    /// <summary>
    /// Every key of an allocated measure needs weights, or its value would vanish from the totals. With the default
    /// fallback (fail) a key with no weights stops the query; with blank it is left empty and the result says so.
    /// </summary>
    private Result? CheckAllocationWeights(string h, List<string> measures, List<ResultSet> resultSets,
        Dictionary<string, ResultSet> weightSets, List<ResultSet> all)
    {
        foreach (var (m, weights) in weightSets)
        {
            var spec = cat.Doc.Measures[m].Allocate!;
            if (spec.Fallback == "blank") continue;
            var ds = resultSets.First(d => d.Covers!.Measures.Contains(m));
            if (ds.Grain.Count == 0 || ds.Grain.Count == weights.Grain.Count && ds.Grain.All(weights.Grain.Contains) && weights.Grain.All(ds.Grain.Contains)) continue;
            var keys = string.Join(", ", ds.Grain.Select(d => $"x.\"{Catalog.ColName(d)}\""));
            var on = string.Join(" AND ", ds.Grain.Select(d => $"x.\"{Catalog.ColName(d)}\" = k.\"{Catalog.ColName(d)}\""));
            var sql = $"SELECT DISTINCT {keys} FROM \"{ds.Id}\" x LEFT JOIN (SELECT {string.Join(", ", ds.Grain.Select(d => $"\"{Catalog.ColName(d)}\""))}, " +
                      $"SUM(\"{Catalog.ColName(spec.WeightMeasure!)}\") AS s FROM \"{weights.Id}\" GROUP BY {string.Join(", ", ds.Grain.Select(d => $"\"{Catalog.ColName(d)}\""))}) k " +
                      $"ON {on} WHERE COALESCE(k.s, 0) = 0";
            var missing = Sandbox.Run(sql, all, rowCap + 1).Rows;
            if (missing.Count > 0)
                return Result.Failed(level, h, [Err("allocation_weights_invalid", "$.query.measures",
                    $"{m} has no weights from {spec.WeightMeasure} for {string.Join("; ", missing.Select(r => string.Join(", ", r.Values)))}, so its value would be lost. " +
                    "Set fallback to blank to leave those keys empty.", "mashup")]);
        }
        return null;
    }

    private Result CombineResultSets(string h, List<ResultSet> scanned, List<ResultSet> resultSets, List<ResultSet> derived,
        Dictionary<string, List<Filter>> scans, SemanticRequest req, List<Warning> warnings,
        List<string> outputs, List<TransformRef> windowTransforms, Dictionary<string, EntityRollup> vias)
    {
        var measures = resultSets.SelectMany(d => d.Covers!.Measures).Distinct().ToList();

        var rescans = new Dictionary<string, ResultSet>();
        var all = scanned.Concat(derived).ToList();

        // A source can compute its own subtotals: one query with ROLLUP over the result set's grain, in place of rolling the rows up here.
        if (req.Totals is not null)
            foreach (var ds in resultSets.Where(d => scans.ContainsKey(d.Id) && !vias.ContainsKey(d.Id)))
            {
                var native = Combine.NativeMeasures(cat, ds, req.Totals, ComputeModeFor(ds, req.Totals));
                if (native.Count == 0) continue;
                var (grouped, groupFailure) = ScanSource($"{ds.Id}_g", ds.Source!, ds.Grain, native, scans[ds.Id], [], [], null, h, "$.result_sets",
                    Combine.NativeChain(ds, req.Totals), Combine.NativeLevelMap(ds, req.Totals), cube: req.Totals.Cube);
                if (groupFailure is not null) return groupFailure;
                rescans[Combine.NativeKey(ds.Id)] = grouped!;
                all.Add(grouped!);
            }

        // Measures with rollup rule none can't be rolled up from detail rows: ask the source again at the coarser grain.
        int t = 0;
        // Only result sets that came straight from a source can be asked again; a result set rolled through a bridge never holds rule-none measures.
        foreach (var need in Combine.RescanNeeds(cat, resultSets.Where(d => scans.ContainsKey(d.Id)).ToList(), req.Attributes, req.Totals, d => ComputeModeFor(d, req.Totals)))
        {
            t++;
            ResultSet? ds;
            Result? failure;
            if (vias.TryGetValue(need.Parent.Id, out var via))
            {
                // Per-entity values can't be summed up: repeat the per-entity scan at the coarser grain and roll it up again.
                var id = $"{need.Parent.Id}_t{t}";
                (var raw, failure) = ScanSource($"{id}e", need.Parent.Source!, need.Grain.Concat(via.Entities).ToList(), need.Measures,
                    scans[need.Parent.Id], [], [], null, h, "$.result_sets");
                if (failure is not null) return failure;
                all.Add(raw!);
                ds = RollEntity(raw!, need.Grain, need.Measures, via, id, h, scans[need.Parent.Id]);
            }
            else
                (ds, failure) = ScanSource($"{need.Parent.Id}_t{t}", need.Parent.Source!, need.Grain, need.Measures,
                    scans[need.Parent.Id], [], [], null, h, "$.result_sets");
            if (failure is not null) return failure;
            rescans[Combine.RescanKey(need.Parent.Id, need.Grain)] = ds!;
            all.Add(ds!);
        }

        // A measure allocated by another measure needs that measure at the requested grain: use a result set that has it, or ask its source.
        var weightSets = new Dictionary<string, ResultSet>();
        int w = 0;
        foreach (var m in measures.Where(m => cat.Doc.Measures[m].Allocate is { By: "measure" }))
        {
            var weightMeasure = cat.Doc.Measures[m].Allocate!.WeightMeasure!;
            using var weightRoles = cat.WithRoles(cat.RoleMap(weightMeasure, RequestMembers(req)));
            var offsetUnit = cat.Doc.Measures[m].Allocate!.OffsetUnit;
            var existing = offsetUnit is not null ? null
                : scanned.FirstOrDefault(d => d.Covers!.Measures.Contains(weightMeasure) && req.Attributes.All(d.Grain.Contains));
            if (existing is null)
            {
                var source = cat.Doc.Measures[weightMeasure].Source;
                var weightAttrs = req.Attributes.Where(d => cat.Binding(d, source) is not null).ToList();
                if (weightAttrs.Count != req.Attributes.Count)
                    return Result.Failed(level, h, [Err("allocation_weights_unavailable", "$.query.measures",
                        $"{weightMeasure} can't be broken down by {string.Join(", ", req.Attributes.Except(weightAttrs))}, so it can't weight {m}.", "mashup")]);
                var filters = req.Filters.Where(f => f.Names().All(x => cat.Binding(x, source) is not null)).ToList();
                ResultSet? wds;
                if (offsetUnit is not null)
                {
                    // Weights from the previous period: the same measure scanned one unit earlier, relabeled to the current members.
                    var (shifted, rawWeights, shiftFailure) = ShiftedScan($"w{++w}", source, weightAttrs, [weightMeasure], filters, offsetUnit, cat.Doc.Measures[m].Allocate!.Offset, h);
                    if (shiftFailure is not null) return shiftFailure;
                    all.Add(rawWeights!);
                    wds = shifted;
                }
                else
                {
                    var (scannedWeights, wfail) = ScanSource($"w{++w}", source, weightAttrs, [weightMeasure], filters, [], [], null, h, "$.result_sets");
                    if (wfail is not null) return wfail;
                    wds = scannedWeights;
                }
                all.Add(wds!);
                existing = wds;
            }
            weightSets[m] = existing!;
        }
        var weightError = CheckAllocationWeights(h, measures, resultSets, weightSets, all);
        if (weightError is not null) return weightError;

        var (sql, notes) = Combine.BuildSql(cat, resultSets, rescans, req.Attributes, measures, req.Order, req.Limit, req.Totals, weightSets,
            AttributeMembers, windowTransforms, outputs, req.PostFilters.Concat(req.Having).ToList());
        warnings = warnings.Concat(notes.Select(n => new Warning("spread_blank", n))).ToList();
        var rows = Sandbox.Run(sql, all, req.Limit + 1);
        var truncated = rows.Rows.Count > req.Limit;
        var kept = rows.Rows.Take(req.Limit).ToList();

        var known = new Dictionary<string, JsonNode>();
        foreach (var ds in all)
            foreach (var f in ds.Descriptor["schema"]!["fields"]!.AsArray())
                known.TryAdd(f!["name"]!.GetValue<string>(), f);
        foreach (var d in req.Attributes)
            known.TryAdd(Catalog.ColName(d), new JsonObject
                { ["name"] = Catalog.ColName(d), ["type"] = cat.TypeOf(d), ["x_kind"] = "attribute", ["x_binding"] = d });
        known[Combine.LevelColumn] = new JsonObject { ["name"] = Combine.LevelColumn, ["type"] = "integer", ["x_kind"] = "column" };
        foreach (var n in outputs.Where(o => cat.Doc.Measures.TryGetValue(o, out var d) && d.Derived is not null))
            known[Catalog.ColName(n)] = new JsonObject
            {
                ["name"] = Catalog.ColName(n), ["type"] = cat.TypeOf(n), ["x_kind"] = "measure", ["x_binding"] = cat.Doc.Measures[n].Base ?? n,
                ["x_additivity"] = "non_additive",
                ["x_semantics"] = new JsonObject { ["rollup"] = new JsonObject { ["default"] = "none" }, ["spread"] = "blank", ["status"] = "governed" },
            };
        foreach (var tr in windowTransforms)
            known[tr.Alias] = new JsonObject
            {
                ["name"] = tr.Alias, ["type"] = tr.Type is "rank" ? "integer" : "number", ["x_kind"] = "measure", ["x_binding"] = tr.Measure,
                ["x_transform"] = tr.Type,
                // A derived value can't be summed up or repeated, so it carries no rollup of its own.
                ["x_additivity"] = "non_additive",
                ["x_semantics"] = new JsonObject { ["rollup"] = new JsonObject { ["default"] = "none" }, ["spread"] = "blank", ["status"] = "inferred" },
            };
        var fields = new JsonArray(rows.Columns.Select(c => known[c].DeepClone()).ToArray());
        var descriptor = new JsonObject
        {
            ["descriptor_version"] = "1.0", ["name"] = "combined",
            ["schema"] = new JsonObject { ["fields"] = fields, ["primaryKey"] = new JsonArray(req.Attributes.Select(d => (JsonNode)Catalog.ColName(d)).ToArray()) },
            ["x_grain"] = new JsonArray(req.Attributes.Select(d => (JsonNode)d).ToArray()),
            ["x_row_count"] = kept.Count, ["x_truncated"] = truncated,
            ["x_provenance"] = new JsonObject
            {
                ["spec"] = "mashup", ["spec_version"] = SpecVersion, ["mode"] = "engine", ["request_hash"] = h,
                ["inputs"] = new JsonArray(all.Select(d => (JsonNode)d.Id).ToArray()), ["engine_level"] = level,
            },
        };
        var provenance = descriptor["x_provenance"]!.AsObject();
        if (windowTransforms.Count > 0 || measures.Any(m => cat.Doc.Measures[m].Base is not null))
        {
            provenance["overrides"] = new JsonArray(measures.Where(m => cat.Doc.Measures[m].Base is not null).Select(m => (JsonNode)new JsonObject
            {
                ["as"] = m, ["measure"] = cat.Doc.Measures[m].Base!,
                ["spread"] = cat.SpreadRule(m),
                ["rollup"] = cat.Doc.Measures[m].Via is { } v && cat.Doc.Measures[m].Base is not null && !windowTransforms.Any(tr => tr.Alias == m)
                    ? new JsonObject(v.Steps.Select(s => KeyValuePair.Create(s.Entity, (JsonNode?)s.Agg))) : null,
                ["filters"] = new JsonArray(cat.Doc.Measures[m].Filters.TakeLast(cat.Doc.Measures[m].RequestFilters).Select(f => (JsonNode)f.Text()).ToArray()),
            }).ToArray());
            provenance["transforms"] = new JsonArray(windowTransforms.Select(tr => tr.Raw.DeepClone()).ToArray());
        }
        if (req.Document["assumptions"] is JsonArray assumptions) provenance["assumptions"] = assumptions.DeepClone();
        if (req.Totals is { } totals)
            descriptor["x_totals"] = new JsonObject
            {
                ["level_field"] = Combine.LevelColumn,
                ["subtotal_by"] = new JsonArray(totals.Rollup.Select(r => (JsonNode)r).ToArray()),
                ["levels"] = Combine.LevelMap(req.Attributes, totals),
            };
        return new Result
        {
            Status = "complete", RequestHash = h, EngineLevel = level, ResultSets = all, Remaining = [],
            Combined = new ResultSet("combined", null, req.Attributes, null, descriptor, null, kept),
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private List<JsonObject> Remaining(List<ResultSet> resultSets, SemanticRequest req, List<JsonNode> unsupported,
        List<(string Attr, AdHocRelation Rel)> adhoc, List<TransformRef> windowTransforms, List<string> derivedOutputs)
    {
        var steps = new List<JsonObject>();
        var inputs = new JsonArray();
        var to = new JsonArray(req.Attributes.Select(d => (JsonNode)d).ToArray());
        foreach (var raw in resultSets)
        {
            // A result set at a catalog key is first rolled up through the bridge to the ad hoc attribute.
            var through = adhoc.Where(a => raw.Grain.Contains(a.Rel.KeyAttribute)).ToList();
            var ds = raw;
            if (through.Count > 0)
            {
                var grain = req.Attributes.Where(d => raw.Grain.Contains(d) || through.Any(a => a.Attr == d)).ToList();
                steps.Add(new JsonObject
                {
                    ["step"] = steps.Count + 1, ["op"] = "rollup", ["input"] = raw.Id,
                    ["to"] = new JsonArray(grain.Select(g => (JsonNode)g).ToArray()),
                    ["through"] = new JsonArray(through.Select(a => (JsonNode)a.Attr).ToArray()),
                });
                ds = raw with { Grain = grain };
            }
            if (!ds.Grain.SequenceEqual(req.Attributes))
            {
                var spread = new JsonObject();
                foreach (var m in ds.Covers!.Measures)
                    spread[m] = cat.SpreadRule(m);
                steps.Add(new JsonObject
                {
                    ["step"] = steps.Count + 1, ["op"] = "spread", ["input"] = through.Count > 0 ? $"step:{steps.Count}" : ds.Id, ["to"] = to.DeepClone(),
                    ["spread"] = spread,
                    ["note"] = $"{ds.Source} has no {string.Join(", ", req.Attributes.Except(ds.Grain))}",
                });
                inputs.Add($"step:{steps.Count}");
            }
            else inputs.Add(through.Count > 0 ? $"step:{steps.Count}" : ds.Id);
        }
        if (resultSets.Count > 1)
            steps.Add(new JsonObject { ["step"] = steps.Count + 1, ["op"] = "union", ["inputs"] = inputs, ["grain"] = to.DeepClone() });
        if (windowTransforms.Count > 0 || derivedOutputs.Count > 0)
            steps.Add(new JsonObject
            {
                ["step"] = steps.Count + 1, ["op"] = "compute",
                ["transforms"] = new JsonArray(windowTransforms.Select(t => (JsonNode)t.Alias).Concat(derivedOutputs.Select(d => (JsonNode)d)).ToArray()),
            });
        if (req.Totals is { } totals)
            steps.Add(new JsonObject
            {
                ["step"] = steps.Count + 1, ["op"] = "totals",
                ["subtotal_by"] = new JsonArray(totals.Rollup.Select(r => (JsonNode)r).ToArray()), ["grand_total"] = totals.GrandTotal, ["cube"] = totals.Cube ? true : null,
            });
        foreach (var entry in unsupported)
            steps.Add(new JsonObject
            {
                ["step"] = steps.Count + 1, ["op"] = "measure_expression",
                ["entry"] = entry.DeepClone(), ["reason"] = "level_not_implemented",
            });
        if (steps.Count > 0)
            steps.Add(new JsonObject
            {
                ["step"] = steps.Count + 1, ["op"] = "finalize",
                ["order"] = new JsonArray(req.Order.Select(o => (JsonNode)new JsonArray(o.Member, o.Direction)).ToArray()),
                ["limit"] = req.Limit,
                ["having"] = req.Having.Count > 0 ? new JsonArray(req.Having.Select(FilterJson).ToArray()) : null,
            });
        return steps;
    }

    // ---------- level 0 ----------

    private Result RunNativeBatch(JsonObject doc)
    {
        var h = QisJson.RequestHash(doc);
        var errors = validator.SchemaErrors(doc, "native_batch", "core");
        if (errors.Count > 0) return Result.Failed(level, h, errors);
        if (!executor.RunsNativeText)
            return Result.Failed(level, h, [Err("native_execution_unavailable", "$", "This executor can't run agent-written native queries.", "core")]);

        var resultSets = new List<ResultSet>();
        foreach (var q in doc["queries"]!.AsArray())
        {
            var id = q!["id"]!.GetValue<string>();
            var source = q["source"]!.GetValue<string>();
            var native = new NativeQuery(q["dialect"]!.GetValue<string>(), q["text"]!.GetValue<string>());
            QueryRows rows;
            try { rows = executor.ExecuteNativeText(source, native, rowCap + 1); }
            catch (NotSupportedException e)
            { return Result.Failed(level, h, [Err(native.Dialect == "dax" ? "dax_execution_unavailable" : "native_execution_unavailable", $"$.queries[{id}]", e.Message, "core")]); }
            catch (Exception e)
            { return Result.Failed(level, h, [Err("native_query_failed", $"$.queries[{id}].text", e.Message.Split('\n')[0], "core")]); }
            if (rows.Rows.Count > rowCap)
                return Result.Failed(level, h, [Err("row_cap_exceeded", "$.queries", $"{id} returned more than {rowCap} rows.", "core")]);
            resultSets.Add(new ResultSet(id, source, [], new Covers([], [], []),
                InferredDescriptor(id, rows, h, source), native, rows.Rows));
        }

        List<JsonObject> remaining = resultSets.Count > 1
            ? [new JsonObject { ["step"] = 1, ["op"] = "combine", ["inputs"] = new JsonArray(resultSets.Select(d => (JsonNode)d.Id).ToArray()) }]
            : [];
        var result = new Result
        {
            Status = remaining.Count > 0 ? "partial" : "complete", RequestHash = h, EngineLevel = level,
            ResultSets = resultSets, Combined = remaining.Count > 0 ? null : resultSets[0], Remaining = remaining,
            ReasonCode = remaining.Count > 0 ? "agent_combines" : null,
        };
        if (remaining.Count > 0) _pending[h] = (doc, result, []);
        return result;
    }

    // ---------- completion ----------

    public Result Complete(JsonObject completionDoc)
    {
        var errors = validator.SchemaErrors(completionDoc, "completion", "mashup");
        var h = completionDoc["request_hash"]?.GetValue<string>();
        if (errors.Count > 0) return Result.Failed(level, h, errors);
        var completion = completionDoc.Deserialize<Completion>(QisJson.Options)!;

        if (!_pending.TryGetValue(completion.RequestHash, out var state))
        {
            // The engine now executes these steps itself; an agent's completion is compared with its result, not used.
            if (_shadowed.TryGetValue(completion.RequestHash, out var engineResult))
                return CompleteInShadow(completion, engineResult);
            return Result.Failed(level, h, [Err("unknown_request", "$.request_hash", "No partial result with this request hash.", "mashup")]);
        }
        var needed = state.Partial.Remaining!.Select(s => s["step"]!.GetValue<int>()).Order().ToList();
        if (!completion.Completes.Order().SequenceEqual(needed))
            return Result.Failed(level, h, [Err("incomplete_completion", "$.completes", $"Completion must cover steps [{string.Join(", ", needed)}].", "mashup")]);

        var resultSets = state.Partial.ResultSets!;
        errors = Sandbox.Validate(completion.Sql, resultSets.Select(d => d.Id).ToHashSet());
        if (errors.Count > 0) return Result.Failed(level, h, errors);

        QueryRows rows;
        try { rows = Sandbox.Run(completion.Sql, resultSets, rowCap + 1); }
        catch (Exception e) { return Result.Failed(level, h, [Err("completion_failed", "$.sql", e.Message.Split('\n')[0], "mashup")]); }

        var combined = new ResultSet("combined", null, state.Grain, null,
            CompletionDescriptor(rows, resultSets, state.Grain, completion), null, rows.Rows);
        _pending.Remove(completion.RequestHash);
        var complete = state.Partial with { Status = "complete", Combined = combined, Remaining = [], ReasonCode = null };
        SaveRun(state.Request, completionDoc, complete);
        return complete;
    }

    // ---------- run records ----------

    /// <summary>Saves {request, completion, native_queries} so the run can be replayed without the agent.</summary>
    private void SaveRun(JsonObject request, JsonObject? completionDoc, Result result)
    {
        if (runDir is null) return;
        Directory.CreateDirectory(runDir);
        JsonObject? completion = null;
        if (completionDoc is not null)
        {
            completion = completionDoc.DeepClone().AsObject();
            completion.Remove("request_hash");
        }
        var native = new JsonObject();
        foreach (var d in result.ResultSets!)
            native[d.Id] = d.NativeQuery is null ? null : JsonSerializer.SerializeToNode(d.NativeQuery, QisJson.Options);
        var record = new JsonObject
        {
            ["setup"] = new JsonArray(_registrations.Select(r => r.DeepClone()).ToArray()),
            ["request"] = request.DeepClone(), ["completion"] = completion, ["native_queries"] = native,
            // A relative range means something different tomorrow, so the record keeps the date it was resolved on.
            ["today"] = _resolved.Count > 0 ? _resolved[0].Today : null,
        };
        File.WriteAllText(Path.Combine(runDir, $"{result.RequestHash}.json"), record.ToJsonString(QisJson.Options));
    }

    /// <summary>Re-runs a saved request and its completion without the agent.</summary>
    public Result Replay(string runFile)
    {
        var saved = JsonNode.Parse(File.ReadAllText(runFile))!.AsObject();
        foreach (var setup in saved["setup"]?.AsArray() ?? [])
        {
            var registered = Run(setup!.DeepClone().AsObject());
            if (registered.Status != "complete") return registered;
        }
        var clock = Today;
        if (saved["today"] is { } today) Today = () => ParseDate(today.GetValue<string>());
        Result first;
        try { first = Run(saved["request"]!.AsObject()); }
        finally { Today = clock; }
        if (first.Status != "partial" || saved["completion"] is not JsonObject completion) return first;
        var doc = completion.DeepClone().AsObject();
        doc["request_hash"] = first.RequestHash;
        return Complete(doc);
    }

    // ---------- descriptors ----------

    private JsonObject Descriptor(string name, string source, List<string> attrs, List<string> measures, int rowCount, bool truncated, string h)
    {
        var fields = new JsonArray();
        foreach (var d in attrs)
            fields.Add(new JsonObject { ["name"] = Catalog.ColName(d), ["type"] = cat.TypeOf(d), ["x_kind"] = "attribute", ["x_binding"] = d });
        foreach (var m in measures)
        {
            var field = new JsonObject
            {
                ["name"] = Catalog.ColName(m), ["type"] = cat.TypeOf(m), ["x_kind"] = "measure", ["x_binding"] = cat.Doc.Measures[m].Base ?? m,
                ["x_additivity"] = cat.Additivity(m),
                ["x_semantics"] = Catalog.ToJson(cat.Doc.Measures[m].Semantics),
            };
            if (cat.Doc.Measures[m].Format is { } format) field["x_format"] = format.DeepClone();
            if (cat.Doc.Measures[m].Roles.Count > 0) field["x_kinds"] = Catalog.ToJson(cat.Doc.Measures[m].Roles.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
            fields.Add(field);
        }
        return new JsonObject
        {
            ["descriptor_version"] = "1.0", ["name"] = name,
            ["schema"] = new JsonObject { ["fields"] = fields, ["primaryKey"] = new JsonArray(attrs.Select(d => (JsonNode)Catalog.ColName(d)).ToArray()) },
            ["x_grain"] = new JsonArray(attrs.Select(d => (JsonNode)d).ToArray()),
            ["x_row_count"] = rowCount, ["x_truncated"] = truncated,
            ["x_provenance"] = new JsonObject
            {
                ["spec"] = "native_query", ["spec_version"] = SpecVersion, ["source"] = source,
                ["request_hash"] = h, ["engine_level"] = level,
            },
        };
    }

    private static string TypeOfValue(object? v) => v switch
    {
        decimal or double or float => "number",
        long or int or short or byte or System.Numerics.BigInteger => "integer",
        bool => "boolean",
        _ => "string",
    };

    private JsonObject InferredDescriptor(string name, QueryRows rows, string h, string source)
    {
        var fields = new JsonArray();
        foreach (var c in rows.Columns)
        {
            var type = TypeOfValue(rows.Rows.Select(r => r[c]).FirstOrDefault(v => v is not null));
            var f = new JsonObject { ["name"] = c, ["type"] = type, ["x_kind"] = type is "number" or "integer" ? "measure" : "column" };
            if (type is "number" or "integer")
                f["x_semantics"] = new JsonObject { ["rollup"] = new JsonObject { ["default"] = "none" }, ["spread"] = "blank", ["status"] = "inferred" };
            fields.Add(f);
        }
        return new JsonObject
        {
            ["descriptor_version"] = "1.0", ["name"] = name, ["schema"] = new JsonObject { ["fields"] = fields },
            ["x_grain"] = null, ["x_row_count"] = null, ["x_truncated"] = false,
            ["x_provenance"] = new JsonObject
            {
                ["spec"] = "native_query", ["spec_version"] = SpecVersion, ["source"] = source,
                ["request_hash"] = h, ["engine_level"] = level, ["mode"] = "agent_native",
            },
        };
    }

    private JsonObject CompletionDescriptor(QueryRows rows, List<ResultSet> resultSets, List<string> grain, Completion completion)
    {
        var known = new Dictionary<string, JsonNode>();
        foreach (var ds in resultSets)
            foreach (var f in ds.Descriptor["schema"]!["fields"]!.AsArray())
                known.TryAdd(f!["name"]!.GetValue<string>(), f);
        var fields = new JsonArray();
        foreach (var c in rows.Columns)
            fields.Add(known.TryGetValue(c, out var f)
                ? f.DeepClone()
                : new JsonObject
                {
                    ["name"] = c, ["type"] = TypeOfValue(rows.Rows.Select(r => r[c]).FirstOrDefault(v => v is not null)),
                    ["x_kind"] = "column", ["x_semantics"] = new JsonObject { ["status"] = "inferred" },
                });
        var sqlHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(completion.Sql)))[..16];
        return new JsonObject
        {
            ["descriptor_version"] = "1.0", ["name"] = "combined", ["schema"] = new JsonObject { ["fields"] = fields },
            ["x_grain"] = new JsonArray(grain.Select(g => (JsonNode)g).ToArray()), ["x_truncated"] = false,
            ["x_provenance"] = new JsonObject
            {
                ["spec"] = "mashup", ["spec_version"] = SpecVersion, ["mode"] = "agent_completion",
                ["request_hash"] = completion.RequestHash, ["completion_sql_hash"] = sqlHash,
                ["inputs"] = new JsonArray(resultSets.Select(d => (JsonNode)d.Id).ToArray()), ["engine_level"] = level,
            },
        };
    }
}
