using DbDataKit.Apps;
using DbDataKit.Config.Yaml;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>Dashboards and reports in an app (plan 32 DBR13): listed under <c>views</c>, read by the views loader, checked against the manifest: its models, the targets of its links and drills, a custom widget's component.</summary>
public class AppViewDocumentsTests
{
    private const string Manifest = """
        schemaVersion: 1
        name: sales-portal
        version: 1.0.0
        entry: src/main.tsx
        semantic: [sales]
        models:
          - model: territories
            entities:
              territory: [read, write]
        views:
          - views/overview.yaml
          - views/regional.yaml
          - views/orders-form.yaml
          - name: orders-grid
            kind: widget
            export: OrdersGrid
            params: [{name: region, type: string, required: true}, {name: top, type: integer}]
            queries: {orders: {semantic: {model: sales, request: {measures: [orders.net_revenue], limit: 10}}}}
        """;

    private const string Regional = """
        schemaVersion: 1
        kind: report
        title: Regional
        parameters:
          - {name: region, type: string, required: true}
        widgets:
          t: {type: text, text: hello}
        layouts:
          - {name: print, type: flow, default: true, sections: [{blocks: [{widget: t}]}]}
        """;

    private const string Form = """
        schemaVersion: 1
        kind: form
        title: Order
        model: territories
        entity: territory
        fields: [name]
        """;

    private static string Overview(string widgets = "", string links = "") => $$"""
        schemaVersion: 1
        kind: dashboard
        title: Overview
        parameters:
          - {name: region, type: string}
          - {name: year, type: integer}
        {{links}}
        widgets:
          by_region:
            type: chart
            mark: bar
            encodings: {x: customers.region, y: orders.net_revenue}
            query: {model: sales, limit: 20}
            on_click: go
          grid:
            type: custom
            component: sales-portal/OrdersGrid
            params: {region: $params.region, top: 5}
        {{widgets}}
        layouts:
          - {name: desk, type: reflow, default: true, grid: {columns: 12}, tiles: [{widget: by_region, x: 0, y: 0, w: 6, h: 4}, {widget: grid, x: 6, y: 0, w: 6, h: 4}]}
        """;

    private const string Links = "links:\n  go: {report: regional, with: {region: $customers.region}}";

    private static IReadOnlyList<ConfigDiagnostic> Run(string overview, string? manifest = null)
    {
        var files = new Dictionary<string, string> { ["views/overview.yaml"] = overview, ["views/regional.yaml"] = Regional, ["views/orders-form.yaml"] = Form };
        var m = AppManifestLoader.Load(manifest ?? Manifest);
        Assert.True(m.Ok, string.Join("\n", m.Diagnostics));
        return AppChecks.ViewSpecs(m.Manifest!, p => files.GetValueOrDefault(p), documents: AppViewDocuments.Instance);
    }

    [Fact]
    public void A_dashboard_and_a_report_in_an_app_load_and_pass_their_checks()
    {
        var found = Run(Overview(links: Links));
        Assert.Empty(found);
    }

    [Fact]
    public void Without_a_loader_a_document_is_a_warning_not_a_silence()
    {
        var m = AppManifestLoader.Load(Manifest).Manifest!;
        var found = AppChecks.ViewSpecs(m, p => p == "views/regional.yaml" ? Regional : p == "views/orders-form.yaml" ? Form : Overview(links: Links));
        Assert.All(found, d => Assert.Equal((AppManifestLoader.ViewDocumentNotChecked, ConfigSeverity.Warning), (d.Code, d.Severity)));
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void A_dashboard_that_queries_a_semantic_model_the_manifest_does_not_list_is_refused_naming_it()
    {
        var found = Run(Overview(links: Links), Manifest.Replace("semantic: [sales]", "semantic: [other]"));
        var d = Assert.Single(found, x => x.Code == AppManifestLoader.ViewDocumentModel);
        Assert.Contains("`sales`", d.Message);
        Assert.Equal("views/overview.yaml", d.Path);
        Assert.True(d.Line > 0);
    }

    [Fact]
    public void A_store_read_without_read_is_refused()
    {
        var store = "  tbl:\n    type: table\n    encodings: {columns: [{field: name}]}\n    query: {mode: explicit, store: {model: territories, entity: territory, columns: [name]}}";
        Assert.Empty(Run(Overview(store, Links).Replace("tiles: [", "tiles: [{widget: tbl, x: 0, y: 4, w: 12, h: 4}, ")));
        var found = Run(Overview(store.Replace("entity: territory", "entity: place"), Links).Replace("tiles: [", "tiles: [{widget: tbl, x: 0, y: 4, w: 12, h: 4}, "));
        Assert.Contains(found, d => d.Code == AppManifestLoader.ViewDocumentModel && d.Message.Contains("territories.place"));
    }

    [Fact]
    public void A_drill_to_a_view_the_app_lacks_is_refused()
    {
        var drill = "  by_region2:\n    type: chart\n    mark: bar\n    encodings: {x: customers.region, y: orders.net_revenue}\n    query: {model: sales, limit: 20}\n    drill: {to: {view: nowhere}}";
        var text = Overview(drill, Links).Replace("tiles: [", "tiles: [{widget: by_region2, x: 0, y: 4, w: 12, h: 4}, ");
        var found = Run(text);
        var d = Assert.Single(found);
        Assert.Equal(ViewsCodes.LinkTargetUnknown, d.Code);
        Assert.Contains("`nowhere`", d.Message);
    }

    [Fact]
    public void A_link_to_a_report_the_app_lacks_or_with_a_name_it_lacks_is_refused()
    {
        Assert.Contains(Run(Overview(links: Links.Replace("report: regional", "report: gone"))), d => d.Code == ViewsCodes.LinkTargetUnknown);
        Assert.Contains(Run(Overview(links: Links.Replace("with: {region", "with: {reigon"))), d => d.Code == ViewsCodes.LinkNameUnknown);
    }

    [Fact]
    public void A_custom_widget_must_name_a_widget_of_this_app_and_give_its_parameters_by_type()
    {
        Assert.Contains(Run(Overview(links: Links).Replace("sales-portal/OrdersGrid", "other-app/OrdersGrid")), d => d.Code == AppManifestLoader.ViewDocumentComponent && d.Message.Contains("`other-app`"));
        Assert.Contains(Run(Overview(links: Links).Replace("sales-portal/OrdersGrid", "sales-portal/Missing")), d => d.Message.Contains("not a widget the manifest declares"));
        Assert.Contains(Run(Overview(links: Links).Replace("{region: $params.region, top: 5}", "{top: 5}")), d => d.Message.Contains("required parameter `region`"));
        Assert.Contains(Run(Overview(links: Links).Replace("top: 5", "top: five")), d => d.Message.Contains("`top` of `OrdersGrid` is integer"));
        Assert.Contains(Run(Overview(links: Links).Replace("top: 5}", "top: 5, extra: 1}")), d => d.Message.Contains("does not declare"));
    }

    [Fact]
    public void A_report_is_not_a_view_spec_and_a_dashboard_with_layouts_is_not_the_mini_dashboard()
    {
        Assert.True(ViewSpecLoader.IsDocument(Regional));
        Assert.True(ViewSpecLoader.IsDocument(Overview()));
        Assert.False(ViewSpecLoader.IsDocument(Form));
        Assert.False(ViewSpecLoader.IsDocument("schemaVersion: 1\nkind: dashboard\ntitle: t\nmodel: m\nwidgets: []\n"));
    }
}
