using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    /// <summary>The engine's clock for relative ranges. Tests set it; a replayed run uses the date its record was made on.</summary>
    public Func<DateOnly> Today
    {
        get => _today ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
        set => _today = value;
    }

    private Func<DateOnly>? _today;

    /// <summary>
    /// Today as a business date: the date a test pinned, or else the date it is now in the zone the request is read in (it is already tomorrow in Tokyo
    /// when it is still evening in Chicago).
    /// </summary>
    private DateOnly TodayFor(string zone) =>
        _today is not null ? _today() : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZones.Find(zone) ?? TimeZoneInfo.Utc));

    /// <summary>The ranges resolved for the request being run, reported in the result.</summary>
    private List<ResolvedRange> _resolved = [];

    private static DateOnly ParseDate(string text) =>
        DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves the relative operators (last_complete, current, period_to_date) of the request's filters, wherever they are (the query's, a filtered measure's, inside
    /// and/or), to filters on the calendar's members or dates. Everything after this sees an ordinary query.
    /// </summary>
    private (JsonObject Doc, Result? Failure) ExpandPeriods(JsonObject doc, string hash)
    {
        if (!HasRelative(doc["query"])) return (doc, null);
        var expanded = doc.DeepClone().AsObject();
        QisError? error = null;

        JsonNode? Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o when o["operator"] is JsonValue op && Validator.RelativeOps.Contains(op.GetValue<string>()):
                    var (resolved, failure) = ResolveRelative((LeafFilter)Filter.Parse(o), path);
                    if (failure is not null) { error ??= failure; return node; }
                    return FilterJson(resolved!);
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        var child = o[key];
                        var replacement = Walk(child, $"{path}.{key}");
                        if (!ReferenceEquals(replacement, child)) o[key] = replacement;
                    }
                    return o;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++)
                    {
                        var child = a[i];
                        var replacement = Walk(child, $"{path}[{i}]");
                        if (!ReferenceEquals(replacement, child)) a[i] = replacement;
                    }
                    return a;
                default:
                    return node;
            }
        }
        Walk(expanded["query"], "$.query");
        return error is null ? (expanded, null) : (doc, Result.Failed(level, hash, [error]));
    }

    private static bool HasRelative(JsonNode? node) => node switch
    {
        JsonObject o => o["operator"] is JsonValue op && Validator.RelativeOps.Contains(op.GetValue<string>()) || o.Any(p => HasRelative(p.Value)),
        JsonArray a => a.Any(HasRelative),
        _ => false,
    };

    /// <summary>One relative filter: the period is the filter's attribute, so its unit is that attribute's level in the calendar it belongs to.</summary>
    private (Filter? Filter, QisError? Error) ResolveRelative(LeafFilter leaf, string path)
    {
        var (cal, unit) = cat.PeriodOf(leaf.Name)!.Value;
        var range = new JsonObject
        {
            ["type"] = leaf.Operator switch { "last_complete" => "last_n_complete", _ => leaf.Operator },
            ["unit"] = unit,
        };
        if (leaf.Count is { } count) range["count"] = count;
        var (filter, error) = ResolveRange(cal, range, path);
        if (error is null && _resolved.Count > 0) _resolved[^1] = _resolved[^1] with { Attribute = leaf.Name, Operator = leaf.Operator };
        return (filter, error);
    }

    /// <summary>
    /// A relative range as a filter. Whole periods (the last n complete, the current one, one by name) filter on the
    /// level's members, so they apply to every source that has that level and can be moved back a year. A range that
    /// stops partway through a period (to date) or is written as dates filters the calendar's date dimension.
    /// </summary>
    private (Filter? Filter, QisError? Error) ResolveRange(CalendarDef cal, JsonObject range, string path)
    {
        QisError Fail(string code, string message) => new("mashup", code, path, message);
        var type = range["type"]!.GetValue<string>();
        Filter Dates(DateOnly s, DateOnly e) => new LeafFilter(cal.Date, "between", [JsonValue.Create(Iso(s))!, JsonValue.Create(Iso(e))!]);
        if (cal.Implicit)
        {
            // A datetime tied to a date dimension reads that attribute's periods (fiscal months and the like) through its own date.
            if (cal.Base is not null && cal.Base.Levels.ContainsKey(range["unit"]!.GetValue<string>()) && range["unit"]!.GetValue<string>() != "day")
            {
                var (inner, innerError) = ResolveRange(cal.Base, range, path);
                return innerError is null ? (OnDatetime(inner!, cal), null) : (null, innerError);
            }
            return ResolveImplicitRange(cal, range, path);
        }
        var unit = range["unit"]!.GetValue<string>();
        var levelAttr = cal.Levels[unit];
        var (members, error) = LevelRanges(cal, levelAttr, path);
        if (error is not null) return (null, error);

        var today = TodayFor(cat.Zone);
        var current = members.FindIndex(m => m.Start <= today && today <= m.End);
        List<(string Member, DateOnly Start, DateOnly End)> whole;
        switch (type)
        {
            case "period_to_date":
                if (current < 0)
                    return (null, Fail("range_unresolved", $"The date table has no {unit} that contains today, {Iso(today)}."));
                _resolved.Add(new ResolvedRange(path, Iso(today), Iso(members[current].Start), Iso(today)));
                return (Dates(members[current].Start, today), null);
            case "current":
                if (current < 0)
                    return (null, Fail("range_unresolved", $"The date table has no {unit} that contains today, {Iso(today)}."));
                whole = [members[current]];
                break;
            default:
                var n = range["count"]!.GetValue<int>();
                if (current < 0)
                    return (null, Fail("range_unresolved", $"The date table has no {unit} that contains today, {Iso(today)}."));
                if (current < n)
                    return (null, Fail("range_unresolved", $"The date table has only {current} complete {unit} before today, {Iso(today)}; last_complete asks for {n}."));
                whole = members.GetRange(current - n, n);
                break;
        }
        var (s, e) = (whole[0].Start, whole[^1].End);
        _resolved.Add(new ResolvedRange(path, Iso(today), Iso(s), Iso(e), levelAttr == cal.Date ? null : levelAttr, levelAttr == cal.Date ? null : whole.Select(m => m.Member).ToList()));
        // Days are one range; any larger unit is the list of its members.
        return (levelAttr == cal.Date ? Dates(s, e) : new LeafFilter(levelAttr, "equals", whole.Select(m => (JsonNode)JsonValue.Create(m.Member)!).ToList()), null);
    }

    /// <summary>A filter on the date dimension's attributes as the same filter on those attributes read through a datetime.</summary>
    private Filter OnDatetime(Filter f, CalendarDef cal)
    {
        string Map(string member) =>
            member == cal.Base!.Date ? cal.Date
            : cal.Base.Levels.ContainsValue(member) ? $"{cal.Date}_{member[(member.IndexOf('.') + 1)..]}" : member;
        return f switch
        {
            AndFilter a => new AndFilter(a.Items.Select(i => OnDatetime(i, cal)).ToList()),
            OrFilter o => new OrFilter(o.Items.Select(i => OnDatetime(i, cal)).ToList()),
            LeafFilter l => l with { Name = Map(l.Name) },
            _ => f,
        };
    }

    /// <summary>
    /// A relative range on a datetime dimension, which has no date table: periods are the calendar's own (weeks start on Monday, quarters on the first of
    /// January, April, July and October), counted in the request's zone, and the range is the days they cover as a between filter on the datetime.
    /// </summary>
    private (Filter? Filter, QisError? Error) ResolveImplicitRange(CalendarDef cal, JsonObject range, string path)
    {
        var type = range["type"]!.GetValue<string>();
        var unit = range["unit"]!.GetValue<string>();
        var today = TodayFor(cat.Zone);
        DateOnly Start(DateOnly d) => unit switch
        {
            "day" => d,
            "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
            "month" => new DateOnly(d.Year, d.Month, 1),
            "quarter" => new DateOnly(d.Year, (d.Month - 1) / 3 * 3 + 1, 1),
            _ => new DateOnly(d.Year, 1, 1),
        };
        DateOnly Add(DateOnly start, int n) => unit switch
        {
            "day" => start.AddDays(n), "week" => start.AddDays(7 * n), "month" => start.AddMonths(n),
            "quarter" => start.AddMonths(3 * n), _ => start.AddYears(n),
        };
        DateOnly s, e;
        switch (type)
        {
            case "current": (s, e) = (Start(today), Add(Start(today), 1).AddDays(-1)); break;
            case "period_to_date": (s, e) = (Start(today), today); break;
            case "last_n_complete":
                var n = range["count"]!.GetValue<int>();
                (s, e) = (Add(Start(today), -n), Start(today).AddDays(-1));
                break;
            default:
                return (null, new QisError("mashup", "range_unresolved", path, $"{type} isn't a relative period operator."));
        }
        _resolved.Add(new ResolvedRange(path, Iso(today), Iso(s), Iso(e)));
        return (new LeafFilter(cal.Date, "between", [JsonValue.Create(Iso(s))!, JsonValue.Create(Iso(e))!]), null);
    }

    /// <summary>
    /// Every member of a calendar level with its first and last day, in date order, read from the date table itself, so a
    /// period with no sales is still there. It needs a SQL source where the level and the date dimension are columns of the same table.
    /// </summary>
    private (List<(string Member, DateOnly Start, DateOnly End)> Members, QisError? Error) LevelRanges(CalendarDef cal, string levelAttr, string path)
    {
        foreach (var (source, def) in cat.Doc.Sources)
        {
            if (def.Kind != "sqlserver") continue;
            var level = cat.Binding(levelAttr, source);
            var date = cat.Binding(cal.Date, source);
            if (level is null || date is null || level.Join != date.Join || level.Join is null) continue;
            var text = Emit.SqlEmitter.BuildLevelRanges(cat, source, levelAttr, cal.Date);
            var rows = executor.Execute(new CompiledQuery(source, new NativeQuery("tsql", text), text, []), rowCap + 1).Rows;
            var members = rows.Select(r => (Member: r["lvl"]!.ToString()!, Start: ParseDate(r["lo"]!.ToString()!), End: ParseDate(r["hi"]!.ToString()!)))
                .OrderBy(m => m.Start).ToList();
            return (members, null);
        }
        return ([], new QisError("mashup", "calendar_unavailable", path,
            $"No SQL source has {levelAttr} and {cal.Date} as columns of one date table, so a relative range can't be resolved."));
    }
}
