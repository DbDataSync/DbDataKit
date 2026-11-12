namespace DbDataKit.Views.Export;

/// <summary>A file the print page loads: served from memory under the page's own origin, never from disk and never from the network.</summary>
public sealed record PrintAsset(byte[] Content, string ContentType);

/// <summary>A page margin in millimetres.</summary>
public sealed record PageMargins(double Top, double Right, double Bottom, double Left);

/// <summary>The paper of the PDF: its size and margins (or, with <c>PreferCssPage</c>, the page's own <c>@page</c> rule, which the print page carries from the same numbers), and the running header and footer as the HTML templates Chromium takes (classes <c>pageNumber</c>, <c>totalPages</c>, <c>title</c>, <c>date</c>).</summary>
public sealed record PdfLayout(double WidthMm, double HeightMm, PageMargins Margins, string? HeaderHtml = null, string? FooterHtml = null, bool PreferCssPage = false)
{
    public static readonly PdfLayout A4 = new(210, 297, new PageMargins(20, 15, 20, 15));
}

/// <summary>
/// One export: the page (its HTML, and the files it loads under <c>assets</c>, relative paths), and how to cut it into paper. The page signals that it is drawn by setting
/// <c>window.__reportReady = true</c> (after its charts are drawn and <c>document.fonts.ready</c>), or says it cannot with <c>window.__reportError = "words"</c>.
/// </summary>
public sealed record PrintJob(string Title, string Html, IReadOnlyDictionary<string, PrintAsset> Assets, PdfLayout Layout);

/// <summary>What an export gives: the PDF, its page count, the browser that made it and what to tell the person (a blocked request, a missing font).</summary>
public sealed record ExportResult(byte[] Pdf, int Pages, string BrowserVersion, IReadOnlyList<string> Warnings, IReadOnlyList<string> EmbeddedFonts);

/// <summary>What an SVG export gives: the chart's <c>&lt;svg&gt;</c> as the page drew it (a standalone document: the namespace is declared), the browser that drew it and what to tell the person.</summary>
public sealed record SvgResult(string Svg, string BrowserVersion, IReadOnlyList<string> Warnings);

/// <summary>What a picture export gives (plan 32 DBR18): the PNG of the page as drawn, its size in pixels, the browser that drew it and what to tell the person.</summary>
public sealed record PngResult(byte[] Png, int Width, int Height, string BrowserVersion, IReadOnlyList<string> Warnings);

/// <summary>Whether this host can export, and if not, the reason in words that name the setting.</summary>
public sealed record ExportAvailability(bool Available, string Reason, string? BrowserVersion = null, string? Mode = null)
{
    public static ExportAvailability Off(string reason) => new(false, "export unavailable: " + reason);
}

/// <summary>An export that did not happen, with a code the module turns into a status: <c>unavailable</c>, <c>deadline</c>, <c>pages</c>, <c>page</c> (the page said it could not be drawn), <c>busy</c>, <c>cancelled</c>, <c>failed</c>.</summary>
public sealed class ExportException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>Draws a print page to a PDF, or takes a chart's SVG out of it (plan 32 section 4.5, DBR17). One implementation drives a browser; a module and a runner task use it, a test can replace it.</summary>
public interface IReportExporter : IAsyncDisposable
{
    /// <summary>What the last check found (before the first, unavailable and saying so).</summary>
    ExportAvailability Availability { get; }

    /// <summary>The bundled fonts once a check loaded them (their files are added to every job's assets); null when none are configured.</summary>
    BundledFonts? Fonts { get; }

    /// <summary>Looks at the host: the browser exists, starts and is the pinned version; the bundled fonts match their pin; the promised host fonts are present. Cheap enough to repeat when the answer is "no".</summary>
    Task<ExportAvailability> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Draws the page. Refused with an <see cref="ExportException"/> when the host cannot, the page cannot be drawn, a limit is past or the deadline ends.</summary>
    Task<ExportResult> ExportPdfAsync(PrintJob job, CancellationToken cancellationToken = default);

    /// <summary>Draws the page and takes the SVG of the chart <paramref name="chartId"/> (a widget id) out of it, as it is drawn there: the same chart the screen and the PDF show, at the size the layout gives it. Refused like <see cref="ExportPdfAsync"/>, and with code <c>page</c> when the page has no such chart or could not draw it.</summary>
    Task<SvgResult> ExportSvgAsync(PrintJob job, string chartId, CancellationToken cancellationToken = default);

    /// <summary>Draws the page and takes a picture of it, as wide as the layout's paper (at 96 pixels to the inch) and as tall as its content, up to <paramref name="maxHeight"/> pixels: the preview a subscription's message shows inline, beside the PDF it attaches. A host whose exporter cannot (a test's) refuses with <c>unavailable</c>.</summary>
    Task<PngResult> ExportPngAsync(PrintJob job, int maxHeight = 4000, CancellationToken cancellationToken = default) =>
        throw new ExportException("unavailable", "this exporter makes no pictures of a page.");
}
