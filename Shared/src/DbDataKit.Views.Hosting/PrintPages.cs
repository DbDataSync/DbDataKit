using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Hosting;

/// <summary>The paper a layout is printed on and the page's own rule for it: the numbers the PDF options, the <c>@page</c> rule and the visuals' <c>pageGeometry</c> share.</summary>
public sealed record PrintPaper(string CssSize, double WidthMm, double HeightMm, PageMargins Margins, JsonObject? Header, JsonObject? Footer);

/// <summary>
/// The print page of a layout (plan 32 section 4.5, DBR15): a small HTML shell with the document, the layout in play and the rendered widgets as one JSON block, the bundled fonts as <c>@font-face</c> and the visuals' print bundle
/// (<c>print.js</c>, <c>print.css</c>) that draws the layout and sets <c>window.__reportReady</c>. The same page is served to the browser's own print and handed to the exporter, so the PDF is what the screen draws.
/// Nothing in it points outside: every address is relative to the page or to the module's own asset route.
/// </summary>
public static class PrintPages
{
    /// <summary>Page sizes in millimetres (portrait), the table of the visuals' <c>pageGeometry</c>.</summary>
    private static readonly Dictionary<string, (double W, double H)> Sizes = new() { ["A3"] = (297, 420), ["A4"] = (210, 297), ["A5"] = (148, 210), ["Letter"] = (215.9, 279.4), ["Legal"] = (215.9, 355.6) };

    /// <summary>The paper of a layout: a flow layout's <c>page</c> (A4 portrait with 20 mm and 15 mm margins when it says nothing), a canvas at its own size without margins, any other layout on A4 landscape with 10 mm margins (plan 32 section 3.6).</summary>
    public static PrintPaper Paper(ViewLayout layout)
    {
        switch (layout.Type)
        {
            case LayoutType.Canvas when layout.Canvas is { } canvas:
                var w = Math.Round(canvas.WidthPx * 25.4 / 96, 2);
                var h = Math.Round(canvas.HeightPx * 25.4 / 96, 2);
                return new PrintPaper(Mm(w) + " " + Mm(h), w, h, new PageMargins(0, 0, 0, 0), null, null);
            case LayoutType.Flow:
                var page = layout.Raw["page"] as JsonObject;
                double pw = 210, ph = 297;
                var size = "A4";
                if (page?["size"] is JsonValue sv && sv.TryGetValue<string>(out var named) && Sizes.TryGetValue(named, out var dims)) { size = named; (pw, ph) = dims; }
                else if (page?["size"] is JsonObject custom && custom["w"] is { } cw && custom["h"] is { } ch) { size = "custom"; pw = cw.GetValue<double>(); ph = ch.GetValue<double>(); }
                var orientation = page?["orientation"] is JsonValue ov && ov.TryGetValue<string>(out var o) ? o : null;
                if (orientation == "landscape" && ph > pw) (pw, ph) = (ph, pw);
                else if (orientation == "portrait" && pw > ph) (pw, ph) = (ph, pw);
                var margins = page?["margins"] as JsonObject;
                double M(string key, double fallback) => margins?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;
                var m = new PageMargins(M("top", 20), M("right", 15), M("bottom", 20), M("left", 15));
                var css = size == "custom" ? Mm(pw) + " " + Mm(ph) : size + " " + (pw > ph ? "landscape" : "portrait");
                return new PrintPaper(css, pw, ph, m, layout.Raw["header"] as JsonObject, layout.Raw["footer"] as JsonObject);
            default:
                return new PrintPaper("A4 landscape", 297, 210, new PageMargins(10, 10, 10, 10), null, null);
        }
    }

    private static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "mm";

    public static string Rule(PrintPaper p) =>
        $"@page {{ size: {p.CssSize}; margin: {Mm(p.Margins.Top)} {Mm(p.Margins.Right)} {Mm(p.Margins.Bottom)} {Mm(p.Margins.Left)} }}";

    /// <summary>The page. <paramref name="customFrames"/> maps a custom widget's component to the address of the app page that draws it (<see cref="CustomViewPrint.Frames"/>). <paramref name="assetBase"/> is where <c>print.js</c>, <c>print.css</c> and the fonts are loaded from: <c>""</c> for the exporter (its own origin) and the module's asset route for a browser.</summary>
    public static string Html(PublishedView view, ViewLayout layout, PrintPaper paper, JsonObject rendered, JsonObject parameters, string? viewer, BundledFonts? fonts, string assetBase, IReadOnlyDictionary<string, string>? customFrames = null)
    {
        var payload = new JsonObject
        {
            ["kind"] = view.Kind,
            ["name"] = view.Name,
            ["version"] = view.Version,
            ["title"] = view.Document.Title,
            ["viewer"] = viewer,
            ["layout"] = layout.Name,
            ["layout_type"] = layout.Type.ToString().ToLowerInvariant(),
            ["parameters"] = parameters,
            ["paper"] = new JsonObject
            {
                ["width_mm"] = paper.WidthMm, ["height_mm"] = paper.HeightMm,
                ["margins_mm"] = new JsonObject { ["top"] = paper.Margins.Top, ["right"] = paper.Margins.Right, ["bottom"] = paper.Margins.Bottom, ["left"] = paper.Margins.Left },
            },
            ["document"] = view.DocumentJson(),
            ["rendered"] = rendered,
        };
        // the app files that draw custom widgets (DBR32): each component's page, relative to this page, served from the same one-shot origin
        if (customFrames is { Count: > 0 })
            payload["custom"] = new JsonObject { ["frames"] = new JsonObject(customFrames.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)JsonValue.Create(f.Value)))) };
        // the JSON sits in a script block that is not run: nothing in a value can close it
        var json = payload.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("<", "\\u003c", StringComparison.Ordinal).Replace("\u2028", "\\u2028", StringComparison.Ordinal).Replace("\u2029", "\\u2029", StringComparison.Ordinal);
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\" data-medium=\"print\"><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(view.Document.Title)).Append("</title>\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width\">\n<link rel=\"icon\" href=\"data:,\">\n<style>\n").Append(fonts?.FontFaceCss(assetBase) ?? "").Append(Rule(paper)).Append('\n')
            .Append(":root { --font-sans: \"DbDataKit Sans\", sans-serif; --font-serif: \"DbDataKit Serif\", serif; --font-mono: \"DbDataKit Mono\", monospace }\n")
            .Append("html, body { margin: 0; padding: 0; background: #fff }\n</style>\n")
            .Append("<link rel=\"stylesheet\" href=\"").Append(assetBase).Append("print.css\"></head>\n<body>\n<div id=\"root\"></div>\n")
            .Append("<script id=\"dbdatakit-print\" type=\"application/json\">").Append(json).Append("</script>\n")
            .Append("<script src=\"").Append(assetBase).Append("print.js\"></script>\n</body></html>\n");
        return html.ToString();
    }

    private static readonly Regex Token = new(@"\{(?<t>[^{}]*)\}", RegexOptions.CultureInvariant);

    /// <summary>A running header or footer (<c>{left, center, right}</c>) as the HTML template Chromium takes: tokens become the browser's own page fields, or text. Null when the layout has none or every part is empty.</summary>
    public static string? RunningTemplate(JsonObject? part, PrintPaper paper, string title, string? asOf, string? viewer, JsonObject parameters)
    {
        if (part is null) return null;
        string Cell(string key)
        {
            var text = part[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            var html = new StringBuilder();
            var last = 0;
            foreach (Match m in Token.Matches(text))
            {
                html.Append(WebUtility.HtmlEncode(text[last..m.Index]));
                last = m.Index + m.Length;
                var t = m.Groups["t"].Value;
                html.Append(t switch
                {
                    "title" => WebUtility.HtmlEncode(title),
                    "page" => "<span class=\"pageNumber\"></span>",
                    "pages" => "<span class=\"totalPages\"></span>",
                    "asof" => WebUtility.HtmlEncode(FormatAsOf(asOf)),
                    "viewer" => WebUtility.HtmlEncode(viewer ?? ""),
                    _ when t.StartsWith("params.", StringComparison.Ordinal) => WebUtility.HtmlEncode(Describe(parameters[t[7..]])),
                    _ => WebUtility.HtmlEncode(m.Value),
                });
            }
            html.Append(WebUtility.HtmlEncode(text[last..]));
            return html.ToString();
        }
        var (l, c, r) = (Cell("left"), Cell("center"), Cell("right"));
        if (l.Length == 0 && c.Length == 0 && r.Length == 0) return null;
        var edge = paper.Margins;
        return $"<div style=\"box-sizing:border-box;width:100%;padding:0 {Mm(edge.Right)} 0 {Mm(edge.Left)};font-size:8px;color:#555;display:flex;justify-content:space-between;font-family:sans-serif\"><span style=\"flex:1;text-align:left\">{l}</span><span style=\"flex:1;text-align:center\">{c}</span><span style=\"flex:1;text-align:right\">{r}</span></div>";
    }

    /// <summary>
    /// The other way to run a header and footer (plan 32 section 4.3): CSS <c>@page</c> margin boxes (<c>@top-left</c> to <c>@bottom-right</c>) with <c>counter(page)</c> and <c>counter(pages)</c>, written into the page's own stylesheet instead of
    /// the browser's templates. Chromium prints them from version 131 (checked here against a real PDF); the exporter still uses the templates unless a caller asks for this rule. Empty when the layout has no header or footer.
    /// Text is written as CSS strings (a quote, a backslash or a newline cannot end one); the tokens are those of <see cref="RunningTemplate"/>.
    /// </summary>
    public static string MarginBoxRule(PrintPaper paper, string title, string? asOf, string? viewer, JsonObject parameters)
    {
        var boxes = new StringBuilder();
        void Part(JsonObject? part, string edge)
        {
            if (part is null) return;
            foreach (var key in new[] { "left", "center", "right" })
            {
                var text = part[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                var pieces = new List<string>();
                var last = 0;
                foreach (Match m in Token.Matches(text))
                {
                    if (m.Index > last) pieces.Add(CssString(text[last..m.Index]));
                    last = m.Index + m.Length;
                    var t = m.Groups["t"].Value;
                    pieces.Add(t switch
                    {
                        "title" => CssString(title),
                        "page" => "counter(page)",
                        "pages" => "counter(pages)",
                        "asof" => CssString(FormatAsOf(asOf)),
                        "viewer" => CssString(viewer ?? ""),
                        _ when t.StartsWith("params.", StringComparison.Ordinal) => CssString(Describe(parameters[t[7..]])),
                        _ => CssString(m.Value),
                    });
                }
                if (last < text.Length) pieces.Add(CssString(text[last..]));
                if (pieces.Count == 0) continue;
                boxes.Append("  @").Append(edge).Append('-').Append(key).Append(" { content: ").Append(string.Join(' ', pieces)).Append("; font: 8px var(--font-sans, sans-serif); color: #555 }\n");
            }
        }
        Part(paper.Header, "top");
        Part(paper.Footer, "bottom");
        return boxes.Length == 0 ? "" : $"@page {{\n{boxes}}}";
    }

    private static string CssString(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\a ", '\r' => "", '<' => "\\3c ", _ => c.ToString() });
        return sb.Append('"').ToString();
    }

    private static string FormatAsOf(string? asOf)
    {
        if (asOf is null) return "";
        var m = Regex.Match(asOf, @"^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2}(?:\.\d+)?)?Z$", RegexOptions.CultureInvariant);
        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value} UTC" : asOf;
    }

    private static string Describe(JsonNode? value) => value switch
    {
        null => "all",
        JsonArray a => a.Count == 0 ? "all" : string.Join(", ", a.Select(x => x?.ToString())),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "yes" : "no",
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0 ? "all" : s,
        _ => value.ToJsonString(),
    };

    /// <summary>The files the page loads (the visuals' print bundle), read from the assets folder, or the reason there are none.</summary>
    public static (Dictionary<string, PrintAsset>? Assets, string? Missing) LoadAssets(string? folder)
    {
        if (folder is null) return (null, "the print page is not configured: set views.export.page_assets to the folder with print.js and print.css (built from web/visuals).");
        var assets = new Dictionary<string, PrintAsset>(StringComparer.Ordinal);
        foreach (var (file, type) in new[] { ("print.js", "text/javascript; charset=utf-8"), ("print.css", "text/css; charset=utf-8") })
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path)) return (null, $"the print page is not there: {path} is missing (views.export.page_assets).");
            assets[file] = new PrintAsset(File.ReadAllBytes(path), type);
        }
        return (assets, null);
    }
}
