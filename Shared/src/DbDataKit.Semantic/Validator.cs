using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>Validates documents against the JSON Schemas, then semantic requests against the catalog.</summary>
public sealed class Validator(Catalog cat)
{
    private static readonly HashSet<string> StringOps = ["equals", "not_equals", "contains", "not_contains", "starts_with", "set", "not_set"];
    /// <summary>A string attribute whose values are periods (2026-07, FY27-Q1) sorts in time order, so it can also be compared.</summary>
    private static readonly HashSet<string> TimeStringOps = [.. StringOps, "gt", "gte", "lt", "lte", "between"];
    private static readonly HashSet<string> DateOps = ["equals", "not_equals", "gt", "gte", "lt", "lte", "between", "set", "not_set"];
    private static readonly HashSet<string> NumberOps = ["equals", "not_equals", "gt", "gte", "lt", "lte", "between", "set", "not_set"];

    private static QisError Err(string code, string path, string message, List<string>? suggestions = null, string spec = "native_query") =>
        new(spec, code, path, message, suggestions is { Count: > 0 } ? suggestions : null);

    /// <summary>JSON Schema check of a document against the named schema in the schema folder.</summary>
    public List<QisError> SchemaErrors(JsonNode doc, string schemaName, string spec) =>
        SchemaStore.Errors(doc, SchemaStore.Get(schemaName), "schema_invalid", spec);

    /// <summary>The most rows a request can ask for.</summary>
    public const int MaxLimit = 10_000;

    /// <summary>Fields that were written with an x_ prefix and are spelled without one now, so the error can say what to write.</summary>
    private static readonly Dictionary<string, string> RenamedExtensions = new()
    {
        ["x_totals"] = "totals", ["x_post_filters"] = "post_filters", ["x_transform"] = "transform",
        ["x_assumptions"] = "assumptions", ["x_having"] = "having", ["x_resolved"] = "resolved",
    };

    /// <summary>
    /// What the schema would only call invalid, said specifically: a request states its limit and keeps it within bounds, and has no x_ fields (the prefix marks
    /// extensions of the descriptor and catalog; a request's own fields are plain, and a field that used to have the prefix names its new spelling).
    /// </summary>
    private static List<QisError> CheckRequestShape(JsonObject doc)
    {
        var errors = new List<QisError>();
        if (doc["query"] is not JsonObject query) return errors;
        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key.StartsWith("x_", StringComparison.Ordinal))
                            errors.Add(new QisError("native_query", "extension_misplaced", $"{path}.{key}",
                                RenamedExtensions.TryGetValue(key, out var plain)
                                    ? $"{key} is now written {plain}: a request's own fields have no x_ prefix."
                                    : $"{key} isn't a field of a request: the x_ prefix marks extensions of the descriptor and the catalog, and a request has none.",
                                RenamedExtensions.TryGetValue(key, out var suggestion) ? [suggestion] : null));
                        Walk(value, $"{path}.{key}");
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
                    break;
            }
        }
        Walk(query, "$.query");
        if (!query.ContainsKey("limit"))
            errors.Add(new QisError("native_query", "limit_missing", "$.query.limit", $"A query states its limit, from 1 to {MaxLimit:N0}; there is no default."));
        else if (query["limit"] is JsonValue limit && limit.TryGetValue<decimal>(out var n) && n > MaxLimit)
            errors.Add(new QisError("native_query", "limit_exceeded", "$.query.limit", $"A query returns at most {MaxLimit:N0} rows; {n:N0} is more. Ask for fewer, or narrow the query."));
        return errors;
    }

    public List<QisError> ValidateRequest(JsonObject doc)
    {
        var shape = CheckRequestShape(doc);
        if (shape.Count > 0) return shape;
        var errors = SchemaErrors(doc, "semantic_request", "native_query");
        if (errors.Count > 0) return errors;

        if (doc["query"]!["timezone"]?.GetValue<string>() is { } zone && !TimeZones.IsValid(zone))
            return [Err("unknown_timezone", "$.query.timezone", $"{zone} isn't a time zone; use an IANA name such as America/Chicago or UTC.")];

        var req = new SemanticRequest(doc);
        var namesInQuery = new HashSet<string>();
        var measureOutputs = new HashSet<string>();

        var segments = doc["query"]!["segments"]?.AsArray().Select(s => s!.GetValue<string>()).ToList() ?? [];
        for (int i = 0; i < segments.Count; i++)
            if (!cat.Doc.Segments.ContainsKey(segments[i]))
                errors.Add(Err("unknown_segment", $"$.query.segments[{i}]", $"{segments[i]} isn't a segment in the catalog.", [.. cat.Doc.Segments.Keys]));

        var earlierTransforms = new Dictionary<string, TransformRef>();
        for (int i = 0; i < req.MeasureEntries.Count; i++)
        {
            var m = req.MeasureEntries[i];
            var p = $"$.query.measures[{i}]";
            if (m.GetValueKind() == JsonValueKind.String)
            {
                CheckMember(m.GetValue<string>(), "measure", p, errors);
                namesInQuery.Add(m.GetValue<string>());
                measureOutputs.Add(m.GetValue<string>());
            }
            else if (TransformRef.IsRef(m))
            {
                var t = TransformRef.Parse(m);
                // A transform reads a catalog measure, or the output of a transform written before it.
                if (t.Measure.Contains('.')) CheckMember(t.Measure, "measure", p + ".measure", errors);
                else if (!earlierTransforms.ContainsKey(t.Measure))
                    errors.Add(Err("transform_base_unknown", p + ".measure",
                        $"{t.Measure} isn't a transform written before this one, so it can't be the measure of {t.Alias}. A transform reads a catalog measure or an earlier transform's `as`.",
                        [.. earlierTransforms.Keys]));
                CheckTransform(t, req, p, errors, earlierTransforms);
                earlierTransforms[t.Alias] = t;
                namesInQuery.Add(t.Alias);
            }
            else if (m["measure"] is JsonNode measure)
            {
                CheckMember(measure.GetValue<string>(), "measure", p + ".measure", errors);
                var r = MeasureRef.Parse(m);
                CheckOverride(r, req, p, errors);
                namesInQuery.Add(r.Name);
                measureOutputs.Add(r.Name);
            }
            else if (m["of"] is JsonNode of)
            {
                if (!cat.Exists(of.GetValue<string>()))
                    errors.Add(Err("unknown_member", p + ".of", $"{of} is not a catalog member.", cat.Suggestions(of.GetValue<string>())));
                namesInQuery.Add(m["as"]!.GetValue<string>());
                measureOutputs.Add(m["as"]!.GetValue<string>());
            }
        }

        for (int i = 0; i < req.Attributes.Count; i++)
        {
            CheckMember(req.Attributes[i], "attribute", $"$.query.attributes[{i}]", errors);
            namesInQuery.Add(req.Attributes[i]);
        }

        for (int i = 0; i < req.Attributes.Count; i++)
            if (cat.Doc.Attributes.TryGetValue(req.Attributes[i], out var attr) && attr.Relation is { Confirmed: false })
                errors.Add(Err("unconfirmed_binding", $"$.query.attributes[{i}]",
                    $"{req.Attributes[i]} comes from a binding nobody has confirmed. An unconfirmed binding can be shown but can't relate measures to it.", spec: "mapping"));

        // A filter on a measure is a condition on the answer. It names a measure the query returns, and can't be mixed with a
        // filter on rows inside one and/or, because the two apply at different points.
        for (int i = 0; i < req.Filters.Count; i++)
        {
            var leaves = Leaves([req.Filters[i]], "").Select(l => l.Item1).ToList();
            var onMeasures = leaves.Count(l => l.OnMeasure);
            if (onMeasures > 0 && onMeasures < leaves.Count)
                errors.Add(Err("filter_mixes_kinds", $"$.query.filters[{i}]",
                    "One filter entry can't test both measures and attributes: a measure's value exists only after the rows are grouped. Put them in separate entries of 'filters' (which are combined with AND)."));
        }
        foreach (var (leaf, path) in Leaves(req.Filters, "$.query.filters"))
        {
            if (leaf.OnMeasure)
            {
                if (measureOutputs.Contains(leaf.Name))
                {
                    if (!NumberOps.Contains(leaf.Operator))
                        errors.Add(Err("invalid_operator", path + ".operator", $"{leaf.Operator} can't be used on measure {leaf.Name}."));
                    else if (leaf.Operator is not ("set" or "not_set") && (leaf.Values.Count == 0 || leaf.Values.Any(v => v.GetValueKind() != JsonValueKind.Number)))
                        errors.Add(Err("having_value_not_number", path + ".values", $"A filter on measure {leaf.Name} compares it with numbers."));
                    else if (leaf.Operator == "between") CheckBetween(leaf, path, errors);
                }
                else if (cat.IsMeasure(leaf.Name))
                    errors.Add(Err("having_measure_missing", path + ".measure",
                        $"{leaf.Name} is filtered on but isn't one of the measures the query returns. Add it to 'measures' (an 'as' alias is the name to filter on)."));
                else
                    errors.Add(Err("unknown_member", path + ".measure", $"{leaf.Name} is not a measure the query returns.", [.. measureOutputs]));
                continue;
            }
            if (cat.IsMeasure(leaf.Name))
            {
                errors.Add(Err("wrong_member_kind", path + ".attribute", $"{leaf.Name} is a measure, not an attribute: a condition on its value is a filter with a 'measure' key."));
                continue;
            }
            if (!cat.Exists(leaf.Name))
            {
                errors.Add(Err("unknown_member", path + ".attribute", $"{leaf.Name} is not a catalog member.", cat.Suggestions(leaf.Name)));
                continue;
            }
            if (cat.Doc.Attributes.TryGetValue(leaf.Name, out var filtered) && filtered.Relation is not null)
            {
                errors.Add(Err("adhoc_filter_unsupported", path + ".attribute",
                    $"{leaf.Name} is an ad hoc attribute; filter on {filtered.Relation.KeyAttribute} instead.", spec: "mapping"));
                continue;
            }
            CheckLeaf(leaf, path, errors);
        }

        for (int i = 0; i < req.Order.Count; i++)
            if (!namesInQuery.Contains(req.Order[i].Member))
                errors.Add(Err("order_member_missing", $"$.query.order[{i}]",
                    $"{req.Order[i].Member} is ordered on but isn't a measure or attribute in the query."));

        if (req.Totals is { } totals)
        {
            for (int i = 0; i < totals.Rollup.Count; i++)
                if (!req.Attributes.Contains(totals.Rollup[i]))
                    errors.Add(Err("totals_member_missing", $"$.query.totals.subtotal_by[{i}]",
                        $"{totals.Rollup[i]} is rolled up in totals but isn't a attribute in the query."));
            if (totals.Cube && totals.Rollup.Count > RollupSets.MaxCubeWidth)
                errors.Add(Err("cube_too_wide", "$.query.totals.subtotal_by", $"A cube has 2 to the power n sets; at most {RollupSets.MaxCubeWidth} attributes can be listed."));
            if (totals.Rollup.Count != totals.Rollup.Distinct().Count())
                errors.Add(Err("totals_member_duplicate", "$.query.totals.subtotal_by", "A attribute is listed more than once."));
            // Total rows sort after their detail rows, so the order is the attributes' own.
            for (int i = 0; i < req.Order.Count; i++)
                if (!req.Attributes.Contains(req.Order[i].Member))
                    errors.Add(Err("totals_order_not_attribute", $"$.query.order[{i}]",
                        "With totals, rows are ordered by attribute; order can't name a measure."));
        }

        var aliases = req.Transforms.Select(t => t.Alias).ToHashSet();
        for (int i = 0; i < req.PostFilters.Count; i++)
            foreach (var (leaf, lp) in Leaves([req.PostFilters[i]], $"$.query.post_filters[{i}]"))
            {
                if (!aliases.Contains(leaf.Name))
                    errors.Add(Err("alias_invalid", lp + ".measure", $"{leaf.Name} isn't a transform alias. A post-filter filters a transform's output; filter other members in 'filters'."));
                else if (!NumberOps.Contains(leaf.Operator))
                    errors.Add(Err("invalid_operator", lp + ".operator", $"{leaf.Operator} can't be used on a transform output."));
                else if (leaf.Operator is not ("set" or "not_set") && leaf.Values.Count == 0)
                    errors.Add(Err("missing_values", lp + ".values", $"{leaf.Operator} needs at least one value."));
            }

        var plain = req.OutputOrder.ToList();
        if (plain.Count != plain.Distinct().Count())
            errors.Add(Err("duplicate_measure", "$.query.measures", "A measure is listed more than once."));
        return errors;
    }

    /// <summary>
    /// Whether a transform's output adds up across an attribute, for a transform that reads it. A share adds up (its parts are
    /// fractions of one whole); a prior period's value or delta adds up when the measure it shifts does; a window, a running
    /// total, a rank and a percentage change are computed over rows and are never summed again.
    /// </summary>
    private bool ChainAdds(TransformRef t, string attr, Dictionary<string, TransformRef> earlier) => t.Type switch
    {
        "pct_of_total" or "share_of_parent" => true,
        "prior_period" => (t.Str("output") ?? "value") != "pct_change"
            && (earlier.TryGetValue(t.Measure, out var inner) ? ChainAdds(inner, attr, earlier) : cat.RollupRule(t.Measure, attr) == "sum"),
        _ => false,
    };

    /// <summary>
    /// A transform needs its dimensions in the query, and a measure that adds up across the attribute it totals or
    /// accumulates over (rule sum), or the result would be wrong.
    /// </summary>
    private void CheckTransform(TransformRef t, SemanticRequest req, string path, List<QisError> errors, Dictionary<string, TransformRef> earlier)
    {
        void InQuery(string attr, string key)
        {
            if (!req.Attributes.Contains(attr))
                errors.Add(Err("transform_member_missing", $"{path}.{key}", $"{attr} is named by the {t.Type} transform but isn't a attribute in the query."));
        }
        // The output of an earlier transform adds up across an attribute only for some transforms (see ChainAdds).
        var chained = earlier.TryGetValue(t.Measure, out var baseTransform);
        void Adds(string attr, string key)
        {
            if (chained)
            {
                if (cat.IsAttribute(attr) && !ChainAdds(baseTransform!, attr, earlier))
                    errors.Add(Err("transform_not_allowed", $"{path}.{key}",
                        $"{t.Type} needs {t.Measure} to add up across {attr}, but {t.Measure} is the output of a {baseTransform!.Type} transform, which doesn't add up."));
                return;
            }
            if (cat.IsMeasure(t.Measure) && cat.IsAttribute(attr) && cat.RollupRule(t.Measure, attr) != "sum")
                errors.Add(Err("transform_not_allowed", $"{path}.{key}",
                    $"{t.Type} needs {t.Measure} to add up across {attr}, but its rollup rule there is {cat.RollupRule(t.Measure, attr)}."));
        }

        if (cat.IsMeasure(t.Measure) && cat.Doc.Measures[t.Measure].Derived is not null && t.Type is "prior_period" or "per_entity" or "window" or "index")
            errors.Add(Err("transform_not_allowed", path, $"{t.Type} can't be applied to {t.Measure}, which is computed from other measures."));

        // These scan the source again, so what they read must be a catalog measure, not the output of another transform.
        if (chained && t.Type is "window" or "prior_period" or "per_entity" or "index")
            errors.Add(Err("transform_base_not_scannable", path + ".measure",
                $"{t.Type} scans the source again, so it needs a catalog measure; {t.Measure} is the output of a {baseTransform!.Type} transform."));

        switch (t.Type)
        {
            case "pct_of_total":
                foreach (var d in t.List("over")) { InQuery(d, "over"); Adds(d, "over"); }
                break;
            case "share_of_parent":
                var (attribute, parent) = (t.Str("attribute")!, t.Str("parent")!);
                InQuery(attribute, "attribute"); InQuery(parent, "parent");
                if (!cat.IsParentLevel(attribute, parent))
                    errors.Add(Err("hierarchy_not_declared", path, $"{parent} isn't declared in the catalog as the level directly above {attribute}."));
                Adds(attribute, "attribute");
                break;
            case "per_entity":
                var entity = t.Str("entity")!;
                CheckMember(entity, "attribute", path + ".entity", errors);
                if (req.Attributes.Contains(entity))
                    errors.Add(Err("transform_entity_in_query", path + ".entity", $"{entity} is already an attribute of the query, so there is nothing to aggregate over."));
                break;
            case "running_total":
                InQuery(t.Str("along")!, "along"); Adds(t.Str("along")!, "along");
                if (t.Str("reset_by") is { } reset) InQuery(reset, "reset_by");
                break;
            case "rank":
                foreach (var d in t.List("within")) InQuery(d, "within");
                break;
            case "window":
                var along = t.Str("along")!;
                InQuery(along, "along");
                var alongPeriod = cat.PeriodOf(along);
                if (alongPeriod is null)
                    errors.Add(Err("transform_needs_time", path + ".along", $"window needs a period attribute (a level of a calendar, or a day) in along; {along} isn't one."));
                var hasOffset = t.Raw["offset"] is not null;
                if (hasOffset && (t.Raw["start"] is not null || t.Raw["end"] is not null))
                    errors.Add(Err("window_frame_invalid", path + ".offset", "offset is a window of one member, so it can't be combined with start or end."));
                else if (!hasOffset && (t.Raw["start"] is null || t.Raw["end"] is null))
                    errors.Add(Err("window_frame_invalid", path, "A window needs either an offset, or both a start and an end."));
                else if (t.Raw["start"] is JsonObject startObj)
                {
                    var startPeriod = startObj["period_start"]!.GetValue<string>();
                    if (alongPeriod is { } ap && !(startPeriod != along && ap.Calendar.Levels.ContainsValue(startPeriod)))
                        errors.Add(Err("window_period_not_coarser", path + ".start.period_start", $"{startPeriod} isn't another level of the calendar that {along} is in, so it can't be the period the window starts in."));
                    if (t.Raw["end"] is JsonValue endV && endV.TryGetValue<int>(out var endBefore) && endBefore < 0)
                        errors.Add(Err("window_frame_invalid", path + ".end", "A window that starts at the beginning of a period can't end before the current member."));
                }
                else if (t.Raw["start"] is JsonValue startV && startV.TryGetValue<int>(out var startOffset))
                {
                    if (startOffset > 0)
                        errors.Add(Err("window_frame_invalid", path + ".start", "A window starts at or before the current member: start is 0 or a negative number of members."));
                    else if (t.Raw["end"] is JsonValue endV2 && endV2.TryGetValue<int>(out var endOffset) && endOffset < startOffset)
                        errors.Add(Err("window_frame_invalid", path + ".end", $"The window ends ({endOffset}) before it starts ({startOffset})."));
                }
                if (req.Totals is not null)
                    errors.Add(Err("window_totals_unsupported", path, "A window is computed for each member shown, so it has no total row; request it without totals."));
                break;
            case "index":
                var indexAlong = t.Str("along")!;
                InQuery(indexAlong, "along");
                if (cat.PeriodOf(indexAlong) is null)
                    errors.Add(Err("transform_needs_time", path + ".along", $"index needs a period attribute (a level of a calendar, or a day) in along; {indexAlong} isn't one."));
                break;
            case "prior_period":
                var unit = t.Raw["period"]!["unit"]!.GetValue<string>();
                if (!cat.Doc.Shifts.TryGetValue(unit, out var offsets))
                    errors.Add(Err("unknown_period_unit", path + ".period.unit", $"The catalog's calendars can't move back by {unit}."));
                else if (!req.Attributes.Concat(req.Filters.SelectMany(f => f.Names())).Any(offsets.ContainsKey))
                    errors.Add(Err("transform_needs_time", path, $"prior_period needs a time attribute in the query, or a filter on one, that the calendar can move back by {unit}."));
                if (cat.IsMeasure(t.Measure) && req.Totals is not null && req.Attributes.Any(d => cat.RollupRule(t.Measure, d) == "none"))
                    errors.Add(Err("prior_period_totals_unsupported", path, $"{t.Measure} can't be added up, so its earlier value can't be totalled; request prior_period without totals."));
                break;
        }
    }

    /// <summary>A clarify option or partial query: members must exist and be the right kind, filters well formed.</summary>
    public List<QisError> ValidateClarify(JsonObject doc)
    {
        var errors = SchemaErrors(doc, "clarify", "native_query");
        if (errors.Count > 0) return errors;
        var clarify = doc["clarify"]!.AsObject();
        var options = clarify["options"]!.AsArray();
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i]!["measure"] is JsonNode m) CheckMember(m.GetValue<string>(), "measure", $"$.clarify.options[{i}].measure", errors);
            if (options[i]!["attribute"] is JsonNode d) CheckMember(d.GetValue<string>(), "attribute", $"$.clarify.options[{i}].attribute", errors);
        }
        if (clarify["partial"] is JsonObject partial)
        {
            foreach (var (key, kind) in new[] { ("measures", "measure"), ("attributes", "attribute") })
                if (partial[key] is JsonArray names)
                    for (int i = 0; i < names.Count; i++)
                        CheckMember(names[i]!.GetValue<string>(), kind, $"$.clarify.partial.{key}[{i}]", errors);
            var filters = partial["filters"]?.AsArray().Select(n => Filter.Parse(n!)).ToList() ?? [];
            foreach (var (leaf, path) in Leaves(filters, "$.clarify.partial.filters")) CheckLeaf(leaf, path, errors);
        }
        return errors;
    }

    /// <summary>An alias, filters and overrides on a catalog measure. The measure itself is checked by the caller.</summary>
    private void CheckOverride(MeasureRef r, SemanticRequest req, string path, List<QisError> errors)
    {
        if (!cat.IsMeasure(r.Measure)) return;
        var source = cat.Doc.Measures[r.Measure].Source;
        if (cat.Doc.Measures[r.Measure].Derived is not null && (r.Filters.Count > 0 || r.Spread is not null || r.HasRollup || r.HasAllocate || r.HasEmpty))
        {
            errors.Add(Err("derived_measure_override", path, $"{r.Measure} is computed from other measures, so it takes only an alias. Filter, spread or roll up the measures it reads instead."));
            return;
        }

        if (r.Filters.Count > 0 && r.Name == r.Measure)
            errors.Add(Err("alias_required", path + ".as", "A measure with its own filters needs an 'as' alias, so it can't be mistaken for the unfiltered measure."));
        if (r.Name != r.Measure && (cat.Exists(r.Name) || r.Name.Contains('.')))
            errors.Add(Err("alias_invalid", path + ".as", $"{r.Name} collides with a catalog member name; an alias is a new snake_case name."));

        foreach (var (leaf, lp) in Leaves(r.Filters, path + ".filters"))
        {
            CheckLeaf(leaf, lp, errors);
            if (cat.IsAttribute(leaf.Name) && cat.Binding(leaf.Name, source) is null)
                errors.Add(Err("filter_member_not_in_source", lp + ".attribute",
                    $"{leaf.Name} isn't available in {source}, where {r.Measure} comes from, so it can't filter that measure."));
        }
        if (r.Allocate is { } alloc && alloc["period"] is JsonNode period)
        {
            var unit = period["unit"]!.GetValue<string>();
            if (!cat.Doc.Shifts.ContainsKey(unit))
                errors.Add(Err("unknown_period_unit", path + ".spread.allocate.period.unit", $"The catalog's calendars can't move back by {unit}."));
        }
        if (r.Allocate is { } allocate && allocate["measure"] is JsonNode weight)
        {
            CheckMember(weight.GetValue<string>(), "measure", path + ".spread.allocate.measure", errors);
            if (weight.GetValue<string>() == r.Measure)
                errors.Add(Err("allocate_weight_is_target", path + ".spread.allocate.measure", "A measure can't be allocated by itself."));
        }
        if (r.Empty == "zero" && r.Rollup.Count > 1)
            errors.Add(Err("empty_needs_one_attribute", path + ".empty", "Counting empty members as zero works over one attribute; this rollup names several."));
        if (r.Empty is not null && !r.HasRollup)
            errors.Add(Err("empty_needs_rollup", path + ".empty", "'empty' says how a rollup override treats members with no value; there is no rollup override here."));
        if (r.Raw["rollup"] is JsonObject rollup)
            foreach (var (attr, rule) in rollup)
            {
                CheckMember(attr, "attribute", path + ".rollup." + attr, errors);
                if (!cat.IsAttribute(attr)) continue;
                if (req.Attributes.Contains(attr))
                    errors.Add(Err("rollup_attribute_in_query", path + ".rollup." + attr,
                        $"{attr} is already an attribute of the query, so there is nothing to roll the measure up over."));
                // Summing a value the catalog says can't be added (a distinct count, a ratio) is a common mistake: ask first.
                if (cat.IsMeasure(r.Measure) && rule!.GetValue<string>() == "sum" && cat.RollupRule(r.Measure, attr) == "none" && !r.Acknowledge)
                    errors.Add(Err("acknowledge_required", path + ".rollup." + attr,
                        $"The catalog says {r.Measure} can't be added across {attr}. Summing it anyway needs \"acknowledge\": true on this entry."));
            }
    }

    private void CheckLeaf(LeafFilter leaf, string path, List<QisError> errors)
    {
        if (leaf.OnMeasure)
        {
            errors.Add(Err("measure_filter_misplaced", path + ".measure", "A condition on a measure's value belongs in the query's 'filters', not here."));
            return;
        }
        if (!cat.Exists(leaf.Name))
        {
            errors.Add(Err("unknown_member", path + ".attribute", $"{leaf.Name} is not a catalog member.", cat.Suggestions(leaf.Name)));
            return;
        }
        if (leaf.Count is not null && leaf.Operator != "last_complete")
            errors.Add(Err("period_count_invalid", path + ".count", $"count belongs to last_complete (how many whole periods); {leaf.Operator} takes none."));
        if (RelativeOps.Contains(leaf.Operator))
        {
            CheckRelative(leaf, path, errors);
            return;
        }
        var type = cat.TypeOf(leaf.Name);
        var ops = type == "string" ? (cat.Doc.Attributes.TryGetValue(leaf.Name, out var attribute) && attribute.IsTime ? TimeStringOps : StringOps)
            : type is "date" or "datetime" ? DateOps : NumberOps;
        if (!ops.Contains(leaf.Operator))
            errors.Add(Err("invalid_operator", path + ".operator", $"{leaf.Operator} can't be used on {type} member {leaf.Name}."));
        if (leaf.Operator is not ("set" or "not_set") && leaf.Values.Count == 0)
            errors.Add(Err("missing_values", path + ".values", $"{leaf.Operator} needs at least one value."));
        else if (leaf.Operator == "between" && type is not ("date" or "datetime"))
            CheckBetween(leaf, path, errors);
        else if (type == "date" && leaf.Operator is not ("set" or "not_set"))
            CheckDates(leaf, path, errors);
        else if (type == "datetime" && leaf.Operator is not ("set" or "not_set"))
            CheckDatetimes(leaf, path, errors);
    }

    /// <summary>The operators that are relative to today: they take a period attribute, and no values.</summary>
    internal static readonly HashSet<string> RelativeOps = ["last_complete", "current", "period_to_date"];

    /// <summary>
    /// A relative operator is used on a period: a level of a calendar, a day, or a datetime's day to year (not hour). last_complete says how many periods; the others
    /// say none, and none of them take values.
    /// </summary>
    private void CheckRelative(LeafFilter leaf, string path, List<QisError> errors)
    {
        if (cat.PeriodOf(leaf.Name) is not { } period || period.Calendar.Implicit && period.Unit == "hour")
            errors.Add(Err("period_operator_not_period_attribute", path + ".attribute",
                $"{leaf.Operator} counts periods, and {leaf.Name} isn't one. Use it on a level of a calendar (a fiscal month), a calendar's date, or a datetime's day, week, month, quarter or year.",
                cat.PeriodAttributes()));
        if (leaf.Operator == "last_complete" && (leaf.Count is null || leaf.Count < 1))
            errors.Add(Err("period_count_invalid", path + ".count", "last_complete needs a count of at least 1: how many whole periods before the current one."));
        if (leaf.Values.Count > 0)
            errors.Add(Err("period_operator_takes_values", path + ".values", $"{leaf.Operator} is relative to today and takes no values."));
    }

    /// <summary>between takes the low end and then the high end, both included, of something that sorts: two numbers, or two period labels in order.</summary>
    private static void CheckBetween(LeafFilter leaf, string path, List<QisError> errors)
    {
        var v = leaf.Values;
        if (v.Count != 2) { errors.Add(Err("between_invalid", path + ".values", "between takes two values, the low end and the high end, both included.")); return; }
        var numbers = v.All(x => x.GetValueKind() == JsonValueKind.Number);
        var text = v.All(x => x.GetValueKind() == JsonValueKind.String);
        if (!numbers && !text) errors.Add(Err("between_invalid", path + ".values", "between compares two numbers or two period labels, not a mix."));
        else if (numbers ? v[0].GetValue<decimal>() > v[1].GetValue<decimal>() : string.CompareOrdinal(v[0].GetValue<string>(), v[1].GetValue<string>()) > 0)
            errors.Add(Err("between_invalid", path + ".values", "between takes the low end first."));
    }

    /// <summary>
    /// A datetime filter's values are instants (ISO 8601, with an offset or Z; without one UTC is meant) or dates, which mean that whole day in the
    /// request's zone: lt a date is before the day starts, lte a date is through its end, and between two dates is both days and everything between.
    /// </summary>
    private static void CheckDatetimes(LeafFilter leaf, string path, List<QisError> errors)
    {
        var instants = new List<DateTime>();
        for (int i = 0; i < leaf.Values.Count; i++)
        {
            var text = leaf.Values[i].GetValueKind() == System.Text.Json.JsonValueKind.String ? leaf.Values[i].GetValue<string>() : null;
            if (text is null || !TimeZones.TryInstant(text, TimeZoneInfo.Utc, out var instant))
                errors.Add(Err("invalid_datetime", $"{path}.values[{i}]", $"{leaf.Values[i].ToJsonString()} isn't a date or an instant; write a date as 2026-07-01 or an instant as 2026-07-01T04:30:00Z."));
            else instants.Add(instant);
        }
        if (instants.Count != leaf.Values.Count) return;
        if (leaf.Operator == "between" && (instants.Count != 2 || instants[0] > instants[1]))
            errors.Add(Err("between_invalid", path + ".values", "between takes two dates or instants, the low end first."));
        else if (leaf.Operator is "gt" or "gte" or "lt" or "lte" && instants.Count != 1)
            errors.Add(Err("invalid_datetime", path + ".values", $"{leaf.Operator} takes one date or instant."));
    }

    /// <summary>A date filter's values are ISO 8601 dates; a range has a start and an end in order, before and after take one.</summary>
    private static void CheckDates(LeafFilter leaf, string path, List<QisError> errors)
    {
        var dates = new List<DateOnly>();
        for (int i = 0; i < leaf.Values.Count; i++)
        {
            var text = leaf.Values[i].GetValueKind() == System.Text.Json.JsonValueKind.String ? leaf.Values[i].GetValue<string>() : null;
            if (text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                dates.Add(d);
            else
                errors.Add(Err("invalid_date", $"{path}.values[{i}]", $"{leaf.Values[i].ToJsonString()} isn't a date; write it as YYYY-MM-DD."));
        }
        if (dates.Count != leaf.Values.Count) return;
        if (leaf.Operator == "between" && (dates.Count != 2 || dates[0] > dates[1]))
            errors.Add(Err("between_invalid", path + ".values", "between takes a start date and an end date, in that order."));
        else if (leaf.Operator is "gt" or "gte" or "lt" or "lte" && dates.Count != 1)
            errors.Add(Err("between_invalid", path + ".values", $"{leaf.Operator} takes one date."));
    }

    private void CheckMember(string name, string kind, string path, List<QisError> errors)
    {
        if (!cat.Exists(name))
            errors.Add(Err("unknown_member", path, $"{name} is not a catalog member.", cat.Suggestions(name, kind)));
        else if ((kind == "measure") != cat.IsMeasure(name))
            errors.Add(Err("wrong_member_kind", path, $"{name} is a {(cat.IsMeasure(name) ? "measure" : "attribute")}, not a {kind}."));
    }

    internal static IEnumerable<(LeafFilter, string)> Leaves(List<Filter> filters, string path)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            var p = $"{path}[{i}]";
            switch (filters[i])
            {
                case AndFilter a: foreach (var x in Leaves(a.Items, p + ".and")) yield return x; break;
                case OrFilter o: foreach (var x in Leaves(o.Items, p + ".or")) yield return x; break;
                case LeafFilter l: yield return (l, p); break;
            }
        }
    }
}
