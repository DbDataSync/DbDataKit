using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>
/// The strict reader of <c>dashboard/1</c> and <c>report/1</c> (plan 32 sections 2 and 3): one core (widgets, parameters, layouts, selectors) and the rules of each kind on top of it. A YAML or JSON file goes in,
/// a <see cref="ViewDocument"/> or diagnostics with the file, line and column come out; nothing throws on a damaged file. <see cref="DashboardLoader"/> and <see cref="ReportLoader"/> are this loader with the
/// kind fixed, so a file of the other kind is refused saying what it is. The JSON Schemas in <c>web/visuals/schemas</c> (<c>core</c>, <c>dashboard</c>, <c>report</c>) are embedded; this loader is the
/// authority and a test keeps the keys, choices and limits of both equal. The checks that need the catalog (an encoded field the model lacks, an invalid request) are made at publish, not here.
/// </summary>
public static partial class ViewsLoader
{
    public const int MaximumWidgets = 100;
    public const int MaximumLayouts = 20;
    public const int MaximumTiles = 200;
    public const int MaximumParameters = 50;
    public const int MaximumLinks = 100;
    public const int MaximumQueryLimit = 10_000;
    public const int MinimumCanvasPixels = 320;
    public const int MaximumCanvasPixels = 8_000;
    public const double DefaultMinScale = 0.5;
    public const double DefaultMaxScale = 4;

    /// <summary>The keys each part accepts, by part name. The JSON Schema files have the same keys; a test keeps them equal.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Keys = new Dictionary<string, string[]>
    {
        ["dashboard"] = ["schemaVersion", "kind", "title", "parameters", "theme", "locale", "refresh", "kiosk", "links", "widgets", "layouts"],
        ["report"] = ["schemaVersion", "kind", "title", "parameters", "theme", "locale", "strict", "limits", "cover", "links", "widgets", "layouts"],
        ["parameter"] = ["name", "type", "for", "many", "required", "default", "label"],
        ["theme"] = ["font"],
        ["widget.metric"] = ["type", "title", "label", "format", "encodings", "query"],
        ["widget.chart"] = ["type", "title", "mark", "encodings", "query", "on_select", "drill", "on_click"],
        ["widget.matrix"] = ["type", "title", "encodings", "query", "on_select", "drill", "on_click"],
        ["widget.table"] = ["type", "title", "encodings", "query", "drill", "on_click"],
        ["widget.text"] = ["type", "title", "text"],
        ["widget.image"] = ["type", "title", "hash", "media_type", "alt", "fit"],
        ["widget.control"] = ["type", "title", "parameter", "control", "label", "values"],
        ["widget.embed"] = ["type", "title", "url", "with", "link_text"],
        ["widget.custom"] = ["type", "title", "component", "params", "links"],
        ["encodings.metric"] = ["value", "compare"],
        ["encodings.chart"] = ["x", "y", "color", "column", "row", "tooltip"],
        ["encodings.matrix"] = ["rows", "columns", "values", "values_on", "totals", "sort", "column_limit"],
        ["encodings.table"] = ["columns", "sort"],
        ["table_column"] = ["field", "label", "format", "align", "width", "alias", "filters", "display", "icons", "link"],
        ["channel"] = ["field", "type", "sort", "stack", "title", "format", "alias", "filters"],
        ["measure_field"] = ["field", "alias", "label", "filters", "format"],
        ["matrix_value"] = ["field", "attribute", "empty", "format", "label", "alias", "filters", "display", "icons", "many", "link"],
        ["x_level"] = ["field", "format", "sort", "title"],
        ["matrix_axis_entry"] = ["field", "format", "sort"],
        ["mark"] = ["type", "tooltip", "point"],
        ["query.derived"] = ["mode", "model", "filters", "segments", "order", "limit", "totals", "timezone", "params"],
        ["query.semantic"] = ["mode", "semantic", "params"],
        ["query.saved"] = ["mode", "saved", "outputs", "params"],
        ["query.store"] = ["mode", "store", "params"],
        ["query.ref"] = ["mode", "ref", "outputs"],
        ["semantic"] = ["model", "request", "sql"],
        ["store"] = ["model", "entity", "columns", "filter", "sort", "limit"],
        ["on_select"] = ["set", "from"],
        ["drill"] = ["to", "with"],
        ["drill.to"] = ["dashboard", "report", "view"],
        ["link"] = ["dashboard", "report", "view", "page", "form", "with", "new", "edit", "as", "then", "lock", "print", "image", "label"],
        ["control.values"] = ["attribute", "limit"],
        ["layout.common"] = ["name", "type", "default", "when", "widget_options"],
        ["layout.reflow"] = ["grid", "tiles"],
        ["layout.canvas"] = ["canvas", "overflow", "min_scale", "max_scale", "tiles"],
        ["layout.flow"] = ["page", "header", "footer", "cover", "sections"],
        ["grid"] = ["columns", "rows"],
        ["canvas"] = ["width", "height", "unit"],
        ["tile.reflow"] = ["widget", "x", "y", "w", "h", "min"],
        ["tile.canvas"] = ["widget", "x", "y", "w", "h", "layer", "min"],
        ["min"] = ["w", "h"],
        ["when"] = ["width", "height", "orientation", "aspect", "medium"],
        ["range"] = ["min", "max"],
        ["page"] = ["size", "orientation", "margins"],
        ["margins"] = ["top", "right", "bottom", "left"],
        ["running_text"] = ["left", "center", "right"],
        ["section"] = ["title", "break", "blocks"],
        ["block.widget"] = ["widget", "height", "keep", "repeat_header"],
        ["block.row"] = ["row", "keep"],
        ["block.pagebreak"] = ["pagebreak"],
        ["row_item"] = ["widget", "span"],
        ["widget_options"] = ["density", "show_title", "legend"],
        ["refresh"] = ["every"],
        ["kiosk"] = ["layout"],
        ["limits"] = ["pages", "rows"],
        ["cover"] = ["parameters"],
    };

    /// <summary>The values of each choice, by name.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Choices = new Dictionary<string, string[]>
    {
        ["kind"] = ["dashboard", "report"],
        ["parameterType"] = ["string", "integer", "number", "boolean"],
        ["font"] = ["sans", "serif", "mono"],
        ["locale"] = ["en", "de", "fr", "es", "it", "pt", "nl"],
        ["widgetType"] = ["metric", "chart", "matrix", "table", "text", "image", "control", "embed", "custom"],
        ["format"] = ["catalog", "full", "compact", "percent"],
        ["align"] = ["left", "right", "center"],
        ["display"] = ["text", "icon", "badge"],
        ["many"] = ["mixed", "first", "last", "count", "list"],
        ["icon"] = ["circle", "dot", "check", "cross", "warning", "info", "clock", "flag", "star", "pause", "play", "stop", "lock", "unlock", "arrow-up", "arrow-down", "arrow-right", "plus", "minus", "pin", "bell", "eye", "user", "tag"],
        ["mark"] = ["bar", "line", "area", "point", "rule", "text"],
        ["fieldType"] = ["quantitative", "ordinal", "nominal", "temporal"],
        ["control"] = ["select", "multiselect", "date", "daterange", "number", "toggle", "text"],
        ["mediaType"] = ["image/png", "image/jpeg", "image/webp"],
        ["fit"] = ["contain", "cover"],
        ["layoutType"] = ["reflow", "canvas", "flow"],
        ["orientation"] = ["portrait", "landscape"],
        ["medium"] = ["screen", "print", "export"],
        ["overflow"] = ["scale", "scroll"],
        ["unit"] = ["px", "mm", "in"],
        ["pageSize"] = ["A3", "A4", "A5", "Letter", "Legal"],
        ["break"] = ["before", "after"],
        ["density"] = ["compact", "comfortable"],
        ["legend"] = ["show", "hide"],
        ["linkAs"] = ["navigate", "dialog", "panel"],
        ["linkThen"] = ["refresh", "none"],
        ["linkPrint"] = ["hidden", "normal", "link"],
        ["linkTarget"] = ["dashboard", "report", "view", "page", "form"],
    };

    /// <summary>The smallest a tile of a widget type may be at scale 1, in CSS pixels (plan 32 section 3.3); a type that is not listed has none.</summary>
    public static readonly IReadOnlyDictionary<string, (int W, int H)> Minimums = new Dictionary<string, (int, int)>
    {
        ["metric"] = (160, 88), ["text"] = (160, 56), ["control"] = (180, 56), ["chart"] = (280, 180), ["table"] = (320, 200), ["matrix"] = (360, 220), ["embed"] = (280, 180), ["custom"] = (200, 120),
    };

    /// <summary>The names a header or footer may put in braces, besides <c>params.name</c>.</summary>
    public static readonly string[] RunningTokens = ["title", "page", "pages", "asof", "viewer"];

    /// <summary>Keys of a widget that a layout may never set: it restyles a widget and never changes what it shows.</summary>
    internal static readonly string[] WidgetOwnKeys = ["type", "title", "encodings", "query", "format", "formats", "parameter", "parameters", "mark", "columns", "text", "hash", "label", "values", "control", "link_text"];

    /// <summary>Keys that fetch data or compute a value: a chart spec has none of them, wherever they appear. The list of the Vega-Lite subset check of <c>@dbdatakit/visuals</c>, plus the expression carriers.</summary>
    public static readonly string[] ComputingKeys =
    [
        "data", "transform", "aggregate", "bin", "timeUnit", "url", "datasets", "layer", "facet", "repeat", "concat", "params", "selection",
        "calculate", "expr", "condition", "datum", "value", "filter",
    ];

    /// <summary>The JSON Schema text of a schema file of this format: <c>core</c>, <c>dashboard</c>, <c>report</c>, <c>view</c> or <c>vega-lite-subset</c>.</summary>
    public static string Schema(string name)
    {
        using var stream = typeof(ViewsLoader).Assembly.GetManifestResourceStream($"DbDataKit.Views.schemas.{name}.schema.json")
            ?? throw new ArgumentException($"'{name}' is not a schema of this format.", nameof(name));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads a dashboard or a report.
    /// </summary>
    /// <param name="text">The file's text (YAML, or JSON when the file name ends in <c>.json</c>).</param>
    /// <param name="file">The file's path, which diagnostics name.</param>
    /// <param name="expected">The kind the caller wants (<see cref="DashboardLoader"/>, <see cref="ReportLoader"/>); null accepts either.</param>
    /// <param name="targets">The parameters of the documents a drill or a link may lead to, keyed <c>dashboard:name</c>, <c>report:name</c>, <c>view:name</c>, <c>page:name</c> or <c>form:name</c> (a form lists its fields, and its key as required); null skips the checks of a drill's and a link's targets.</param>
    public static ViewsLoadResult Load(string text, string file, ViewKind? expected = null, IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? targets = null)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = JsonInput.ReadByExtension(text, file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.InvalidValue, file, 1, 1, "The file is empty.", "Write `schemaVersion: 1`, a `kind`, `title`, `widgets` and `layouts`."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var reader = new Reader(file, diags, targets) { Expected = expected };
        var document = reader.Read(root);
        return new(diags.HasErrors() ? null : document, diags);
    }

    /// <summary>
    /// The shape check of a widgets map alone (DBR01): a mapping of widget id to widget, as a document, an assistant's answer or a pin holds it. Every part that needs no layout is checked: the type, the encodings,
    /// the closed Vega-Lite subset of a chart, the safe text and image, the query form and its mode, a parameter named in a marker, a control, a cross-filter or a drill, and the binding of an explicit query's
    /// outputs to the encoded fields (a derived query is made from the encodings, so there is nothing to bind). What needs the catalog (a field the model lacks) is made at publish, by the engine.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="file">The file's path, which diagnostics name.</param>
    /// <param name="parameters">The names of the parameters the widgets may use (a document declares them under <c>parameters</c>).</param>
    /// <param name="targets">The parameters of the documents a drill may lead to, as for <see cref="Load"/>; null skips that check.</param>
    public static WidgetsLoadResult LoadWidgets(string text, string file, IEnumerable<string>? parameters = null, IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? targets = null)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = JsonInput.ReadByExtension(text, file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.InvalidValue, file, 1, 1, "The file is empty.", "Write a mapping of widget id to widget."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var reader = new Reader(file, diags, targets);
        var widgets = reader.ReadWidgetsRoot(root, parameters ?? []);
        return new(diags.HasErrors() ? null : widgets, diags);
    }

    /// <summary>A YAML node as JSON: a plain scalar is a boolean, a number or null when it reads as one, a quoted scalar is always text.</summary>
    internal static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlScalar { Quoted: true } s => JsonValue.Create(s.Value),
        YamlScalar s when s.Value is "true" or "false" => JsonValue.Create(s.Value == "true"),
        YamlScalar s when s.Value is "null" or "~" or "" => null,
        YamlScalar s when long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
        YamlScalar s when double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => JsonValue.Create(d),
        YamlScalar s => JsonValue.Create(s.Value),
        YamlSequence q => new JsonArray(q.Items.Select(ToJson).ToArray()),
        YamlMapping m => ToObject(m),
        _ => null,
    };

    internal static JsonObject ToObject(YamlMapping m)
    {
        var o = new JsonObject();
        foreach (var e in m.Entries) o[e.Key.Value] = ToJson(e.Value);
        return o;
    }
}

/// <summary>The loader of <c>dashboard/1</c>: <see cref="ViewsLoader"/> with the kind fixed, so a report is refused saying it is a report.</summary>
public static class DashboardLoader
{
    public static ViewsLoadResult Load(string text, string file, IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? targets = null) => ViewsLoader.Load(text, file, ViewKind.Dashboard, targets);
}

/// <summary>The loader of <c>report/1</c>: <see cref="ViewsLoader"/> with the kind fixed, so a dashboard is refused saying it is a dashboard.</summary>
public static class ReportLoader
{
    public static ViewsLoadResult Load(string text, string file, IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? targets = null) => ViewsLoader.Load(text, file, ViewKind.Report, targets);
}
