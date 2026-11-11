using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>One grouping set of a totals request: the attributes kept, and its rollup level, the number of listed attributes rolled up (0 is detail).</summary>
public sealed record GroupingSet(List<string> Keys, int Level);

/// <summary>A source query the engine must repeat at a coarser grain because a measure's rollup rule is <c>none</c>.</summary>
public sealed record RescanNeed(ResultSet Parent, List<string> Grain, List<string> Measures);

/// <summary>
/// Spread, rollup, union, totals and finalize, compiled to DuckDB SQL over the result result sets.
///
/// Every grouping set (the detail grain, then each subtotal and the grand total) is built the same way:
/// - A measure whose result set has every attribute of the set is read at that grain: directly, rolled up from the
///   result set by its per-attribute rollup rules, or, for rule <c>none</c>, from a repeat of the source query at that grain.
/// - A measure whose result set lacks some attribute of the set follows its spread rule. <c>repeat</c> copies the value
///   onto every row of the set (the spine, from result sets that do have the attributes). <c>blank</c> shows nothing
///   on that set, so a blank measure appears only on total rows at or above its native grain.
/// Rows from different result sets with the same key merge. Every row of a totals request carries one column, the rollup level, and the descriptor maps each level to the attributes it rolls up.
/// </summary>
public static class Combine
{
    /// <summary>The one column that says which grouping set a row belongs to. 0 is detail; level k has rolled up the k innermost listed attributes.</summary>
    public const string LevelColumn = "_rollup_level";

    private static readonly Dictionary<string, string> TypeSql = new()
    {
        ["string"] = "VARCHAR", ["number"] = "DOUBLE", ["integer"] = "BIGINT", ["boolean"] = "BOOLEAN",
    };

    private static string Col(string member) => Catalog.ColName(member);

    /// <summary>The detail set first, then subtotals from the innermost rolled-up attribute outward.</summary>
    public static List<GroupingSet> GroupingSets(List<string> attrs, TotalsSpec? totals)
    {
        if (totals is null) return [new GroupingSet(attrs, 0)];
        var rolled = totals.Rollup;
        var sets = new List<GroupingSet>();
        var subsets = RollupSets.Subsets(rolled.Count, totals.Cube);
        for (int level = 0; level < subsets.Count; level++)
        {
            var away = subsets[level].Select(p => rolled[p]).ToHashSet();
            var keys = attrs.Where(d => !away.Contains(d)).ToList();
            if (keys.Count == 0 && !totals.GrandTotal) continue;   // the set with nothing left is the grand total
            sets.Add(new GroupingSet(keys, level));
        }
        return sets;
    }

    /// <summary>Which result sets must be queried again at a coarser grain: measures with rollup rule none on a dropped attribute.</summary>
    public static List<RescanNeed> RescanNeeds(Catalog cat, List<ResultSet> resultSets, List<string> attrs, TotalsSpec? totals, Func<ResultSet, string> modeOf)
    {
        var needs = new Dictionary<string, RescanNeed>();
        foreach (var set in GroupingSets(attrs, totals))
            foreach (var ds in resultSets)
            {
                var grain = ds.Grain.Where(set.Keys.Contains).ToList();
                if (grain.Count == ds.Grain.Count) continue;
                var dropped = ds.Grain.Except(grain).ToList();
                // A measure the source subtotals natively isn't repeated.
                var native = NativeMeasures(cat, ds, totals, modeOf(ds));
                var measures = ds.Covers!.Measures.Where(m => !native.Contains(m) && dropped.Any(d => cat.RollupRule(m, d) == "none")).ToList();
                if (measures.Count == 0) continue;
                var key = ds.Id + "|" + string.Join(",", grain);
                if (needs.TryGetValue(key, out var existing))
                    existing.Measures.AddRange(measures.Except(existing.Measures));
                else needs[key] = new RescanNeed(ds, grain, measures);
            }
        return needs.Values.ToList();
    }

    public static string RescanKey(string parentId, List<string> grain) => parentId + "|" + string.Join(",", grain);

    /// <summary>Where a result set's native subtotal query is kept, beside its repeats at a coarser grain.</summary>
    public static string NativeKey(string parentId) => parentId + "|native";

    /// <summary>The attributes of a result set that a totals request rolls up, outermost first.</summary>
    public static List<string> NativeChain(ResultSet ds, TotalsSpec? totals) =>
        totals is null ? [] : totals.Rollup.Where(ds.Grain.Contains).ToList();

    /// <summary>
    /// The measures of a result set whose subtotals the source computes itself, with ROLLUP over the result set's grain.
    /// Mode engine takes none; native takes every measure the source can subtotal correctly; auto takes only those that
    /// would otherwise need a query of their own (a rule of none on a attribute that is rolled up).
    /// A source aggregates the base rows, which is what rule <c>none</c> asks for, and it is the same as rolling the
    /// detail up when the rule is the measure's own aggregate (sum of a sum or a count, min of a min, max of a max).
    /// A rule that does something else on the way up (avg, first, last) stays with the engine.
    /// </summary>
    public static List<string> NativeMeasures(Catalog cat, ResultSet ds, TotalsSpec? totals, string mode)
    {
        var chain = NativeChain(ds, totals);
        if (mode == "engine" || chain.Count == 0) return [];
        return ds.Covers!.Measures.Where(m =>
        {
            var def = cat.Doc.Measures[m];
            if (def.Shift is not null || def.Window is not null || def.Index is not null || def.Via is not null) return false;
            if (mode == "auto" && !chain.Any(d => cat.RollupRule(m, d) == "none")) return false;
            return chain.All(d => cat.RollupRule(m, d) switch
            {
                "none" => true,
                "sum" => def.Agg is "sum" or "count",
                "min" => def.Agg == "min",
                "max" => def.Agg == "max",
                _ => false,
            });
        }).ToList();
    }

    /// <summary>The descriptor's level map for a native result set: levels 0 to the number of attributes it can roll up.</summary>
    public static JsonArray NativeLevelMap(ResultSet ds, TotalsSpec totals) =>
        LevelMap(ds.Grain, new TotalsSpec(NativeChain(ds, totals), true, null, totals.Cube));

    /// <summary>The SQL asks for limit + 1 rows so the caller can detect truncation.</summary>
    public static (string Sql, List<string> Notes) BuildSql(
        Catalog cat, List<ResultSet> resultSets, Dictionary<string, ResultSet> rescans,
        List<string> attrs, List<string> measures, List<(string Member, string Direction)> order,
        int limit, TotalsSpec? totals, Dictionary<string, ResultSet> weightSets, Func<string, List<string>> members,
        List<TransformRef> transforms, List<string> outputOrder, List<Filter> postFilters)
    {
        var notes = new List<string>();
        var sets = GroupingSets(attrs, totals);
        var ctes = new List<string>();
        var selects = new List<string>();
        string AttrType(string d) => TypeSql.GetValueOrDefault(cat.TypeOf(d), "VARCHAR");

        // Typed NULLs for every output column, so the union has all columns even when no row qualifies.
        var anchorCols = attrs.Select(d => $"CAST(NULL AS {AttrType(d)}) AS {Col(d)}").ToList();
        if (totals is not null) anchorCols.Add($"CAST(NULL AS INTEGER) AS {LevelColumn}");
        anchorCols.AddRange(measures.Select(m => $"CAST(NULL AS {TypeSql.GetValueOrDefault(cat.TypeOf(m), "DOUBLE")}) AS {Col(m)}"));
        selects.Add($"SELECT {string.Join(", ", anchorCols)} WHERE FALSE");

        string KeyOutputs(GroupingSet set, string? alias) =>
            string.Join(", ", attrs.Select(d => set.Keys.Contains(d)
                ? (alias is null ? Col(d) : $"{alias}.{Col(d)}")
                : $"CAST(NULL AS {AttrType(d)}) AS {Col(d)}")
                .Concat(totals is null ? [] : [$"{set.Level} AS {LevelColumn}"]));

        var blankMeasures = new HashSet<string>();
        var warnedNoSpine = new HashSet<string>();

        for (int si = 0; si < sets.Count; si++)
        {
            var set = sets[si];

            // Spine: the distinct keys of this set, from result sets that have every attribute of the set.
            var spineParts = resultSets.Where(d => set.Keys.All(d.Grain.Contains) && set.Keys.Count > 0)
                .Select(d => $"SELECT DISTINCT {string.Join(", ", set.Keys.Select(Col))} FROM \"{d.Id}\"").ToList();
            var spine = spineParts.Count > 0 ? $"spine{si}" : null;
            if (spine is not null) ctes.Add($"{spine} AS ({string.Join(" UNION ", spineParts)})");

            foreach (var ds in resultSets)
            {
                var grain = ds.Grain.Where(set.Keys.Contains).ToList();
                var covered = set.Keys.All(ds.Grain.Contains);
                foreach (var m in ds.Covers!.Measures)
                {
                    if (!measures.Contains(m)) continue;
                    if (covered)
                    {
                        selects.Add($"SELECT {KeyOutputs(set, null)}, {Col(m)} FROM ({AtGrain(cat, ds, grain, m, rescans)})");
                        continue;
                    }
                    var spread = cat.SpreadRule(m);
                    if (spread == "allocate")
                    {
                        var weights = AllocationWeights(cat, ds, grain, set, spine, m, weightSets, members, AtGrain(cat, ds, grain, m, rescans));
                        if (weights is not null)
                        {
                            var on = grain.Count > 0 ? string.Join(" AND ", grain.Select(d => $"w.{Col(d)} = x.{Col(d)}")) : "TRUE";
                            var part = grain.Count > 0 ? $"PARTITION BY {string.Join(", ", grain.Select(d => $"w.{Col(d)}"))}" : "";
                            selects.Add($"SELECT {KeyOutputs(set, "w")}, x.{Col(m)} * w.wval / NULLIF(SUM(w.wval) OVER ({part}), 0) AS {Col(m)} " +
                                        $"FROM ({weights}) w JOIN ({AtGrain(cat, ds, grain, m, rescans)}) x ON {on}");
                            continue;
                        }
                        notes.Add($"{m} can't be allocated at {(set.Keys.Count == 0 ? "the grand total" : string.Join(", ", set.Keys))}: no weights there, so it's left blank.");
                        blankMeasures.Add(m);
                        continue;
                    }
                    if (spread == "repeat" && spine is not null)
                    {
                        var on = grain.Count > 0 ? string.Join(" AND ", grain.Select(d => $"s.{Col(d)} = x.{Col(d)}")) : "TRUE";
                        selects.Add($"SELECT {KeyOutputs(set, "s")}, x.{Col(m)} FROM {spine} s JOIN ({AtGrain(cat, ds, grain, m, rescans)}) x ON {on}");
                        continue;
                    }
                    if (spread == "repeat" && warnedNoSpine.Add(m))
                        notes.Add($"{m} can't be repeated: no result set has the attributes to repeat it over, so it's left blank.");
                    blankMeasures.Add(m);
                }
            }
        }

        // A blank measure shows only on total rows; say so if it never appeared anywhere.
        foreach (var m in blankMeasures.Where(m => !warnedNoSpine.Contains(m)))
        {
            var owner = resultSets.First(d => d.Covers!.Measures.Contains(m));
            var shownSomewhere = sets.Any(s => s.Keys.All(owner.Grain.Contains));
            if (!shownSomewhere)
                notes.Add($"{m} is blank at the requested grain and no total rows were requested, so it has no values. Add x_totals or request it at its own grain.");
        }

        var keys = attrs.Select(Col).ToList();
        var groupCols = totals is null ? keys : keys.Concat([LevelColumn]).ToList();
        var output = groupCols.Concat(measures.Select(m => $"SUM({Col(m)}) AS {Col(m)}"));

        // Derived measures are computed from the aggregated components, at every grain, so a ratio is never summed.
        var derivedNames = outputOrder.Where(n => cat.Doc.Measures.TryGetValue(n, out var d) && d.Derived is not null)
            .Concat(transforms.Select(t => t.Measure).Where(m => cat.Doc.Measures.TryGetValue(m, out var d) && d.Derived is not null))
            .Distinct().ToList();

        // Aggregate first, then compute the transforms over the aggregated rows, then filter, order and limit.
        var agg = $"SELECT {string.Join(", ", output)}\nFROM ({string.Join(" UNION ALL BY NAME ", selects)})" +
                  (groupCols.Count > 0 ? $"\nGROUP BY {string.Join(", ", groupCols)}" : "");
        var byAlias = transforms.ToDictionary(t => t.Alias);
        var outputCols = outputOrder.Select(n => byAlias.TryGetValue(n, out var t)
            ? $"{TransformExpr(t, attrs, totals)} AS {Col(n)}" : Col(n)).ToList();
        // A period that has an earlier value (or a base) but no current one isn't a row of the answer, so it is dropped.
        var present = transforms.Where(t => t.Type is "prior_period" or "index").Select(t => $"{Col(t.Measure)} IS NOT NULL").Distinct().ToList();
        if (derivedNames.Count > 0)
            ctes.Add($"agg_derived AS (SELECT *, {string.Join(", ", derivedNames.Select(n => $"{DerivedExpr(cat, cat.Doc.Measures[n].Derived!)} AS {Col(n)}"))} FROM agg)");
        var computed = $"SELECT {string.Join(", ", groupCols.Concat(outputCols))} FROM {(derivedNames.Count > 0 ? "agg_derived" : "agg")}" +
                       (present.Count > 0 ? " WHERE " + string.Join(" AND ", present) : "");
        // A transform that reads another transform's output is computed one step after it: each step is a window
        // expression over the rows of the step before, so the first step's column is there for the next to read.
        int Depth(TransformRef t) => byAlias.TryGetValue(t.Measure, out var b) ? Depth(b) + 1 : 0;
        var steps = new List<string>();
        if (transforms.Any(t => Depth(t) > 0))
        {
            var step = derivedNames.Count > 0 ? "agg_derived" : "agg";
            var stepCount = transforms.Max(Depth) + 1;
            for (int k = 0; k < stepCount; k++)
            {
                var exprs = transforms.Where(t => Depth(t) == k).Select(t => $"{TransformExpr(t, attrs, totals)} AS {Col(t.Alias)}");
                steps.Add($"step{k} AS (SELECT *, {string.Join(", ", exprs)} FROM {step}" + (k == 0 && present.Count > 0 ? " WHERE " + string.Join(" AND ", present) : "") + ")");
                step = $"step{k}";
            }
            computed = $"SELECT {string.Join(", ", groupCols.Concat(outputOrder.Select(Col)))} FROM {step}";
        }
        // A rank with keep is a filter on its own output, applied last like a post filter: the other transforms have already
        // read every row, and a total row (which has no rank) stays.
        var keeps = transforms.Where(t => t.Type == "rank" && t.Raw["keep"] is not null)
            .Select(t => totals is null ? $"{Col(t.Alias)} <= {t.Raw["keep"]!.GetValue<int>()}"
                : $"({LevelColumn} <> 0 OR {Col(t.Alias)} <= {t.Raw["keep"]!.GetValue<int>()})");
        var conditions = postFilters.Select(PostFilterSql).Concat(keeps).ToList();
        var where = conditions.Count > 0 ? "\nWHERE " + string.Join(" AND ", conditions) : "";
        ctes.Insert(ctes.Count - (derivedNames.Count > 0 ? 1 : 0), $"agg AS ({agg})");
        ctes.AddRange(steps);
        ctes.Add($"computed AS ({computed})");

        var sql = "WITH " + string.Join(", ", ctes) + $"\nSELECT * FROM computed{where}";
        if (groupCols.Count > 0)
        {
            List<(string Column, string Dir)> orderCols;
            if (totals is null)
            {
                orderCols = order.Select(o => (Col(o.Member), o.Direction.ToUpperInvariant())).ToList();
                orderCols.AddRange(keys.Where(k => orderCols.All(o => o.Column != k)).Select(k => (k, "ASC")));
            }
            else
            {
                // Attribute order, with NULLS LAST so a subtotal follows its detail rows; the level separates a
                // real NULL member from a total.
                orderCols = order.Select(o => (Col(o.Member), o.Direction.ToUpperInvariant())).ToList();
                orderCols.AddRange(keys.Where(k => orderCols.All(o => o.Column != k)).Select(k => (k, "ASC")));
                orderCols.Add((LevelColumn, "ASC"));
            }
            sql += $"\nORDER BY {string.Join(", ", orderCols.Select(c => $"{c.Column} {c.Dir} NULLS LAST"))}";
            sql += $"\nLIMIT {limit + 1}";
        }
        return (sql, notes);
    }

    /// <summary>The measures to scan: a derived measure stands for the measures it reads, recursively; the rest scan as themselves.</summary>
    public static List<string> ScanNames(Catalog cat, IEnumerable<string> names)
    {
        var result = new List<string>();
        void Add(string n)
        {
            if (cat.Doc.Measures[n].Derived is { } spec) { foreach (var m in spec.Measures()) Add(m); }
            else if (!result.Contains(n)) result.Add(n);
        }
        foreach (var n in names) Add(n);
        return result;
    }

    /// <summary>A derived measure as SQL over the aggregated columns of the measures it reads (derived ones inlined).</summary>
    internal static string DerivedExpr(Catalog cat, ComputeSpec spec)
    {
        string Arg(ComputeArg a) =>
            a.Number is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : a.Nested is { } nested ? DerivedExpr(cat, nested)
            : cat.Doc.Measures[a.Measure!].Derived is { } inner ? DerivedExpr(cat, inner)
            : $"CAST({Col(a.Measure!)} AS DOUBLE)";
        var args = spec.Args.Select(Arg).ToList();
        return spec.Op switch
        {
            "divide" => $"({args[0]} / NULLIF({args[1]}, 0))",
            "subtract" => $"({args[0]} - {args[1]})",
            "add" => "(" + string.Join(" + ", args) + ")",
            "multiply" => "(" + string.Join(" * ", args) + ")",
            "coalesce" => "COALESCE(" + string.Join(", ", args) + ")",
            _ => throw new NotSupportedException($"Operation {spec.Op} can't be computed."),
        };
    }

    /// <summary>The levels at which every one of these attributes is rolled up (none if none of them is a listed attribute).</summary>
    private static List<int> LevelsRolling(IEnumerable<string> over, List<string> attrs, TotalsSpec? totals)
    {
        if (totals is null) return [];
        var listed = over.Where(totals.Rollup.Contains).ToList();
        return listed.Count == 0 ? [] : GroupingSets(attrs, totals).Where(s => listed.All(d => !s.Keys.Contains(d))).Select(s => s.Level).ToList();
    }

    /// <summary>
    /// What each rollup level means, for the descriptor: the attributes it keeps and the ones it has rolled up.
    /// Only levels the result contains are listed (a grand total nobody asked for is absent).
    /// </summary>
    public static JsonArray LevelMap(List<string> attrs, TotalsSpec totals) =>
        new(GroupingSets(attrs, totals).OrderBy(s => s.Level).Select(s => (JsonNode)new JsonObject
        {
            ["level"] = s.Level,
            ["kind"] = s.Level == 0 ? "detail" : s.Keys.Count == 0 ? "grand_total" : "subtotal",
            ["kept"] = new JsonArray(s.Keys.Select(k => (JsonNode)k).ToArray()),
            ["rolled_up"] = new JsonArray(attrs.Except(s.Keys).Select(k => (JsonNode)k).ToArray()),
        }).ToArray());

    /// <summary>
    /// One transform as a window expression over the aggregated rows. It is computed on detail rows only: with totals,
    /// a total row gets NULL, except that pct_of_total is 100% on a row that has rolled up every attribute it totals over.
    /// </summary>
    internal static string TransformExpr(TransformRef t, List<string> attrs, TotalsSpec? totals)
    {
        var m = Col(t.Measure);
        var detail = totals is null ? "TRUE" : $"{LevelColumn} = 0";
        string Part(IEnumerable<string> by, string? orderBy = null)
        {
            var cols = by.Select(Col).ToList();
            var part = cols.Count > 0 ? $"PARTITION BY {string.Join(", ", cols)}" : "";
            return orderBy is null ? part : $"{part} ORDER BY {orderBy}".TrimStart();
        }
        var detailValue = $"CASE WHEN {detail} THEN {m} END";

        switch (t.Type)
        {
            case "pct_of_total" or "share_of_parent":
            {
                var over = t.Type == "pct_of_total" ? t.List("over") : [t.Str("attribute")!];
                var levels = LevelsRolling(over, attrs, totals);
                var rolled = levels.Count > 0 ? $" WHEN {LevelColumn} IN ({string.Join(", ", levels)}) THEN 1.0" : "";
                return $"CASE WHEN {detail} THEN CAST({m} AS DOUBLE) / NULLIF(CAST(SUM({detailValue}) OVER ({Part(attrs.Except(over))}) AS DOUBLE), 0){rolled} END";
            }
            case "running_total":
            {
                var along = t.Str("along")!;
                var frame = Part(attrs.Where(d => d != along), $"{Col(along)} ASC NULLS LAST") + " ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";
                return $"CASE WHEN {detail} THEN SUM({detailValue}) OVER ({frame}) END";
            }
            case "rank":
            {
                var ties = t.Str("ties") ?? "skip";
                var fn = ties switch { "dense" => "DENSE_RANK", "break" => "ROW_NUMBER", _ => "RANK" };
                var dir = (t.Str("order") ?? "desc").ToUpperInvariant();
                // Breaking a tie orders by the value and then by the query's attributes, so the same N rows come back every time.
                var orderBy = $"{detailValue} {dir} NULLS LAST" + (ties == "break" ? string.Concat(attrs.Select(a => $", {Col(a)} ASC NULLS LAST")) : "");
                return $"CASE WHEN {detail} THEN {fn}() OVER ({Part(t.List("within"), orderBy)}) END";
            }
            case "window" or "prior_period":
            {
                // The earlier value (a prior period, or a window) is a hidden measure, summed like any other, so it is available on total rows too.
                var prior = $"CAST({Col(t.Alias + "__prior")} AS DOUBLE)";
                var current = $"CAST({m} AS DOUBLE)";
                return (t.Str("output") ?? "value") switch
                {
                    "delta" => $"{current} - {prior}",
                    "pct_change" => $"({current} - {prior}) / NULLIF({prior}, 0)",
                    _ => prior,
                };
            }
            case "index":
            {
                // The base value is a hidden measure (the measure at the base member, for each combination of the other attributes), so each row is divided by its own group's base. A total row has no base.
                var baseValue = $"CAST({Col(t.Alias + "__prior")} AS DOUBLE)";
                var scale = (t.Raw["scale"]?.GetValue<double>() ?? 100d).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return $"CASE WHEN {detail} THEN CAST({m} AS DOUBLE) / NULLIF({baseValue}, 0) * {scale} END";
            }
            default:
                throw new NotSupportedException($"Transform {t.Type} can't be computed here.");
        }
    }

    private static string PostFilterSql(Filter f) => f switch
    {
        AndFilter a => "(" + string.Join(" AND ", a.Items.Select(PostFilterSql)) + ")",
        OrFilter o => "(" + string.Join(" OR ", o.Items.Select(PostFilterSql)) + ")",
        LeafFilter l => PostLeafSql(l),
        _ => throw new NotSupportedException(),
    };

    private static string PostLeafSql(LeafFilter l)
    {
        var col = Col(l.Name);
        string Lit(System.Text.Json.Nodes.JsonNode v) => v.GetValue<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return l.Operator switch
        {
            "equals" => l.Values.Count == 1 ? $"{col} = {Lit(l.Values[0])}" : $"{col} IN ({string.Join(", ", l.Values.Select(Lit))})",
            "not_equals" => l.Values.Count == 1 ? $"{col} <> {Lit(l.Values[0])}" : $"{col} NOT IN ({string.Join(", ", l.Values.Select(Lit))})",
            "between" => $"{col} BETWEEN {Lit(l.Values[0])} AND {Lit(l.Values[1])}",
            "gt" => $"{col} > {Lit(l.Values[0])}",
            "gte" => $"{col} >= {Lit(l.Values[0])}",
            "lt" => $"{col} < {Lit(l.Values[0])}",
            "lte" => $"{col} <= {Lit(l.Values[0])}",
            "set" => $"{col} IS NOT NULL",
            "not_set" => $"{col} IS NULL",
            _ => throw new NotSupportedException($"operator {l.Operator} on a transform output"),
        };
    }

    /// <summary>
    /// The weights to split a measure by, as SQL over the set's keys with a <c>wval</c> column, or null if this set has none.
    /// By measure: the weight measure's rows rolled up to the set. Equal over the members present: one weight per row of
    /// the spine. Equal over catalog members: the measure's own keys crossed with every member of the missing attributes.
    /// </summary>
    private static string? AllocationWeights(Catalog cat, ResultSet ds, List<string> grain, GroupingSet set, string? spine,
        string measure, Dictionary<string, ResultSet> weightSets, Func<string, List<string>> members, string atGrain)
    {
        var alloc = cat.Doc.Measures[measure].Allocate!;
        var keys = string.Join(", ", set.Keys.Select(Col));
        if (alloc.By == "measure")
        {
            if (!weightSets.TryGetValue(measure, out var w) || !set.Keys.All(w.Grain.Contains)) return null;
            return $"SELECT {keys}, SUM({Col(alloc.WeightMeasure!)}) AS wval FROM \"{w.Id}\" GROUP BY {keys}";
        }
        if (alloc.Members == "present")
            return spine is null ? null : $"SELECT {keys}, 1 AS wval FROM {spine}";

        var missing = set.Keys.Except(grain).ToList();
        var crossed = new List<string>();
        for (int i = 0; i < missing.Count; i++)
        {
            List<string> values;
            try { values = members(missing[i]); } catch (Exception) { return null; }
            if (values.Count == 0) return null;
            crossed.Add($"CROSS JOIN (VALUES {string.Join(", ", values.Select(v => $"('{v.Replace("'", "''")}')"))}) t{i}({Col(missing[i])})");
        }
        var own = grain.Count > 0 ? $"(SELECT DISTINCT {string.Join(", ", grain.Select(Col))} FROM ({atGrain}))" : "(SELECT 1 AS one)";
        return $"SELECT {keys}, 1 AS wval FROM {own} x {string.Join(" ", crossed)}";
    }

    /// <summary>The measure at the given grain (a subset of the result set's): as is, from a source repeat, or rolled up here.</summary>
    private static string AtGrain(Catalog cat, ResultSet ds, List<string> grain, string measure, Dictionary<string, ResultSet> rescans)
    {
        var cols = string.Join(", ", grain.Select(Col).Concat([Col(measure)]));
        if (grain.Count == ds.Grain.Count) return $"SELECT {cols} FROM \"{ds.Id}\"";

        var dropped = ds.Grain.Except(grain).ToList();
        // The source computed this result set's subtotals itself: the rows with this many attributes rolled up.
        if (rescans.TryGetValue(NativeKey(ds.Id), out var native) && native.Covers!.Measures.Contains(measure))
        {
            var level = native.Descriptor["x_totals"]!["levels"]!.AsArray()
                .First(l => l!["rolled_up"]!.AsArray().Select(d => d!.GetValue<string>()).ToHashSet().SetEquals(dropped))!["level"]!.GetValue<int>();
            return $"SELECT {cols} FROM \"{native.Id}\" WHERE {LevelColumn} = {level}";
        }
        if (dropped.Any(d => cat.RollupRule(measure, d) == "none"))
        {
            var rescan = rescans[RescanKey(ds.Id, grain)];
            return $"SELECT {cols} FROM \"{rescan.Id}\"";
        }
        return RollupSql(cat, ds, grain, measure);
    }

    /// <summary>
    /// Rolls one measure up to a coarser grain, one removed attribute at a time. first and last come first, so
    /// "last month, then sum over warehouses" is never "sum, then last". last means the last member in scope
    /// for the whole group, like DAX LASTDATE; a group member with no row there contributes nothing.
    /// </summary>
    internal static string RollupSql(Catalog cat, ResultSet ds, List<string> grain, string measure)
    {
        var m = Col(measure);
        var dropped = ds.Grain.Except(grain)
            .OrderBy(d => cat.RollupRule(measure, d) is "first" or "last" ? 0 : 1)
            .ThenByDescending(d => ds.Grain.IndexOf(d)).ToList();
        var curKeys = ds.Grain.ToList();
        var cur = $"SELECT {string.Join(", ", curKeys.Select(Col).Concat([m]))} FROM \"{ds.Id}\"";
        foreach (var attr in dropped)
        {
            var rule = cat.RollupRule(measure, attr);
            var newKeys = curKeys.Where(k => k != attr).ToList();
            var keySel = string.Join(", ", newKeys.Select(Col).Concat([m]));
            switch (rule)
            {
                case "sum" or "min" or "max" or "avg":
                {
                    var agg = rule.ToUpperInvariant();
                    var groupBy = newKeys.Count > 0 ? $" GROUP BY {string.Join(", ", newKeys.Select(Col))}" : "";
                    var sel = string.Join(", ", newKeys.Select(Col).Concat([$"{agg}({m}) AS {m}"]));
                    cur = $"SELECT {sel} FROM ({cur}){groupBy}";
                    break;
                }
                case "first" or "last":
                {
                    var fn = rule == "first" ? "MIN" : "MAX";
                    var part = newKeys.Count > 0 ? $"PARTITION BY {string.Join(", ", newKeys.Select(Col))}" : "";
                    cur = $"SELECT {keySel} FROM (SELECT *, {fn}({Col(attr)}) OVER ({part}) AS _x FROM ({cur})) WHERE {Col(attr)} = _x";
                    break;
                }
                default:
                    throw new NotSupportedException($"Rollup rule '{rule}' can't be computed from detail rows.");
            }
            curKeys = newKeys;
        }
        return cur;
    }
}
