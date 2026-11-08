using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

/// <summary>
/// One block of a portal's home view (plan 26 section 4a, AP12). A home view is data the shell draws, never script. Block kinds are <see cref="HomeText"/>, <see cref="HomeLinks"/> and <see cref="HomeTiles"/>.
/// EXTENSION POINT: dashboard blocks (a view of the semantic layer, plan 22) are a further subtype that the loader will accept under a `dashboard` key; the loader refuses that key today with a message saying so,
/// and the shell draws only the kinds above.
/// </summary>
public abstract record HomeBlock(int Line);

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

    public const int Version = 1;
    public const int MaximumBlocks = 50;
    public const int MaximumTextLength = 20_000;
    public const int MaximumLinks = 50;

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
    /// <param name="mounted">The instances the portal mounts, to check the tiles' names against; null skips that check.</param>
    public static HomeViewLoadResult Load(string yaml, string file, IReadOnlySet<string>? mounted = null)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadHomeView, file, 1, 1, "The home view file is empty.", "Write `schemaVersion: 1` and a list of `blocks`."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var view = new Reader(file, diags, mounted).Read(root);
        return new(diags.HasErrors() ? null : view, diags);
    }

    private sealed class Reader(string file, List<ConfigDiagnostic> diags, IReadOnlySet<string>? mounted) : YamlFieldReader(file, diags)
    {
        public PortalHomeView? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Add(BadHomeView, root, "A home view is a mapping with `schemaVersion: 1` and `blocks`."); return null; }
            CheckKeys(map, ["schemaVersion", "blocks"], "a home view");
            if (map.Get("schemaVersion") is not YamlScalar { Value: "1" }) { Add(BadHomeView, (YamlNode?)map.Get("schemaVersion") ?? map, "`schemaVersion` must be 1.", "Write `schemaVersion: 1` first."); return null; }
            if (map.Get("blocks") is not YamlSequence list) { Add(BadHomeView, (YamlNode?)map.Get("blocks") ?? map, "`blocks` is a list of text, links and tiles."); return null; }
            if (list.Items.Count > MaximumBlocks) Add(BadHomeView, list, $"A home view has at most {MaximumBlocks} blocks.");
            var blocks = new List<HomeBlock>();
            foreach (var item in list.Items.Take(MaximumBlocks))
                if (Block(item) is { } block) blocks.Add(block);
            return new PortalHomeView(blocks);
        }

        private HomeBlock? Block(YamlNode item)
        {
            if (item is not YamlMapping block || block.Entries.Count != 1) { Add(BadHomeView, item, "A block is a mapping with exactly one of `text`, `links` or `tiles`."); return null; }
            var entry = block.Entries[0];
            switch (entry.Key.Value)
            {
                case "text": return Text(entry);
                case "links": return Links(entry);
                case "tiles": return Tiles(entry);
                case "dashboard":
                    Add(BadHomeView, entry.Key, "Dashboard blocks (a view of the semantic layer) are not in this version of home views.", "Use `text`, `links` or `tiles`.");
                    return null;
                default:
                    Add(BadHomeView, entry.Key, $"`{entry.Key.Value}` is not a block.", "Use `text`, `links` or `tiles`.");
                    return null;
            }
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
