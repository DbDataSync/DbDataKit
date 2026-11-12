using System.Text.Json.Nodes;
using DbDataKit.Semantic.Presentation;

namespace DbDataKit.Views;

internal static partial class ResultShaper
{
    /// <summary>The most distinct values a <c>many: list</c> cell names before it says "…".</summary>
    internal const int ListCap = 5;

    /// <summary>
    /// An attribute entry of a matrix's <c>values</c> (plan 32 DBR51): where it is in the list, its entry, the field of the answer, the synthetic measure the engine draws in its place (so the grid, its labels, its totals
    /// and its column limit are the engine's own), and the distinct values it has in each cell, in the order the answer gave them.
    /// </summary>
    internal sealed record CellAttribute(int Index, JsonObject Entry, ResultField Field, string Synthetic);

    internal sealed class CellPlan(List<CellAttribute> items, int entries, int rowAttributes, int columnAttributes, Dictionary<string, Dictionary<string, List<JsonNode?>>> values)
    {
        public List<CellAttribute> Items { get; } = items;
        /// <summary>How many entries <c>values</c> has (the engine draws this many cells for each column member).</summary>
        public int Entries { get; } = entries;
        public int RowAttributes { get; } = rowAttributes;
        public int ColumnAttributes { get; } = columnAttributes;
        /// <summary>The cell (the row members and the column members) to the synthetic measure to the distinct values of the attribute there.</summary>
        public Dictionary<string, Dictionary<string, List<JsonNode?>>> Values { get; } = values;
    }

    private const char KeySeparator = '\u001f';

    /// <summary>
    /// The one-row rule's first half (the answer's side): the request asked for the attribute at the cells' grain and rolled it up last (<see cref="DeriveRequest.CellAttributes"/>), so the answer holds, for each cell, the rows
    /// that name its values (the detail rows) and the cell's own row with the attribute rolled up, whose measures are the engine's (never added across the values here). This reads the values from the detail rows, drops them,
    /// and gives the engine a result of the cell rows (and the total rows) in which each attribute entry is a synthetic measure it can lay out.
    /// </summary>
    private static (AnswerRows Answer, JsonObject Encodings, CellPlan? Plan) PrepareCellAttributes(ViewWidget widget, AnswerRows answer, JsonObject encodings)
    {
        var values = encodings["values"] as JsonArray ?? [];
        if (!values.OfType<JsonObject>().Any(o => o["attribute"] is not null)) return (answer, encodings, null);
        var table = answer.Table;
        var names = DeriveRequest.CellAttributes(encodings);

        ResultField Field(string name, string where) => table.Resolve(name) ?? throw Missing(widget, answer, name, where);
        var items = new List<CellAttribute>();
        for (var i = 0; i < values.Count; i++)
            if (values[i] is JsonObject o && o["attribute"]?.GetValue<string>() is { } attribute)
                items.Add(new CellAttribute(i, o, Field(attribute, $"encodings.values[{i}].attribute"), $"__cell_{i}"));

        List<ResultField> Axis(string axis) => (encodings[axis] as JsonArray ?? []).Select((n, i) => Field(Ref(n)!, $"encodings.{axis}[{i}]")).ToList();
        var rowFields = Axis("rows");
        var columnFields = Axis("columns");
        string CellKey(JsonObject row) => string.Join(KeySeparator, rowFields.Concat(columnFields).Select(f => ResultTable.Label(row[f.Name])));

        var rolledAway = names.Select(n => Field(n, "encodings.values")).Select(f => f.Binding ?? f.Name).ToHashSet();
        if (rolledAway.Count > 0 && table.LevelField is null)
            throw new WidgetProblem("totals_missing", "The answer has no rows where the attribute of the cell area is rolled up. Request the query with totals.", $"widgets.{widget.Id}.encodings.values");

        // the values of each cell: the detail rows say them (a row of the result with nothing rolled up)
        var seen = new Dictionary<string, Dictionary<string, List<JsonNode?>>>();
        foreach (var row in table.Rows.Where(table.IsDetail))
        {
            var key = CellKey(row);
            if (!seen.TryGetValue(key, out var perAttribute)) seen[key] = perAttribute = [];
            foreach (var item in items)
            {
                if (!perAttribute.TryGetValue(item.Synthetic, out var list)) perAttribute[item.Synthetic] = list = [];
                var value = row[item.Field.Name];
                if (!list.Any(v => JsonNode.DeepEquals(v, value))) list.Add(value?.DeepClone());
            }
        }

        // the rows the engine is given: the cell rows and the total rows, never the detail rows that name the values
        var result = new JsonObject();
        var descriptor = (JsonObject)answer.Result["descriptor"]!.DeepClone();
        var fields = descriptor["schema"]!["fields"]!.AsArray();
        var drop = names.Select(n => Field(n, "encodings.values").Name).ToHashSet();
        for (var i = fields.Count - 1; i >= 0; i--) if (drop.Contains(fields[i]!["name"]!.GetValue<string>())) fields.RemoveAt(i);
        foreach (var item in items)
            fields.Add(new JsonObject { ["name"] = item.Synthetic, ["type"] = "integer", ["x_kind"] = "measure", ["x_binding"] = item.Synthetic, ["x_additivity"] = "additive" });
        if (descriptor["x_totals"] is JsonObject totals)
        {
            static JsonArray Without(JsonNode? list, HashSet<string> names) => new(((list as JsonArray) ?? []).Where(n => !names.Contains(n!.GetValue<string>())).Select(n => (JsonNode)n!.GetValue<string>()).ToArray());
            if (totals["subtotal_by"] is not null) totals["subtotal_by"] = Without(totals["subtotal_by"], rolledAway);
            foreach (var level in (totals["levels"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (level["rolled_up"] is not null) level["rolled_up"] = Without(level["rolled_up"], rolledAway);
                if (level["kept"] is not null) level["kept"] = Without(level["kept"], rolledAway);
            }
        }
        result["descriptor"] = descriptor;
        var rows = new JsonArray();
        foreach (var source in table.Rows)
        {
            var rolled = table.RolledUp(source);
            if (rolledAway.Count > 0 && !rolledAway.IsSubsetOf(rolled)) continue;              // a detail row, or a roll-up that kept the attribute
            var copy = new JsonObject();
            foreach (var (key, value) in source) if (!drop.Contains(key)) copy[key] = value?.DeepClone();
            var cell = rolled.Count == rolledAway.Count;                                      // nothing but the cell area's attributes is rolled up: the cell's own row
            var counts = cell && seen.TryGetValue(CellKey(source), out var found) ? found : null;
            // a number as parsed JSON, which is how the engine's renderer reads every number of a result
            foreach (var item in items) copy[item.Synthetic] = cell ? JsonNode.Parse((counts is not null && counts.TryGetValue(item.Synthetic, out var list) ? list.Count : 0).ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
            rows.Add(copy);
        }
        result["rows"] = rows;
        var prepared = new AnswerRows(result, new ResultTable(result)).WithLocale(answer.Locale);

        var next = (JsonObject)encodings.DeepClone();
        var list2 = new JsonArray();
        for (var i = 0; i < values.Count; i++)
        {
            if (items.FirstOrDefault(it => it.Index == i) is { } item)
                list2.Add(new JsonObject { ["field"] = item.Synthetic, ["label"] = item.Entry["label"]?.GetValue<string>() ?? item.Field.Binding ?? item.Field.Name, ["empty"] = "blank" });
            else list2.Add(values[i]?.DeepClone());
        }
        next["values"] = list2;
        return (prepared, next, new CellPlan(items, values.Count, rowFields.Count, columnFields.Count, seen));
    }

    /// <summary>
    /// The one-row rule's second half: each attribute cell of the rendered matrix is read from the values its cell has. Exactly one value shows (as text, an icon chosen by it, or a badge); several show by the entry's
    /// <c>many</c> policy (<c>mixed</c> is the default: a marker and the values for a tooltip; <c>first</c> and <c>last</c> in the answer's order; <c>count</c>; <c>list</c>, comma-joined and capped); none is empty. A total row or
    /// column, and an Other column, leave an attribute cell empty: there is no one value to show.
    /// </summary>
    private static void FillCellAttributes(JsonObject rendered, CellPlan plan, AnswerRows answer)
    {
        if (rendered["rows"] is not JsonArray rows || rendered["columns"] is not JsonArray columns) return;
        var items = plan.Items.ToDictionary(i => i.Index);
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (row["cells"] is not JsonArray cells) continue;
            var rowLabels = (row["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? "").Take(plan.RowAttributes);
            var rowIsTotal = row["total"]?.GetValue<bool>() == true || row["other"]?.GetValue<bool>() == true;
            for (var c = 0; c < cells.Count; c++)
            {
                if (!items.TryGetValue(c % plan.Entries, out var item) || cells[c] is not JsonObject cell) continue;
                var column = c < columns.Count ? columns[c] as JsonObject : null;
                var empty = rowIsTotal || column is null || column["total"]?.GetValue<bool>() == true || column["other"]?.GetValue<bool>() == true;
                var key = string.Join(KeySeparator, rowLabels.Concat((column?["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? "").Take(plan.ColumnAttributes)));
                var distinct = !empty && plan.Values.TryGetValue(key, out var found) && found.TryGetValue(item.Synthetic, out var list) ? list : [];
                Describe(cell, item.Entry, item.Field, distinct, answer.Locale, !empty);
            }
        }
    }

    /// <summary>Writes an attribute cell: its text and, by the entry, its icon, badge, mixed marker, values, link.</summary>
    private static void Describe(JsonObject cell, JsonObject entry, ResultField field, List<JsonNode?> distinct, string? locale, bool allowLink)
    {
        var format = entry["format"];
        string TextOf(JsonNode? value) => value is null ? "" : ViewFormat.Text(value, format, field.Format, locale);
        string Key(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
        var display = entry["display"]?.GetValue<string>() ?? (entry["icons"] is not null ? "icon" : "text");
        var many = entry["many"]?.GetValue<string>() ?? "mixed";
        cell.Remove("mixed"); cell.Remove("values"); cell.Remove("icon"); cell.Remove("badge"); cell.Remove("link");
        cell["v"] = null;
        cell["text"] = "";
        if (distinct.Count == 0) return;

        JsonNode? shown = null;
        if (distinct.Count == 1) shown = distinct[0];
        else
            switch (many)
            {
                case "first": shown = distinct[0]; break;
                case "last": shown = distinct[^1]; break;
                case "count":
                    cell["v"] = distinct.Count;
                    cell["text"] = ViewFormat.Number(distinct.Count, null, null, locale);
                    break;
                case "list":
                    cell["text"] = string.Join(", ", distinct.Take(ListCap).Select(TextOf)) + (distinct.Count > ListCap ? ", …" : "");
                    cell["values"] = new JsonArray(distinct.Take(ListCap * 4).Select(d => (JsonNode)TextOf(d)).ToArray());
                    break;
                default:
                    cell["text"] = "Mixed";
                    cell["mixed"] = true;
                    cell["values"] = new JsonArray(distinct.Take(ListCap * 4).Select(d => (JsonNode)TextOf(d)).ToArray());
                    break;
            }
        if (shown is not null || distinct.Count == 1)
        {
            cell["text"] = TextOf(shown);
            if (display == "icon" && entry["icons"] is JsonObject icons && icons[Key(shown)] is JsonValue icon && icon.TryGetValue<string>(out var name)) cell["icon"] = name;
            else if (display == "badge") cell["badge"] = true;
        }
        if (allowLink && entry["link"] is JsonValue link && link.TryGetValue<string>(out var linkName) && cell["text"]!.GetValue<string>().Length > 0) cell["link"] = linkName;
    }

    /// <summary>The same presentation for a table column that says it (<c>display</c>, <c>icons</c>, <c>link</c>): a table row has one value, so there is nothing to reduce.</summary>
    private static void DecorateColumn(JsonObject cell, JsonObject column, JsonNode? source)
    {
        if (source is null || (column["display"] is null && column["icons"] is null && column["link"] is null)) return;
        string Key(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
        var display = column["display"]?.GetValue<string>() ?? (column["icons"] is not null ? "icon" : "text");
        if (display == "icon" && column["icons"] is JsonObject icons && icons[Key(source)] is JsonValue icon && icon.TryGetValue<string>(out var name)) cell["icon"] = name;
        else if (display == "badge") cell["badge"] = true;
        if (column["link"] is JsonValue link && link.TryGetValue<string>(out var linkName) && cell["text"]!.GetValue<string>().Length > 0) cell["link"] = linkName;
    }
}
