using System.Text.Json.Nodes;
using DbDataKit.Semantic.Presentation;

namespace DbDataKit.Views;

internal static partial class ResultShaper
{
    /// <summary>One level of a chart's nested <c>x</c>: the field, its format, and its sort (<c>sort_key</c> by default is the rows' own ascending order; see <see cref="DrawNestedAxis"/>).</summary>
    internal sealed record AxisLevel(string Field, JsonNode? Format, string? SortBy, bool Descending, string? Title);

    /// <summary>The levels of a nested <c>x</c>, outermost first (at least two; a list of one is the plain channel).</summary>
    internal sealed class NestedLevels(List<AxisLevel> levels)
    {
        public List<AxisLevel> Levels { get; } = levels;
        public AxisLevel Inner => Levels[^1];
        public AxisLevel Outer => Levels[0];

        /// <summary>What the engine is given for a level: the field and its scale type. The format, the sort and the title are drawn here, after the engine has resolved the field.</summary>
        public JsonObject EngineChannel(AxisLevel level, ResultTable table) => new() { ["field"] = level.Field, ["type"] = ScaleType(table, level.Field) };
    }

    private static NestedLevels? ReadNestedLevels(JsonObject encodings)
    {
        if (encodings["x"] is not JsonArray { Count: > 1 } list) return null;
        var levels = new List<AxisLevel>();
        foreach (var node in list)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var field)) levels.Add(new AxisLevel(field, null, null, false, null));
            else if (node is JsonObject o && o["field"] is JsonValue fv && fv.TryGetValue<string>(out var name))
            {
                var sort = o["sort"] as JsonObject;
                levels.Add(new AxisLevel(name, o["format"]?.DeepClone(), sort?["by"]?.GetValue<string>(), sort?["dir"]?.GetValue<string>() == "desc", o["title"]?.GetValue<string>()));
            }
        }
        return levels.Count > 1 ? new NestedLevels(levels) : null;
    }

    /// <summary>
    /// Draws a nested axis (plan 32 DBR42): the rendered chart's <c>x</c> is the innermost level and its <c>column</c> the outer levels (a facet the renderer writes; an author's <c>facet</c> stays refused), and it says
    /// <c>nested_axis: true</c> so the client draws the outer labels as group headers below the inner ones. The members of a level are written as the level's format reads them, in the level's order: ascending by value (a date,
    /// a number, the text) unless the level sorts by <c>sort_key</c> (the order the rows arrived in, which the request's <c>order</c> made), <c>label</c> (the text as shown) or a measure (the rows' order, which the request
    /// made by that measure), whatever order the rows happen to arrive in otherwise; <c>desc</c> reverses it. With three levels the two outer ones are one column of labels joined by &quot; / &quot;. More than
    /// <see cref="DeriveRequest.LeafLimit"/> member combinations are cut, the first ones kept, and the chart says so in its <c>notice</c>.
    /// </summary>
    private static void DrawNestedAxis(ViewWidget widget, JsonObject rendered, AnswerRows answer, NestedLevels nested)
    {
        if (rendered["vega_lite"] is not JsonObject vega || vega["encoding"] is not JsonObject encoding || vega["data"]?["values"] is not JsonArray valueArray) return;
        var rows = valueArray.OfType<JsonObject>().ToList();
        var n = nested.Levels.Count;
        var columns = new string[n];
        var fields = new ColumnInfo[n];
        for (var i = 0; i < n; i++)
        {
            var level = nested.Levels[i];
            var field = answer.Table.Resolve(level.Field) ?? throw Missing(widget, answer, level.Field, $"encodings.x[{i}]");
            columns[i] = field.Name;
            fields[i] = new ColumnInfo(field.Format, field.Binding ?? level.Field);
        }

        // the leaf cap: the first member combinations in the order the rows came, the others dropped
        string Leaf(JsonObject r) => string.Join("\u0001", columns.Select(c => r[c]?.ToJsonString() ?? "null"));
        var leaves = new List<string>();
        var seen = new HashSet<string>();
        foreach (var r in rows) if (seen.Add(Leaf(r))) leaves.Add(Leaf(r));
        if (leaves.Count > DeriveRequest.LeafLimit)
        {
            var keep = leaves.Take(DeriveRequest.LeafLimit).ToHashSet();
            rows = rows.Where(r => keep.Contains(Leaf(r))).ToList();
            valueArray.Clear();
            foreach (var r in rows) valueArray.Add(r);
            rendered["notice"] = $"Showing the first {DeriveRequest.LeafLimit} of {leaves.Count} member combinations on the axis.";
        }

        // each level's members, in the level's order
        var order = new List<List<JsonNode?>>();
        var labelOf = new List<Func<JsonNode?, string>>();
        for (var i = 0; i < n; i++)
        {
            var level = nested.Levels[i];
            var catalog = fields[i].Catalog;
            string Text(JsonNode? raw) => raw is null ? "(none)" : level.Format is null ? ResultTable.Label(raw) : Label(raw, level.Format, catalog, answer.Locale);
            labelOf.Add(Text);
            var members = new List<JsonNode?>();
            foreach (var r in rows) { var raw = r[columns[i]]; if (!members.Any(m => JsonNode.DeepEquals(m, raw))) members.Add(raw?.DeepClone()); }
            switch (level.SortBy)
            {
                case "sort_key": break;
                case "label": members.Sort((a, b) => string.CompareOrdinal(Text(a), Text(b))); break;
                case null: members.Sort(Compare); break;
                default: break; // a measure: the rows' order is the request's order by that measure
            }
            if (level.Descending) members.Reverse();
            order.Add(members);
        }
        int Rank(int level, JsonNode? raw) => order[level].FindIndex(m => JsonNode.DeepEquals(m, raw));

        // the x: the innermost level's labels; the column: the outer levels' labels joined, ordered by each level in turn
        var xKey = $"{columns[n - 1]}__x";
        var outerKey = $"{columns[0]}__x_outer";
        string OuterLabel(JsonObject r) => string.Join(" / ", Enumerable.Range(0, n - 1).Select(i => labelOf[i](r[columns[i]])));
        foreach (var r in rows)
        {
            r[xKey] = labelOf[n - 1](r[columns[n - 1]]);
            r[outerKey] = OuterLabel(r);
        }
        IOrderedEnumerable<JsonObject> ordered = rows.OrderBy(r => Rank(0, r[columns[0]]));
        for (var i = 1; i < n - 1; i++) { var level = i; ordered = ordered.ThenBy(r => Rank(level, r[columns[level]])); }
        var outerLabels = ordered.Select(r => r[outerKey]!.GetValue<string>()).Distinct().ToList();
        var xLabels = order[n - 1].Select(m => labelOf[n - 1](m)).Distinct().ToList();

        var x = encoding["x"] as JsonObject ?? [];
        x["field"] = xKey;
        x["type"] = "ordinal";
        x["sort"] = new JsonArray(xLabels.Select(l => (JsonNode)l).ToArray());
        x["title"] = nested.Inner.Title ?? fields[n - 1].Title;
        encoding["x"] = x;
        encoding["column"] = new JsonObject
        {
            ["field"] = outerKey,
            ["type"] = "ordinal",
            ["sort"] = new JsonArray(outerLabels.Select(l => (JsonNode)l).ToArray()),
            ["title"] = string.Join(" / ", Enumerable.Range(0, n - 1).Select(i => nested.Levels[i].Title ?? fields[i].Title)),
        };
        rendered["nested_axis"] = true;
    }

    private readonly record struct ColumnInfo(JsonObject? Catalog, string Title);
}
