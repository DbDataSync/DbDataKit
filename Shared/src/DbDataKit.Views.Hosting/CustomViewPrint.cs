using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// The built files of the app that owns a custom view (plan 32 DBR32): every file by its path relative to the app's output folder, and the path of the page that mounts views (it calls <c>startViewFrame</c> of
/// <c>@dbdatakit/views-sdk</c>). The apps host, which holds an instance's approved version, supplies them; nothing here reads a disk or a network.
/// </summary>
public sealed record CustomAppFiles(string App, IReadOnlyDictionary<string, PrintAsset> Files, string Page = "index.html");

/// <summary>
/// What a host that serves apps tells the views module (plan 32 DBR31, DBR32): the manifest entry of a custom view by its component (<c>&lt;app&gt;/&lt;Export&gt;</c>, for the resolver) and the app's built files (for the export). A module with none
/// resolves a custom widget as an error entry that says the host serves no app.
/// </summary>
public interface ICustomViewApps
{
    CustomView? Find(string component);
    CustomAppFiles? Files(string component);

    /// <summary>A custom <b>page</b> of an app instance by the view's name (the address <c>/apps/&lt;instance&gt;/_views/&lt;name&gt;</c>): its manifest entry, its files and the app they come from; null for a name the instance does not have, or one that is a widget.</summary>
    CustomPageApp? FindPage(string instance, string name) => null;

    /// <summary>Every custom page the host serves, by instance (the catalog lists them by their manifest entries); empty for a host that serves none.</summary>
    IEnumerable<CustomPageApp> Pages() => [];

    /// <summary>The absolute address of a page on the apps host (<c>https://apps.example/apps/&lt;instance&gt;/_views/&lt;name&gt;</c>), or null when this host does not know where its apps are served (the catalog then lists the page but cannot open it).</summary>
    string? PageAddress(string instance, string name) => null;

    /// <summary>Whether this person may open the instance at all (the reading rule of an app: any role on it). A host that cannot say refuses, so a page is never exported for a person nobody checked.</summary>
    bool MayOpen(ViewsPrincipal who, string instance) => false;
}

/// <summary>A custom page and what draws it: the instance it is addressed through, its manifest entry and the app's built files.</summary>
public sealed record CustomPageApp(string Instance, CustomView View, CustomAppFiles Files);

/// <summary>
/// Custom views in the print page (plan 32 section 9f, DBR32). **A widget** is drawn in its box by a frame: the app's built files are served from the exporter's one-shot origin under <c>_app/&lt;app&gt;/</c> (so a frame loads nothing from outside, and
/// the exporter names a request outside the origin), and the print page embeds the rows as JSON and gives them to the frame as the screen does. **A page** is the top-level document: the app's own page, served at the origin's root, with the view's name
/// and props embedded as JSON (<c>#dbdatakit-view-init</c>), the paper's <c>@page</c> rule and the bundled fonts added, so Chromium paginates it like any document. Both signal readiness with <c>window.__reportReady</c>, which the view's <c>ready()</c> sets.
/// </summary>
public static class CustomViewPrint
{
    /// <summary>The folder of an app's files under the one-shot origin, for widgets (a page is served from the root).</summary>
    public const string FramePrefix = "_app/";

    /// <summary>
    /// Adds the app files of each component to <paramref name="assets"/> (under <c>_app/&lt;app&gt;/</c>) and returns the page address by component, for <see cref="PrintPages.Html"/>. A component the host has no files for is left out, and the print page
    /// says so for its widget (an export error naming it).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Frames(IEnumerable<string> components, ICustomViewApps apps, IDictionary<string, PrintAsset> assets)
    {
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in components.Distinct(StringComparer.Ordinal))
        {
            if (apps.Files(component) is not { } files) continue;
            var root = FramePrefix + files.App + "/";
            foreach (var (path, asset) in files.Files) assets[root + path.TrimStart('/')] = asset;
            frames[component] = root + files.Page.TrimStart('/');
        }
        return frames;
    }

    /// <summary>The ink a view's <c>var(--sui-text)</c> and the others are given on paper; the same colours as the visuals' chart palette in light (<c>themeTokens('light')</c>), which a test keeps equal.</summary>
    public static readonly IReadOnlyDictionary<string, string> LightTokens = new Dictionary<string, string>
    {
        ["sui-text"] = "#1f2328", ["sui-muted"] = "#59636e", ["sui-grid"] = "#d8dee4", ["sui-axis"] = "#8c959f", ["sui-accent"] = "#0072B2", ["sui-bg"] = "#ffffff",
    };

    /// <summary>
    /// The export of a custom <b>page</b>: <paramref name="files"/>' page with the view's props embedded (the rows <paramref name="resolved"/> gave as the viewer, the address's parameters, the light theme, the printable width and an automatic height, the
    /// <c>print</c> mode), the paper's <c>@page</c> rule and the bundled fonts' <c>@font-face</c> injected, and the app's files as the job's assets at the origin's root. The export ends when the view's <c>ready()</c> has been called, or with its words when it called <c>fail</c>.
    /// </summary>
    public static PrintJob Page(CustomView view, CustomAppFiles files, JsonObject resolved, PrintPaper paper, string title, BundledFonts? fonts, IReadOnlyDictionary<string, string>? tokens = null)
    {
        if (view.Kind != CustomViewKind.Page) throw new ArgumentException($"The custom view '{view.Name}' is a widget, not a page.", nameof(view));
        if (!files.Files.TryGetValue(files.Page, out var page)) throw new ExportException("page", $"the app '{files.App}' has no page '{files.Page}' among its built files, so the custom page '{view.Name}' cannot be drawn.");
        var width = Math.Round((paper.WidthMm - paper.Margins.Left - paper.Margins.Right) * 96 / 25.4, 2);
        var props = new JsonObject
        {
            ["data"] = resolved["data"]?.DeepClone() ?? new JsonObject(),
            ["params"] = resolved["params"]?.DeepClone() ?? new JsonObject(),
            ["theme"] = new JsonObject { ["scheme"] = "light", ["tokens"] = new JsonObject((tokens ?? LightTokens).Select(t => KeyValuePair.Create(t.Key, (JsonNode?)JsonValue.Create(t.Value)))) },
            ["size"] = new JsonObject { ["width"] = width, ["height"] = "auto" },
            ["mode"] = "print",
            ["links"] = new JsonObject(),
        };
        var init = new JsonObject { ["name"] = view.Export, ["props"] = props };
        // inside a script block that is not run: nothing in a value can close it
        var json = init.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("<", "\\u003c", StringComparison.Ordinal).Replace("\u2028", "\\u2028", StringComparison.Ordinal).Replace("\u2029", "\\u2029", StringComparison.Ordinal);
        var head = new StringBuilder();
        head.Append("<link rel=\"icon\" href=\"data:,\">\n<style>\n").Append(fonts?.FontFaceCss("") ?? "").Append(PrintPages.Rule(paper)).Append("\n</style>\n")
            .Append("<script id=\"dbdatakit-view-init\" type=\"application/json\">").Append(json).Append("</script>\n");
        var html = Encoding.UTF8.GetString(page.Content);
        var at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        html = at >= 0 ? html.Insert(at, head.ToString()) : head + html;
        var assets = new Dictionary<string, PrintAsset>(StringComparer.Ordinal);
        foreach (var (path, asset) in files.Files) assets[path.TrimStart('/')] = asset;
        return new PrintJob(title, html, assets, new PdfLayout(paper.WidthMm, paper.HeightMm, paper.Margins, null, null, PreferCssPage: true));
    }

    /// <summary>The paper of a custom page: the manifest's <c>page: {size, orientation}</c> (A4 portrait when it says nothing) with the margins of a flow layout (20 mm and 15 mm).</summary>
    public static PrintPaper PaperOf(CustomView view)
    {
        var sizes = new Dictionary<string, (double W, double H)> { ["A3"] = (297, 420), ["A4"] = (210, 297), ["A5"] = (148, 210), ["Letter"] = (215.9, 279.4), ["Legal"] = (215.9, 355.6) };
        var size = view.PageSize is { } named && sizes.ContainsKey(named) ? named : "A4";
        var (w, h) = sizes[size];
        if (view.Orientation == "landscape" && h > w) (w, h) = (h, w);
        return new PrintPaper($"{size} {(w > h ? "landscape" : "portrait")}", w, h, new PageMargins(20, 15, 20, 15), null, null);
    }
}
