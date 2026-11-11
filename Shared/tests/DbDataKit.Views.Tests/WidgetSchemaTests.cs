using System.Text.Json.Nodes;
using Json.Schema;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>
/// The loader is the authority and the JSON Schema <c>core.schema.json</c> (with <c>view.schema.json</c> and <c>vega-lite-subset.schema.json</c>) is for editors and for the TypeScript validators; these tests keep
/// the widget vocabulary of the two equal, and keep the matrix and chart encodings the view schema's own definitions.
/// </summary>
public class WidgetSchemaTests
{
    private static readonly object Gate = new();
    private static readonly Lazy<JsonSchema> Widgets = new(() =>
    {
        Conformance.RegisterSchemas();
        return JsonSchema.FromText("{\"$ref\": \"https://dbdatakit.invalid/visuals/core.schema.json#/$defs/widgets\"}");
    });

    private static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance.SchemaRoot, name + ".schema.json")))!;

    private static JsonNode At(JsonNode root, string pointer)
    {
        var node = root;
        foreach (var part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node is JsonArray a ? a[int.Parse(part)]! : node[part] ?? throw new InvalidOperationException($"{pointer}: no '{part}'");
        return node;
    }

    private static string[] Props(JsonNode node) => [.. (node["properties"] as JsonObject ?? throw new InvalidOperationException("no properties")).Select(p => p.Key).Order()];

    [Fact]
    public void The_embedded_schemas_are_the_files_of_the_visuals_package()
    {
        foreach (var name in new[] { "core", "view", "vega-lite-subset" })
            Assert.Equal(File.ReadAllText(Path.Combine(Conformance.SchemaRoot, name + ".schema.json")), ViewsLoader.Schema(name));
    }

    [Fact]
    public void The_keys_of_every_widget_part_are_the_keys_of_the_schema()
    {
        var core = Load("core"); var subset = Load("vega-lite-subset");
        var parts = new Dictionary<string, string[]>
        {
            ["parameter"] = Props(At(core, "$defs/parameter")),
            ["widget.metric"] = Props(At(core, "$defs/widget_metric")), ["widget.chart"] = Props(At(core, "$defs/widget_chart")), ["widget.matrix"] = Props(At(core, "$defs/widget_matrix")),
            ["widget.table"] = Props(At(core, "$defs/widget_table")), ["widget.text"] = Props(At(core, "$defs/widget_text")), ["widget.image"] = Props(At(core, "$defs/widget_image")),
            ["widget.control"] = Props(At(core, "$defs/widget_control")),
            ["encodings.metric"] = Props(At(core, "$defs/widget_metric/properties/encodings")), ["encodings.chart"] = Props(At(core, "$defs/chart_encodings")),
            ["encodings.matrix"] = Props(At(core, "$defs/matrix_encodings")), ["encodings.table"] = Props(At(core, "$defs/widget_table/properties/encodings")),
            ["table_column"] = Props(At(core, "$defs/table_column")), ["channel"] = Props(At(core, "$defs/chart_channel")), ["mark"] = Props(At(subset, "$defs/spec/properties/mark/oneOf/1")),
            ["query.derived"] = Props(At(core, "$defs/query_derived")), ["query.semantic"] = Props(At(core, "$defs/query_explicit_semantic")), ["query.saved"] = Props(At(core, "$defs/query_explicit_saved")),
            ["query.store"] = Props(At(core, "$defs/query_explicit_store")), ["query.ref"] = Props(At(core, "$defs/query_explicit_ref")),
            ["semantic"] = Props(At(core, "$defs/query_explicit_semantic/properties/semantic")), ["store"] = Props(At(core, "$defs/query_explicit_store/properties/store")),
            ["on_select"] = Props(At(core, "$defs/on_select")), ["drill"] = Props(At(core, "$defs/drill")), ["drill.to"] = Props(At(core, "$defs/drill/properties/to")),
            ["control.values"] = Props(At(core, "$defs/widget_control/properties/values")),
        };
        foreach (var (part, keys) in parts) Assert.True(ViewsLoader.Keys[part].Order().SequenceEqual(keys), $"{part}: loader {string.Join(",", ViewsLoader.Keys[part].Order())} / schema {string.Join(",", keys)}");
    }

    [Fact]
    public void The_choices_of_the_loader_are_the_enums_of_the_schema()
    {
        var core = Load("core"); var subset = Load("vega-lite-subset");
        string[] Enum(JsonNode n) => [.. n["enum"]!.AsArray().Select(v => v!.GetValue<string>())];
        var checks = new Dictionary<string, string[]>
        {
            ["parameterType"] = Enum(At(core, "$defs/parameter/properties/type")), ["format"] = Enum(At(core, "$defs/format")),
            ["align"] = Enum(At(core, "$defs/table_column/properties/align")), ["mark"] = Enum(At(subset, "$defs/mark")), ["fieldType"] = Enum(At(subset, "$defs/channel/properties/type")),
            ["control"] = Enum(At(core, "$defs/widget_control/properties/control")), ["mediaType"] = Enum(At(core, "$defs/widget_image/properties/media_type")),
            ["fit"] = Enum(At(core, "$defs/widget_image/properties/fit")),
            ["widgetType"] = [.. new[] { "metric", "chart", "matrix", "table", "text", "image", "control" }.Where(t => At(core, $"$defs/widget_{t}/properties/type")["const"]!.GetValue<string>() == t)],
        };
        foreach (var (name, values) in checks) Assert.True(ViewsLoader.Choices[name].Order().SequenceEqual(values.Order()), $"{name}: loader {string.Join(",", ViewsLoader.Choices[name])} / schema {string.Join(",", values)}");
        Assert.Equal(ViewsLoader.MaximumQueryLimit, At(core, "$defs/query_derived/properties/limit")["maximum"]!.GetValue<int>());
        Assert.Equal(["metric", "chart", "matrix", "table", "text", "image", "control"], ViewsLoader.Choices["widgetType"]);
        Assert.DoesNotContain("embed", ViewsLoader.Choices["widgetType"]);
    }

    [Fact]
    public void A_matrix_and_a_chart_widget_use_the_view_schemas_own_definitions()
    {
        var core = Load("core"); var view = Load("view"); var subset = Load("vega-lite-subset");
        var viewId = view["$id"]!.GetValue<string>(); var subsetId = subset["$id"]!.GetValue<string>();
        var matrix = At(view, "$defs/matrix");
        var expected = Props(matrix).Where(p => p is not ("type" or "id")).Order().ToArray();
        var encodings = At(core, "$defs/matrix_encodings");
        Assert.Equal(expected, Props(encodings));
        // the axes and the values carry a format (DBR41), so they are the core's own; the rest is the view's definition, referenced
        foreach (var p in expected.Where(p => p is not ("rows" or "columns" or "values")))
            Assert.Equal($"{viewId}#/$defs/matrix/properties/{p}", encodings["properties"]![p]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/matrix_axis", encodings["properties"]!["rows"]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/matrix_axis", encodings["properties"]!["columns"]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/matrix_values", encodings["properties"]!["values"]!["$ref"]!.GetValue<string>());
        Assert.Equal(Props(At(view, "$defs/matrix/properties/values/items")), Props(At(core, "$defs/matrix_values/items")));
        Assert.Equal(["values"], matrix["required"]!.AsArray().Select(v => v!.GetValue<string>()).Where(v => v is not ("type" or "id")));
        Assert.Equal(["values"], encodings["required"]!.AsArray().Select(v => v!.GetValue<string>()));
        // the chart: the channels are the subset's, and so are the mark and the channel object
        Assert.Equal(Props(At(subset, "$defs/spec/properties/encoding")), Props(At(core, "$defs/chart_encodings")));
        Assert.Equal($"{subsetId}#/$defs/spec/properties/mark", At(core, "$defs/widget_chart/properties/mark")["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/chart_channel", At(core, "$defs/encoding_channel/oneOf/1")["$ref"]!.GetValue<string>());
        // the channel object is the subset's, field, type, sort, stack and title by reference, plus the format
        Assert.Equal(new[] { "field", "format", "sort", "stack", "title", "type" }, [.. Props(At(core, "$defs/chart_channel"))]);
        foreach (var p in Props(At(subset, "$defs/channel"))) Assert.Equal($"{subsetId}#/$defs/channel/properties/{p}", At(core, "$defs/chart_channel")["properties"]![p]!["$ref"]!.GetValue<string>());
        Assert.Equal($"{viewId}#/$defs/sort", At(core, "$defs/widget_table/properties/encodings/properties/sort")["$ref"]!.GetValue<string>());
    }

    [Fact]
    public void Every_good_widget_map_is_valid_under_the_schema()
    {
        foreach (var c in Conformance.Cases("widgets/cases.json")["good"]!.AsArray())
        {
            var file = "widgets/" + c!["file"]!.GetValue<string>();
            var errors = Errors(file);
            Assert.True(errors.Count == 0, $"{file}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void Every_bad_widget_map_the_schema_can_express_is_invalid_under_it_and_the_others_are_valid_so_the_cases_say_so_honestly()
    {
        var checkedCount = 0;
        foreach (var c in Conformance.Cases("widgets/cases.json")["bad"]!.AsArray())
        {
            var file = "widgets/" + c!["file"]!.GetValue<string>();
            var invalid = Errors(file).Count > 0;
            Assert.True(invalid == c["schema"]!.GetValue<bool>(), $"{file} is {(invalid ? "invalid" : "valid")} under the schema; the case says schema: {c["schema"]}");
            if (invalid) checkedCount++;
        }
        Assert.True(checkedCount >= 15, $"only {checkedCount} cases are checked against the schema");
    }

    private static List<string> Errors(string file)
    {
        var doc = JsonNode.Parse(Conformance.Read(file))!;
        EvaluationResults result;
        lock (Gate) result = Widgets.Value.Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return [.. result.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).Take(5)];
    }
}
