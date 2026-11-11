using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>The widget vocabulary (plan 32 section 2, DBR01): <c>conformance/views/widgets</c>, which the TypeScript tests of <c>@dbdatakit/visuals</c> read too.</summary>
public class WidgetTests
{
    public static IEnumerable<object[]> Good => Conformance.Rows("good", "widgets/cases.json");
    public static IEnumerable<object[]> Bad => Conformance.Rows("bad", "widgets/cases.json");

    private static JsonNode Case(string section, string file) => Conformance.Cases("widgets/cases.json")[section]!.AsArray().First(c => c!["file"]!.GetValue<string>() == file)!;
    private static IEnumerable<string> Parameters(JsonNode c) => c["parameters"]!.AsArray().Select(p => p!.GetValue<string>());

    private static WidgetsLoadResult Load(string relative, IEnumerable<string>? parameters = null, JsonNode? targets = null) =>
        ViewsLoader.LoadWidgets(Conformance.Read("widgets/" + relative), "widgets/" + relative, parameters, Conformance.Targets(targets));

    [Theory, MemberData(nameof(Good))]
    public void A_good_widget_map_loads_without_a_diagnostic(string file)
    {
        var result = Load(file, Parameters(Case("good", file)));
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Theory, MemberData(nameof(Bad))]
    public void A_bad_widget_map_is_refused_with_its_code_its_path_and_its_line(string file)
    {
        var c = Case("bad", file);
        var code = c["code"]!.GetValue<string>();
        var path = c["path"]!.GetValue<string>();
        var result = Load(file, Parameters(c), c["targets"]);
        Assert.False(result.Ok, $"{file} was accepted");
        Assert.Null(result.Widgets);
        var hit = result.Diagnostics.FirstOrDefault(d => d.Code == code && d.KeyPath == path && d.Severity == ConfigSeverity.Error);
        Assert.True(hit is not null, $"expected {code} at {path}; got:\n{Conformance.Describe(result.Diagnostics)}");
        Assert.Equal("widgets/" + file, hit!.Path);
        Assert.Equal(c["line"]!.GetValue<int>(), hit.Line);
        Assert.True(hit.Column > 0);
        // each bad map differs from a good one by one defect: one refusal, not a flood
        Assert.True(result.Diagnostics.Count <= 2, Conformance.Describe(result.Diagnostics));
    }

    [Fact]
    public void The_encoded_fields_of_a_widget_are_listed_in_the_order_they_are_written_each_with_its_place()
    {
        var widgets = Load("good/dashboard-widgets.json", ["region", "quarter"]).Widgets!;
        Assert.Equal(["regions", "note", "logo", "revenue", "by_region", "pivot", "orders_table", "frame"], widgets.Select(w => w.Id));
        var by = (string id) => widgets.Single(w => w.Id == id);
        Assert.Equal(["x:customers.region", "y:orders.net_revenue", "color:products.category"], by("by_region").Fields.Select(f => $"{f.Channel}:{f.Field}"));
        Assert.Equal(["widgets.by_region.encodings.x", "widgets.by_region.encodings.y.field", "widgets.by_region.encodings.color"], by("by_region").Fields.Select(f => f.Path));
        Assert.Equal(["value:orders.net_revenue", "compare:orders.net_revenue_prev"], by("revenue").Fields.Select(f => $"{f.Channel}:{f.Field}"));
        Assert.Equal(["rows:customers.region", "columns:date.fiscal_quarter", "values:orders.net_revenue"], by("pivot").Fields.Select(f => $"{f.Channel}:{f.Field}"));
        Assert.Equal(["columns:orders.order_id", "columns:customers.region", "columns:orders.net_revenue"], by("orders_table").Fields.Select(f => $"{f.Channel}:{f.Field}"));
        Assert.Empty(by("note").Fields);
        Assert.All(by("by_region").Fields, f => Assert.True(f.Line > 1 && f.Column > 0));
    }

    [Fact]
    public void A_query_is_derived_from_the_encodings_unless_it_says_it_is_explicit()
    {
        var derived = Load("good/dashboard-widgets.json", ["region", "quarter"]).Widgets!.Single(w => w.Id == "revenue");
        Assert.Equal("derived", derived.Query!.Form);
        Assert.False(derived.Query.Explicit);
        Assert.Equal("sales", derived.Query.Model);
        var explicitWidgets = Load("good/explicit-queries.json", ["quarter", "top"]).Widgets!;
        Assert.Equal(["semantic", "semantic_sql", "saved", "store", "ref"], explicitWidgets.Select(w => w.Query!.Form));
        Assert.All(explicitWidgets, w => Assert.True(w.Query!.Explicit));
        Assert.Equal(["sales", "sales", null, "crm", null], explicitWidgets.Select(w => w.Query!.Model));
    }

    [Fact]
    public void A_derived_query_has_nothing_to_bind_because_the_request_is_made_from_the_encodings()
    {
        var text = Conformance.Read("widgets/good/dashboard-widgets.json").Replace("\"orders.net_revenue_prev\"", "\"anything.at_all\"");
        Assert.True(ViewsLoader.LoadWidgets(text, "w.json", ["region", "quarter"]).Ok);
    }

    [Fact]
    public void A_widget_map_in_yaml_is_read_with_the_file_and_line_of_each_refusal()
    {
        const string yaml = "revenue:\n  type: metric\n  encodings: {value: orders.net_revenue}\n  query: {mode: explicit, saved: total, outputs: [total]}\nlogo:\n  type: image\n  url: https://example.com/a.png\n  hash: \"sha256:0000000000000000000000000000000000000000000000000000000000000000\"\n  media_type: image/png\n  alt: Logo\n";
        var result = ViewsLoader.LoadWidgets(yaml, "views/w.yaml");
        Assert.Equal(
            [("views/w.yaml", ViewsCodes.UnsafeImage, 7), ("views/w.yaml", ViewsCodes.FieldNotInQuery, 3)],
            result.Diagnostics.Select(d => (d.Path, d.Code, d.Line)).Order().ToArray());
    }

    [Fact]
    public void A_text_link_may_only_stay_on_the_host()
    {
        Assert.True(ViewsLoader.IsSafeTextLink("/docs/glossary"));
        Assert.False(ViewsLoader.IsSafeTextLink("https://example.com"));
        Assert.False(ViewsLoader.IsSafeTextLink("//example.com"));
        Assert.False(ViewsLoader.IsSafeTextLink("javascript:alert(1)"));
        Assert.False(ViewsLoader.IsSafeTextLink("/a b"));
    }

    [Fact]
    public void An_embed_widget_is_read_with_an_https_address_and_a_declared_parameter_and_an_unknown_type_is_refused_by_the_shared_code()
    {
        var plain = Assert.Single(Load("bad/embed-url-not-https.json", ["region", "quarter"]).Diagnostics);
        Assert.Equal(ViewsCodes.InvalidValue, plain.Code);
        Assert.Equal("widgets.frame.url", plain.KeyPath);
        var undeclared = Assert.Single(Load("bad/embed-undeclared-parameter.json", ["region", "quarter"]).Diagnostics);
        Assert.Equal(ViewsCodes.UndeclaredParameter, undeclared.Code);
        Assert.Equal("DBD-107", Assert.Single(Load("bad/widget-type-unknown.json", ["region", "quarter"]).Diagnostics).Code);
    }

    [Fact]
    public void The_computing_keys_are_refused_wherever_they_appear_in_a_chart()
    {
        foreach (var key in ViewsLoader.ComputingKeys)
        {
            var yaml = $"c:\n  type: chart\n  mark: bar\n  encodings: {{x: {{field: a.b, {key}: x}}, y: a.c}}\n  query: {{model: m}}\n";
            var result = ViewsLoader.LoadWidgets(yaml, "w.yaml");
            Assert.True(result.Diagnostics.Any(d => d.Code == ViewsCodes.ChartNotInSubset && d.KeyPath == $"widgets.c.encodings.x.{key}"), $"{key}: {Conformance.Describe(result.Diagnostics)}");
        }
    }

    [Fact]
    public void A_parameter_marker_may_be_mapped_by_the_query_s_params()
    {
        const string yaml = "m:\n  type: metric\n  encodings: {value: a.b}\n  query: {model: s, filters: [{attribute: a.c, operator: equals, values: [{param: q}]}], params: {q: quarter}}\n";
        Assert.True(ViewsLoader.LoadWidgets(yaml, "w.yaml", ["quarter"]).Ok);
        var undeclared = ViewsLoader.LoadWidgets(yaml, "w.yaml", []);
        Assert.Equal([ViewsCodes.UndeclaredParameter], undeclared.Diagnostics.Select(d => d.Code));
        Assert.Equal("widgets.m.query.params.q", undeclared.Diagnostics[0].KeyPath);
    }
}
