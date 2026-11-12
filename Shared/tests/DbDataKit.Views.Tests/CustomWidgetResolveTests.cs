using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Apps;
using DbDataKit.Serving;

namespace DbDataKit.Views.Tests;

/// <summary>
/// A custom widget's rows (plan 32 DBR31): the host runs the queries its manifest entry declares, as the viewer, and gives the code the rows by the query's name. The same serving core, so two viewers with different access get
/// different rows; a denied query fails that widget with the core's own code and the rest of the layout resolves; the widget's parameters bind the request's markers like a standard widget's.
/// </summary>
public class CustomWidgetResolveTests
{
    private const string Manifest = """
        schemaVersion: 1
        name: sales-portal
        version: 1.0.0
        entry: src/main.tsx
        semantic: [retail_sales]
        views:
          - name: region-grid
            kind: widget
            export: RegionGrid
            params:
              - {name: region, type: string, many: true}
            queries:
              by_region:
                semantic:
                  model: retail_sales
                  request:
                    measures: [sales.units]
                    attributes: [customers.region]
                    filters: [{attribute: customers.region, operator: equals, values: {param: region}}]
          - name: need-region
            kind: widget
            export: NeedRegion
            params:
              - {name: region, type: string, required: true}
            queries:
              by_region: {semantic: {model: retail_sales, request: {measures: [sales.units], attributes: [customers.region]}}}
          - name: a-page
            kind: page
            export: APage
            queries:
              all: {semantic: {model: retail_sales, request: {measures: [sales.units]}}}
        """;

    private static readonly IReadOnlyDictionary<string, CustomView> Views = Load();

    private static IReadOnlyDictionary<string, CustomView> Load()
    {
        var loaded = AppManifestLoader.Load(Manifest);
        Assert.NotNull(loaded.Manifest);
        return loaded.Manifest!.CustomViews.ToDictionary(v => "sales-portal/" + v.Export);
    }

    private static ServingContext Viewer(params (string Key, string Value)[] claims) =>
        new(new ServingCaller("ana", "ana", new HashSet<string> { "analyst" }, claims.ToDictionary(c => c.Key, c => c.Value)), TraceId: "t");

    private static WidgetResolver Resolver(IServingQueries serving, bool withViews = true) =>
        new(serving, model => model == "retail_sales" ? new CatalogRoles("retail_sales", ["sales.units"], ["customers.region"]) : null, new ResolverOptions { CustomViews = withViews ? name => Views.GetValueOrDefault(name) : null });

    private static ViewDocument Doc(string widgets, string parameters = "[{name: region, type: string, many: true, default: []}]")
    {
        var ids = Regex.Matches(widgets, @"^  ([a-z][a-z0-9_-]*):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var tiles = string.Join("\n", ids.Select((id, i) => $"      - {{widget: {id}, x: 0, y: {i * 2}, w: 12, h: 2}}"));
        var yaml = $"schemaVersion: 1\nkind: dashboard\ntitle: Test\nparameters: {parameters}\nwidgets:\n{widgets}\nlayouts:\n  - name: desk\n    type: reflow\n    default: true\n    grid: {{columns: 12, rows: {ids.Count * 2}}}\n    tiles:\n{tiles}\n";
        var loaded = ViewsLoader.Load(yaml, "test.yaml");
        Assert.True(loaded.Ok, Conformance.Describe(loaded.Diagnostics) + "\n" + yaml);
        return loaded.Document!;
    }

    private const string Widgets = """
          grid: {type: custom, component: sales-portal/RegionGrid, params: {region: $params.region}}
          units: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}
        """;

    [Fact]
    public async Task A_custom_widget_is_given_the_rows_of_its_declared_query_as_the_viewer()
    {
        var serving = new RetailServing();
        var rendered = await Resolver(serving).ResolveAsync(Viewer(), Doc(Widgets), "desk");
        var grid = rendered.Widgets["grid"];
        Assert.Equal("custom", grid["type"]!.GetValue<string>());
        Assert.Equal("sales-portal/RegionGrid", grid["component"]!.GetValue<string>());
        var by = grid["data"]!["by_region"]!;
        Assert.Equal(["customers_region", "sales_units"], by["columns"]!.AsArray().Select(c => c!.GetValue<string>()).Order());
        Assert.Equal(4, by["rows"]!.AsArray().Count);                                      // four regions, the viewer has no restriction
        Assert.NotEqual("error", rendered.Widgets["units"]["type"]!.GetValue<string>());   // a standard widget beside it
    }

    [Fact]
    public async Task Two_viewers_with_different_access_get_different_rows()
    {
        var resolver = Resolver(new RetailServing());
        var all = await resolver.ResolveAsync(Viewer(), Doc(Widgets), "desk", only: ["grid"]);
        var west = await resolver.ResolveAsync(Viewer(("region", "West")), Doc(Widgets), "desk", only: ["grid"]);
        Assert.Equal(4, all.Widgets["grid"]["data"]!["by_region"]!["rows"]!.AsArray().Count);
        Assert.Single(west.Widgets["grid"]["data"]!["by_region"]!["rows"]!.AsArray());
    }

    [Fact]
    public async Task The_widgets_parameters_bind_the_requests_markers_and_a_filter_without_a_value_drops_out()
    {
        var serving = new RetailServing();
        var resolver = Resolver(serving);
        var chosen = await resolver.ResolveAsync(Viewer(), Doc(Widgets), "desk", new Dictionary<string, JsonNode?> { ["region"] = new JsonArray("East") }, only: ["grid"]);
        Assert.Equal(["East"], chosen.Widgets["grid"]["data"]!["by_region"]!["rows"]!.AsArray().Select(r => r!["customers_region"]!.GetValue<string>()));
        var sent = JsonNode.Parse(((ServingRequest.Semantic)serving.Received[^1].Request).Json)!;
        Assert.Equal("""{"region":["East"]}""", sent["parameters"]!.ToJsonString());
        Assert.Equal("string", sent["parameter_definitions"]!["region"]!["type"]!.GetValue<string>());
        Assert.Equal("""{"param":"region"}""", sent["query"]!["filters"]![0]!["values"]!.ToJsonString());      // the marker stays: the literal form's twin

        serving.Received.Clear();
        var none = await resolver.ResolveAsync(Viewer(), Doc(Widgets), "desk", only: ["grid"]);
        Assert.Equal(4, none.Widgets["grid"]["data"]!["by_region"]!["rows"]!.AsArray().Count);
        Assert.Null(JsonNode.Parse(((ServingRequest.Semantic)serving.Received[^1].Request).Json)!["query"]!["filters"]);
    }

    [Fact]
    public async Task A_denied_query_fails_that_widget_with_the_cores_words_and_the_rest_resolves()
    {
        var serving = new RetailServing { Before = (_, request) => ((ServingRequest.Semantic)request).Json.Contains("customers.region", StringComparison.Ordinal) ? throw new ServingDeniedException("The role may not read customers.") : Task.CompletedTask };
        var rendered = await Resolver(serving).ResolveAsync(Viewer(), Doc(Widgets + "\n  other: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}"), "desk");
        var grid = rendered.Widgets["grid"];
        Assert.Equal("error", grid["type"]!.GetValue<string>());
        Assert.Equal("denied", grid["code"]!.GetValue<string>());
        Assert.Contains("`by_region` of `sales-portal/RegionGrid`", grid["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("may not read customers", grid["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_parameter_with_no_value_and_a_component_the_host_does_not_serve_are_error_entries()
    {
        var needs = await Resolver(new RetailServing()).ResolveAsync(Viewer(), Doc("  g: {type: custom, component: sales-portal/NeedRegion}"), "desk");
        Assert.Equal(ResolveCodes.RequiredParameter, needs.Widgets["g"]["code"]!.GetValue<string>());

        var other = await Resolver(new RetailServing()).ResolveAsync(Viewer(), Doc("  g: {type: custom, component: other-app/Thing}"), "desk");
        Assert.Equal(WidgetResolver.CustomNotServed, other.Widgets["g"]["code"]!.GetValue<string>());

        var page = await Resolver(new RetailServing()).ResolveAsync(Viewer(), Doc("  g: {type: custom, component: sales-portal/APage}"), "desk");
        Assert.Contains("page of the app", page.Widgets["g"]["message"]!.GetValue<string>(), StringComparison.Ordinal);

        var none = await Resolver(new RetailServing(), withViews: false).ResolveAsync(Viewer(), Doc(Widgets), "desk", only: ["grid"]);
        Assert.Equal(WidgetResolver.CustomNotServed, none.Widgets["grid"]["code"]!.GetValue<string>());
    }

    [Fact]
    public void A_custom_widget_uses_the_document_parameters_its_params_read()
    {
        Assert.Equal(["region"], WidgetResolver.UsedParameters(Doc(Widgets).Widget("grid")!));
    }
}
