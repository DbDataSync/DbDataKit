using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    /// <summary>
    /// The base of an <c>index</c> transform: the measure at one member of <c>along</c>, for each combination of the query's other attributes, repeated against every member the
    /// answer shows so each row can be divided by its own group's base. The base is a scan of its own with the query's filters on the calendar left out, so a query limited to
    /// 2026 can still be indexed to a month of 2025, and a distinct count (or any measure that can't be added) is right because the source computes it. Other attributes of the
    /// query that are coarser levels of the same calendar (the fiscal year beside the fiscal month) are read from the date table for each member, as a window does.
    /// </summary>
    private (ResultSet? Base, List<ResultSet>? Raw, Result? Failure) IndexScan(
        string id, string source, List<string> attrs, List<string> measures, List<Filter> applied, IndexSpec index, string hash)
    {
        var along = index.Along;
        var cal = cat.PeriodOf(along)?.Calendar;
        if (cal is null)
            return (null, null, Result.Failed(level, hash, [CalendarError("transform_needs_time", $"index needs a period attribute in along: {along} isn't a level of a calendar.")]));
        if (cat.Binding(along, source) is null)
            return (null, null, Result.Failed(level, hash, [CalendarError("calendar_unavailable", $"{source} can't read an index along {along}: it has no {along}.")]));

        // A datetime tied to a date dimension orders its periods by the date table's own calendar.
        var (ordered, rangeError) = cal.Base is not null
            ? LevelRanges(cal.Base, cat.Doc.Attributes[along].Linked?.Attribute ?? along, index.Path)
            : LevelRanges(cal, along, index.Path);
        if (rangeError is not null) return (null, null, Result.Failed(level, hash, [rangeError]));
        var baseAt = ordered.FindIndex(m => m.Member == index.Base);
        if (baseAt < 0)
        {
            var close = ordered.Select(m => (Member: m.Member, Score: Fuzzy.JaroWinkler(Fuzzy.Normalize(index.Base), Fuzzy.Normalize(m.Member))))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Member, StringComparer.Ordinal).Take(3).Select(x => x.Member).ToList();
            return (null, null, Result.Failed(level, hash, [new QisError("mashup", "index_base_unknown", index.Path + ".base",
                $"{index.Base} isn't a member of {along} in the date table.", close)]));
        }

        // Other attributes of the query that are levels of this calendar take their value from the along member.
        var calendarAttrs = cal.Levels.Values.Append(cal.Date).Concat(cal.Base is null ? [] : cal.Base.Levels.Values.Append(cal.Base.Date)).ToHashSet();
        var read = attrs.Where(d => d != along && calendarAttrs.Contains(d)).ToList();
        var readFrom = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var attr in read)
        {
            if (cat.Binding(attr, source) is null)
                return (null, null, Result.Failed(level, hash, [CalendarError("calendar_unavailable", $"{source} can't read {attr} for each member of {along}.")]));
            var groups = PairRows(source, along, attr)
                .Where(r => r[Catalog.ColName(along)] is not null).GroupBy(r => r[Catalog.ColName(along)]!.ToString()!).ToList();
            if (groups.Any(g => g.Select(r => r[Catalog.ColName(attr)]?.ToString()).Distinct().Count() > 1))
                return (null, null, Result.Failed(level, hash, [CalendarError("index_attribute_not_coarser",
                    $"{attr} is in the query beside {along}, but a member of {along} has more than one {attr}; an index can only sit beside a level that is coarser than {along}.")]));
            readFrom[attr] = groups.ToDictionary(g => g.Key, g => g.First()[Catalog.ColName(attr)]);
        }

        // The members the answer shows come from the query's own filters; the base is read without the filters on the calendar, with every other filter kept.
        var counting = applied.Where(f => !f.Names().All(calendarAttrs.Contains)).ToList();
        var shown = Scan(source, [along], applied).Select(r => r[Catalog.ColName(along)]?.ToString())
            .Where(m => m is not null && ordered.Any(o => o.Member == m)).Distinct().Cast<string>().ToList();

        var others = attrs.Where(d => d != along && !read.Contains(d)).ToList();
        var filters = counting.Append(new LeafFilter(along, "equals", [JsonValue.Create(index.Base)!])).ToList();
        var (raw, failure) = ScanSource($"{id}_b", source, others, measures, filters, [], [], null, hash, "$.result_sets");
        if (failure is not null) return (null, null, failure);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var member in shown)
            foreach (var row in raw!.Rows)
            {
                var copy = new Dictionary<string, object?>(row) { [Catalog.ColName(along)] = member };
                foreach (var attr in read) copy[Catalog.ColName(attr)] = readFrom[attr].GetValueOrDefault(member);
                rows.Add(copy);
            }

        var today = TodayFor(cat.Zone);
        var baseRange = ordered[baseAt];
        _resolved.Add(new ResolvedRange(index.Path, Iso(today), Iso(baseRange.Start), Iso(baseRange.End), along, [index.Base], "index"));

        var descriptor = Descriptor(id, source, attrs, measures, rows.Count, false, hash);
        descriptor["x_provenance"] = new JsonObject
        {
            ["spec"] = "mashup", ["spec_version"] = SpecVersion, ["mode"] = "engine", ["request_hash"] = hash,
            ["inputs"] = new JsonArray((JsonNode)raw!.Id), ["engine_level"] = level,
            ["index"] = new JsonObject { ["along"] = along, ["base"] = index.Base },
        };
        return (new ResultSet(id, source, attrs, raw.Covers, descriptor, raw.NativeQuery, rows), [raw], null);
    }
}
