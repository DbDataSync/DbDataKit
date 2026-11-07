using System.Text;
using System.Text.Json;

namespace DbDataKit.Apps;

/// <summary>
/// A portal's look as the shell draws it (plan 26 section 4a, AP11): identity, theme tokens, layout and a menu filtered to what this person may open. Built from the portal instance's declaration; the portal contains no
/// script, so everything here is data the host's own page renders (every value is encoded or checked on the way out).
/// </summary>
public static class PortalChrome
{
    /// <summary>The portal instance a request is framed by: the one the app instance's <c>portal</c> setting names, when that instance is a portal.</summary>
    public const string PortalSetting = "portal";

    /// <summary>The mount of a portal at <paramref name="path"/> (a single segment such as <c>sales</c>), or null.</summary>
    public static PortalMount? MountAt(PortalSpec portal, string path) => portal.Mounts.FirstOrDefault(m => m.Path.Trim('/') == path.Trim('/'));

    public static string MountUrl(string portalInstance, PortalMount mount) => $"{AppSite.BasePath}{Uri.EscapeDataString(portalInstance)}/{mount.Path.Trim('/')}/";

    /// <summary>The look of <paramref name="portal"/> (an instance of an app of kind portal) for this person, with <paramref name="currentApp"/> (a mounted instance, or null for the portal's home) marked.</summary>
    public static AppShell.PortalLook Look(AppInstanceStore instances, ResolvedInstance portal, AppSession session, string? currentApp)
    {
        var spec = portal.Manifest.Portal!;
        var bundle = $"{AppSite.BasePath}{Uri.EscapeDataString(portal.Instance.Name)}/{AppSite.BundleFolder}";
        var layout = spec.Layout;
        return new AppShell.PortalLook(
            spec.Identity.Title ?? portal.Instance.Title ?? portal.Manifest.Title ?? portal.Instance.Name,
            spec.Identity.Logo is { } logo ? bundle + logo : null,
            spec.Identity.Favicon is { } icon ? bundle + icon : null,
            spec.Identity.FooterText, spec.Identity.SupportLink,
            Theme(spec.Theme), ThemeJson(spec.Theme),
            layout.Menu == PortalMenu.Side, layout.Footer, layout.Width, layout.Header.Contains("person"),
            Items(instances, portal, spec.Navigation, spec, session, currentApp),
            Slots(instances, spec, session));
    }

    /// <summary>The apps in the portal's slots that this person may open (AP13); a slot whose app they hold no role on is left empty, like a menu entry.</summary>
    private static Dictionary<string, AppShell.SlotApp> Slots(AppInstanceStore instances, PortalSpec spec, AppSession session)
    {
        var slots = new Dictionary<string, AppShell.SlotApp>();
        foreach (var (slot, app) in spec.Slots)
            if (session.RolesOn(app).Count > 0 && instances.Resolve(app) is { } member)
                slots[slot] = new AppShell.SlotApp(app, member.Instance.Title ?? member.Manifest.Title ?? app);
        return slots;
    }

    /// <summary>The portal's home (AP12): one tile for each mounted instance the person holds a role on, in the order of the mounts. The same rule as the menu, so the home never advertises what ends in a refusal.</summary>
    public static List<AppShell.Tile> Tiles(AppInstanceStore instances, ResolvedInstance portal, AppSession session) =>
        portal.Manifest.Portal!.Mounts
            .Where(m => session.RolesOn(m.App).Count > 0)
            .Select(m => (Mount: m, Member: instances.Resolve(m.App)))
            .Where(x => x.Member is not null)
            .Select(x => new AppShell.Tile(x.Member!.Instance.Title ?? x.Member.Manifest.Title ?? x.Member.Instance.Name, x.Member.Manifest.Description, MountUrl(portal.Instance.Name, x.Mount)))
            .ToList();

    private static List<AppShell.NavItem> Items(AppInstanceStore instances, ResolvedInstance portal, IReadOnlyList<PortalNav> entries, PortalSpec spec, AppSession session, string? currentApp)
    {
        var items = new List<AppShell.NavItem>();
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case NavKind.App:
                    var mount = spec.Mounts.First(m => m.App == entry.App);
                    if (session.RolesOn(mount.App).Count == 0 || instances.Resolve(mount.App) is not { } member) break;
                    if (entry.Role is { } r && !session.RolesOn(mount.App).Contains(r) && !session.RolesOn(portal.Instance.Name).Contains(r)) break;
                    items.Add(new AppShell.NavItem(NavKind.App, entry.Label ?? member.Instance.Title ?? member.Manifest.Title ?? member.Instance.Name, entry.Icon, MountUrl(portal.Instance.Name, mount), mount.App == currentApp, []));
                    break;
                case NavKind.Link:
                    if (entry.Role is { } lr && !session.RolesOn(portal.Instance.Name).Contains(lr)) break;
                    items.Add(new AppShell.NavItem(NavKind.Link, entry.Label ?? entry.Href, entry.Icon, entry.Href, false, []));
                    break;
                case NavKind.Group:
                    if (entry.Role is { } gr && !session.RolesOn(portal.Instance.Name).Contains(gr)) break;
                    var children = Items(instances, portal, entry.Children, spec, session, currentApp);
                    // a group nobody may open anything in is not shown
                    if (children.Any(c => c.Kind != NavKind.Divider)) items.Add(new AppShell.NavItem(NavKind.Group, entry.Label, entry.Icon, null, children.Any(c => c.Current), children));
                    break;
                default:
                    items.Add(new AppShell.NavItem(NavKind.Divider, null, null, null, false, []));
                    break;
            }
        }
        return items;
    }

    /// <summary>The theme as CSS: light on <c>:root</c>, dark under the browser's preference. A token is a custom property (<c>--sui-accent</c>); values were limited by the manifest's loader to colours, sizes and font lists, and one that could open a comment is dropped here as well.</summary>
    public static string Theme(PortalTheme theme)
    {
        static string Block(IReadOnlyDictionary<string, string> tokens) => string.Concat(tokens.Where(t => !t.Value.Contains("/*")).OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"--{t.Key}:{t.Value};"));
        var css = new StringBuilder();
        if (theme.Light.Count > 0) css.Append(":root{").Append(Block(theme.Light)).Append('}');
        if (theme.Dark.Count > 0) css.Append("@media (prefers-color-scheme: dark){:root{").Append(Block(theme.Dark)).Append("}}");
        return css.ToString();
    }

    /// <summary>The same tokens as JSON, for the shell to pass to the app's frame: the frame is a different document and cannot read the shell's variables (<c>{type: "theme", light, dark}</c>).</summary>
    public static string ThemeJson(PortalTheme theme) => JsonSerializer.Serialize(new { light = theme.Light.Where(t => !t.Value.Contains("/*")).ToDictionary(t => t.Key, t => t.Value), dark = theme.Dark.Where(t => !t.Value.Contains("/*")).ToDictionary(t => t.Key, t => t.Value) });
}
