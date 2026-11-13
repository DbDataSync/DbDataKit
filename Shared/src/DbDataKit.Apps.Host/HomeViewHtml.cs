using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Apps;

/// <summary>
/// A portal's declarative home (plan 26 section 4a, AP12) as the shell draws it: the blocks of its home view files in order, text as the markdown subset, links as a list, tiles for the apps this person holds a role on.
/// Nothing is script and everything is encoded; a link that is not https:// or a path on the host is drawn as plain text even when a damaged file carried it.
/// </summary>
public static partial class HomeViewHtml
{
    /// <summary>
    /// The home's content for this person, or null when the portal declares no home view (the shell then shows the catalog as its tiles). A file that is missing or does not read is skipped: publishing refuses such a portal,
    /// so this is only a damaged store, and the person still gets what the other files and the tiles give.
    /// </summary>
    public static string? Render(AppInstanceStore instances, ResolvedInstance portal, AppSession session)
    {
        var spec = portal.Manifest.Portal!;
        if (spec.Home.Count == 0) return null;
        var mounted = spec.Mounts.Select(m => m.App).ToHashSet(StringComparer.Ordinal);
        var catalog = PortalChrome.Tiles(instances, portal, session);
        var html = new StringBuilder("<main style=\"padding:16px;max-width:60em\">");
        var drew = false;
        foreach (var path in spec.Home)
        {
            if (!portal.Package.Files.TryGetValue(path, out var bytes)) continue;
            if (HomeViewLoader.Load(Encoding.UTF8.GetString(bytes), path, mounted, portal.Package.Files.ContainsKey) is not { Ok: true, View: { } view }) continue;
            foreach (var block in view.Blocks)
            {
                if (!MayBeSeen(block, portal, session)) continue;
                switch (block)
                {
                    case HomeDashboard dashboard:
                        // the viewer's own access decides: a person with no role on the app has no dashboard, as they have no tile
                        if (session.RolesOn(dashboard.App).Count == 0 || instances.Resolve(dashboard.App) is not { } member) continue;
                        html.Append(DashboardFrame(dashboard, member, spec));
                        break;
                    case HomeImage image:
                        html.Append($"<p><img alt=\"{WebUtility.HtmlEncode(image.Alt)}\" src=\"{WebUtility.HtmlEncode(AppSite.BasePath + Uri.EscapeDataString(portal.Instance.Name) + "/" + AppSite.BundleFolder + string.Join('/', image.File.Split('/').Select(Uri.EscapeDataString)))}\" style=\"max-width:100%;height:auto{(image.Width is { } w ? $";width:{w}px" : "")}\"></p>");
                        break;
                    case HomeTable table:
                        html.Append("<table class=\"home-table\" style=\"border-collapse:collapse;margin:8px 0\"><thead><tr>");
                        foreach (var column in table.Columns) html.Append("<th style=\"text-align:left;border-bottom:1px solid #8886;padding:4px 12px 4px 0\">").Append(WebUtility.HtmlEncode(column)).Append("</th>");
                        html.Append("</tr></thead><tbody>");
                        foreach (var row in table.Rows)
                        {
                            html.Append("<tr>");
                            foreach (var cell in row) html.Append("<td style=\"padding:4px 12px 4px 0\">").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
                            html.Append("</tr>");
                        }
                        html.Append("</tbody></table>");
                        break;
                    case HomeText text: html.Append(Markdown(text.Markdown)); break;
                    case HomeLinks links:
                        html.Append("<ul class=\"links\">");
                        foreach (var link in links.Links) html.Append($"<li><a href=\"{WebUtility.HtmlEncode(link.Href)}\"{(link.Href.StartsWith('/') ? "" : " rel=\"noopener noreferrer\"")}>{WebUtility.HtmlEncode(link.Label)}</a></li>");
                        html.Append("</ul>");
                        break;
                    case HomeTiles tiles:
                        // the same rule as the catalog: only apps the person may open; a list names which, and in what order
                        var shown = tiles.Apps is null ? catalog
                            : [.. tiles.Apps.Select(app => spec.Mounts.FirstOrDefault(m => m.App == app)).Where(m => m is not null)
                                .Select(m => catalog.FirstOrDefault(t => t.Url == PortalChrome.MountUrl(portal.Instance.Name, m!))).Where(t => t is not null).Select(t => t!)];
                        html.Append(AppShell.TilesHtml(shown));
                        break;
                }
                drew = true;
            }
        }
        html.Append("</main>");
        return drew ? html.ToString() : null;
    }

    /// <summary>A block with <c>roles</c> is seen by a person who holds one of them on the portal, or, for a dashboard, on its app; a block without them by everyone the home is open to.</summary>
    private static bool MayBeSeen(HomeBlock block, ResolvedInstance portal, AppSession session) =>
        block.Roles is not { Count: > 0 } roles
        || roles.Any(role => session.RolesOn(portal.Instance.Name).Contains(role) || (block is HomeDashboard d && session.RolesOn(d.App).Contains(role)));

    /// <summary>A dashboard block: the mounted app as a sandboxed frame of its own (the same sandbox and broker as a slot, its token for its own instance only, the portal's theme), opened at the named view.</summary>
    private static string DashboardFrame(HomeDashboard block, ResolvedInstance member, PortalSpec spec)
    {
        var title = member.Instance.Title ?? member.Manifest.Title ?? member.Instance.Name;
        // a dashboard or a report file of the app is drawn to the frame's whole box (no menu, no title: `_as=block`), in the layout its selectors pick for that box; the other views are drawn as they are
        var isDocument = block.View is not null && member.Package.Files.TryGetValue(block.View, out var bytes) && ViewSpecLoader.IsDocument(Encoding.UTF8.GetString(bytes));
        var route = block.View is null ? "" : $" data-route=\"{WebUtility.HtmlEncode("/" + block.View + (isDocument ? "?_as=block" : ""))}\"";
        return $"<iframe class=\"slot home-dashboard\" data-instance=\"{WebUtility.HtmlEncode(block.App)}\"{route} data-theme=\"{WebUtility.HtmlEncode(PortalChrome.ThemeJson(spec.Theme))}\" title=\"{WebUtility.HtmlEncode(title)}\" sandbox=\"allow-scripts allow-forms allow-popups\" style=\"height:{block.Height}px;width:100%;flex:none;border:1px solid #8884;border-radius:8px;margin:8px 0\" src=\"{WebUtility.HtmlEncode(AppSite.BasePath + Uri.EscapeDataString(block.App) + "/" + AppSite.BundleFolder)}\"></iframe>";
    }

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|\*(?<italic>[^*\s][^*]*)\*|\[(?<text>[^\]]*)\]\((?<href>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Inline();

    /// <summary>The markdown subset as HTML: <c>#</c> to <c>###</c> headings, bullet lists (<c>- </c> or <c>* </c>), paragraphs, and inside them code, bold, italic and links. Text is encoded; a link target that is not safe is dropped to its text.</summary>
    public static string Markdown(string markdown)
    {
        var html = new StringBuilder();
        var paragraph = new List<string>();
        var inList = false;
        void EndParagraph() { if (paragraph.Count > 0) html.Append("<p>").Append(Spans(string.Join(' ', paragraph))).Append("</p>"); paragraph.Clear(); }
        void EndList() { if (inList) html.Append("</ul>"); inList = false; }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { EndParagraph(); EndList(); continue; }
            var level = line.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 3 && line.Length > level && line[level] == ' ')
            {
                EndParagraph(); EndList();
                html.Append($"<h{level}>").Append(Spans(line[(level + 1)..].Trim())).Append($"</h{level}>");
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                EndParagraph();
                if (!inList) html.Append("<ul>");
                inList = true;
                html.Append("<li>").Append(Spans(line[2..].Trim())).Append("</li>");
            }
            else { EndList(); paragraph.Add(line.Trim()); }
        }
        EndParagraph(); EndList();
        return html.ToString();
    }

    private static string Spans(string text)
    {
        var html = new StringBuilder();
        var at = 0;
        foreach (Match m in Inline().Matches(text))
        {
            html.Append(WebUtility.HtmlEncode(text[at..m.Index]));
            at = m.Index + m.Length;
            if (m.Groups["code"].Success) html.Append("<code>").Append(WebUtility.HtmlEncode(m.Groups["code"].Value)).Append("</code>");
            else if (m.Groups["bold"].Success) html.Append("<strong>").Append(WebUtility.HtmlEncode(m.Groups["bold"].Value)).Append("</strong>");
            else if (m.Groups["italic"].Success) html.Append("<em>").Append(WebUtility.HtmlEncode(m.Groups["italic"].Value)).Append("</em>");
            else
            {
                var label = WebUtility.HtmlEncode(m.Groups["text"].Value);
                var href = m.Groups["href"].Value;
                html.Append(HomeViewLoader.IsSafeLink(href) ? $"<a href=\"{WebUtility.HtmlEncode(href)}\"{(href.StartsWith('/') ? "" : " rel=\"noopener noreferrer\"")}>{label}</a>" : label);
            }
        }
        html.Append(WebUtility.HtmlEncode(text[at..]));
        return html.ToString();
    }
}
