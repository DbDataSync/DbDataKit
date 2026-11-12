using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using UglyToad.PdfPig;

namespace DbDataKit.Views.Export;

/// <summary>
/// <see cref="IReportExporter"/> over PuppeteerSharp and one Chromium (plan 32 section 4.5): the browser is launched from a path (<b>never downloaded</b>: no <c>BrowserFetcher</c> call exists here), with a throwaway profile, per export.
/// The page is served from memory under <c>http://print.localhost/</c> by request interception, so <b>no request leaves the process</b> (its files carry <c>Access-Control-Allow-Origin: *</c>, because a custom view's frame is sandboxed with an opaque origin and loads its module script with CORS): anything the page asks for that is not one of its own files is aborted and reported as a warning.
/// The page says it is drawn with <c>window.__reportReady</c>; then <c>Page.printToPDF</c> cuts it into the layout's paper, margins, header and footer, with the outline and a tagged PDF. A deadline covers the whole export and stops the browser.
/// </summary>
public sealed class ChromiumExporter(ExportSettings settings, ILogger? logger = null) : IReportExporter
{
    public const string Origin = "http://print.localhost/";

    private readonly SemaphoreSlim slots = new(Math.Max(1, settings.MaxConcurrent));
    private readonly SemaphoreSlim checking = new(1, 1);
    private volatile ExportAvailability availability = ExportAvailability.Off("not checked yet.");
    private DateTimeOffset checkedAt = DateTimeOffset.MinValue;
    private string? executable;
    private string? expectedVersion;
    private volatile BundledFonts? fonts;

    public ExportAvailability Availability => availability;

    public BundledFonts? Fonts => fonts;

    public async Task<ExportAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        await checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            availability = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
            checkedAt = DateTimeOffset.UtcNow;
            return availability;
        }
        finally { checking.Release(); }
    }

    private async Task<ExportAvailability> CheckCoreAsync(CancellationToken ct)
    {
        try
        {
            // the browser: a path, and for the pinned mode the version the pin names
            string? path = settings.Path;
            if (settings.Mode == BrowserMode.Pinned)
            {
                var pinFile = settings.Pin ?? ChromiumPin.Find(AppContext.BaseDirectory) ?? ChromiumPin.Find(Directory.GetCurrentDirectory());
                if (settings.PinnedVersion is null && pinFile is null) return ExportAvailability.Off("mode `pinned` needs the version of chromium.pin: set views.export.pin (or views.export.pinned_version), or use mode `system`.");
                var pin = pinFile is null ? null : ChromiumPin.Read(pinFile);
                expectedVersion = settings.PinnedVersion ?? pin!.Version;
                path ??= pin?.Executable;
                if (path is null) return ExportAvailability.Off("no pinned browser for this platform: set views.export.path, or run scripts/fetch-chromium (the pin has no build for this platform).");
            }
            else if (path is null) return ExportAvailability.Off("mode `system` needs views.export.path (the installed Chrome or Edge).");
            if (!File.Exists(path)) return ExportAvailability.Off($"no browser at {path} (views.export.path); run scripts/fetch-chromium or set the setting.");
            executable = path;

            // the bundled fonts: every file against fonts.pin
            if (settings.FontsDirectory is not null || settings.FontsPin is not null || FindFonts() is not null)
            {
                var (directory, pin) = FindFonts() ?? (settings.FontsDirectory!, settings.FontsPin);
                fonts = BundledFonts.Load(directory, pin);
            }

            // the browser starts, and is the version the pin names
            await using var launched = await LaunchAsync(ct).ConfigureAwait(false);
            var version = await Until(launched.Browser.GetVersionAsync(), ct).ConfigureAwait(false);
            if (settings.Mode == BrowserMode.Pinned && !version.EndsWith("/" + expectedVersion, StringComparison.Ordinal))
                return ExportAvailability.Off($"the browser at {path} is {version} and the pin names {expectedVersion}; a pinned host refuses a different version (run scripts/fetch-chromium, or use mode `system`).");

            // the host fonts the host promised
            foreach (var family in settings.RequiredFonts)
                if (!await FontPresentAsync(launched.Browser, family, ct).ConfigureAwait(false))
                    return ExportAvailability.Off($"fonts: the promised font '{family}' is not installed on this host (views.export.fonts.required); install the package that provides it (for example fonts-noto-cjk on Debian and Ubuntu), or remove it from the list.");

            return new ExportAvailability(true, "ok", version, settings.Mode == BrowserMode.Pinned ? "pinned" : "system");
        }
        catch (ExportException e) { return ExportAvailability.Off(e.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger?.LogWarning(e, "The export browser could not be started.");
            return ExportAvailability.Off($"the browser at {executable} did not start ({e.GetType().Name}: {e.Message}).");
        }
    }

    /// <summary>The bundled fonts next to a <c>fonts.pin</c> the program ships with, when the settings name none.</summary>
    private (string Directory, string? Pin)? FindFonts()
    {
        if (settings.FontsDirectory is not null) return (settings.FontsDirectory, settings.FontsPin);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "fonts.pin")) && Directory.Exists(Path.Combine(dir.FullName, ".fonts")))
                return (Path.Combine(dir.FullName, ".fonts"), Path.Combine(dir.FullName, "fonts.pin"));
        return null;
    }

    public Task<ExportResult> ExportPdfAsync(PrintJob job, CancellationToken cancellationToken = default) =>
        GuardedAsync(cancellationToken, ct => RunAsync(job, ct, async drawn =>
        {
            var layout = job.Layout;
            var paper = new PdfOptions
            {
                Width = $"{layout.WidthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}mm",
                Height = $"{layout.HeightMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}mm",
                MarginOptions = new MarginOptions { Top = Mm(layout.Margins.Top), Right = Mm(layout.Margins.Right), Bottom = Mm(layout.Margins.Bottom), Left = Mm(layout.Margins.Left) },
                DisplayHeaderFooter = layout.HeaderHtml is not null || layout.FooterHtml is not null,
                HeaderTemplate = layout.HeaderHtml ?? "<span></span>",
                FooterTemplate = layout.FooterHtml ?? "<span></span>",
                PrintBackground = true,
                PreferCSSPageSize = layout.PreferCssPage,
                Outline = true,
                Tagged = true,
                Timeout = 0,
            };
            var pdf = await Until(drawn.Page.PdfDataAsync(paper), ct).ConfigureAwait(false);
            var facts = Read(pdf);
            if (facts.Pages > settings.MaxPages) throw new ExportException("pages", $"the document has {facts.Pages} pages and an export is limited to {settings.MaxPages} (views.export.max_pages).");
            lock (drawn.Errors) foreach (var e in drawn.Errors.Take(5)) drawn.Warnings.Add("The page reported an error: " + e);
            // the browser's build and the fonts it embedded are in the file itself (plan 32 DBR16), beside the title and the producer the browser wrote
            pdf = PdfMetadata.Stamp(pdf, $"browser: {drawn.Version}; fonts: {string.Join(", ", facts.Fonts)}");
            return new ExportResult(pdf, facts.Pages, drawn.Version, [.. drawn.Warnings], facts.Fonts);
        }));

    public Task<SvgResult> ExportSvgAsync(PrintJob job, string chartId, CancellationToken cancellationToken = default) =>
        GuardedAsync(cancellationToken, ct => RunAsync(job, ct, async drawn =>
        {
            // the chart of the print page: the visuals mark each chart `data-testid="vz-chart-<id>"` and draw its SVG inside; a chart that could not be drawn says so in a `role=alert` paragraph
            var found = await Until(drawn.Page.EvaluateFunctionAsync<string[]?>(
                """
                (id) => {
                  const host = document.querySelector('[data-testid="vz-chart-' + id + '"]');
                  if (!host) return ['missing', ''];
                  const svg = host.querySelector('svg.marks') || host.querySelector('svg');
                  if (svg) return ['svg', new XMLSerializer().serializeToString(svg)];
                  const alert = host.querySelector('[role="alert"]');
                  return ['error', alert ? alert.textContent : ''];
                }
                """, chartId), ct).ConfigureAwait(false);
            if (found is not { Length: 2 } || found[0] == "missing") throw new ExportException("page", $"the page has no chart '{chartId}' (the layout in play does not draw it).");
            if (found[0] == "error") throw new ExportException("page", $"the chart '{chartId}' could not be drawn: {found[1]}");
            lock (drawn.Errors) foreach (var e in drawn.Errors.Take(5)) drawn.Warnings.Add("The page reported an error: " + e);
            return new SvgResult(found[1], drawn.Version, [.. drawn.Warnings]);
        }));

    public Task<PngResult> ExportPngAsync(PrintJob job, int maxHeight = 4000, CancellationToken cancellationToken = default) =>
        GuardedAsync(cancellationToken, ct => RunAsync(job, ct, async drawn =>
        {
            var width = Math.Clamp((int)Math.Round(job.Layout.WidthMm / 25.4 * 96), 320, 2400);
            // the page is the one the PDF is cut from: the print medium, at the paper's width, the whole of its content down to the limit
            await Until(drawn.Page.EmulateMediaTypeAsync(MediaType.Print), ct).ConfigureAwait(false);
            await Until(drawn.Page.SetViewportAsync(new ViewPortOptions { Width = width, Height = 800 }), ct).ConfigureAwait(false);
            var height = await Until(drawn.Page.EvaluateFunctionAsync<int>("() => Math.ceil(Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0))"), ct).ConfigureAwait(false);
            height = Math.Clamp(height, 1, Math.Max(1, maxHeight));
            if (height >= maxHeight) drawn.Warnings.Add($"The picture shows the first {maxHeight} pixels of the page; the PDF has the rest.");
            var png = await Until(drawn.Page.ScreenshotDataAsync(new ScreenshotOptions { Type = ScreenshotType.Png, Clip = new PuppeteerSharp.Media.Clip { X = 0, Y = 0, Width = width, Height = height }, CaptureBeyondViewport = true }), ct).ConfigureAwait(false);
            lock (drawn.Errors) foreach (var e in drawn.Errors.Take(5)) drawn.Warnings.Add("The page reported an error: " + e);
            return new PngResult(png, width, height, drawn.Version, [.. drawn.Warnings]);
        }));

    /// <summary>One export under the deadline and the host's concurrency limit: what <paramref name="work"/> does runs with the linked token.</summary>
    private async Task<T> GuardedAsync<T>(CancellationToken cancellationToken, Func<CancellationToken, Task<T>> work)
    {
        if (!availability.Available && (checkedAt == DateTimeOffset.MinValue || DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromSeconds(10)))
            await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.Available) throw new ExportException("unavailable", availability.Reason);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.Deadline);
        var ct = deadline.Token;
        try
        {
            try { await slots.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ExportException("busy", $"export is busy: {settings.MaxConcurrent} at once on this host and the deadline ended while waiting (views.export.max_concurrent)."); }
            try { return await work(ct).ConfigureAwait(false); }
            finally { slots.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new ExportException("cancelled", "the export was cancelled."); }
        catch (OperationCanceledException) { throw new ExportException("deadline", $"the export took longer than {settings.Deadline.TotalSeconds:0.#} s (views.export.deadline_seconds) and was stopped."); }
    }

    /// <summary>The page once it says it is drawn: the browser's page, its version and what was collected so far.</summary>
    private sealed record Drawn(IPage Page, string Version, List<string> Warnings, List<string> Errors);

    private async Task<T> RunAsync<T>(PrintJob job, CancellationToken ct, Func<Drawn, Task<T>> use)
    {
        var assets = new Dictionary<string, PrintAsset>(job.Assets, StringComparer.Ordinal);
        if (fonts is not null) foreach (var (k, v) in fonts.Assets) assets.TryAdd(k, v);
        assets["index.html"] = new PrintAsset(Encoding.UTF8.GetBytes(job.Html), "text/html; charset=utf-8");

        await using var launched = await LaunchAsync(ct).ConfigureAwait(false);
        var browser = launched.Browser;
        var version = availability.BrowserVersion ?? await Until(browser.GetVersionAsync(), ct).ConfigureAwait(false);
        var warnings = new List<string>();
        var page = await Until(browser.NewPageAsync(), ct).ConfigureAwait(false);
        await Until(page.SetRequestInterceptionAsync(true), ct).ConfigureAwait(false);
        page.Request += async (_, e) =>
        {
            try
            {
                var url = e.Request.Url;
                if (url.StartsWith(Origin, StringComparison.Ordinal))
                {
                    var key = Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimStart('/'));
                    if (assets.TryGetValue(key, out var asset))
                    {
                        await e.Request.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = asset.ContentType, BodyData = asset.Content, Headers = new Dictionary<string, object> { ["Cache-Control"] = "no-store", ["Access-Control-Allow-Origin"] = "*" } }).ConfigureAwait(false);
                        return;
                    }
                    await e.Request.RespondAsync(new ResponseData { Status = HttpStatusCode.NotFound, ContentType = "text/plain", Body = "not found" }).ConfigureAwait(false);
                    lock (warnings) warnings.Add($"The page asked for {key}, which it does not have.");
                    return;
                }
                if (url.StartsWith("data:", StringComparison.Ordinal) || url.StartsWith("blob:", StringComparison.Ordinal) || url.StartsWith("about:", StringComparison.Ordinal))
                {
                    await e.Request.ContinueAsync().ConfigureAwait(false);
                    return;
                }
                lock (warnings) warnings.Add($"A request to {Describe(url)} was blocked: an export loads nothing from outside.");
                await e.Request.AbortAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { logger?.LogDebug(ex, "A request of the export page could not be answered."); }
        };
        var errors = new List<string>();
        page.PageError += (_, e) => { lock (errors) errors.Add(e.Message); };

        await Until(page.GoToAsync(Origin + "index.html", new NavigationOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = 0 }), ct).ConfigureAwait(false);
        await Until(page.WaitForFunctionAsync("() => window.__reportReady === true || typeof window.__reportError === 'string'", new WaitForFunctionOptions { Timeout = 0 }), ct).ConfigureAwait(false);
        var failure = await Until(page.EvaluateFunctionAsync<string?>("() => typeof window.__reportError === 'string' ? window.__reportError : null"), ct).ConfigureAwait(false);
        if (failure is not null) throw new ExportException("page", $"the page could not be drawn: {failure}");

        return await use(new Drawn(page, version, warnings, errors)).ConfigureAwait(false);
    }

    private static string Mm(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "mm";

    /// <summary>The page count and the fonts embedded (subset prefixes removed) of a PDF.</summary>
    public static (int Pages, string[] Fonts) Read(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var page in document.GetPages())
            foreach (var letter in page.Letters)
                if (letter.FontName is { } name) names.Add(name.Length > 7 && name[6] == '+' ? name[7..] : name);
        return (document.NumberOfPages, [.. names]);
    }

    private static string Describe(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "(an address)";

    // ---- the browser ----

    private sealed class Launched(IBrowser browser, string profile) : IAsyncDisposable
    {
        public IBrowser Browser { get; } = browser;

        public async ValueTask DisposeAsync()
        {
            var process = Browser.Process;
            try { await Task.WhenAny(Browser.CloseAsync(), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false); } catch (Exception e) when (e is not OutOfMemoryException) { }
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is not OutOfMemoryException) { }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); return; }
                catch (IOException) { await Task.Delay(200).ConfigureAwait(false); }
                catch (UnauthorizedAccessException) { await Task.Delay(200).ConfigureAwait(false); }
            }
        }
    }

    private async Task<Launched> LaunchAsync(CancellationToken ct)
    {
        var profile = Path.Combine(Path.GetTempPath(), "dbdatakit-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var args = new List<string>
        {
            "--disable-gpu", "--disable-extensions", "--disable-background-networking", "--disable-component-update", "--disable-sync", "--no-first-run", "--no-default-browser-check",
            "--disable-dev-shm-usage", "--font-render-hinting=none", "--metrics-recording-only", "--mute-audio", "--hide-scrollbars", "--no-proxy-server",
            "--disable-features=Translate,MediaRouter,OptimizationHints,InterestFeedContentSuggestions",
            // one argument with spaces: PuppeteerSharp joins the arguments into one command line, so it is quoted
            "--host-resolver-rules=\"MAP * ~NOTFOUND , EXCLUDE print.localhost\"",
        };
        if (settings.NoSandbox)
        {
            args.Add("--no-sandbox");
            logger?.LogWarning("The export browser runs without its sandbox (views.export.no_sandbox): only a container that forces it should.");
        }
        var start = Puppeteer.LaunchAsync(new LaunchOptions { ExecutablePath = executable, Headless = true, Args = [.. args], UserDataDir = profile, Timeout = 0, ProtocolTimeout = 0 });
        try
        {
            return new Launched(await Until(start, ct).ConfigureAwait(false), profile);
        }
        catch
        {
            // a launch that was given up (deadline, cancel) may still finish: the browser it starts is closed and its profile removed when it does
            _ = start.ContinueWith(async done =>
            {
                if (done.Status == TaskStatus.RanToCompletion) await new Launched(done.Result, profile).DisposeAsync().ConfigureAwait(false);
                else try { Directory.Delete(profile, true); } catch (Exception e) when (e is not OutOfMemoryException) { }
            }, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Whether the browser sets text in <paramref name="family"/> with a font of that name: the probe page asks the DevTools protocol which platform font it used.</summary>
    private async Task<bool> FontPresentAsync(IBrowser browser, string family, CancellationToken ct)
    {
        var page = await Until(browser.NewPageAsync(), ct).ConfigureAwait(false);
        try
        {
            var quoted = family.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            await Until(page.SetContentAsync($"<html><body><span id=\"probe\" style=\"font-family:&quot;{System.Net.WebUtility.HtmlEncode(quoted)}&quot;, serif\">Probe 0123</span></body></html>"), ct).ConfigureAwait(false);
            var session = await Until(page.CreateCDPSessionAsync(), ct).ConfigureAwait(false);
            await Until(session.SendAsync("DOM.enable"), ct).ConfigureAwait(false);
            await Until(session.SendAsync("CSS.enable"), ct).ConfigureAwait(false);
            var document = await Until(session.SendAsync("DOM.getDocument"), ct).ConfigureAwait(false);
            var root = document!.Value.GetProperty("root").GetProperty("nodeId").GetInt32();
            var found = await Until(session.SendAsync("DOM.querySelector", new { nodeId = root, selector = "#probe" }), ct).ConfigureAwait(false);
            var node = found!.Value.GetProperty("nodeId").GetInt32();
            var used = await Until(session.SendAsync("CSS.getPlatformFontsForNode", new { nodeId = node }), ct).ConfigureAwait(false);
            return used!.Value.GetProperty("fonts").EnumerateArray().Any(f => string.Equals(f.GetProperty("familyName").GetString(), family, StringComparison.OrdinalIgnoreCase));
        }
        finally { await page.CloseAsync().ConfigureAwait(false); }
    }

    // ---- cancellation: a protocol call cannot be cancelled, the wait for it can, and the browser is stopped by the caller's dispose ----

    private static async Task<T> Until<T>(Task<T> work, CancellationToken ct)
    {
        if (work.IsCompleted) return await work.ConfigureAwait(false);
        var gate = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(), gate);
        var done = await Task.WhenAny(work, gate.Task).ConfigureAwait(false);
        if (done != work)
        {
            _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            ct.ThrowIfCancellationRequested();
        }
        return await work.ConfigureAwait(false);
    }

    private static async Task Until(Task work, CancellationToken ct)
    {
        await Until(Wrap(work), ct).ConfigureAwait(false);

        static async Task<bool> Wrap(Task t) { await t.ConfigureAwait(false); return true; }
    }

    public ValueTask DisposeAsync()
    {
        slots.Dispose();
        checking.Dispose();
        return ValueTask.CompletedTask;
    }
}
