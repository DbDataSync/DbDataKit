using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Emit;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    private static QisError CalendarError(string code, string message) => new("mashup", code, "$.query", message);

    /// <summary>Time attributes the calendar can move back by this unit, among the given attributes and the filter members.</summary>
    private List<string> ShiftableAttributes(string unit, IEnumerable<string> attrs, IEnumerable<Filter> filters) =>
        !cat.Doc.Shifts.TryGetValue(unit, out var offsets) ? [] :
        attrs.Concat(filters.SelectMany(f => f.Names())).Where(offsets.ContainsKey).Distinct().ToList();

    /// <summary>
    /// A source scanned for the period one calendar unit earlier, relabeled to the current members. The query's time
    /// filters move back first: a filter on FY27-Q1 asks for FY26-Q1. Rows of the earlier period then take the label of the
    /// current member they correspond to, so they line up with the current rows in the combine.
    /// </summary>
    private (ResultSet? Shifted, ResultSet? Raw, Result? Failure) ShiftedScan(
        string id, string source, List<string> attrs, List<string> measures, List<Filter> applied, string unit, int offset, string hash)
    {
        var timeAttrs = ShiftableAttributes(unit, attrs, applied);
        if (timeAttrs.Count == 0)
            return (null, null, Result.Failed(level, hash, [CalendarError("shift_needs_time",
                $"A {unit} shift needs a time attribute or a filter on one that the calendar can move back; the query has neither.")]));

        // Each time member and its counterpart one unit earlier, for the members that pass the query's own filters.
        var pairs = new Dictionary<string, List<(string Current, string Prior)>>();
        // A date's counterpart is read from the date table, for every day, because a filter's boundaries needn't be days with fact rows.
        var dayMaps = new Dictionary<string, DayMap>();
        // For a filter that isn't an equals (a fiscal month from 2026-07 on), every member of the dimension in the date table, each with its counterpart.
        var universe = new Dictionary<string, List<(string Member, string Target)>>();
        foreach (var d in timeAttrs)
        {
            var shifted = cat.Doc.Shifts[unit][d];
            if (cat.Binding(d, source) is null || cat.Binding(shifted, source) is null)
                return (null, null, Result.Failed(level, hash, [CalendarError("calendar_unavailable",
                    $"{source} can't move {d} back by {unit}: it needs both {d} and {shifted}.")]));
            // One period earlier reads each member's counterpart from the rows themselves. Any other offset follows the calendar's counterparts from the whole date
            // table: two periods earlier is the counterpart of the counterpart, and later is the counterpart read the other way, so every member in between has to be there.
            List<(string, string)> list;
            if (offset == -1)
            {
                var rows = Scan(source, [d, shifted], applied);
                list = rows.Select(r => (r[Catalog.ColName(d)]?.ToString()!, r[Catalog.ColName(shifted)]?.ToString()!))
                    .Where(p => p.Item1 is not null && p.Item2 is not null).Distinct().ToList();
            }
            else
            {
                var (composed, chainError) = Counterparts(source, d, shifted, offset, unit);
                if (chainError is not null) return (null, null, Result.Failed(level, hash, [chainError]));
                list = Scan(source, [d], applied).Select(r => r[Catalog.ColName(d)]?.ToString()).Where(m => m is not null && composed!.ContainsKey(m))
                    .Distinct().Select(m => (m!, composed![m!])).ToList();
            }
            if (list.GroupBy(p => p.Item2).Any(g => g.Count() > 1))
                return (null, null, Result.Failed(level, hash, [CalendarError("calendar_not_one_to_one",
                    $"Two {d} members share the same {unit} counterpart, so rows of the other period can't be relabeled unambiguously.")]));
            pairs[d] = list;
            if (cat.TypeOf(d) != "date" && applied.Any(f => FilteredBeyondEquals(f, d)))
            {
                var (all, allError) = Counterparts(source, d, shifted, offset, unit);
                if (allError is not null) return (null, null, Result.Failed(level, hash, [allError]));
                universe[d] = all!.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();
            }
            if (cat.TypeOf(d) == "date")
            {
                var (map, mapError) = DayCounterparts(source, d, shifted);
                if (mapError is not null) return (null, null, Result.Failed(level, hash, [mapError]));
                dayMaps[d] = offset == -1 ? map! : new DayMap(FollowOffset(map!.Earlier, offset).Map);
            }
        }

        var (filters, error) = ShiftFilters(applied, pairs, dayMaps, universe);
        if (error is not null) return (null, null, Result.Failed(level, hash, [error]));

        var (raw, failure) = ScanSource($"{id}_raw", source, attrs, measures, filters, [], [], null, hash, "$.result_sets");
        if (failure is not null) return (null, null, failure);

        // Relabel: join the rows of the earlier period to the member they correspond to.
        var shiftedAttrs = attrs.Where(timeAttrs.Contains).ToList();
        var joins = new List<string>();
        var cols = new List<string>();
        foreach (var d in attrs)
        {
            var i = shiftedAttrs.IndexOf(d);
            if (i < 0) { cols.Add($"x.\"{Catalog.ColName(d)}\""); continue; }
            var values = string.Join(", ", pairs[d].Select(p => $"('{p.Prior.Replace("'", "''")}', '{p.Current.Replace("'", "''")}')"));
            joins.Add($"JOIN (VALUES {values}) m{i}(prior, cur) ON x.\"{Catalog.ColName(d)}\" = m{i}.prior");
            cols.Add($"m{i}.cur AS \"{Catalog.ColName(d)}\"");
        }
        cols.AddRange(measures.Select(m => $"x.\"{Catalog.ColName(m)}\""));
        var relabeled = Sandbox.Run($"SELECT {string.Join(", ", cols)} FROM \"{raw!.Id}\" x {string.Join(" ", joins)}", [raw], rowCap + 1);

        var descriptor = Descriptor(id, source, attrs, measures, relabeled.Rows.Count, false, hash);
        descriptor["x_provenance"] = new JsonObject
        {
            ["spec"] = "mashup", ["spec_version"] = SpecVersion, ["mode"] = "engine", ["request_hash"] = hash,
            ["inputs"] = new JsonArray(raw.Id), ["engine_level"] = level, ["shifted"] = unit, ["offset"] = offset,
        };
        return (new ResultSet(id, source, attrs, raw.Covers, descriptor, raw.NativeQuery, relabeled.Rows), raw, null);
    }

    /// <summary>
    /// Every member that has a counterpart <paramref name="offset"/> periods away, from the calendar's one-period counterparts: followed |offset| times for an
    /// earlier period, or read the other way (which needs them one-to-one) for a later one.
    /// </summary>
    private (Dictionary<string, string>? Map, QisError? Error) Counterparts(string source, string d, string shifted, int offset, string unit)
    {
        var earlier = new Dictionary<string, string>();
        foreach (var r in PairRows(source, d, shifted))
            if (r[Catalog.ColName(d)]?.ToString() is { } m && r[Catalog.ColName(shifted)]?.ToString() is { } p) earlier[m] = p;
        var (map, problem) = FollowOffset(earlier, offset);
        return problem is null ? (map, null) : (null, CalendarError("calendar_not_one_to_one", $"{problem} Two {d} members share the same {unit} counterpart, so the calendar can't be read the other way."));
    }

    /// <summary>The map after following an earlier-counterpart map |offset| times (a negative offset), or its inverse (a positive one).</summary>
    internal static (Dictionary<string, string> Map, string? Problem) FollowOffset(IReadOnlyDictionary<string, string> earlier, int offset)
    {
        IReadOnlyDictionary<string, string> step = earlier;
        string? problem = null;
        if (offset > 0)
        {
            var inverse = new Dictionary<string, string>();
            step = inverse;
            foreach (var (current, prior) in earlier)
                if (!inverse.TryAdd(prior, current)) problem = $"{prior} is the counterpart of more than one member.";
        }
        var result = new Dictionary<string, string>();
        foreach (var start in step.Keys)
        {
            var at = start;
            var ok = true;
            for (int i = 0; i < Math.Abs(offset) && ok; i++) ok = step.TryGetValue(at, out at!);
            if (ok) result[start] = at;
        }
        return (result, problem);
    }

    /// <summary>Every row of the date table pairing two columns of it (so a member with no fact rows is there), else the pairs the fact rows hold.</summary>
    private List<Dictionary<string, object?>> PairRows(string source, string a, string b)
    {
        var (ba, bb) = (cat.Binding(a, source)!, cat.Binding(b, source)!);
        if (ba.Join is not null && ba.Join == bb.Join && CanReadDimension(cat.Doc.Sources[source], ba, bb))
            return ReadDimension(source, SqlEmitter.BuildPairs(cat, source, a, b), () => DaxEmitter.BuildPairs(cat, source, a, b));
        return Scan(source, [a, b], []);
    }

    /// <summary>True when a source can be asked for a whole dimension (its own table, not the fact rows): SQL Server, or an Analysis Services model whose columns have DAX.</summary>
    private static bool CanReadDimension(SourceDef source, params BindingDef[] bindings) =>
        source.Kind == "sqlserver" || (source.Kind == "ssas" && bindings.All(b => b.Dax is not null));

    /// <summary>
    /// Reads a dimension with no parameters: T-SQL on SQL Server; on Analysis Services the DAX, with the SQL text as the twin the test stand-in runs. The rows are what the
    /// columns hold, so a member with no fact rows is there.
    /// </summary>
    private List<Dictionary<string, object?>> ReadDimension(string source, string sql, Func<string> dax)
    {
        var native = cat.Doc.Sources[source].Kind == "ssas" ? new NativeQuery("dax", dax()) : new NativeQuery("tsql", sql);
        return executor.Execute(new CompiledQuery(source, native, sql, []), rowCap + 1).Rows;
    }

    private static bool FilteredBeyondEquals(Filter f, string attr) => f switch
    {
        AndFilter a => a.Items.Any(i => FilteredBeyondEquals(i, attr)),
        OrFilter o => o.Items.Any(i => FilteredBeyondEquals(i, attr)),
        LeafFilter l => l.Name == attr && l.Operator is not ("equals" or "set" or "not_set"),
        _ => false,
    };

    /// <summary>Whether a period label passes a filter on its dimension; labels are compared as text, so they must sort in time order, as ISO labels do.</summary>
    private static bool PassesFilter(LeafFilter l, string member)
    {
        string Text(JsonNode v) => v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        var values = l.Values.Select(Text).ToList();
        return l.Operator switch
        {
            "not_equals" => !values.Contains(member),
            "gt" => string.CompareOrdinal(member, values[0]) > 0,
            "gte" => string.CompareOrdinal(member, values[0]) >= 0,
            "lt" => string.CompareOrdinal(member, values[0]) < 0,
            "lte" => string.CompareOrdinal(member, values[0]) <= 0,
            "contains" => values.Any(v => member.Contains(v, StringComparison.OrdinalIgnoreCase)),
            "not_contains" => !values.Any(v => member.Contains(v, StringComparison.OrdinalIgnoreCase)),
            "starts_with" => values.Any(v => member.StartsWith(v, StringComparison.OrdinalIgnoreCase)),
            _ => values.Contains(member),
        };
    }

    /// <summary>
    /// Moves filters on time attributes to the other period through the member pairs. An equals moves each member; any other operator selects the members of the date
    /// table that pass it and moves those, so "from 2026-07 on" becomes the counterparts of every month from 2026-07 on.
    /// </summary>
    private (List<Filter> Filters, QisError? Error) ShiftFilters(List<Filter> filters, Dictionary<string, List<(string Current, string Prior)>> pairs,
        Dictionary<string, DayMap> dayMaps, Dictionary<string, List<(string Member, string Target)>> universe)
    {
        QisError? error = null;
        Filter Shift(Filter f)
        {
            switch (f)
            {
                case AndFilter a: return new AndFilter(a.Items.Select(Shift).ToList());
                case OrFilter o: return new OrFilter(o.Items.Select(Shift).ToList());
                case LeafFilter l when dayMaps.TryGetValue(l.Name, out var days):
                    var (dateShifted, problem) = days.Shift(l);
                    if (problem is not null) { error ??= CalendarError("shift_date_unmapped", problem); return l; }
                    return dateShifted!;
                case LeafFilter l when cat.IsAttribute(l.Name) && cat.TypeOf(l.Name) == "datetime":
                    error ??= CalendarError("shift_filter_unsupported", $"{l.Name} is a datetime, and a range of dates on it can't be moved back; filter on a period of its date dimension (a fiscal month or quarter) instead.");
                    return l;
                case LeafFilter l when cat.IsAttribute(l.Name) && cat.TypeOf(l.Name) == "date":
                    error ??= CalendarError("shift_filter_unsupported", $"{l.Name} is a date, and the calendar has no earlier counterpart for a date; filter on a period the calendar can move back instead.");
                    return l;
                case LeafFilter l when pairs.TryGetValue(l.Name, out var map):
                    if (l.Operator is "set" or "not_set") return l;   // nothing about the period's value
                    if (l.Operator != "equals")
                    {
                        if (!universe.TryGetValue(l.Name, out var members))
                        {
                            error ??= CalendarError("shift_filter_unsupported", $"{l.Name} is filtered with {l.Operator}, which can't be moved through the calendar here.");
                            return l;
                        }
                        var targets = members.Where(x => PassesFilter(l, x.Member)).Select(x => (JsonNode)JsonValue.Create(x.Target)!).ToList();
                        if (targets.Count == 0) targets.Add(JsonValue.Create("\u0000no member")!);
                        return new LeafFilter(l.Name, "equals", targets);
                    }
                    // A member with no counterpart has no earlier rows: it matches nothing.
                    var moved = l.Values.Select(v => map.FirstOrDefault(p => p.Current == v.GetValue<string>()).Prior).Where(p => p is not null)
                        .Select(p => (JsonNode)JsonValue.Create(p)!).ToList();
                    if (moved.Count == 0) moved.Add(JsonValue.Create("\u0000no earlier member")!);
                    return new LeafFilter(l.Name, "equals", moved);
                default: return f;
            }
        }
        var shifted = filters.Select(Shift).ToList();
        return (shifted, error);
    }

    /// <summary>
    /// Every day and its counterpart one unit earlier, from the date table when both are columns of one table, else from the fact rows.
    /// </summary>
    private (DayMap? Map, QisError? Error) DayCounterparts(string source, string date, string earlier)
    {
        var rows = PairRows(source, date, earlier);
        var map = new Dictionary<string, string>();
        foreach (var r in rows)
            if (r[Catalog.ColName(date)]?.ToString() is { } d && r[Catalog.ColName(earlier)]?.ToString() is { } p) map[d] = p;
        return (new DayMap(map), null);
    }

    /// <summary>Runs a attributes-only scan (no measures) and returns its rows. Used to read members and calendar pairs.</summary>
    private List<Dictionary<string, object?>> Scan(string source, List<string> attrs, List<Filter> filters)
    {
        var native = cat.Doc.Sources[source].Kind == "ssas"
            ? new NativeQuery("dax", DaxEmitter.BuildDax(cat, source, attrs, [], filters, [], null))
            : new NativeQuery("tsql", SqlEmitter.BuildSelect(cat, source, attrs, [], filters, [], null, EmitDialects.TSql).Text);
        var (duckSql, duckParams) = SqlEmitter.BuildSelect(cat, source, attrs, [], filters, [], null, EmitDialects.DuckDb);
        var execText = native.Dialect == "tsql" ? SqlEmitter.BuildSelect(cat, source, attrs, [], filters, [], null, EmitDialects.TSql).Text : null;
        return executor.Execute(new CompiledQuery(source, native, duckSql, duckParams, execText), rowCap + 1).Rows;
    }
}
