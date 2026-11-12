using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Semantic.Presentation;

namespace DbDataKit.Views;

internal static partial class ResultShaper
{
    private static string GroupColumn(string name) => $"__group_{name}";

    private static readonly Regex Placeholder = new(@"\{([0-9]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static ViewGroup GroupOf(ViewWidget widget, string name, string where) =>
        widget.Groups is not null && widget.Groups.TryGetValue(name, out var group) ? group
        : throw new WidgetProblem(ViewsCodes.UnknownGroup, $"`{name}` is not a group of this document.", $"widgets.{widget.Id}.{where}.group");

    /// <summary>One level of a member as text: the level's own format when it has one, else the value as it is (a year is 2026, never 2,026).</summary>
    private static string LevelText(JsonNode? node, GroupLevel level, ResultField field, string? locale)
    {
        if (node is null) return "(none)";
        if (level.Format is null && ResultTable.Number(node) is { } n && n == Math.Floor(n)) return ((long)n).ToString(CultureInfo.InvariantCulture);
        return ViewFormat.Text(node, level.Format, field.Format, locale);
    }

    /// <summary>The label of a group's member: the template over the levels' texts, or the texts joined by a space.</summary>
    private static string GroupLabel(ViewGroup group, ResultTable table, JsonObject row, string? locale)
    {
        var texts = group.Levels.Select(l => { var f = table.Resolve(l.Field)!; return LevelText(row[f.Name], l, f, locale); }).ToList();
        return group.Template is null
            ? string.Join(' ', texts)
            : Placeholder.Replace(group.Template, m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var n && n >= 1 && n <= texts.Count ? texts[n - 1] : "");
    }

    /// <summary>The members of a group in the order of their levels: each level ascending by value in turn (numbers by value, text by code point), the order the axis shows them in.</summary>
    private static List<string> GroupMembers(ViewGroup group, ResultTable table, IEnumerable<JsonObject> rows, string? locale, bool descending)
    {
        var fields = group.Levels.Select(l => table.Resolve(l.Field)!).ToList();
        var distinct = new List<(List<JsonNode?> Key, string Label)>();
        foreach (var row in rows)
        {
            var key = fields.Select(f => row[f.Name]).ToList();
            if (!distinct.Any(d => d.Key.Zip(key).All(p => JsonNode.DeepEquals(p.First, p.Second)))) distinct.Add((key, GroupLabel(group, table, row, locale)));
        }
        distinct.Sort((a, b) =>
        {
            for (var i = 0; i < a.Key.Count; i++) if (Compare(a.Key[i], b.Key[i]) is var c && c != 0) return c;
            return 0;
        });
        if (descending) distinct.Reverse();
        return [.. distinct.Select(d => d.Label).Distinct()];
    }

    /// <summary>The answer with a column more, for a group's members (the group's name in the column's name): the label of each row's member, or null where the row rolls the group up.</summary>
    private static AnswerRows WithGroupColumn(AnswerRows answer, string column, Func<JsonObject, string?> label, Func<JsonObject, bool> keep, IEnumerable<string>? dropFields = null)
    {
        var result = new JsonObject();
        var descriptor = (JsonObject)answer.Result["descriptor"]!.DeepClone();
        var fields = descriptor["schema"]!["fields"]!.AsArray();
        var drop = (dropFields ?? []).ToHashSet();
        for (var i = fields.Count - 1; i >= 0; i--) if (drop.Contains(fields[i]!["name"]!.GetValue<string>())) fields.RemoveAt(i);
        fields.Add(new JsonObject { ["name"] = column, ["type"] = "string", ["x_kind"] = "attribute", ["x_binding"] = column });
        result["descriptor"] = descriptor;
        var rows = new JsonArray();
        foreach (var source in answer.Table.Rows.Where(keep))
        {
            var copy = new JsonObject();
            foreach (var (key, value) in source) if (!drop.Contains(key)) copy[key] = value?.DeepClone();
            copy[column] = label(source) is { } text ? JsonValue.Create(text) : null;
            rows.Add(copy);
        }
        result["rows"] = rows;
        return new AnswerRows(result, new ResultTable(result)).WithLocale(answer.Locale);
    }

    /// <summary>
    /// A field group in a chart channel (plan 32 DBR52): the channel is **one field**, a column of the answer that holds the member's label (the group's template over its levels), and the members are ordered by the levels in
    /// turn, whatever order the rows arrive in, so a line over a year-and-month group is one continuous line labelled <c>Mar 2026</c> in calendar order. The channel's <c>title</c> and <c>sort</c> are the only keys a group channel has.
    /// </summary>
    private static (AnswerRows Answer, JsonObject Encodings) PrepareChartGroups(ViewWidget widget, AnswerRows answer, JsonObject encodings)
    {
        var next = encodings;
        foreach (var channel in new[] { "x", "color", "column", "row" })
        {
            if (next[channel] is not JsonObject { } spec || spec["group"] is not JsonValue gv || !gv.TryGetValue<string>(out var name)) continue;
            var group = GroupOf(widget, name, $"encodings.{channel}");
            foreach (var level in group.Levels) if (answer.Table.Resolve(level.Field) is null) throw Missing(widget, answer, level.Field, $"encodings.{channel}.group");
            var column = GroupColumn(name);
            var descending = spec["sort"]?.GetValue<string>() == "descending";
            var members = GroupMembers(group, answer.Table, answer.Table.Rows, answer.Locale, descending);
            var table = answer.Table;
            answer = WithGroupColumn(answer, column, row => GroupLabel(group, table, row, answer.Locale), _ => true);
            if (ReferenceEquals(next, encodings)) next = (JsonObject)encodings.DeepClone();
            next[channel] = new JsonObject
            {
                ["field"] = column, ["type"] = "ordinal", ["title"] = spec["title"]?.GetValue<string>() ?? group.Label ?? name,
                ["sort"] = new JsonArray(members.Select(m => (JsonNode)m).ToArray()),
            };
        }
        return (answer, next);
    }

    /// <summary>
    /// Field groups on a matrix's axes (DBR52). <b>Combined</b> (<c>{group: name}</c>): the levels become one axis attribute whose member is the label (the answer's rows get the column; a row that rolls only some of the levels up is
    /// not a total of anything and goes; one that rolls them all up is a total, as any rolled-up attribute). <b>Details</b> (<c>{group: name, show: always}</c> in <c>rows</c>): the levels stay attributes and come after the row's own
    /// fields, so they are extra header columns of every row; the rows the request made by rolling only the details up are the row's own total over its details and go, the total row (the rows rolled up too) stays.
    /// </summary>
    private static (AnswerRows Answer, JsonObject Encodings) PrepareMatrixGroups(ViewWidget widget, AnswerRows answer, JsonObject encodings)
    {
        if (!new[] { "rows", "columns" }.Any(a => (encodings[a] as JsonArray ?? []).OfType<JsonObject>().Any(o => o["group"] is not null))) return (answer, encodings);
        var table = answer.Table;
        var next = (JsonObject)encodings.DeepClone();
        var combined = new List<(ViewGroup Group, string Column, List<ResultField> Fields)>();
        var details = new List<List<ResultField>>();
        var baseRows = new List<ResultField>();
        foreach (var axis in new[] { "rows", "columns" })
        {
            if (encodings[axis] is not JsonArray entries) continue;
            var list = new JsonArray();
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is JsonObject o && o["group"] is JsonValue gv && gv.TryGetValue<string>(out var name))
                {
                    // the details of a row that expands on request are not in the answer: the rows are asked for without them (DBR55)
                    if (o["show"]?.GetValue<string>() == "drill") continue;
                    var group = GroupOf(widget, name, $"encodings.{axis}[{i}]");
                    var fields = group.Levels.Select(l => table.Resolve(l.Field) ?? throw Missing(widget, answer, l.Field, $"encodings.{axis}[{i}].group")).ToList();
                    if (o["show"] is not null)
                    {
                        details.Add(fields);
                        foreach (var (level, field) in group.Levels.Zip(fields)) list.Add(level.Format is null ? JsonValue.Create(level.Field) : new JsonObject { ["field"] = level.Field, ["format"] = level.Format.DeepClone() });
                    }
                    else { combined.Add((group, GroupColumn(name), fields)); list.Add(GroupColumn(name)); }
                }
                else
                {
                    list.Add(entries[i]?.DeepClone());
                    if (axis == "rows" && Ref(entries[i]) is { } plain && table.Resolve(plain) is { } f) baseRows.Add(f);
                }
            }
            next[axis] = list;
        }

        string Binding(ResultField f) => f.Binding ?? f.Name;
        bool Keep(JsonObject row)
        {
            var rolled = table.RolledUp(row);
            foreach (var (_, _, fields) in combined)
            {
                var n = fields.Count(f => rolled.Contains(Binding(f)));
                if (n != 0 && n != fields.Count) return false;
            }
            foreach (var fields in details)
                if (fields.All(f => rolled.Contains(Binding(f))) && !(baseRows.Count == 0 || baseRows.All(f => rolled.Contains(Binding(f))))) return false;
                else if (fields.Any(f => rolled.Contains(Binding(f))) && !fields.All(f => rolled.Contains(Binding(f)))) return false;
            return true;
        }

        // one new column for each combined group, each row's member or nothing where the group is rolled up
        foreach (var (group, column, fields) in combined)
        {
            var current = answer.Table;
            var dropNames = fields.Select(f => f.Name);
            var isLast = ReferenceEquals(combined[^1].Group, group);
            answer = WithGroupColumn(answer, column, row => current.RolledUp(row).Contains(Binding(fields[0])) ? null : GroupLabel(group, current, row, answer.Locale), isLast ? Keep : _ => true, dropNames);
            RewriteRolled(answer, fields, column);
        }
        if (combined.Count == 0)
        {
            var kept = table.Rows.Where(Keep).ToList();
            if (kept.Count != table.Rows.Count)
            {
                var result = new JsonObject { ["descriptor"] = answer.Result["descriptor"]!.DeepClone(), ["rows"] = new JsonArray(kept.Select(r => (JsonNode)r.DeepClone()).ToArray()) };
                answer = new AnswerRows(result, new ResultTable(result)).WithLocale(answer.Locale);
            }
        }
        return (answer, next);
    }

    /// <summary>In the descriptor of the totals, the levels' bindings that a row rolls up together stand for the one attribute that replaced them.</summary>
    private static void RewriteRolled(AnswerRows answer, List<ResultField> fields, string column)
    {
        if (answer.Result["descriptor"]?["x_totals"] is not JsonObject totals) return;
        var names = fields.Select(f => f.Binding ?? f.Name).ToHashSet();
        foreach (var level in (totals["levels"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (level["rolled_up"] is not JsonArray rolled) continue;
            var has = rolled.Select(n => n!.GetValue<string>()).ToList();
            if (!names.All(has.Contains)) continue;
            var replaced = new JsonArray();
            foreach (var n in has) if (!names.Contains(n)) replaced.Add(n);
            replaced.Add(column);
            level["rolled_up"] = replaced;
        }
    }
}

internal static partial class ResultShaper
{
    /// <summary>Whether a matrix has a row entry that expands on request (<c>{group, show: drill}</c>, plan 32 DBR55).</summary>
    internal static bool HasDrillGroup(ViewWidget widget) =>
        widget.Type == "matrix" && widget.Raw["encodings"]?["rows"] is JsonArray rows && rows.OfType<JsonObject>().Any(o => o["group"] is not null && o["show"]?.GetValue<string>() == "drill");

    /// <summary>The widget as it is run to read the details of an expanded row: its <c>show: drill</c> groups say <c>always</c>, so the matrix shows the details as extra header columns of the rows, and it has no totals (the request has none).</summary>
    internal static ViewWidget AsDetails(ViewWidget widget)
    {
        var raw = (JsonObject)widget.Raw.DeepClone();
        (raw["encodings"] as JsonObject)?.Remove("totals");
        foreach (var o in (raw["encodings"]?["rows"] as JsonArray ?? []).OfType<JsonObject>())
            if (o["group"] is not null && o["show"]?.GetValue<string>() == "drill") o["show"] = "always";
        return widget with { Raw = raw };
    }

    /// <summary>The rows' own fields of a matrix (the entries that are not groups), in order.</summary>
    private static List<string> OwnRowFields(JsonObject encodings) =>
        (encodings["rows"] as JsonArray ?? []).Where(e => !(e is JsonObject o && o["group"] is not null)).Select(e => Ref(e) ?? "").ToList();

    /// <summary>
    /// Marks each row of a rendered matrix that can expand (DBR55): <c>expandable</c> and <c>key</c>, the typed member values of the row's own fields as the answer gave them (what a request for its details filters by). Called
    /// right after the engine's rendering, while the labels are still the members' own text.
    /// </summary>
    private static void MarkExpandable(JsonObject rendered, ViewWidget widget, AnswerRows answer)
    {
        var encodings = widget.Raw["encodings"] as JsonObject ?? [];
        var fields = OwnRowFields(encodings).Select(n => answer.Table.Resolve(n)).ToList();
        if (fields.Count == 0 || fields.Any(f => f is null) || rendered["rows"] is not JsonArray rows) return;
        string Key(IEnumerable<string> labels) => string.Join('\u001f', labels);
        var keys = new Dictionary<string, JsonArray>();
        foreach (var row in answer.Table.Rows.Where(answer.Table.IsDetail))
        {
            var k = Key(fields.Select(f => ResultTable.Label(row[f!.Name])));
            if (!keys.ContainsKey(k)) keys[k] = new JsonArray(fields.Select(f => row[f!.Name]?.DeepClone()).ToArray());
        }
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (row["total"]?.GetValue<bool>() == true || row["other"]?.GetValue<bool>() == true) continue;
            var labels = (row["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? "").Take(fields.Count);
            if (keys.TryGetValue(Key(labels), out var key)) { row["expandable"] = true; row["key"] = key; }
        }
    }

    /// <summary>
    /// Puts the details of one expanded row into the base matrix (DBR55): the base row whose <c>key</c> is <paramref name="key"/> becomes <c>expanded</c>, and its <c>children</c> are the rows of the details matrix that begin with the
    /// row's own labels, each with the labels after them and a cell for every column of the base matrix (an empty one where the details have none there).
    /// </summary>
    internal static void AttachDetails(JsonObject baseMatrix, JsonObject details, JsonArray key)
    {
        if (baseMatrix["rows"] is not JsonArray baseRows || details["rows"] is not JsonArray detailRows) return;
        var row = baseRows.OfType<JsonObject>().FirstOrDefault(r => r["key"] is JsonArray k && JsonNode.DeepEquals(k, key));
        if (row is null) return;
        var own = key.Count;
        var labels = (row["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? "").ToList();
        var baseColumns = (baseMatrix["columns"] as JsonArray ?? []).OfType<JsonObject>().Select(c => string.Join('\u001f', (c["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? ""))).ToList();
        var detailColumns = (details["columns"] as JsonArray ?? []).OfType<JsonObject>().Select(c => string.Join('\u001f', (c["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? ""))).ToList();
        var children = new JsonArray();
        foreach (var d in detailRows.OfType<JsonObject>())
        {
            if (d["total"]?.GetValue<bool>() == true || d["other"]?.GetValue<bool>() == true) continue;
            var dl = (d["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? "").ToList();
            if (dl.Count <= own || !dl.Take(own).SequenceEqual(labels.Take(own))) continue;
            var cells = new JsonArray();
            foreach (var column in baseColumns)
            {
                var at = detailColumns.IndexOf(column);
                cells.Add(at >= 0 && d["cells"] is JsonArray dc && at < dc.Count && dc[at] is { } cell ? cell.DeepClone() : new JsonObject { ["v"] = null, ["text"] = "" });
            }
            children.Add(new JsonObject { ["labels"] = new JsonArray(dl.Skip(own).Select(l => (JsonNode)l).ToArray()), ["cells"] = cells });
        }
        row["expanded"] = true;
        row["children"] = children;
    }
}
