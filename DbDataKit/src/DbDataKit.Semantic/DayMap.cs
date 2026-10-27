using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>
/// Each day of a calendar's date table and its counterpart one unit earlier. It moves a filter on dates back by taking the
/// counterparts of the days the filter selects, so it assumes the counterparts keep the order of the days. A boundary that
/// isn't a day of the table (the 31st of a month the table has no 31st for, a today past its end) moves to the nearest day
/// inside the filter, so the filter selects the same days it did.
/// </summary>
public sealed class DayMap
{
    /// <summary>A date no table has, for a filter that selects no days.</summary>
    private const string Nothing = "0001-01-01";

    private readonly Dictionary<string, string> _earlier;

    /// <summary>The counterpart of each day, as given.</summary>
    public IReadOnlyDictionary<string, string> Earlier => _earlier;
    private readonly string[] _days;   // ISO dates sort as text

    public DayMap(Dictionary<string, string> earlier)
    {
        _earlier = earlier;
        _days = earlier.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The first day on or after the date.</summary>
    private string? Up(string date) => _days.FirstOrDefault(d => string.CompareOrdinal(d, date) >= 0);

    /// <summary>The last day on or before the date.</summary>
    private string? Down(string date) => _days.LastOrDefault(d => string.CompareOrdinal(d, date) <= 0);

    private static LeafFilter Leaf(LeafFilter l, string op, params string[] values) =>
        new(l.Name, op, values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList());

    private LeafFilter None(LeafFilter l) => Leaf(l, "between", Nothing, Nothing);

    /// <summary>The same filter over the earlier days, or a message for a value that has no counterpart.</summary>
    public (LeafFilter? Filter, string? Problem) Shift(LeafFilter l)
    {
        if (_days.Length == 0) return (null, $"The calendar's date table has no days for {l.Name}.");
        var v = l.Values.Select(x => x.GetValue<string>()).ToList();
        switch (l.Operator)
        {
            case "equals" or "not_equals":
                foreach (var d in v)
                    if (!_earlier.ContainsKey(d)) return (null, $"{d} has no earlier counterpart in the calendar's date table, so {l.Name} can't be moved back by it.");
                return (Leaf(l, l.Operator, v.Select(d => _earlier[d]).ToArray()), null);
            case "between":
            {
                var (lo, hi) = (Up(v[0]), Down(v[1]));
                return (lo is null || hi is null || string.CompareOrdinal(lo, hi) > 0 ? None(l) : Leaf(l, "between", _earlier[lo], _earlier[hi]), null);
            }
            case "gt":
            {
                var d = Down(v[0]);
                return (d is null ? Leaf(l, "gte", _earlier[_days[0]]) : Leaf(l, "gt", _earlier[d]), null);
            }
            case "gte":
            {
                var d = Up(v[0]);
                return (d is null ? None(l) : Leaf(l, "gte", _earlier[d]), null);
            }
            case "lt":
            {
                var d = Up(v[0]);
                return (d is null ? Leaf(l, "lte", _earlier[_days[^1]]) : Leaf(l, "lt", _earlier[d]), null);
            }
            case "lte":
            {
                var d = Down(v[0]);
                return (d is null ? None(l) : Leaf(l, "lte", _earlier[d]), null);
            }
            default:   // set, not_set: nothing about the date's value
                return (l, null);
        }
    }
}
