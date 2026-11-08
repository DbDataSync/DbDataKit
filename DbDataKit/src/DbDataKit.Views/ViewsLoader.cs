using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>
/// The strict reader of the widget vocabulary of <c>dashboard/1</c> and <c>report/1</c> (plan 32 section 2): a widgets map in YAML or JSON goes in, widgets or diagnostics with the file, line and column come out;
/// nothing throws on a damaged file. The JSON Schemas in <c>web/visuals/schemas</c> (<c>core</c>, with <c>view</c> and <c>vega-lite-subset</c>) are embedded; this loader is the authority and a test keeps the keys,
/// choices and limits of both equal. The checks that need the catalog (an encoded field the model lacks, an invalid request) are made at publish, not here.
/// </summary>
public static partial class ViewsLoader
{
    public const int MaximumWidgets = 100;
    public const int MaximumQueryLimit = 10_000;

    /// <summary>The keys each part accepts, by part name. The JSON Schema files have the same keys; a test keeps them equal.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Keys = new Dictionary<string, string[]>
    {
                ["parameter"] = ["name", "type", "for", "many", "required", "default", "label"],
            ["widget.metric"] = ["type", "title", "label", "format", "encodings", "query"],
        ["widget.chart"] = ["type", "title", "mark", "encodings", "query", "on_select", "drill"],
        ["widget.matrix"] = ["type", "title", "encodings", "query", "on_select", "drill"],
        ["widget.table"] = ["type", "title", "encodings", "query", "drill"],
        ["widget.text"] = ["type", "title", "text"],
        ["widget.image"] = ["type", "title", "hash", "media_type", "alt", "fit"],
        ["widget.control"] = ["type", "title", "parameter", "control", "label", "values"],
        ["encodings.metric"] = ["value", "compare"],
        ["encodings.chart"] = ["x", "y", "color", "column", "row", "tooltip"],
        ["encodings.matrix"] = ["rows", "columns", "values", "values_on", "totals", "sort", "column_limit"],
        ["encodings.table"] = ["columns", "sort"],
        ["table_column"] = ["field", "label", "format", "align", "width"],
        ["channel"] = ["field", "type", "sort", "stack", "title"],
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
        ["control.values"] = ["attribute", "limit"],
                                                                                                    };

    /// <summary>The values of each choice, by name.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Choices = new Dictionary<string, string[]>
    {
        ["parameterType"] = ["string", "integer", "number", "boolean"],
        ["widgetType"] = ["metric", "chart", "matrix", "table", "text", "image", "control"],
        ["format"] = ["catalog", "full", "compact", "percent"],
        ["align"] = ["left", "right", "center"],
        ["mark"] = ["bar", "line", "area", "point", "rule", "text"],
        ["fieldType"] = ["quantitative", "ordinal", "nominal", "temporal"],
        ["control"] = ["select", "multiselect", "date", "daterange", "number", "toggle", "text"],
        ["mediaType"] = ["image/png", "image/jpeg", "image/webp"],
        ["fit"] = ["contain", "cover"],
    };

    /// <summary>Keys that fetch data or compute a value: a chart spec has none of them, wherever they appear. The list of the Vega-Lite subset check of <c>@dbdatakit/visuals</c>, plus the expression carriers.</summary>
    public static readonly string[] ComputingKeys =
    [
        "data", "transform", "aggregate", "bin", "timeUnit", "url", "datasets", "layer", "facet", "repeat", "concat", "params", "selection",
        "calculate", "expr", "condition", "datum", "value", "filter",
    ];

    /// <summary>The JSON Schema text of a schema file of this format: <c>core</c>, <c>view</c> or <c>vega-lite-subset</c>.</summary>
    public static string Schema(string name)
    {
        using var stream = typeof(ViewsLoader).Assembly.GetManifestResourceStream($"DbDataKit.Views.schemas.{name}.schema.json")
            ?? throw new ArgumentException($"'{name}' is not a schema of this format.", nameof(name));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The shape check of a widgets map alone (DBR01): a mapping of widget id to widget, as a document, an assistant's answer or a pin holds it. Every part that needs no layout is checked: the type, the encodings,
    /// the closed Vega-Lite subset of a chart, the safe text and image, the query form and its mode, a parameter named in a marker, a control, a cross-filter or a drill, and the binding of an explicit query's
    /// outputs to the encoded fields (a derived query is made from the encodings, so there is nothing to bind). What needs the catalog (a field the model lacks) is made at publish, by the engine.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="file">The file's path, which diagnostics name.</param>
    /// <param name="parameters">The names of the parameters the widgets may use (a document declares them under <c>parameters</c>).</param>
    /// <param name="targets">The parameters of the documents a drill may lead to, as <c>dashboard/1</c> will give them (keyed <c>report:name</c>); null skips that check.</param>
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

