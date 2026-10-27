using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    /// <summary>The most members of the <c>along</c> attribute a window scan will take: each is a scan of its own.</summary>
    private const int MaxWindowMembers = 60;

    /// <summary>
    /// A measure aggregated over a window of calendar members around each member of <c>along</c>: year to date at each month, or the trailing twelve months. A distinct count or
    /// any measure that can't be added is right because the window is asked of the source for each member, one scan per member, and relabeled to that member. Members are counted
    /// in the date table's own order, so a member with no fact rows still counts, and a window that runs off either end covers the members that exist. The filters on the
    /// calendar (the members shown) pick which members to show, not which to count, so they are left out of the scan; every other filter stays. Other attributes of the query that
    /// are coarser levels of the same calendar (the fiscal year beside the fiscal month) are read from the date table for each member, not grouped in the scan, since a window may cross them.
    /// </summary>
    private (ResultSet? Accumulated, List<ResultSet>? Raw, Result? Failure) WindowScan(
        string id, string source, List<string> attrs, List<string> measures, List<Filter> applied, WindowSpec window, string hash)
    {
        var along = window.Along;
        var cal = cat.PeriodOf(along)?.Calendar;
        if (cal is null)
            return (null, null, Result.Failed(level, hash, [CalendarError("transform_needs_time", $"window needs a period attribute in along: {along} isn't a level of a calendar.")]));
        if (cat.Binding(along, source) is null || (window.StartPeriod is not null && cat.Binding(window.StartPeriod, source) is null))
            return (null, null, Result.Failed(level, hash, [CalendarError("calendar_unavailable", $"{source} can't count a window along {along}: it needs {along}{(window.StartPeriod is null ? "" : $" and {window.StartPeriod}")}.")]));

        // A datetime tied to a date dimension orders its periods by the date table's own calendar.
        var (ordered, rangeError) = cal.Base is not null
            ? LevelRanges(cal.Base, cat.Doc.Attributes[along].Linked?.Attribute ?? along, "$.query.measures")
            : LevelRanges(cal, along, "$.query.measures");
        if (rangeError is not null) return (null, null, Result.Failed(level, hash, [rangeError]));
        var names = ordered.Select(m => m.Member).ToList();
        var position = names.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);

        // Each member's enclosing period, for a window that starts at the beginning of one.
        var enclosing = new Dictionary<string, string>();
        if (window.StartPeriod is not null)
        {
            var groups = PairRows(source, along, window.StartPeriod)
                .Where(r => r[Catalog.ColName(along)] is not null && r[Catalog.ColName(window.StartPeriod)] is not null)
                .GroupBy(r => r[Catalog.ColName(along)]!.ToString()!).ToList();
            if (groups.Any(g => g.Select(r => r[Catalog.ColName(window.StartPeriod)]!.ToString()).Distinct().Count() > 1))
                return (null, null, Result.Failed(level, hash, [CalendarError("window_period_not_coarser",
                    $"{window.StartPeriod} is not a coarser level than {along}: a member of {along} falls in more than one of its members.")]));
            foreach (var g in groups) enclosing[g.Key] = g.First()[Catalog.ColName(window.StartPeriod)]!.ToString()!;
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
                return (null, null, Result.Failed(level, hash, [CalendarError("window_attribute_not_coarser",
                    $"{attr} is in the query beside {along}, but a member of {along} has more than one {attr}; a window can only sit beside a level that is coarser than {along}.")]));
            readFrom[attr] = groups.ToDictionary(g => g.Key, g => g.First()[Catalog.ColName(attr)]);
        }

        // The members the answer shows, and the filters that count rather than show.
        var counting = applied.Where(f => !f.Names().All(calendarAttrs.Contains)).ToList();
        var shown = Scan(source, [along], applied).Select(r => r[Catalog.ColName(along)]?.ToString()).Where(m => m is not null && position.ContainsKey(m)).Distinct().Cast<string>().ToList();
        if (shown.Count > MaxWindowMembers)
            return (null, null, Result.Failed(level, hash, [CalendarError("window_too_many_periods",
                $"{shown.Count} {along} members are shown and a window is a scan for each; narrow the time range to {MaxWindowMembers} or fewer.")]));

        var others = attrs.Where(d => d != along && !read.Contains(d)).ToList();
        var rows = new List<Dictionary<string, object?>>();
        var raws = new List<ResultSet>();
        var k = 0;
        foreach (var member in shown)
        {
            var at = position[member];
            var first = window.StartPeriod is null
                ? Math.Max(0, at + window.StartOffset)
                : enclosing.TryGetValue(member, out var period)
                    ? Enumerable.Range(0, at + 1).First(j => enclosing.TryGetValue(names[j], out var p) && p == period)
                    : at;
            var last = Math.Min(names.Count - 1, at + window.EndOffset);
            if (last < first) continue;                       // the window lies wholly before the data: nothing to count
            var inWindow = names.GetRange(first, last - first + 1).Select(m => (JsonNode)JsonValue.Create(m)!).ToList();
            var filters = counting.Append(new LeafFilter(along, "equals", inWindow)).ToList();
            var (raw, failure) = ScanSource($"{id}_w{++k}", source, others, measures, filters, [], [], null, hash, "$.result_sets");
            if (failure is not null) return (null, null, failure);
            raws.Add(raw!);
            foreach (var row in raw!.Rows)
            {
                var copy = new Dictionary<string, object?>(row) { [Catalog.ColName(along)] = member };
                foreach (var attr in read) copy[Catalog.ColName(attr)] = readFrom[attr].GetValueOrDefault(member);
                rows.Add(copy);
            }
        }
        if (raws.Count == 0)
        {
            var (empty, emptyFailure) = ScanSource($"{id}_w1", source, others, measures, counting.Append(new LeafFilter(along, "equals", [JsonValue.Create("\u0000no member")!])).ToList(), [], [], null, hash, "$.result_sets");
            if (emptyFailure is not null) return (null, null, emptyFailure);
            raws.Add(empty!);
        }

        var descriptor = Descriptor(id, source, attrs, measures, rows.Count, false, hash);
        descriptor["x_provenance"] = new JsonObject
        {
            ["spec"] = "mashup", ["spec_version"] = SpecVersion, ["mode"] = "engine", ["request_hash"] = hash,
            ["inputs"] = new JsonArray(raws.Select(r => (JsonNode)r.Id).ToArray()), ["engine_level"] = level,
            ["window"] = new JsonObject
            {
                ["along"] = window.Along,
                ["start"] = window.StartPeriod is null ? window.StartOffset : new JsonObject { ["period_start"] = window.StartPeriod },
                ["end"] = window.EndOffset,
            },
        };
        return (new ResultSet(id, source, attrs, raws[0].Covers, descriptor, raws[0].NativeQuery, rows), raws, null);
    }
}
