using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DbDataKit.Views;

/// <summary>What the check of an agent's view says: whether it may be shown, and, when not, a message the model can act on.</summary>
public sealed record AnswerViewCheck(bool Ok, string Message, string? Kind = null);

/// <summary>
/// The check of a view an agent returns in an answer (plan 27a AG22, plan 27 section 3.17): the same closed subset a dashboard's widgets are read by (<see cref="ViewsLoader.LoadWidgets"/>: the encodings, the closed Vega-Lite subset of a chart with no data, transform, aggregate or
/// expression in it) plus what an answer adds: the data is <b>inline</b> (<c>columns</c> and <c>rows</c> of the answer, never fetched), the widget's query is the reference to it (<c>mode: explicit, ref: answer</c>, or none to write), every field the widget encodes is a column of the
/// data, the kind is one the agent's definition allows (<c>output:</c>), and no value anywhere is an address. Links in an answer's <i>text</i> are text: the suite shows them and never fetches them, so nothing here looks at text outside a view.
/// </summary>
public static class AgentAnswerViews
{
    public const string ResultName = "answer";

    public const int MaximumRows = 2_000;

    public const int MaximumColumns = 50;

    // a scheme, a protocol-relative address or a host written out: nothing an answer's view carries is fetched, so none of it is allowed
    private static readonly Regex Address = new(@"(^|[\s'""(<])((https?|ftp|ftps|file|data|javascript|ws|wss):|//[A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The widget type of a view kind an agent may return: a table, a matrix or a chart (<c>vega-lite</c>).</summary>
    public static string WidgetType(string kind) => kind == "vega-lite" ? "chart" : kind;

    public static string KindOf(string widgetType) => widgetType == "chart" ? "vega-lite" : widgetType;

    /// <param name="view"><c>{ widget: { type, ... }, data: { columns: [..], rows: [[..]] } }</c>.</param>
    /// <param name="allowedKinds">The kinds the agent's definition lists under <c>output:</c> (<c>table</c>, <c>matrix</c>, <c>vega-lite</c>); empty means it may return none.</param>
    public static AnswerViewCheck Check(JsonObject view, IReadOnlyCollection<string> allowedKinds)
    {
        if (view["widget"] is not JsonObject widget) return Refuse("`widget` is missing: give the view as `widget` (a table, matrix or chart widget as a dashboard writes it) and `data` (the values, inline).");
        if (view["data"] is not JsonObject data) return Refuse("`data` is missing: give the values the view shows inline as `{ \"columns\": [..], \"rows\": [[..]] }`. A view never fetches its data.");
        var unknown = view.Select(p => p.Key).Where(k => k is not ("widget" or "data")).ToList();
        if (unknown.Count > 0) return Refuse($"`{unknown[0]}` is not part of a view: only `widget` and `data`.");

        if (FindAddress(widget, "widget") is { } inWidget) return Refuse(inWidget);
        if (FindAddress(data, "data") is { } inData) return Refuse(inData);

        var type = widget["type"]?.GetValue<string>();
        if (type is not ("table" or "matrix" or "chart")) return Refuse($"The widget type `{type}` is not one an answer may show: use `table`, `matrix` or `chart`.");
        var kind = KindOf(type);
        if (!allowedKinds.Contains(kind)) return Refuse(allowedKinds.Count == 0
            ? "This agent may not return views: its definition lists no `output:` kinds."
            : $"This agent may return {string.Join(", ", allowedKinds.Select(k => $"`{k}`"))}, not `{kind}`. Show the values another way.", kind);

        var (columns, dataProblem) = ReadData(data);
        if (dataProblem is not null) return Refuse(dataProblem, kind);

        // the widget is the dashboard's: the same reader and the same closed subset
        var query = widget["query"] as JsonObject;
        if (query is not null && (query["mode"]?.GetValue<string>() != "explicit" || query["ref"]?.GetValue<string>() != ResultName || query.Count != 2))
            return Refuse($"The widget's query is the data that came with it: write `query: {{ mode: explicit, ref: {ResultName} }}` or leave `query` out. A query that asks for data is not run for an answer.", kind);
        var clone = (JsonObject)widget.DeepClone();
        clone["query"] = new JsonObject { ["mode"] = "explicit", ["ref"] = ResultName, ["outputs"] = new JsonArray(columns.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()) };
        var text = new JsonObject { [ResultName] = clone }.ToJsonString();
        var loaded = ViewsLoader.LoadWidgets(text, "view.json");
        if (!loaded.Ok)
            return Refuse(string.Join(" ", loaded.Diagnostics.Where(d => d.Severity == Config.Yaml.ConfigSeverity.Error).Take(3).Select(d => $"{d.Message}{(d.Fix is null ? "" : " " + d.Fix)}")), kind);

        var missing = loaded.Widgets![0].Fields.Where(f => !columns.Contains(f.Field, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
            return Refuse($"The widget encodes `{missing[0].Field}` ({missing[0].Channel}), which is not a column of `data`. The columns are: {string.Join(", ", columns)}.", kind);
        return new AnswerViewCheck(true, "The view is shown.", kind);
    }

    private static AnswerViewCheck Refuse(string message, string? kind = null) => new(false, "The view was not shown. " + message, kind);

    private static (IReadOnlyList<string> Columns, string? Problem) ReadData(JsonObject data)
    {
        if (data["columns"] is not JsonArray cols || cols.Count == 0 || cols.Any(c => c is not JsonValue v || !v.TryGetValue<string>(out _)))
            return ([], "`data.columns` is a list of column names.");
        if (cols.Count > MaximumColumns) return ([], $"`data.columns` has {cols.Count} columns; a view shows at most {MaximumColumns}.");
        var names = cols.Select(c => c!.GetValue<string>()).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) return ([], "`data.columns` names a column twice.");
        if (data["rows"] is not JsonArray rows) return ([], "`data.rows` is a list of rows, each a list of values in the order of the columns.");
        if (rows.Count > MaximumRows) return ([], $"`data.rows` has {rows.Count} rows; a view shows at most {MaximumRows}. Summarise first.");
        for (var i = 0; i < rows.Count; i++)
            if (rows[i] is not JsonArray row || row.Count != names.Count) return ([], $"`data.rows[{i}]` has {(rows[i] as JsonArray)?.Count.ToString() ?? "no"} values for {names.Count} columns.");
        return (names, null);
    }

    private static string? FindAddress(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key is "url" or "href" or "src" or "uri" or "$schema") return $"`{path}.{key}` is an address. A view is made of its inline data only; nothing is fetched.";
                    if (FindAddress(value, $"{path}.{key}") is { } found) return found;
                }
                return null;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (FindAddress(a[i], $"{path}[{i}]") is { } found) return found;
                return null;
            case JsonValue v when v.TryGetValue<string>(out var s) && Address.IsMatch(s):
                return $"`{path}` has an address in it (`{(s.Length > 60 ? s[..60] + "..." : s)}`). A view is made of its inline data only and the suite fetches nothing: put the values in `data` and take the address out. A link belongs in the answer's text.";
            default:
                return null;
        }
    }
}
