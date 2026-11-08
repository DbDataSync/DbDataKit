using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Export;
using DbDataKit.Views.Export.Tests;
using UglyToad.PdfPig;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// Export through the module (plan 32 DBR15): <c>GET .../{name}/export?format=pdf&amp;layout=</c> resolves the layout as the caller, composes the print page, and has the exporter draw it in a real Chromium; the print route and its files;
/// the refusals in words that name the setting; the audit row with the browser's version. Most tests use a small stand-in for the visuals' print bundle (it draws the rendered widgets as plain text), so the module's part is tested alone;
/// <see cref="The_visuals_print_bundle_draws_the_dashboard_and_the_report_into_a_pdf"/> uses the real bundle when it is built. Run on SQLite (the state engine is not what these tests are about; the module's own suite covers every engine).
/// </summary>
public sealed class ViewsExportTests : IDisposable
{
    private readonly StateEngines engines = new();
    private readonly List<string> folders = [];

    public void Dispose()
    {
        engines.Dispose();
        foreach (var f in folders) try { Directory.Delete(f, true); } catch (IOException) { }
    }

    private const string StandInScript = """
        (function () {
          var p = JSON.parse(document.getElementById('dbdatakit-print').textContent);
          var root = document.getElementById('root');
          var h = document.createElement('h1'); h.textContent = p.title; root.appendChild(h);
          var w = p.rendered.widgets;
          Object.keys(w).forEach(function (id) {
            var x = w[id]; var d = document.createElement('div');
            if (x.type === 'metric') d.textContent = 'Metric ' + id + ': ' + (x.value ? x.value.text : '-');
            else if (x.type === 'table') d.textContent = 'Table ' + id + ' rows ' + x.rows.length;
            else d.textContent = id + ' ' + x.type;
            root.appendChild(d);
          });
          document.fonts.ready.then(function () { window.__reportReady = true; });
        })();
        """;

    private string PageAssets(string? script = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        folders.Add(folder);
        File.WriteAllText(Path.Combine(folder, "print.js"), script ?? StandInScript);
        File.WriteAllText(Path.Combine(folder, "print.css"), "body { font-family: 'DbDataKit Sans', sans-serif; font-size: 12pt }");
        return folder;
    }

    private async Task<(ViewsHarness H, ChromiumExporter Exporter)> StartAsync(string? pageAssets = null, Func<ExportSettings, ExportSettings>? change = null)
    {
        var exporter = new ChromiumExporter(TestBrowser.Settings(change));
        var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, exporter: exporter, pageAssets: pageAssets ?? PageAssets());
        return (h, exporter);
    }

    private static string[] PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    private static string RichReport(string title = "Quarterly summary") => """
        schemaVersion: 1
        kind: report
        title: "__TITLE__"
        widgets:
          revenue:
            type: metric
            label: Revenue
            encodings: {value: orders.net_revenue}
            query: {model: __MODEL__}
        layouts:
          - name: print
            type: flow
            default: true
            page: {size: A4, orientation: landscape, margins: {top: 20, right: 15, bottom: 20, left: 15}}
            header: {left: "{title}", right: "{asof}"}
            footer: {left: "{viewer}", right: "Page {page} of {pages}"}
            sections:
              - title: Summary
                blocks:
                  - {widget: revenue}
        """.Replace("__TITLE__", title.Replace("\"", "\\\""), StringComparison.Ordinal).Replace("__MODEL__", ViewsHarness.Model, StringComparison.Ordinal);

    private void PublishRich(ViewsHarness h, string title = "Quarterly summary")
    {
        h.Publisher.Publish(new PublishRequest("report", "quarterly", RichReport(title), "ada", "prod", PublishMode.Serve, [$"semantic-model:{ViewsHarness.Model}@1"]));
        h.Module.Refresh();
    }

    private IReadOnlyList<AuditRecord> Audited(ViewsHarness h, string verb) => [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 100)).Reverse()];

    [Fact]
    public async Task A_report_is_exported_to_a_pdf_with_the_layouts_paper_header_and_footer_and_audited_with_the_browser()
    {
        var (h, exporter) = await StartAsync();
        await using var _ = h;
        await using var __ = exporter;
        PublishRich(h);

        var (status, bytes, response) = await h.GetBytesAsync("/api/v1/reports/quarterly/export?format=pdf", "all");
        Assert.Equal(200, status);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("quarterly-print.pdf", response.Content.Headers.ContentDisposition!.FileName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));

        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal("Quarterly summary", pdf.Information.Title);
        Assert.Equal(297 / 25.4 * 72, pdf.GetPage(1).Width, 0.8);   // A4 landscape from the layout's page
        Assert.Equal(210 / 25.4 * 72, pdf.GetPage(1).Height, 0.8);
        var text = PageTexts(bytes)[0];
        Assert.Contains("Metric revenue: 1,600", text);              // the rows of the caller who exported
        Assert.Contains("Page 1 of 1", text);                         // the footer's tokens are the browser's page fields
        Assert.Contains("u-all", text);                               // {viewer}
        Assert.Contains("Quarterly summary", text);                   // {title} in the header (and the page's own heading)
        Assert.Matches(@"\d{4}\D?\d{2}\D?\d{2} \d{2}\D?\d{2} UTC", text);   // {asof} (the text reader may leave out the separators of the header's small type)

        var row = Assert.Single(Audited(h, "view.export")).Entry;
        Assert.Equal(("ok", "u-all", "quarterly@2".Replace("@2", "@1")), (row.Outcome, row.ActorId, row.TargetId));
        var detail = JsonNode.Parse(row.Detail!)!;
        Assert.Equal(("pdf", "print", 1), (detail["format"]!.GetValue<string>(), detail["layout"]!.GetValue<string>(), detail["pages"]!.GetValue<int>()));
        Assert.Matches(@"Chrome/\d+\.\d+\.\d+\.\d+$", detail["browser"]!.GetValue<string>());
        Assert.Equal(response.Headers.GetValues("X-DbData-Export-Browser").Single(), detail["browser"]!.GetValue<string>());
        Assert.Contains(detail["mode"]!.GetValue<string>(), new[] { "pinned", "system" });
    }

    [Fact]
    public async Task Two_callers_get_two_pdfs_of_their_own_rows_and_a_named_layout_and_a_parameter_are_honoured()
    {
        var (h, exporter) = await StartAsync();
        await using var _ = h;
        await using var __ = exporter;

        var all = PageTexts((await h.GetBytesAsync("/api/v1/dashboards/sales/export?format=pdf", "all")).Bytes)[0];
        var west = PageTexts((await h.GetBytesAsync("/api/v1/dashboards/sales/export?format=pdf", "west")).Bytes)[0];
        Assert.Contains("Metric revenue: 1,600", all);
        Assert.Contains("Metric revenue: 900", west);
        Assert.DoesNotContain("1,600", west);

        // a dashboard without a print layout exports its default layout on one page of A4 landscape; a named layout is the one asked for
        var kpi = await h.GetBytesAsync("/api/v1/dashboards/sales/export?format=pdf&layout=kpi", "all");
        Assert.Equal(200, kpi.Status);
        Assert.Equal("sales-kpi.pdf", kpi.Response.Content.Headers.ContentDisposition!.FileName);
        Assert.Contains("kpi", JsonNode.Parse(Audited(h, "view.export")[^1].Entry.Detail!)!["layout"]!.GetValue<string>());
        using (var pdf = PdfDocument.Open(all is null ? [] : (await h.GetBytesAsync("/api/v1/dashboards/sales/export", "all")).Bytes))
        {
            Assert.Equal(297 / 25.4 * 72, pdf.GetPage(1).Width, 0.8);
            Assert.Single(pdf.GetPages());
        }

        // a parameter from the address narrows the rows, and its value is never in the audit
        var one = PageTexts((await h.GetBytesAsync("/api/v1/dashboards/sales/export?p.region=West", "all")).Bytes)[0];
        Assert.Contains("Metric revenue: 900", one);
        Assert.DoesNotContain("West", Audited(h, "view.export")[^1].Entry.Detail!);
    }

    [Fact]
    public async Task A_request_that_is_not_one_is_refused_in_words_and_audited()
    {
        var (h, exporter) = await StartAsync();
        await using var _ = h;
        await using var __ = exporter;

        var layout = await h.GetAsync("/api/v1/dashboards/sales/export?layout=nope", "all");
        Assert.Equal(400, layout.Status);
        Assert.Contains("nope", layout.Object["message"]!.GetValue<string>());
        var svg = await h.GetAsync("/api/v1/dashboards/sales/export?format=svg", "all");
        Assert.Equal((400, "format_unsupported"), (svg.Status, svg.Object["code"]!.GetValue<string>()));
        Assert.Equal((400, "invalid"), ((await h.GetAsync("/api/v1/dashboards/sales/export?format=docx", "all")).Status, "invalid"));
        Assert.Equal(404, (await h.GetAsync("/api/v1/dashboards/missing/export", "all")).Status);
        Assert.Equal(401, (await h.GetAsync("/api/v1/dashboards/sales/export", null)).Status);
        // an app whose manifest lacks the model is refused before anything is drawn
        Assert.Equal(403, (await h.GetAsync("/api/v1/dashboards/sales/export", "app-other")).Status);
        Assert.Equal(["refused", "refused", "refused", "refused", "refused"], Audited(h, "view.export").Select(r => r.Entry.Outcome));
    }

    [Fact]
    public async Task A_host_without_a_browser_names_the_setting_keeps_serving_screens_and_says_so_in_the_health()
    {
        var missing = new ChromiumExporter(new ExportSettings { Mode = BrowserMode.System, Path = Path.Combine(Path.GetTempPath(), "no-browser-here", "chrome") });
        await using var _ = missing;
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, exporter: missing, pageAssets: PageAssets());

        var export = await h.GetAsync("/api/v1/dashboards/sales/export", "all");
        Assert.Equal((501, "export_unavailable"), (export.Status, export.Object["code"]!.GetValue<string>()));
        Assert.Contains("views.export.path", export.Object["message"]!.GetValue<string>());
        var health = (await h.GetAsync("/health", null)).Object;
        Assert.False(health["export"]!["available"]!.GetValue<bool>());
        Assert.StartsWith("export unavailable:", health["export"]!["reason"]!.GetValue<string>());
        Assert.Equal("ok", health["state"]!.GetValue<string>());     // the module keeps serving
        Assert.Equal(200, (await h.RenderAsync("all")).Status);
        Assert.Equal(200, (await h.GetAsync("/api/v1/dashboards/sales", "all")).Status);
    }

    [Fact]
    public async Task A_host_whose_print_page_is_missing_says_so()
    {
        var (h, exporter) = await StartAsync(pageAssets: Path.Combine(Path.GetTempPath(), "no-print-page"));
        await using var _ = h;
        await using var __ = exporter;
        var export = await h.GetAsync("/api/v1/dashboards/sales/export", "all");
        Assert.Equal(501, export.Status);
        Assert.Contains("views.export.page_assets", export.Object["message"]!.GetValue<string>());
        Assert.False((await h.GetAsync("/health", null)).Object["export"]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_health_of_an_exporting_host_names_the_browser()
    {
        var (h, exporter) = await StartAsync();
        await using var _ = h;
        await using var __ = exporter;
        var export = (await h.GetAsync("/health", null)).Object["export"]!;
        Assert.True(export["available"]!.GetValue<bool>());
        Assert.Matches(@"Chrome/\d+\.\d+\.\d+\.\d+$", export["browser"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_print_route_serves_the_same_page_with_its_files_from_the_module_and_nothing_from_outside()
    {
        var (h, exporter) = await StartAsync();
        await using var _ = h;
        await using var __ = exporter;
        PublishRich(h, "Title </script><script>alert(1)</script>");

        var page = await h.GetBytesAsync("/api/v1/reports/quarterly/print", "all");
        Assert.Equal(200, page.Status);
        Assert.Equal("text/html", page.Response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("connect-src 'none'", string.Join(";", page.Response.Headers.GetValues("Content-Security-Policy")));
        var html = Encoding.UTF8.GetString(page.Bytes);
        Assert.Contains("id=\"dbdatakit-print\"", html);
        Assert.Contains("src=\"/api/v1/views/print/print.js\"", html);
        Assert.Contains("@page { size: A4 landscape; margin: 20mm 15mm 20mm 15mm }", html);
        Assert.Contains("font-family:\"DbDataKit Sans\"", html);
        // a title cannot close the script block or the title element
        Assert.DoesNotContain("</script><script>alert", html);
        Assert.Equal(2, Regex.Matches(html, "</script>").Count);       // the data block's and the bundle's own
        Assert.Contains("\\u003c/script>\\u003cscript>alert(1)", html);
        // every address in the page is the module's own
        Assert.DoesNotMatch(@"(src|href)=""https?://", html);
        Assert.DoesNotMatch(@"url\(https?://", html);

        var js = await h.GetBytesAsync("/api/v1/views/print/print.js", null);
        Assert.Equal((200, "text/javascript"), (js.Status, js.Response.Content.Headers.ContentType!.MediaType));
        Assert.Equal(200, (await h.GetBytesAsync("/api/v1/views/print/print.css", null)).Status);
        if (exporter.Fonts is { } fonts)
        {
            var font = fonts.Assets.Keys.First();
            var served = await h.GetBytesAsync("/api/v1/views/print/" + font, null);
            Assert.Equal(200, served.Status);
            Assert.Equal(fonts.Assets[font].Content, served.Bytes);
        }
        Assert.Equal(404, (await h.GetBytesAsync("/api/v1/views/print/..%2F..%2Fserver.yaml", null)).Status);
        Assert.Equal(404, (await h.GetBytesAsync("/api/v1/views/print/other.js", null)).Status);
        Assert.Equal(["ok"], Audited(h, "view.print").Select(r => r.Entry.Outcome));
    }

    [Fact]
    public async Task A_page_that_cannot_be_drawn_is_a_500_in_its_words_and_audited_as_failed()
    {
        var (h, exporter) = await StartAsync(pageAssets: PageAssets("window.__reportError = 'the print bundle is the wrong version'"));
        await using var _ = h;
        await using var __ = exporter;
        var export = await h.GetAsync("/api/v1/dashboards/sales/export", "all");
        Assert.Equal((500, "export_failed"), (export.Status, export.Object["code"]!.GetValue<string>()));
        Assert.Contains("the print bundle is the wrong version", export.Object["message"]!.GetValue<string>());
        Assert.Equal("failed", Assert.Single(Audited(h, "view.export")).Entry.Outcome);
    }

    [Fact]
    public async Task A_page_over_the_deadline_is_a_504_and_over_the_page_limit_a_413()
    {
        var (h, exporter) = await StartAsync(pageAssets: PageAssets("/* never ready */"), change: s => s with { Deadline = TimeSpan.FromSeconds(3) });
        await using var _ = h;
        await using var __ = exporter;
        var late = await h.GetAsync("/api/v1/dashboards/sales/export", "all");
        Assert.Equal((504, "export_timeout"), (late.Status, late.Object["code"]!.GetValue<string>()));
        Assert.Contains("views.export.deadline_seconds", late.Object["message"]!.GetValue<string>());

        var (h2, exporter2) = await StartAsync(pageAssets: PageAssets(StandInScript + "\n document.body.insertAdjacentHTML('beforeend', '<div style=\"break-before:page\">two</div><div style=\"break-before:page\">three</div>');"), change: s => s with { MaxPages = 1 });
        await using var _2 = h2;
        await using var __2 = exporter2;
        var big = await h2.GetAsync("/api/v1/dashboards/sales/export", "all");
        Assert.Equal((413, "export_too_large"), (big.Status, big.Object["code"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_visuals_print_bundle_draws_the_dashboard_and_the_report_into_a_pdf()
    {
        var bundle = FindBundle();
        if (bundle is null)
        {
            if (TestBrowser.Required) Assert.Fail("the print bundle is required here: run `npm run build:print -w @dbdatakit/visuals` and set DBDATA_TEST_PRINT_ASSETS.");
            Assert.Skip("The visuals' print bundle is not built (npm run build:print -w @dbdatakit/visuals; DBDATA_TEST_PRINT_ASSETS names the folder): this test was not run.");
        }
        var (h, exporter) = await StartAsync(pageAssets: bundle);
        await using var _ = h;
        await using var __ = exporter;
        PublishRich(h);

        var report = await h.GetBytesAsync("/api/v1/reports/quarterly/export", "all");
        Assert.True(report.Status == 200, Encoding.UTF8.GetString(report.Bytes));
        var reportText = PageTexts(report.Bytes);
        Assert.Contains("Quarterly summary", reportText[0]);
        Assert.Contains("Summary", reportText[0]);              // the section's title, drawn by the report's own view
        Assert.Contains("1,600", reportText[0]);
        Assert.Contains("Page 1 of", reportText[0]);

        var board = await h.GetBytesAsync("/api/v1/dashboards/sales/export", "west");
        Assert.True(board.Status == 200, Encoding.UTF8.GetString(board.Bytes));
        var boardText = string.Join(" ", PageTexts(board.Bytes));
        Assert.Contains("customers.region", boardText);        // the table's column heading, drawn by the dashboard's own view
        Assert.Contains("900", boardText);
        Assert.DoesNotContain("1,600", boardText);
        // the bundled fonts, not the system's
        Assert.Contains(PdfDocument.Open(board.Bytes).GetPages().SelectMany(p => p.Letters).Select(l => l.FontName ?? ""), f => f.Contains("Noto", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindBundle()
    {
        if (Environment.GetEnvironmentVariable("DBDATA_TEST_PRINT_ASSETS") is { Length: > 0 } env && File.Exists(Path.Combine(env, "print.js"))) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var built = Path.Combine(dir.FullName, "DbDataKit", "web", "visuals", "dist", "print");
            if (File.Exists(Path.Combine(built, "print.js"))) return built;
            built = Path.Combine(dir.FullName, "web", "visuals", "dist", "print");
            if (File.Exists(Path.Combine(built, "print.js"))) return built;
        }
        return null;
    }
}
