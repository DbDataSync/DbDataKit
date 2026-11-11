using System.Xml.Linq;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Export.Tests;

/// <summary>The chart SVG of a print page (plan 32 DBR17), in a real Chromium: the exporter takes the SVG of the chart the visuals mark <c>data-testid="vz-chart-&lt;id&gt;"</c> out of the page once it says it is drawn.</summary>
// one browser test class at a time: ExporterTests counts the browser processes before and after an export, so another class starting one in parallel made it fail (seen once, batch 18)
[Collection("chromium")]
public sealed class SvgExportTests
{
    private static PrintJob Page(string body, string? script = null) => new("Charts", $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Charts</title></head><body>{{body}}<script>{{script ?? "window.__reportReady = true"}}</script></body></html>
        """, new Dictionary<string, PrintAsset>(), PdfLayout.A4);

    private static ChromiumExporter Exporter() => new(TestBrowser.Settings());

    [Fact]
    public async Task The_svg_of_the_named_chart_is_taken_standalone_and_another_chart_is_not_mixed_in()
    {
        await using var exporter = Exporter();
        var page = Page("""
            <div data-testid="vz-chart-sales"><div role="img"><div class="vega-embed"><svg class="marks" width="200" height="100" viewBox="0 0 200 100"><rect width="10" height="20"/><text x="1" y="2">Sales &lt;&amp;&gt;</text></svg></div></div></div>
            <div data-testid="vz-chart-orders"><svg class="marks" width="50" height="50"><circle r="5"/></svg></div>
            <div data-testid="vz-widget-other"><svg><path d="M0 0"/></svg></div>
            """);
        var result = await exporter.ExportSvgAsync(page, "sales");
        var svg = XDocument.Parse(result.Svg);                                       // well-formed XML, with the namespace declared
        Assert.Equal(XNamespace.Get("http://www.w3.org/2000/svg") + "svg", svg.Root!.Name);
        Assert.Equal("200", svg.Root.Attribute("width")!.Value);
        Assert.Equal("Sales <&>", svg.Descendants().Single(e => e.Name.LocalName == "text").Value);
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "circle");
        Assert.Matches(@"Chrome/\d+\.\d+\.\d+\.\d+$", result.BrowserVersion);

        var orders = XDocument.Parse((await exporter.ExportSvgAsync(page, "orders")).Svg);
        Assert.Single(orders.Descendants(), e => e.Name.LocalName == "circle");
    }

    [Fact]
    public async Task A_chart_the_page_lacks_or_could_not_draw_is_refused_in_words_and_a_page_that_loads_nothing_from_outside_warns_when_it_tries()
    {
        await using var exporter = Exporter();
        var absent = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportSvgAsync(Page("<p>no chart</p>"), "sales"));
        Assert.Equal("page", absent.Code);
        Assert.Contains("no chart 'sales'", absent.Message);

        var failed = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportSvgAsync(Page("""<div data-testid="vz-chart-sales"><p role="alert">The chart could not be drawn: bad spec</p></div>"""), "sales"));
        Assert.Equal("page", failed.Code);
        Assert.Contains("bad spec", failed.Message);

        var error = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportSvgAsync(Page("", "window.__reportError = 'the bundle is the wrong version'"), "sales"));
        Assert.Contains("the bundle is the wrong version", error.Message);

        var outside = await exporter.ExportSvgAsync(Page("""<div data-testid="vz-chart-a"><svg class="marks"/></div><img src="http://example.invalid/x.png">"""), "a");
        Assert.Contains(outside.Warnings, w => w.Contains("example.invalid", StringComparison.Ordinal) && w.Contains("blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_that_is_never_ready_is_stopped_at_the_deadline_for_an_svg_as_for_a_pdf()
    {
        await using var exporter = new ChromiumExporter(TestBrowser.Settings(s => s with { Deadline = TimeSpan.FromSeconds(3) }));
        var late = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportSvgAsync(Page("""<div data-testid="vz-chart-a"><svg class="marks"/></div>""", "/* never */"), "a"));
        Assert.Equal("deadline", late.Code);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var stopped = await Assert.ThrowsAsync<ExportException>(() => exporter.ExportSvgAsync(Page(""), "a", cancelled.Token));
        Assert.Equal("cancelled", stopped.Code);
    }
}
