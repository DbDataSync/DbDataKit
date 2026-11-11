using DbDataKit.Apps;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps.Manifest.Tests;

/// <summary>The custom views of an app's manifest (plan 32 DBR30): a mapping under <c>views</c> beside the file paths, each bad part refused with the manifest's file and line, and the checks against the models and the source.</summary>
public sealed class CustomViewTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "custom-view-" + Guid.NewGuid().ToString("N")[..8]);

    public CustomViewTests() => Directory.CreateDirectory(Path.Combine(folder, "src"));

    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private const string Manifest = """
        schemaVersion: 1
        name: sales-portal
        version: 1.0.0
        entry: src/main.tsx
        semantic: [sales]
        models:
          - model: territories
            entities:
              territory: [read]
        views:
          - name: orders-grid
            kind: widget
            export: OrdersGrid
            title: Orders
            params:
              - {name: region, type: string, required: true}
              - {name: top, type: integer, default: 10}
            queries:
              orders:
                semantic:
                  model: sales
                  request: {measures: [orders.net_revenue], attributes: [customers.region], limit: 100}
              areas:
                store: {model: territories, entity: territory, limit: 50}
          - name: regional-detail
            kind: page
            export: RegionalDetail
            formats: [pdf, png]
            page: {size: A4, orientation: landscape}
            queries:
              orders:
                semantic: {model: sales, request: {measures: [orders.net_revenue]}}
        """;

    private const string Code = "defineWidget('OrdersGrid', OrdersGrid)\ndefinePage(\"RegionalDetail\", Detail)\n";

    private static AppManifest Load(string yaml, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        var r = AppManifestLoader.Load(yaml);
        diagnostics = r.Diagnostics;
        return r.Manifest!;
    }

    [Fact]
    public void A_manifest_declares_a_widget_and_a_page_beside_its_view_files()
    {
        var m = Load(Manifest.Replace("views:\n", "views:\n  - views/home.yaml\n"), out var d);
        Assert.Empty(d);
        Assert.Equal(["views/home.yaml"], m.Views);
        var (widget, page) = (m.CustomViews[0], m.CustomViews[1]);
        Assert.Equal((CustomViewKind.Widget, "OrdersGrid", "orders-grid"), (widget.Kind, widget.Export, widget.Name));
        Assert.Equal(["region", "top"], widget.Params.Select(p => p.Name));
        Assert.True(widget.Params[0].Required);
        Assert.Equal(["semantic", "store"], widget.Queries.Select(q => q.Form));
        Assert.Equal(["orders.net_revenue", "customers.region"], widget.Queries[0].Members);
        Assert.Equal("territory", widget.Queries[1].Entity);
        Assert.Equal((CustomViewKind.Page, "A4", "landscape"), (page.Kind, page.PageSize, page.Orientation));
        Assert.Equal(["pdf", "png"], page.Formats);
    }

    [Fact]
    public void A_manifest_without_custom_views_has_none() =>
        Assert.Empty(Load("schemaVersion: 1\nname: a\nversion: 1.0.0\nviews: [views/home.yaml]\n", out _).CustomViews);

    [Theory]
    [InlineData("kind: widget", "kind: gadget", "`kind`")]
    [InlineData("export: OrdersGrid", "export: 1bad", "`export`")]
    [InlineData("name: orders-grid", "name: Orders Grid", "`name`")]
    [InlineData("title: Orders", "title: Orders\n    colour: red", "Unknown key")]
    [InlineData("{name: region, type: string, required: true}", "{name: region, type: text}", "`type`")]
    [InlineData("formats: [pdf, png]", "formats: [pdf, docx]", "format is one of")]
    [InlineData("export: RegionalDetail", "export: OrdersGrid", "registered by two")]
    [InlineData("semantic: {model: sales, request: {measures: [orders.net_revenue]}}", "semantic: {model: sales}", "`request`")]
    public void A_custom_view_that_is_not_well_formed_is_refused_with_the_manifests_line(string from, string to, string message)
    {
        var r = AppManifestLoader.Load(Manifest.Replace(from, to));
        Assert.Contains(r.Diagnostics, d => d.Severity == ConfigSeverity.Error && d.Message.Contains(message, StringComparison.OrdinalIgnoreCase) && d.Line > 0);
        Assert.Null(r.Manifest);
    }

    [Fact]
    public void The_checks_pass_when_the_models_are_listed_and_the_code_registers_the_exports()
    {
        var m = Load(Manifest, out _);
        Assert.Empty(AppChecks.CustomViews(m, [("src/main.tsx", Code)]));
    }

    [Fact]
    public void A_widget_with_no_declared_query_is_refused_at_its_entry()
    {
        const string yaml = "schemaVersion: 1\nname: a\nversion: 1.0.0\nentry: src/main.tsx\nviews:\n  - {name: g, kind: widget, export: G}\n";
        var found = AppChecks.CustomViews(Load(yaml, out _), [("src/main.tsx", "defineWidget('G', X)")]);
        var d = Assert.Single(found);
        Assert.Equal((AppManifestLoader.CustomViewNoQuery, 6, "app.yaml"), (d.Code, d.Line, d.Path));
    }

    [Fact]
    public void A_query_of_a_model_the_manifest_does_not_list_is_refused_naming_the_model_and_the_line()
    {
        var m = Load(Manifest.Replace("semantic: [sales]", "semantic: [other]").Replace("territory: [read]", "territory: [write]"), out _);
        var found = AppChecks.CustomViews(m, [("src/main.tsx", Code)]);
        Assert.Contains(found, d => d.Code == AppManifestLoader.CustomViewQueryModel && d.Message.Contains("`sales`") && d.Message.Contains("under `semantic`"));
        var store = AppChecks.CustomViews(Load(Manifest.Replace("territory: [read]", "territory: [write]"), out _), [("src/main.tsx", Code)]);
        Assert.Empty(store); // write includes read
        var none = AppChecks.CustomViews(Load(Manifest.Replace("entity: territory,", "entity: province,"), out _), [("src/main.tsx", Code)]);
        Assert.Contains(none, d => d.Message.Contains("`territories.province`") && d.Message.Contains("`read`"));
    }

    [Fact]
    public void A_member_the_model_does_not_have_is_refused()
    {
        var found = AppChecks.CustomViews(Load(Manifest, out _), [("src/main.tsx", Code)], new Dictionary<string, IReadOnlySet<string>> { ["sales"] = new HashSet<string> { "orders.net_revenue", "customers.country" } });
        var d = Assert.Single(found);
        Assert.Contains("`customers.region`", d.Message);
        Assert.Equal(AppManifestLoader.CustomViewQueryModel, d.Code);
    }

    [Fact]
    public void An_export_that_nothing_registers_is_refused_naming_it_and_the_function()
    {
        var found = AppChecks.CustomViews(Load(Manifest, out _), [("src/main.tsx", "defineWidget('Other', X)\ndefineWidget('RegionalDetail', Y)\n")]);
        Assert.Equal(2, found.Count(d => d.Code == AppManifestLoader.CustomViewNotRegistered));
        Assert.Contains(found, d => d.Message.Contains("defineWidget('OrdersGrid'"));
        Assert.Contains(found, d => d.Message.Contains("definePage('RegionalDetail'"));
    }

    [Fact]
    public void App_validate_reads_the_source_folder_and_reports_each_problem_with_the_manifest_line()
    {
        File.WriteAllText(Path.Combine(folder, "app.yaml"), Manifest);
        File.WriteAllText(Path.Combine(folder, "src", "main.tsx"), "defineWidget('OrdersGrid', G)\n");
        var result = AppValidator.Validate(folder);
        Assert.Contains(result.Diagnostics, d => d.Code == AppManifestLoader.CustomViewNotRegistered && d.Message.Contains("RegionalDetail"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("`OrdersGrid` of"));
        File.WriteAllText(Path.Combine(folder, "src", "main.tsx"), Code);
        Assert.DoesNotContain(AppValidator.Validate(folder).Diagnostics, d => d.Code.StartsWith("DBD-APP-02", StringComparison.Ordinal));
    }
}
