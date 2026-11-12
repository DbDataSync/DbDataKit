using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Export;
using DbDataKit.Views.Export.Tests;
using UglyToad.PdfPig;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// Custom views in the export (plan 32 DBR32), in a real Chromium with the real print bundle and a real built app (the SDK's fixture app, <c>npm run build:fixture-app -w @dbdatakit/visuals</c>): a custom widget is drawn in its box by a sandboxed frame loaded from the
/// app's files at the exporter's one-shot origin, with the rows the module resolved as the person who exports; a view that fails, or is taller than a page, refuses the export naming the widget; a custom page is the top-level document and paginates; and the
/// claim the design rests on (an iframe taller than a page does not split, a top-level document does) is tested on the browser at hand. Skipped, saying so, where the browser or a bundle is not built.
/// </summary>
[Collection("chromium")]
public sealed class CustomViewExportTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private const string Manifest = """
        schemaVersion: 1
        name: sales-portal
        version: 1.0.0
        entry: src/main.tsx
        semantic: [sales_catalog]
        views:
          - name: region-grid
            kind: widget
            export: RegionGrid
            queries:
              by_region: {semantic: {model: sales_catalog, request: {measures: [orders.net_revenue], attributes: [customers.region], limit: 100}}}
          - name: tall
            kind: widget
            export: TallWidget
            queries:
              by_region: {semantic: {model: sales_catalog, request: {measures: [orders.net_revenue], attributes: [customers.region], limit: 100}}}
          - name: failing
            kind: widget
            export: FailingWidget
            queries:
              by_region: {semantic: {model: sales_catalog, request: {measures: [orders.net_revenue], attributes: [customers.region], limit: 100}}}
          - name: regional
            kind: page
            export: RegionalPage
            formats: [pdf]
            page: {size: A4, orientation: portrait}
            queries:
              by_region: {semantic: {model: sales_catalog, request: {measures: [orders.net_revenue], attributes: [customers.region], limit: 100}}}
        """;

    private sealed class Apps(IReadOnlyDictionary<string, PrintAsset> files) : ICustomViewApps
    {
        private readonly Dictionary<string, CustomView> views = AppManifestLoader.Load(Manifest).Manifest!.CustomViews.ToDictionary(v => "sales-portal/" + v.Export);
        public CustomView? Find(string component) => views.GetValueOrDefault(component);
        public CustomAppFiles? Files(string component) => views.ContainsKey(component) ? new CustomAppFiles("sales-portal", files) : null;
        public CustomView View(string export) => views["sales-portal/" + export];
    }

    private static string? Find(string folder)
    {
        if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_" + (folder == "print" ? "PRINT_ASSETS" : "FIXTURE_APP")) is { Length: > 0 } env && Directory.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var root in new[] { Path.Combine(dir.FullName, "Shared", "web", "visuals", "dist"), Path.Combine(dir.FullName, "web", "visuals", "dist") })
                if (Directory.Exists(Path.Combine(root, folder))) return Path.Combine(root, folder);
        return null;
    }

    private static (string Print, IReadOnlyDictionary<string, PrintAsset> App) Bundles()
    {
        var print = Find("print");
        var app = Find("fixture-app");
        if (print is null || app is null || !File.Exists(Path.Combine(print, "print.js")) || !File.Exists(Path.Combine(app, "app.js")))
        {
            if (TestBrowser.Required) Assert.Fail("the print bundle and the fixture app are required here: run `npm run build:print` and `npm run build:fixture-app` in @dbdatakit/visuals.");
            Assert.Skip("The print bundle or the fixture app is not built (npm run build:print, npm run build:fixture-app in @dbdatakit/visuals; DBDATAKIT_TEST_PRINT_ASSETS and DBDATAKIT_TEST_FIXTURE_APP name the folders): this test was not run.");
        }
        var files = Directory.EnumerateFiles(app!, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(app!, f).Replace('\\', '/'),
            f => new PrintAsset(File.ReadAllBytes(f), f.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" : "text/html; charset=utf-8"));
        return (print!, files);
    }

    private static string Board(string widgets, string tiles) => $$"""
        schemaVersion: 1
        kind: dashboard
        title: Custom board
        widgets:
          revenue:
            type: metric
            label: Revenue
            encodings: {value: orders.net_revenue}
            query: {model: {{ViewsHarness.Model}}}
        {{widgets}}
        layouts:
          - name: desk
            type: reflow
            default: true
            grid: {columns: 12, rows: 6}
            tiles:
              - {widget: revenue, x: 0, y: 0, w: 4, h: 2}
        {{tiles}}
        """;

    private async Task<(ViewsHarness H, ChromiumExporter Exporter, Apps Apps)> StartAsync(string board)
    {
        var (print, files) = Bundles();
        var apps = new Apps(files);
        var exporter = new ChromiumExporter(TestBrowser.Settings());
        var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, exporter: exporter, pageAssets: print, customApps: apps);
        h.Publisher.Publish(new PublishRequest("dashboard", "custom-board", board, "ada", "prod", PublishMode.Serve, [$"semantic-model:{ViewsHarness.Model}@1"]));
        h.Module.Refresh();
        return (h, exporter, apps);
    }

    private static string[] PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    /// <summary>The words of a refusal (the body is JSON, which writes a backtick as an escape).</summary>
    private static string Words(byte[] body) => (string?)JsonNode.Parse(body)?["message"] ?? Encoding.UTF8.GetString(body);

    private const string GridBoard = "  grid: {type: custom, title: Regions, component: sales-portal/RegionGrid}";
    private const string GridTile = "      - {widget: grid, x: 4, y: 0, w: 8, h: 4}";

    [Fact]
    public async Task A_custom_widget_is_drawn_in_its_box_by_the_apps_own_frame_with_the_rows_of_the_person_who_exports()
    {
        var (h, exporter, _) = await StartAsync(Board(GridBoard, GridTile));
        await using var _1 = h;
        await using var _2 = exporter;

        var all = await h.GetBytesAsync("/api/v1/dashboards/custom-board/export?format=pdf", "all");
        Assert.True(all.Status == 200, Encoding.UTF8.GetString(all.Bytes));
        var allText = string.Join(" ", PageTexts(all.Bytes));
        Assert.Contains("RegionGrid print", allText);          // drawn in the print mode, by the frame
        Assert.Contains("West", allText);
        Assert.Contains("East", allText);
        // nothing was blocked or missing: every file the frame loaded was the app's own, from the one-shot origin
        Assert.False(all.Response.Headers.Contains("X-DbData-Export-Warnings"), "an export of a custom widget loads only the app's own files");

        var west = await h.GetBytesAsync("/api/v1/dashboards/custom-board/export?format=pdf", "west");
        Assert.Equal(200, west.Status);
        var westText = string.Join(" ", PageTexts(west.Bytes));
        Assert.Contains("West", westText);
        Assert.DoesNotContain("East", westText);                // the rows differ for a person with other access: the module resolved them as that person
    }

    [Fact]
    public async Task A_view_that_fails_refuses_the_export_and_names_the_widget()
    {
        var (h, exporter, _) = await StartAsync(Board("  broken: {type: custom, component: sales-portal/FailingWidget}", "      - {widget: broken, x: 4, y: 0, w: 8, h: 4}"));
        await using var _1 = h;
        await using var _2 = exporter;
        var answer = await h.GetBytesAsync("/api/v1/dashboards/custom-board/export?format=pdf", "all");
        var text = Words(answer.Bytes);
        Assert.Equal(500, answer.Status);
        Assert.True(text.Contains("`broken`"), text);
        Assert.Contains("the chart library did not load", text);
    }

    [Fact]
    public async Task A_widget_taller_than_a_page_is_an_export_error_naming_it()
    {
        var (h, exporter, _) = await StartAsync(Board("  tall: {type: custom, component: sales-portal/TallWidget}", "      - {widget: tall, x: 4, y: 0, w: 8, h: 4}"));
        await using var _1 = h;
        await using var _2 = exporter;
        var answer = await h.GetBytesAsync("/api/v1/dashboards/custom-board/export?format=pdf", "all");
        var text = Words(answer.Bytes);
        Assert.Equal(500, answer.Status);
        Assert.Contains("`tall`", text);
        Assert.Contains("taller than a page", text);
    }

    [Fact]
    public async Task A_widget_the_host_has_no_app_for_is_an_export_error_not_a_silent_gap()
    {
        var (h, exporter, _) = await StartAsync(Board("  ghost: {type: custom, component: other-app/Thing}", "      - {widget: ghost, x: 4, y: 0, w: 8, h: 4}"));
        await using var _1 = h;
        await using var _2 = exporter;
        var answer = await h.GetBytesAsync("/api/v1/dashboards/custom-board/export?format=pdf", "all");
        var text = Words(answer.Bytes);
        Assert.Contains("`ghost`", text);
        Assert.Contains("custom_not_served", text);
    }

    [Fact]
    public async Task A_custom_page_is_the_top_level_document_and_paginates_with_the_rows_embedded_as_json()
    {
        var (print, files) = Bundles();
        _ = print;
        var apps = new Apps(files);
        var settings = TestBrowser.Settings();
        await using var exporter = new ChromiumExporter(settings);
        Assert.True((await exporter.CheckAsync(TestContext.Current.CancellationToken)).Available);
        var view = apps.View("RegionalPage");
        var resolved = JsonNode.Parse("""{"type":"custom","component":"sales-portal/RegionalPage","data":{"by_region":{"columns":["customers_region","orders_net_revenue"],"rows":[{"customers_region":"West","orders_net_revenue":900},{"customers_region":"</script><b>East","orders_net_revenue":700}]}}}""")!.AsObject();

        var job = CustomViewPrint.Page(view, apps.Files("sales-portal/RegionalPage")!, resolved, CustomViewPrint.PaperOf(view), "Regional detail", exporter.Fonts);
        Assert.DoesNotContain("</script><b>East", job.Html);       // a value cannot close the data block
        var result = await exporter.ExportPdfAsync(job, TestContext.Current.CancellationToken);
        var texts = PageTexts(result.Pdf);
        Assert.Equal(3, result.Pages);                              // three sections separated by PageBreak: three pages
        for (var n = 0; n < 3; n++) Assert.Contains($"Section {n + 1}", texts[n]);
        Assert.Contains("West=900", texts[0]);
        Assert.Contains("</script><b>East=700", texts[2]);
        using var pdf = PdfDocument.Open(result.Pdf);
        Assert.Equal(210 / 25.4 * 72, pdf.GetPage(1).Width, 0.8);   // A4 portrait from the manifest's page
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task A_request_of_a_custom_page_outside_the_origin_is_blocked_and_named()
    {
        var (_, files) = Bundles();
        var apps = new Apps(files);
        await using var exporter = new ChromiumExporter(TestBrowser.Settings());
        Assert.True((await exporter.CheckAsync(TestContext.Current.CancellationToken)).Available);
        var withImage = new Dictionary<string, PrintAsset>(files)
        {
            ["index.html"] = new PrintAsset(Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"></head><body><img src=\"https://tracker.example.com/pixel.png\"><script src=\"app.js\"></script></body></html>"), "text/html; charset=utf-8"),
        };
        var view = apps.View("RegionalPage");
        var job = CustomViewPrint.Page(view, new CustomAppFiles("sales-portal", withImage), JsonNode.Parse("""{"data":{"by_region":{"rows":[]}}}""")!.AsObject(), CustomViewPrint.PaperOf(view), "Regional detail", exporter.Fonts);
        var result = await exporter.ExportPdfAsync(job, TestContext.Current.CancellationToken);
        Assert.Contains(result.Warnings, w => w.Contains("tracker.example.com", StringComparison.Ordinal) && w.Contains("blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_iframe_taller_than_a_page_is_cut_across_pages_and_one_shorter_than_a_page_is_clipped_to_its_box()
    {
        // What was believed (an iframe taller than a page does not split) is NOT what Chromium does: it fragments the iframe's box across pages, the frame's content continuing on the next page. That is why a widget
        // taller than a page is an export error (a cut frame is unreadable), why a widget is drawn in a box that fits a page, and why a custom page is the top-level document. Tested on the browser at hand; the pinned build is the CI job's.
        await using var exporter = new ChromiumExporter(TestBrowser.Settings());
        Assert.True((await exporter.CheckAsync(TestContext.Current.CancellationToken)).Available);
        var lines = string.Concat(Enumerable.Range(1, 60).Select(i => $"<div style=\"height:50px\">LINE{i:00}</div>"));
        var assets = new Dictionary<string, PrintAsset> { ["tall.html"] = new PrintAsset(Encoding.UTF8.GetBytes($"<!doctype html><html><body style=\"margin:0\">{lines}</body></html>"), "text/html; charset=utf-8") };
        var layout = new PdfLayout(210, 297, new PageMargins(10, 10, 10, 10));
        string Page(int height) => $"<!doctype html><html><body style=\"margin:0\"><p>before</p><iframe src=\"tall.html\" style=\"width:100%;height:{height}px;border:0\"></iframe><script>window.__reportReady = true</script></body></html>";

        var tall = await exporter.ExportPdfAsync(new PrintJob("tall", Page(3000), assets, layout), TestContext.Current.CancellationToken);
        var tallTexts = PageTexts(tall.Pdf);
        Assert.True(tall.Pages >= 3, $"a 3000px iframe spans pages; got {tall.Pages}");
        var first = Array.FindIndex(tallTexts, t => t.Contains("LINE01", StringComparison.Ordinal));
        Assert.True(first >= 0 && first + 1 < tallTexts.Length);
        Assert.DoesNotContain("LINE22", tallTexts[first]);
        Assert.Contains("LINE22", tallTexts[first + 1]);            // the frame's content continues on the next page: the box is split

        var short_ = await exporter.ExportPdfAsync(new PrintJob("short", Page(600), assets, layout), TestContext.Current.CancellationToken);
        Assert.Equal(1, short_.Pages);
        var shortText = PageTexts(short_.Pdf)[0];
        Assert.Contains("LINE01", shortText);
        Assert.DoesNotContain("LINE20", shortText);                 // 600px shows twelve lines: what overflows its box is clipped, never carried to another page
    }
}
