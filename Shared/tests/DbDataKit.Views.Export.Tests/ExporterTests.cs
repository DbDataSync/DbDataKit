using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Views.Export;
using UglyToad.PdfPig;

namespace DbDataKit.Views.Export.Tests;

/// <summary>
/// The exporter against a real Chromium (plan 32 DBR15). The fixture page is a small report in the shape the module's print page has: the bundled fonts by <c>@font-face</c> from the page's own origin, a table whose head repeats, sections that start a page,
/// a ready signal. Tests compare structure (page count, each page's text, the outline, the embedded fonts), never pixels or bytes. Skipped when no browser is found, and the skip says so; they fail in the job that fetched the pin.
/// </summary>
[Collection("chromium")]
public sealed class ExporterTests
{
    private const string Ready = "document.fonts.ready.then(function(){ window.__reportReady = true })";

    private static PrintJob Report(ExportSettings settings, BundledFonts? fonts, int rows = 120, string? extraBody = null, string? script = null, PdfLayout? layout = null)
    {
        var body = new StringBuilder();
        body.Append("<h1>Sales by region</h1><p>The first section.</p>");
        body.Append("<table><thead><tr><th>Name</th><th>Amount</th></tr></thead><tbody>");
        for (var i = 1; i <= rows; i++) body.Append($"<tr><td>Row {i:000}</td><td>{i * 10}</td></tr>");
        body.Append("</tbody></table>");
        body.Append("<h1 class=\"second\">Second section</h1><p>It starts a page.</p>").Append(extraBody);
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Sales by region</title>
            <style>
            {{fonts?.FontFaceCss() ?? ""}}
            body { font-family: "DbDataKit Sans", sans-serif; font-size: 11pt; margin: 0 }
            table { width: 100%; border-collapse: collapse } thead { display: table-header-group } tr { break-inside: avoid }
            td, th { padding: 6px 4px; border-bottom: 1px solid #ccc; text-align: left }
            h1.second { break-before: page }
            </style></head><body>{{body}}<script>{{script ?? Ready}}</script></body></html>
            """;
        return new PrintJob("Sales by region", html, new Dictionary<string, PrintAsset>(), layout ?? new PdfLayout(210, 297, new PageMargins(20, 15, 20, 15),
            "<div style=\"font-size:8px;width:100%;padding:0 15mm\"><span class=\"title\"></span></div>",
            "<div style=\"font-size:8px;width:100%;text-align:right;padding:0 15mm\">Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>"));
    }

    private static async Task<(ChromiumExporter Exporter, ExportSettings Settings)> Started(Func<ExportSettings, ExportSettings>? change = null)
    {
        var settings = TestBrowser.Settings(change);
        var exporter = new ChromiumExporter(settings);
        var availability = await exporter.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(availability.Available, availability.Reason);
        return (exporter, settings);
    }

    private static string[] PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    [Fact]
    public async Task A_report_exports_to_pages_whose_text_and_numbers_are_the_documents()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        if (exporter.Fonts is null) Assert.Skip("The bundled fonts are not installed (scripts/fetch-fonts); this test needs them.");
        var result = await exporter.ExportPdfAsync(Report(settings, exporter.Fonts), TestContext.Current.CancellationToken);

        var texts = PageTexts(result.Pdf);
        Assert.Equal(result.Pages, texts.Length);
        Assert.True(result.Pages >= 3, $"120 rows and a second section must make at least three pages; got {result.Pages}.");
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(result.Pdf, 0, 5));
        // the head repeats on the pages of the table, the rows are in order and whole, the second section starts the last page
        Assert.Contains("Row 001", texts[0]);
        for (var p = 0; p < texts.Length - 1; p++) Assert.Contains("Name Amount", texts[p]);
        Assert.Contains("Row 120", texts[^2]);
        Assert.Contains("Second section", texts[^1]);
        Assert.DoesNotContain("Second section", texts[0]);
        Assert.Equal(120, texts.Sum(t => System.Text.RegularExpressions.Regex.Matches(t, @"Row \d{3}").Count));
        // "Page n of N" is the browser's own footer, on every page
        for (var p = 0; p < texts.Length; p++) Assert.Contains($"Page {p + 1} of {texts.Length}", texts[p]);
        Assert.All(texts, t => Assert.Contains("Sales by region", t));
    }

    [Fact]
    public async Task The_pdf_embeds_only_the_bundled_fonts_and_carries_the_outline_and_the_title()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        if (exporter.Fonts is null) Assert.Skip("The bundled fonts are not installed (scripts/fetch-fonts); this test needs them.");
        var result = await exporter.ExportPdfAsync(Report(settings, exporter.Fonts, rows: 10), TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.EmbeddedFonts);
        // the body is Noto Sans from the bundle (a footer set by the browser's own template may name its default font: only the page's letters are listed here, and the header and footer are in the page's family through the template)
        Assert.Contains(result.EmbeddedFonts, f => f.Contains("Noto", StringComparison.OrdinalIgnoreCase));
        using var document = PdfDocument.Open(result.Pdf);
        Assert.Equal("Sales by region", document.Information.Title);
        // the file says how it was made (DBR16): the product, the browser's build and the fonts it embeds, beside the producer the browser wrote
        Assert.Equal(PdfMetadata.Creator, document.Information.Creator);
        Assert.Contains($"browser: {result.BrowserVersion}", document.Information.Keywords);
        Assert.All(result.EmbeddedFonts, f => Assert.Contains(f, document.Information.Keywords));
        Assert.StartsWith("Skia/PDF", document.Information.Producer);
        Assert.True(document.TryGetBookmarks(out var bookmarks));
        var titles = bookmarks.GetNodes().Select(n => n.Title).ToList();
        Assert.Contains("Sales by region", titles);
        Assert.Contains("Second section", titles);
    }

    [Fact]
    public void A_pdf_the_stamp_cannot_read_comes_back_as_it_was()
    {
        byte[] notAPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");
        Assert.Same(notAPdf, PdfMetadata.Stamp(notAPdf, "k"));
        byte[] encrypted = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<</Title (T)>>\nendobj\ntrailer\n<</Size 2 /Root 1 0 R /Info 1 0 R /Encrypt 9 0 R>>\nstartxref\n9\n%%EOF\n");
        Assert.Same(encrypted, PdfMetadata.Stamp(encrypted, "k"));
    }

    [Fact]
    public async Task A_picture_of_the_page_is_a_png_as_wide_as_the_paper_and_as_tall_as_its_content()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        if (exporter.Fonts is null) Assert.Skip("The bundled fonts are not installed (scripts/fetch-fonts); this test needs them.");
        var result = await exporter.ExportPngAsync(Report(settings, exporter.Fonts, rows: 10), 4000, TestContext.Current.CancellationToken);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], result.Png.Take(4).Select(b => (int)b));
        Assert.Equal(794, result.Width);          // A4 at 96 pixels to the inch
        Assert.InRange(result.Height, 200, 4000);
        Assert.True(result.Png.Length > 5_000, "a drawn page is more than a blank picture");
        var capped = await exporter.ExportPngAsync(Report(settings, exporter.Fonts, rows: 120), 600, TestContext.Current.CancellationToken);
        Assert.Equal(600, capped.Height);
        Assert.Contains(capped.Warnings, w => w.Contains("first 600 pixels", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_layouts_paper_is_the_pdfs_page_size()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        var landscape = Report(settings, exporter.Fonts, rows: 3, layout: new PdfLayout(297, 210, new PageMargins(10, 10, 10, 10)));
        var result = await exporter.ExportPdfAsync(landscape, TestContext.Current.CancellationToken);
        using var document = PdfDocument.Open(result.Pdf);
        var page = document.GetPage(1);
        Assert.Equal(297 / 25.4 * 72, page.Width, 0.8);
        Assert.Equal(210 / 25.4 * 72, page.Height, 0.8);
    }

    [Fact]
    public async Task An_export_makes_no_request_outside_and_says_what_it_blocked()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        var hits = 0;
        var (listener, port) = StartListener();
        using var _listener = listener;
        var serving = Task.Run(async () =>
        {
            try { while (true) { var c = await listener.GetContextAsync(); Interlocked.Increment(ref hits); c.Response.Close(); } }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        });
        // an image, a stylesheet and a fetch, to loopback and to the world: none may leave
        var extra = $"<img src=\"http://127.0.0.1:{port}/image.png\"><link rel=\"stylesheet\" href=\"http://127.0.0.1:{port}/style.css\"><img src=\"https://example.com/logo.png\">";
        var script = $"fetch('http://127.0.0.1:{port}/fetch').catch(function(){{}}).then(function(){{ return fetch('https://example.org/'); }}).catch(function(){{}}).then(function(){{ window.__reportReady = true }})";
        var result = await exporter.ExportPdfAsync(Report(settings, exporter.Fonts, rows: 3, extraBody: extra, script: script), TestContext.Current.CancellationToken);
        listener.Stop();
        await serving;

        Assert.Equal(0, hits);
        Assert.Contains(result.Warnings, w => w.Contains($"127.0.0.1:{port}", StringComparison.Ordinal) && w.Contains("blocked", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("example.com", StringComparison.Ordinal));
        Assert.True(result.Pages >= 1);
    }

    [Fact]
    public async Task A_page_that_is_never_ready_is_stopped_at_the_deadline_and_the_browser_goes()
    {
        var (exporter, _) = await Started(s => s with { Deadline = TimeSpan.FromSeconds(3) });
        await using var __ = exporter;
        var job = Report(TestBrowser.Settings(), exporter.Fonts, rows: 1, script: "/* never ready */");
        var before = BrowserProcesses();
        var ex = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportPdfAsync(job, TestContext.Current.CancellationToken));
        Assert.Equal("deadline", ex.Code);
        Assert.Contains("views.export.deadline_seconds", ex.Message);
        // the browser the export started is gone (processes of an unrelated browser are not counted)
        Assert.True(BrowserProcesses() <= before, "an export browser is still running after the deadline");
    }

    [Fact]
    public async Task A_page_that_says_it_cannot_be_drawn_is_refused_in_its_words()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        var ex = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportPdfAsync(Report(settings, exporter.Fonts, rows: 1, script: "window.__reportError = 'the chart could not be drawn'"), TestContext.Current.CancellationToken));
        Assert.Equal("page", ex.Code);
        Assert.Contains("the chart could not be drawn", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_export_stops()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        using var cts = new CancellationTokenSource();
        var running = exporter.ExportPdfAsync(Report(settings, exporter.Fonts, rows: 1, script: "/* never ready */"), cts.Token);
        await cts.CancelAsync();
        var ex = await Assert.ThrowsAsync<ExportException>(() => running);
        Assert.Equal("cancelled", ex.Code);
    }

    [Fact]
    public async Task A_document_over_the_page_limit_is_refused_with_the_limit_named()
    {
        var (exporter, settings) = await Started(s => s with { MaxPages = 2 });
        await using var _ = exporter;
        var ex = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportPdfAsync(Report(settings with { MaxPages = 2 }, exporter.Fonts), TestContext.Current.CancellationToken));
        Assert.Equal("pages", ex.Code);
        Assert.Contains("views.export.max_pages", ex.Message);
    }

    [Fact]
    public async Task A_host_without_a_browser_says_which_setting_to_set()
    {
        await using var exporter = new ChromiumExporter(new ExportSettings { Mode = BrowserMode.System, Path = Path.Combine(Path.GetTempPath(), "no-such-browser", "chrome") });
        var availability = await exporter.CheckAsync(TestContext.Current.CancellationToken);
        Assert.False(availability.Available);
        Assert.StartsWith("export unavailable:", availability.Reason);
        Assert.Contains("views.export.path", availability.Reason);
        var ex = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportPdfAsync(new PrintJob("t", "<html></html>", new Dictionary<string, PrintAsset>(), PdfLayout.A4), TestContext.Current.CancellationToken));
        Assert.Equal("unavailable", ex.Code);

        await using var none = new ChromiumExporter(new ExportSettings { Mode = BrowserMode.System });
        Assert.Contains("views.export.path", (await none.CheckAsync(TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public async Task A_pinned_host_refuses_a_browser_of_another_version()
    {
        var settings = TestBrowser.Settings(s => s with { Mode = BrowserMode.Pinned, PinnedVersion = "1.2.3.4" });
        await using var exporter = new ChromiumExporter(settings);
        var availability = await exporter.CheckAsync(TestContext.Current.CancellationToken);
        Assert.False(availability.Available);
        Assert.Contains("1.2.3.4", availability.Reason);
        Assert.Contains("refuses a different version", availability.Reason);
    }

    [Fact]
    public async Task A_promised_font_that_is_missing_stops_the_export_naming_it_and_none_promised_has_nothing_to_check()
    {
        var settings = TestBrowser.Settings(s => s with { RequiredFonts = ["DbDataKit Missing Font"] });
        await using var exporter = new ChromiumExporter(settings);
        var availability = await exporter.CheckAsync(TestContext.Current.CancellationToken);
        Assert.False(availability.Available);
        Assert.StartsWith("export unavailable: fonts:", availability.Reason);
        Assert.Contains("DbDataKit Missing Font", availability.Reason);
        Assert.Contains("views.export.fonts.required", availability.Reason);

        await using var plain = new ChromiumExporter(TestBrowser.Settings());
        Assert.True((await plain.CheckAsync(TestContext.Current.CancellationToken)).Available);
    }

    [Fact]
    public async Task The_version_of_the_browser_is_in_the_result_and_the_mode_in_the_availability()
    {
        var (exporter, settings) = await Started();
        await using var _ = exporter;
        var result = await exporter.ExportPdfAsync(Report(settings, exporter.Fonts, rows: 2), TestContext.Current.CancellationToken);
        Assert.Matches(@"Chrome/\d+\.\d+\.\d+\.\d+$", result.BrowserVersion);
        Assert.Equal(result.BrowserVersion, exporter.Availability.BrowserVersion);
        Assert.Equal(settings.Mode == BrowserMode.Pinned ? "pinned" : "system", exporter.Availability.Mode);
    }

    [Fact]
    public void The_exporter_never_downloads_a_browser()
    {
        // PuppeteerSharp's BrowserFetcher is the one way it fetches a browser: the exporter's assembly does not mention it
        var bytes = File.ReadAllBytes(typeof(ChromiumExporter).Assembly.Location);
        Assert.DoesNotContain("BrowserFetcher", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_byte_of_a_bundled_font_is_refused_naming_the_file()
    {
        var source = TestBrowser.FontsDirectory();
        var pin = TestBrowser.FontsPin();
        if (source is null || pin is null) { if (TestBrowser.Required) Assert.Fail("fonts are required here (scripts/fetch-fonts)."); Assert.Skip("The bundled fonts are not installed (scripts/fetch-fonts)."); }
        var copy = Path.Combine(Path.GetTempPath(), "fonts-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(source!, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(source!, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            Assert.True(BundledFonts.Load(copy, pin).FileCount > 40);
            var victim = Path.Combine(copy, "noto-sans", "noto-sans-latin-400-normal.woff2");
            var bytes = File.ReadAllBytes(victim);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(victim, bytes);
            var ex = Assert.Throws<ExportException>(() => BundledFonts.Load(copy, pin));
            Assert.Contains("noto-sans-latin-400-normal.woff2", ex.Message);
            Assert.Contains("does not match the font pin", ex.Message);
        }
        finally { Directory.Delete(copy, true); }
    }

    [Fact]
    public void The_settings_are_read_strictly()
    {
        var read = ExportSettings.Read(JsonNode.Parse("""{"mode":"system","path":"/usr/bin/chrome","deadline_seconds":30,"max_pages":10,"max_concurrent":1,"no_sandbox":true,"fonts":{"allowed":["Calibri"],"required":["Calibri"]}}""")!.AsObject(), "server.yaml");
        Assert.Equal(BrowserMode.System, read.Mode);
        Assert.Equal(TimeSpan.FromSeconds(30), read.Deadline);
        Assert.Equal(10, read.MaxPages);
        Assert.True(read.NoSandbox);
        Assert.Equal(["Calibri"], read.RequiredFonts);
        Assert.Equal(BrowserMode.Pinned, ExportSettings.Read(new JsonObject(), "server.yaml").Mode);

        var unknown = Assert.Throws<ExportSettingsException>(() => ExportSettings.Read(JsonNode.Parse("""{"browser":"x"}""")!.AsObject(), "/etc/dbdatakit/server.yaml"));
        Assert.Contains("/etc/dbdatakit/server.yaml", unknown.Message);
        Assert.Contains("`browser`", unknown.Message);
        Assert.Contains("`pinned` or `system`", Assert.Throws<ExportSettingsException>(() => ExportSettings.Read(JsonNode.Parse("""{"mode":"edge"}""")!.AsObject(), "s.yaml")).Message);
        Assert.Contains("fonts has no key `dir`", Assert.Throws<ExportSettingsException>(() => ExportSettings.Read(JsonNode.Parse("""{"fonts":{"dir":"x"}}""")!.AsObject(), "s.yaml")).Message);
        Assert.Contains("whole number", Assert.Throws<ExportSettingsException>(() => ExportSettings.Read(JsonNode.Parse("""{"max_pages":0}""")!.AsObject(), "s.yaml")).Message);
    }

    /// <summary>A started listener on a free loopback port. The port is picked first and bound after (an <see cref="HttpListener"/> has no port 0), so another process can take it in between: the start is tried again, up to five times.</summary>
    private static (HttpListener Listener, int Port) StartListener()
    {
        for (var attempt = 1; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    /// <summary>The browser processes whose command line carries an export profile folder (Linux; elsewhere zero, which the comparison tolerates).</summary>
    private static int BrowserProcesses()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            try { if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains("dbdatakit-export-", StringComparison.Ordinal)) count++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return count;
    }
}
