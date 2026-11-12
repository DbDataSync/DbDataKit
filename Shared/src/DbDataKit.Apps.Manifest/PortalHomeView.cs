using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

/// <summary>
/// One block of a portal's home view (plan 26 section 4a, AP12, AP30). A home view is data the shell draws, never script. Block kinds are <see cref="HomeText"/>, <see cref="HomeLinks"/>, <see cref="HomeTiles"/>,
/// <see cref="HomeDashboard"/> (a view of a mounted app of views, drawn as the viewer), <see cref="HomeImage"/> and <see cref="HomeTable"/>. Any block may carry <c>roles</c>: it is shown only to a person who holds one of them.
/// </summary>
public abstract record HomeBlock(int Line)
{
    /// <summary>The roles that may see the block (a role of the portal, or for a dashboard also a role on its app), or null for everyone the page is open to.</summary>
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>
/// A view of a mounted app, drawn in the block's box as a frame of that instance (AP30): the app's own sandbox, its own token for the person, the portal's theme; shown only to a person who holds a role on the app. <see cref="View"/> is the
/// path of a view file of the app (`views/sales.yaml`), or null for the app's first. <see cref="Height"/> is the box's height in pixels.
/// </summary>
public sealed record HomeDashboard(string App, string? View, int Height, int Line) : HomeBlock(Line);

/// <summary>An image that is a file of the portal's own package (<see cref="File"/>, such as `assets/banner.png`), with the text for those who cannot see it.</summary>
public sealed record HomeImage(string File, string Alt, int? Width, int Line) : HomeBlock(Line);

/// <summary>A small table of text typed in the file: the columns and the rows (every cell is text, encoded when drawn).</summary>
public sealed record HomeTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int Line) : HomeBlock(Line);

/// <summary>Text in the markdown subset of <see cref="HomeViewLoader"/>: headings, paragraphs, bullet lists, bold, italic, code and links. Raw HTML is not part of it.</summary>
public sealed record HomeText(string Markdown, int Line) : HomeBlock(Line);

public sealed record HomeLink(string Label, string Href);

/// <summary>A list of links: each an https:// address or a path on the host.</summary>
public sealed record HomeLinks(IReadOnlyList<HomeLink> Links, int Line) : HomeBlock(Line);

/// <summary>Tiles for apps the person may open (the catalog): <see cref="Apps"/> names the portal's mounted instances to show, in this order, or is null for every mounted instance. A person sees only the tiles for apps they hold a role on.</summary>
public sealed record HomeTiles(IReadOnlyList<string>? Apps, int Line) : HomeBlock(Line);

public sealed record PortalHomeView(IReadOnlyList<HomeBlock> Blocks);

public sealed class HomeViewLoadResult(PortalHomeView? view, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public PortalHomeView? View { get; } = view;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => View is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of a portal's home view file (<c>schemaVersion: 1</c> and <c>blocks</c>), minimal first format: markdown text, a list of links and tiles. Each problem is a diagnostic with the file, line and column
/// (<c>DBD-APP-011</c> the shape, <c>DBD-APP-012</c> a tile for an app the portal does not mount, <c>DBD-APP-013</c> raw HTML or an unsafe link in text), in the series of the manifest's own.
/// </summary>
public static partial class HomeViewLoader
{
    public const string BadHomeView = "DBD-APP-011";
    public const string UnknownTileApp = "DBD-APP-012";
    public const string UnsafeHomeText = "DBD-APP-013";
    public const string UnknownRole = "DBD-APP-026";
    public const string MissingHomeFile = "DBD-APP-027";

    public const int Version = 1;
    public const int MaximumBlocks = 50;
    public const int MaximumTextLength = 20_000;
    public const int MaximumLinks = 50;
    public const int MaximumColumns = 12;
    public const int MaximumRows = 200;
    public const int DefaultDashboardHeight = 360;

    // `<` followed by a letter, `/`, `!` or `?` opens a tag, a comment or a declaration: none of it is home text
    [GeneratedRegex(@"<[A-Za-z/!?]", RegexOptions.CultureInvariant)]
    private static partial Regex RawHtml();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\((?<href>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    /// <summary>Whether a link target may be followed from the home: an https:// address with a host, or a path on the host that starts with a single `/`.</summary>
    public static bool IsSafeLink(string href) =>
        href.Length is > 0 and <= 500 && !href.Any(char.IsControl) && !href.Contains(' ') &&
        (href.StartsWith("https://", StringComparison.Ordinal) ? Uri.TryCreate(href, UriKind.Absolute, out var u) && u.Host.Length > 0
            : href.StartsWith('/') && !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains('\\'));

    /// <param name="yaml">The file's text.</param>
    /// <param name="file">The file's path in the app, which diagnostics name.</param>
    /// <param name="mounted">The instances the portal mounts, to check the tiles' and dashboards' names against; null skips that check.</param>
    /// <param name="exists">Whether a file is in the portal's package, to check an image against; null skips that check.</param>
    /// <param name="roles">The roles the portal's manifest declares, to check the blocks' `roles` against; null skips that check.</param>
    public static HomeViewLoadResult Load(string yaml, string file, IReadOnlySet<string>? mounted = null, Func<string, bool>? exists = null, IReadOnlySet<string>? roles = null)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadHomeView, file, 1, 1, "The home view file is empty.", "Write `schemaVersion: 1` and a list of `blocks`."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var view = new Reader(file, diags, mounted, exists, roles).Read(root);
        return new(diags.HasErrors() ? null : view, diags);
    }

    private static readonly Regex ViewPath = new("^[A-Za-z0-9][A-Za-z0-9_./-]{0,200}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class Reader(string file, List<ConfigDiagnostic> diags, IReadOnlySet<string>? mounted, Func<string, bool>? exists, IReadOnlySet<string>? roleNames) : YamlFieldReader(file, diags)
    {
        public PortalHomeView? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Add(BadHomeView, root, "A home view is a mapping with `schemaVersion: 1` and `blocks`."); return null; }
            CheckKeys(map, ["schemaVersion", "blocks"], "a home view");
            if (map.Get("schemaVersion") is not YamlScalar { Value: "1" }) { Add(BadHomeView, (YamlNode?)map.Get("schemaVersion") ?? map, "`schemaVersion` must be 1.", "Write `schemaVersion: 1` first."); return null; }
            if (map.Get("blocks") is not YamlSequence list) { Add(BadHomeView, (YamlNode?)map.Get("blocks") ?? map, "`blocks` is a list of text, links, tiles, dashboards, images and tables."); return null; }
            if (list.Items.Count > MaximumBlocks) Add(BadHomeView, list, $"A home view has at most {MaximumBlocks} blocks.");
            var blocks = new List<HomeBlock>();
            foreach (var item in list.Items.Take(MaximumBlocks))
                if (Block(item) is { } block) blocks.Add(block);
            return new PortalHomeView(blocks);
        }

        private HomeBlock? Block(YamlNode item)
        {
            if (item is not YamlMapping block || block.Entries.Count == 0) { Add(BadHomeView, item, "A block is a mapping with one of `text`, `links`, `tiles`, `dashboard`, `image` or `table`, and optionally `roles`."); return null; }
            var kinds = block.Entries.Where(e => e.Key.Value != "roles").ToList();
            if (kinds.Count != 1) { Add(BadHomeView, item, "A block is a mapping with exactly one of `text`, `links`, `tiles`, `dashboard`, `image` or `table` (and optionally `roles`)."); return null; }
            var entry = kinds[0];
            HomeBlock? result;
            switch (entry.Key.Value)
            {
                case "text": result = Text(entry); break;
                case "links": result = Links(entry); break;
                case "tiles": result = Tiles(entry); break;
                case "dashboard": result = Dashboard(entry); break;
                case "image": result = Image(entry); break;
                case "table": result = Table(entry); break;
                default:
                    Add(BadHomeView, entry.Key, $"`{entry.Key.Value}` is not a block.", "Use `text`, `links`, `tiles`, `dashboard`, `image` or `table`.");
                    return null;
            }
            if (result is null) return null;
            if (block.Get("roles") is { } rolesNode)
            {
                if (rolesNode is not YamlSequence list || list.Items.Count == 0 || list.Items.Count > 20) { Add(BadHomeView, rolesNode, "`roles` is a list of the roles that may see the block."); return null; }
                var names = new List<string>();
                foreach (var role in list.Items)
                {
                    if (role is not YamlScalar name || !AppManifestLoader.NameRule.IsMatch(name.Value)) { Add(AppManifestLoader.BadName, role, "A role is a name: lower-case letters, digits, '-' and '_'."); continue; }
                    if (roleNames is not null && !roleNames.Contains(name.Value)) { Add(UnknownRole, name, $"`{name.Value}` is not a role of this portal; its manifest's `roles` lists the ones there are."); continue; }
                    if (!names.Contains(name.Value)) names.Add(name.Value);
                }
                result = result with { Roles = names };
            }
            return result;
        }

        private HomeDashboard? Dashboard(YamlEntry entry)
        {
            if (entry.Value is not YamlMapping map) { Add(BadHomeView, entry.Value, "`dashboard` is a mapping with the `app` (a mounted instance), an optional `view` and an optional `height`."); return null; }
            CheckKeys(map, ["app", "view", "height"], "a dashboard block");
            var app = Scalar(map, "app", required: true, map);
            if (app is null) return null;
            if (!AppManifestLoader.NameRule.IsMatch(app.Value)) { Add(AppManifestLoader.BadName, app, "A dashboard block names a mounted instance."); return null; }
            if (mounted is not null && !mounted.Contains(app.Value)) { Add(UnknownTileApp, app, $"`{app.Value}` is not mounted in this portal, so it has no dashboard; add it to `mounts`."); return null; }
            string? view = null;
            if (Scalar(map, "view", required: false, map) is { } v)
            {
                if (!ViewPath.IsMatch(v.Value) || v.Value.Contains("..")) { Add(BadHomeView, v, "`view` is the path of a view file of the app, such as `views/sales.yaml`."); return null; }
                view = v.Value;
            }
            var height = DefaultDashboardHeight;
            if (Scalar(map, "height", required: false, map) is { } h
                && (!int.TryParse(h.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out height) || height is < 120 or > 1200))
            { Add(BadHomeView, h, "`height` is a number of pixels from 120 to 1200."); return null; }
            return new HomeDashboard(app.Value, view, height, entry.Key.Line);
        }

        private HomeImage? Image(YamlEntry entry)
        {
            if (entry.Value is not YamlMapping map) { Add(BadHomeView, entry.Value, "`image` is a mapping with the `file` of the portal's package, the `alt` text and an optional `width`."); return null; }
            CheckKeys(map, ["file", "alt", "width"], "an image block");
            var file = Scalar(map, "file", required: true, map);
            var alt = Scalar(map, "alt", required: true, map);
            if (file is null || alt is null) return null;
            if (file.Value.StartsWith('/') || file.Value.Contains("..") || file.Value.Contains('\\') || file.Value.Contains('?') || file.Value.Contains('#') || file.Value.Any(char.IsControl) || file.Value.Length > 300)
            { Add(BadHomeView, file, "`file` is a path inside the portal's package, such as `assets/banner.png` (no leading `/`, no `..`)."); return null; }
            if (exists is not null && !exists(file.Value)) { Add(MissingHomeFile, file, $"`{file.Value}` is not in the portal's package.", "Add the file, or fix the path."); return null; }
            if (alt.Value.Length > 300 || alt.Value.Any(char.IsControl)) { Add(BadHomeView, alt, "`alt` is one line of at most 300 characters."); return null; }
            int? width = null;
            if (Scalar(map, "width", required: false, map) is { } w)
            {
                if (!int.TryParse(w.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var px) || px is < 16 or > 2000) { Add(BadHomeView, w, "`width` is a number of pixels from 16 to 2000."); return null; }
                width = px;
            }
            return new HomeImage(file.Value, alt.Value, width, entry.Key.Line);
        }

        private HomeTable? Table(YamlEntry entry)
        {
            if (entry.Value is not YamlMapping map) { Add(BadHomeView, entry.Value, "`table` is a mapping with `columns` and `rows`."); return null; }
            CheckKeys(map, ["columns", "rows"], "a table block");
            if (map.Get("columns") is not YamlSequence columns || columns.Items.Count is 0 or > MaximumColumns || columns.Items.Any(c => c is not YamlScalar)) { Add(BadHomeView, (YamlNode?)map.Get("columns") ?? map, $"`columns` is a list of one to {MaximumColumns} headings."); return null; }
            if (map.Get("rows") is not YamlSequence rows || rows.Items.Count > MaximumRows) { Add(BadHomeView, (YamlNode?)map.Get("rows") ?? map, $"`rows` is a list of at most {MaximumRows} rows, each a list of cells."); return null; }
            var heads = columns.Items.Select(c => ((YamlScalar)c).Value).ToList();
            var body = new List<IReadOnlyList<string>>();
            foreach (var row in rows.Items)
            {
                if (row is not YamlSequence cells || cells.Items.Count != heads.Count || cells.Items.Any(c => c is not YamlScalar)) { Add(BadHomeView, row, $"A row is a list of {heads.Count} cells, one for each column."); return null; }
                body.Add([.. cells.Items.Select(c => ((YamlScalar)c).Value)]);
            }
            return new HomeTable(heads, body, entry.Key.Line);
        }

        private HomeText? Text(YamlEntry entry)
        {
            if (entry.Value is not YamlScalar text) { Add(BadHomeView, entry.Value, "`text` is markdown text."); return null; }
            if (text.Value.Length > MaximumTextLength) { Add(BadHomeView, text, $"A text block is at most {MaximumTextLength} characters."); return null; }
            var ok = true;
            // line numbers inside the text count from the scalar's own line, or the line after it for a block scalar (`|`), whose text starts below the key
            var first = text.Quoted && text.Value.Contains('\n') ? text.Line + 1 : text.Line;
            var lines = text.Value.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var at = first + i;
                if (RawHtml().Match(lines[i]) is { Success: true } html)
                { Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(UnsafeHomeText, File, at, html.Index + 1, "Home text has no HTML or script: this looks like a tag.", "Write markdown (headings, lists, bold, italic, `code`, links) only.")); ok = false; }
                foreach (Match link in MarkdownLink().Matches(lines[i]))
                    if (!IsSafeLink(link.Groups["href"].Value))
                    { Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(UnsafeHomeText, File, at, link.Index + 1, $"`{link.Groups["href"].Value}` is not a link a home may carry: use an https:// address, or a path on the host that starts with a single `/`.")); ok = false; }
            }
            return ok ? new HomeText(text.Value, text.Line) : null;
        }

        private HomeLinks? Links(YamlEntry entry)
        {
            if (entry.Value is not YamlSequence list || list.Items.Count == 0) { Add(BadHomeView, entry.Value, "`links` is a list of `- label: ...` with an `href`."); return null; }
            if (list.Items.Count > MaximumLinks) { Add(BadHomeView, list, $"A links block has at most {MaximumLinks} links."); return null; }
            var links = new List<HomeLink>();
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping link) { Add(BadHomeView, item, "A link is a mapping with `label` and `href`."); continue; }
                CheckKeys(link, ["label", "href"], "a link");
                var label = Scalar(link, "label", required: true, link);
                var href = Scalar(link, "href", required: true, link);
                if (label is null || href is null) continue;
                if (label.Value.Length > 200 || label.Value.Any(char.IsControl)) { Add(BadHomeView, label, "A link's `label` is one line of at most 200 characters."); continue; }
                if (!IsSafeLink(href.Value)) { Add(UnsafeHomeText, href, $"`{href.Value}` is not a link a home may carry: use an https:// address, or a path on the host that starts with a single `/`."); continue; }
                links.Add(new HomeLink(label.Value, href.Value));
            }
            return new HomeLinks(links, entry.Key.Line);
        }

        private HomeTiles? Tiles(YamlEntry entry)
        {
            if (entry.Value is YamlScalar { Value: "all" }) return new HomeTiles(null, entry.Key.Line);
            if (entry.Value is not YamlSequence list || list.Items.Count == 0) { Add(BadHomeView, entry.Value, "`tiles` is `all`, or a list of the instances to show, in order."); return null; }
            var apps = new List<string>();
            foreach (var item in list.Items)
            {
                if (item is not YamlScalar app || !AppManifestLoader.NameRule.IsMatch(app.Value)) { Add(AppManifestLoader.BadName, item, "A tile names a mounted instance."); continue; }
                if (mounted is not null && !mounted.Contains(app.Value)) { Add(UnknownTileApp, app, $"`{app.Value}` is not mounted in this portal, so it has no tile; add it to `mounts`."); continue; }
                if (!apps.Contains(app.Value)) apps.Add(app.Value);
            }
            return new HomeTiles(apps, entry.Key.Line);
        }
    }
}
