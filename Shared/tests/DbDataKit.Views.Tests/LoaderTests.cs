using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

public class LoaderTests
{
    public static IEnumerable<object[]> Good => Conformance.Rows("good");
    public static IEnumerable<object[]> Bad => Conformance.Rows("bad");
    public static IEnumerable<object[]> Warnings => Conformance.Rows("warnings");

    private static JsonNode Case(string section, string file) => Conformance.Cases()[section]!.AsArray().First(c => c!["file"]!.GetValue<string>() == file)!;

    [Theory, MemberData(nameof(Good))]
    public void A_good_document_loads_without_a_diagnostic(string file)
    {
        var c = Case("good", file);
        var kind = Conformance.Kind(c["kind"]!.GetValue<string>());
        var result = ViewsLoader.Load(Conformance.Read(file), file, kind);
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(kind, result.Document!.Kind);
    }

    [Theory, MemberData(nameof(Bad))]
    public void A_bad_document_is_refused_with_its_code_its_path_and_its_line(string file)
    {
        var c = Case("bad", file);
        var code = c["code"]!.GetValue<string>();
        var path = c["path"]!.GetValue<string>();
        var result = ViewsLoader.Load(Conformance.Read(file), file, Conformance.Kind(c["as"]?.GetValue<string>()), Conformance.Targets(c["targets"]));
        Assert.False(result.Ok, $"{file} was accepted");
        Assert.Null(result.Document);
        var hit = result.Diagnostics.FirstOrDefault(d => d.Code == code && d.KeyPath == path && d.Severity == ConfigSeverity.Error);
        Assert.True(hit is not null, $"expected {code} at {path}; got:\n{Conformance.Describe(result.Diagnostics)}");
        Assert.Equal(file, hit!.Path);
        Assert.Equal(c["line"]!.GetValue<int>(), hit.Line);
        Assert.True(hit.Column > 0);
    }

    [Theory, MemberData(nameof(Bad))]
    public void A_bad_document_is_refused_with_one_cause_not_a_flood(string file)
    {
        var c = Case("bad", file);
        var result = ViewsLoader.Load(Conformance.Read(file), file, Conformance.Kind(c["as"]?.GetValue<string>()), Conformance.Targets(c["targets"]));
        // each bad document differs from a good one by one defect; the refusals it causes are about that defect (the required-control case names every layout that omits it)
        Assert.True(result.Diagnostics.Count(d => d.Severity == ConfigSeverity.Error) <= 4, Conformance.Describe(result.Diagnostics));
    }

    [Theory, MemberData(nameof(Warnings))]
    public void A_document_with_a_warning_still_loads(string file)
    {
        var c = Case("warnings", file);
        var result = ViewsLoader.Load(Conformance.Read(file), file, Conformance.Kind(c["kind"]!.GetValue<string>()));
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
        var hit = Assert.Single(result.Diagnostics);
        Assert.Equal(ConfigSeverity.Warning, hit.Severity);
        Assert.Equal(c["code"]!.GetValue<string>(), hit.Code);
        Assert.Equal(c["path"]!.GetValue<string>(), hit.KeyPath);
        Assert.Equal(c["line"]!.GetValue<int>(), hit.Line);
    }

    [Fact]
    public void The_loader_reads_the_widgets_once_and_the_layouts_over_them()
    {
        var doc = ViewsLoader.Load(Conformance.Read("good/dashboard-layouts.json"), "d.json").Document!;
        Assert.Equal(ViewKind.Dashboard, doc.Kind);
        Assert.Equal(["regions", "note", "logo", "revenue", "by_region", "pivot", "orders_table"], doc.Widgets.Select(w => w.Id));
        Assert.Equal(["desk", "wall", "phone", "print"], doc.Layouts.Select(l => l.Name));
        Assert.Equal([LayoutType.Reflow, LayoutType.Canvas, LayoutType.Canvas, LayoutType.Flow], doc.Layouts.Select(l => l.Type));
        Assert.Equal("desk", doc.DefaultLayout.Name);
        Assert.Equal(7, doc.DefaultLayout.Placed.Count);
        Assert.Equal(["revenue", "by_region", "logo"], doc.Layouts[1].Placed);
        Assert.Equal(["revenue", "note", "by_region", "orders_table", "pivot"], doc.Layouts[3].Placed);
        Assert.Equal("5m", doc.Refresh);
        Assert.Equal("wall", doc.KioskLayout);
        Assert.Null(doc.Strict);
    }

    [Fact]
    public void A_report_reads_its_own_keys()
    {
        var doc = ReportLoader.Load(Conformance.Read("good/report-canvas-summary.json"), "r.json").Document!;
        Assert.Equal(ViewKind.Report, doc.Kind);
        Assert.True(doc.Strict);
        Assert.Equal(50, doc.MaxPages);
        Assert.Equal(5000, doc.MaxRows);
        Assert.True(doc.CoverParameters);
        Assert.Null(doc.Refresh);
        Assert.Equal(LayoutType.Flow, doc.DefaultLayout.Type);
        Assert.Equal("serif", doc.Font);
    }

    [Fact]
    public void A_canvas_in_millimetres_is_measured_at_96_pixels_per_inch()
    {
        var doc = ViewsLoader.Load(Conformance.Read("good/dashboard-two-canvases.json"), "d.json").Document!;
        var sheet = doc.Layouts.Single(l => l.Name == "sheet").Canvas!;
        Assert.Equal(297 * 96 / 25.4, sheet.WidthPx, 6);
        Assert.Equal(210 * 96 / 25.4, sheet.HeightPx, 6);
        Assert.Equal("scroll", doc.Layouts[1].Canvas!.Overflow);
    }

    [Fact]
    public void A_yaml_file_reads_the_same_as_its_json()
    {
        const string yaml = """
            schemaVersion: 1
            kind: dashboard
            title: Sales today
            parameters:
              - {name: region, type: string, for: customers.region, many: true, default: []}
            refresh: {every: 5m}
            widgets:
              revenue: {type: metric, encodings: {value: orders.net_revenue, compare: orders.net_revenue_prev}, query: {model: sales}, label: Revenue}
              by_region:
                type: chart
                mark: bar
                encodings: {x: customers.region, y: orders.net_revenue, color: products.category}
                query: {model: sales, order: [[orders.net_revenue, desc]], limit: 20}
              regions: {type: control, parameter: region}
            layouts:
              - name: desk
                type: reflow
                default: true
                grid: {columns: 12, rows: 8}
                tiles:
                  - {widget: regions, x: 0, y: 0, w: 4, h: 1}
                  - {widget: by_region, x: 0, y: 1, w: 8, h: 4, min: {w: 320, h: 200}}
                  - {widget: revenue, x: 8, y: 1, w: 4, h: 2}
            """;
        var result = DashboardLoader.Load(yaml, "views/sales.yaml");
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
        Assert.Equal(["revenue", "by_region", "regions"], result.Document!.Widgets.Select(w => w.Id));
    }

    [Fact]
    public void A_diagnostic_in_a_yaml_file_names_the_file_the_line_and_the_column()
    {
        const string yaml = "schemaVersion: 1\nkind: report\ntitle: R\nwidgets:\n  n: {type: text, text: 'Hi <b>there</b>'}\nlayouts:\n  - {name: p, type: flow, default: true, sections: [{blocks: [{widget: n}]}]}\n";
        var result = ReportLoader.Load(yaml, "reports/r.yaml");
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(("reports/r.yaml", ViewsCodes.UnsafeText, 5), (d.Path, d.Code, d.Line));
        Assert.True(d.Column > 1);
        Assert.Contains("reports/r.yaml:5:", d.ToString());
    }

    [Fact]
    public void A_file_that_is_not_yaml_or_json_is_one_diagnostic_and_an_empty_file_is_refused()
    {
        Assert.False(ViewsLoader.Load("{ not: [valid", "x.yaml").Ok);
        var empty = ViewsLoader.Load("", "x.yaml");
        Assert.Equal(ViewsCodes.InvalidValue, Assert.Single(empty.Diagnostics).Code);
        Assert.False(ViewsLoader.Load("- a\n- b\n", "x.yaml").Ok);
    }

    [Fact]
    public void A_newer_format_is_refused_naming_the_kind_the_file_and_both_numbers()
    {
        var result = ViewsLoader.Load(Conformance.Read("bad/newer-format.json"), "views/old.json");
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(ViewsCodes.FormatNewer, d.Code);
        Assert.Contains("dashboard", d.Message);
        Assert.Contains("views/old.json", d.Message);
        Assert.Contains("format 2", d.Message);
        Assert.Contains("format 1", d.Message);
    }

    [Fact]
    public void Each_loader_says_what_a_file_of_the_other_kind_is()
    {
        var asReport = ReportLoader.Load(Conformance.Read("good/dashboard-layouts.json"), "d.json");
        Assert.Contains("is a dashboard, not a report", Assert.Single(asReport.Diagnostics).Message);
        var asDashboard = DashboardLoader.Load(Conformance.Read("good/report-minimal.json"), "r.json");
        Assert.Contains("is a report, not a dashboard", Assert.Single(asDashboard.Diagnostics).Message);
        Assert.True(ViewsLoader.Load(Conformance.Read("good/report-minimal.json"), "r.json").Ok);
    }

}
