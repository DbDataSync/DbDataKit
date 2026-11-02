using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

public static partial class AppManifestLoader
{
    private sealed partial class Reader
    {
        private static readonly string[] PortalKeys = ["identity", "theme", "layout", "navigation", "home", "mounts", "slots", "addresses"];
        private static readonly string[] SlotNames = ["header", "footer", "home", "aside"];
        private static readonly string[] HeaderParts = ["search", "person", "switcher"];
        private static readonly Regex TokenName = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TokenValue = new(@"^[A-Za-z0-9#%.,()/ '""_+*-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MountPath = new("^/[a-z0-9][a-z0-9_-]{0,63}/$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HostName = new("^(?=.{1,253}$)[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static PortalSpec Defaults() => new(new PortalIdentity(null, null, null, null, null, null), new PortalTheme(new Dictionary<string, string>(), new Dictionary<string, string>()),
            new PortalLayout(PortalMenu.Top, true, PortalWidth.Wide, ["person"]), [], [], [], new Dictionary<string, string>(), new PortalAddresses(null, []));

        /// <summary>An app that is not a portal has no <c>portal:</c> section.</summary>
        private PortalSpec? NoPortal(YamlMapping map)
        {
            if (map.Get("portal") is { } node) Add(BadPortal, node, "`portal` belongs to a portal: set `kind: portal`.", "Add `kind: portal`, or remove the section.");
            return null;
        }

        private PortalSpec Portal(YamlMapping map, string? entry, IReadOnlyList<string> views, AppTrust trust)
        {
            // A declared portal is data the shell renders. Code is for a trusted portal, which runs only once an administrator approves that version.
            if (trust == AppTrust.Sandbox && (entry is not null || views.Count > 0 || map.Get("entry") is not null || map.Get("views") is not null))
                Add(BadPortal, (YamlNode?)map.Get("entry") ?? map.Get("views") ?? map, "A declared portal contains no script or app views of its own.", "Remove `entry` and `views` (its pages are the `portal:` section), or ask for `trust: trusted` and have it approved.");
            if (map.Get("portal") is not { } node) return Defaults();
            if (node is not YamlMapping portal) { Add(BadPortal, node, "`portal` is a mapping of identity, theme, layout, navigation, home, mounts, slots and addresses."); return Defaults(); }
            CheckKeys(portal, PortalKeys, "`portal`");

            var mounts = Mounts(portal);
            var mounted = mounts.Select(m => m.App).ToHashSet(StringComparer.Ordinal);
            return new PortalSpec(Identity(portal), Theme(portal), Layout(portal), Navigation(portal, mounted), Home(portal), mounts, Slots(portal, mounted), Addresses(portal));
        }

        private string? Text(YamlMapping map, string key, string where)
        {
            if (map.Get(key) is not YamlScalar s) { if (map.Get(key) is { } other) Add(BadPortal, other, $"`{where}.{key}` is text."); return null; }
            if (s.Value.Length > 500 || s.Value.Any(char.IsControl)) { Add(BadPortal, s, $"`{where}.{key}` is one line of at most 500 characters."); return null; }
            return s.Value;
        }

        private string? Link(YamlScalar s, string where)
        {
            var v = s.Value;
            var ok = v.StartsWith("https://", StringComparison.Ordinal) ? Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Host.Length > 0
                : v.StartsWith('/') && !v.StartsWith("//", StringComparison.Ordinal) && !v.Contains('\\');
            if (!ok || v.Any(char.IsControl) || v.Length > 500) { Add(BadPortal, s, $"`{v}` is not a link for {where}: use an https:// address, or a path on the host that starts with a single `/`."); return null; }
            return v;
        }

        private PortalIdentity Identity(YamlMapping portal)
        {
            if (portal.Get("identity") is not { } node) return Defaults().Identity;
            if (node is not YamlMapping id) { Add(BadPortal, node, "`identity` is a mapping: title, logo, favicon, signIn, footer, support."); return Defaults().Identity; }
            CheckKeys(id, ["title", "logo", "favicon", "signIn", "footer", "support"], "`identity`");
            string? Path(string key) => id.Get(key) is YamlScalar s ? SafePath(s) : id.Get(key) is { } other ? Invalid(other, "identity." + key) : null;
            return new PortalIdentity(Text(id, "title", "identity"), Path("logo"), Path("favicon"), Text(id, "signIn", "identity"), Text(id, "footer", "identity"),
                id.Get("support") is YamlScalar support ? Link(support, "`identity.support`") : null);
        }

        private PortalTheme Theme(YamlMapping portal)
        {
            if (portal.Get("theme") is not { } node) return Defaults().Theme;
            if (node is not YamlMapping theme) { Add(BadPortal, node, "`theme` is a mapping with `light` and `dark`, each of design tokens."); return Defaults().Theme; }
            CheckKeys(theme, ["light", "dark"], "`theme`");
            return new PortalTheme(Tokens(theme, "light"), Tokens(theme, "dark"));
        }

        private Dictionary<string, string> Tokens(YamlMapping theme, string mode)
        {
            var tokens = new Dictionary<string, string>();
            if (theme.Get(mode) is not { } node) return tokens;
            if (node is not YamlMapping map) { Add(BadPortal, node, $"`theme.{mode}` is a mapping from token names to values."); return tokens; }
            foreach (var entry in map.Entries)
            {
                if (!TokenName.IsMatch(entry.Key.Value)) { Add(BadPortal, entry.Key, $"`{entry.Key.Value}` is not a token name: lower-case letters, digits and '-', such as `sui-accent`."); continue; }
                if (entry.Value is not YamlScalar value || !TokenValue.IsMatch(value.Value) || value.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) || value.Value.Contains("image", StringComparison.OrdinalIgnoreCase))
                { Add(BadPortal, entry.Value, $"The value of `{entry.Key.Value}` is a colour, a size or a font list: letters, digits and `#%.,()/ '\"` only, and it cannot refer to an image or any address."); continue; }
                tokens[entry.Key.Value] = value.Value;
            }
            return tokens;
        }

        private PortalLayout Layout(YamlMapping portal)
        {
            var d = Defaults().Layout;
            if (portal.Get("layout") is not { } node) return d;
            if (node is not YamlMapping layout) { Add(BadPortal, node, "`layout` is a mapping: menu, footer, width, header."); return d; }
            CheckKeys(layout, ["menu", "footer", "width", "header"], "`layout`");
            var menu = layout.Get("menu") is YamlScalar m ? Choice(m, "layout.menu", new Dictionary<string, PortalMenu> { ["top"] = PortalMenu.Top, ["side"] = PortalMenu.Side }) : d.Menu;
            var width = layout.Get("width") is YamlScalar w ? Choice(w, "layout.width", new Dictionary<string, PortalWidth> { ["narrow"] = PortalWidth.Narrow, ["medium"] = PortalWidth.Medium, ["wide"] = PortalWidth.Wide, ["full"] = PortalWidth.Full }) : d.Width;
            var footer = d.Footer;
            if (layout.Get("footer") is { } f)
            {
                if (f is YamlScalar { Value: "true" or "false" } b) footer = b.Value == "true";
                else Add(BadPortal, f, "`layout.footer` is true or false.");
            }
            var header = new List<string>(d.Header);
            if (layout.Get("header") is { } h)
            {
                header.Clear();
                if (h is not YamlSequence list) Add(BadPortal, h, "`layout.header` is a list of what the header shows: search, person, switcher.");
                else foreach (var item in list.Items)
                    if (item is YamlScalar s && HeaderParts.Contains(s.Value)) { if (!header.Contains(s.Value)) header.Add(s.Value); }
                    else Add(BadPortal, item, $"`{(item as YamlScalar)?.Value}` is not something the header shows.", $"Use: {string.Join(", ", HeaderParts)}.");
            }
            return new PortalLayout(menu, footer, width, header);
        }

        private List<PortalMount> Mounts(YamlMapping portal)
        {
            var mounts = new List<PortalMount>();
            if (portal.Get("mounts") is not { } node) return mounts;
            if (node is not YamlSequence list) { Add(BadPortal, node, "`mounts` is a list: `- app: sales` with a `path: /sales/`."); return mounts; }
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping mount) { Add(BadPortal, item, "A mount is a mapping with `app` and `path`."); continue; }
                CheckKeys(mount, ["app", "path"], "a mount");
                var app = Scalar(mount, "app", required: true, mount);
                var path = Scalar(mount, "path", required: true, mount);
                if (app is null || path is null) continue;
                if (!NameRule.IsMatch(app.Value)) { Add(BadName, app, $"`{app.Value}` is not an instance name."); continue; }
                if (!MountPath.IsMatch(path.Value) || Reserved.Contains(path.Value.Trim('/'))) { Add(BadPortal, path, $"`{path.Value}` is not a mount path: one lower-case segment between slashes, such as `/sales/`, not one the host keeps for itself."); continue; }
                if (mounts.Any(m => m.Path == path.Value)) { Add(BadPortal, path, $"`{path.Value}` is mounted twice."); continue; }
                if (mounts.Any(m => m.App == app.Value)) { Add(BadPortal, app, $"`{app.Value}` is mounted twice; one instance has one place in a portal."); continue; }
                mounts.Add(new PortalMount(app.Value, path.Value));
            }
            return mounts;
        }

        private List<PortalNav> Navigation(YamlMapping portal, HashSet<string> mounted)
        {
            if (portal.Get("navigation") is not { } node) return [];
            return Entries(node, mounted, "navigation", 0);
        }

        private List<PortalNav> Entries(YamlNode node, HashSet<string> mounted, string where, int depth)
        {
            var found = new List<PortalNav>();
            if (node is not YamlSequence list) { Add(BadPortal, node, $"`{where}` is a list of menu entries."); return found; }
            if (depth > 3) { Add(BadPortal, node, "A menu nests at most three levels."); return found; }
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping entry) { Add(BadPortal, item, "A menu entry is a mapping with one of `app`, `link`, `group` or `divider`."); continue; }
                CheckKeys(entry, ["app", "link", "group", "divider", "label", "icon", "role", "items"], "a menu entry");
                var kinds = new[] { "app", "link", "group", "divider" }.Where(k => entry.Get(k) is not null).ToList();
                if (kinds.Count != 1) { Add(BadPortal, entry, "A menu entry is exactly one of `app`, `link`, `group` or `divider`."); continue; }
                var label = Text(entry, "label", "a menu entry");
                var icon = entry.Get("icon") is YamlScalar i && Identifier.IsMatch(i.Value.Replace('-', '_')) ? i.Value : null;
                if (entry.Get("icon") is { } ic && icon is null) Add(BadPortal, ic, "`icon` is the name of an icon: letters, digits and '-'.");
                string? role = null;
                if (entry.Get("role") is YamlScalar r) { if (NameRule.IsMatch(r.Value)) role = r.Value; else Add(BadName, r, $"`{r.Value}` is not a role name."); }
                switch (kinds[0])
                {
                    case "app":
                        if (entry.Get("app") is YamlScalar app && NameRule.IsMatch(app.Value))
                        {
                            if (!mounted.Contains(app.Value)) { Add(BadPortal, app, $"`{app.Value}` is not mounted in this portal; add it to `mounts`.", "A menu entry can only open an instance the portal mounts."); break; }
                            found.Add(new PortalNav(NavKind.App, label, icon, app.Value, null, role, []));
                        }
                        else Add(BadName, entry.Get("app")!, "`app` names a mounted instance.");
                        break;
                    case "link":
                        if (entry.Get("link") is YamlScalar link && Link(link, "a menu entry") is { } href) found.Add(new PortalNav(NavKind.Link, label, icon, null, href, role, []));
                        break;
                    case "group":
                        if (entry.Get("group") is YamlScalar title && entry.Get("items") is { } items) found.Add(new PortalNav(NavKind.Group, title.Value, icon, null, null, role, Entries(items, mounted, "items", depth + 1)));
                        else Add(BadPortal, entry, "A group has a name and its `items`.");
                        break;
                    default:
                        found.Add(new PortalNav(NavKind.Divider, null, null, null, null, null, []));
                        break;
                }
            }
            return found;
        }

        private List<string> Home(YamlMapping portal)
        {
            var home = new List<string>();
            if (portal.Get("home") is not { } node) return home;
            if (node is not YamlSequence list) { Add(BadPortal, node, "`home` is a list of view files."); return home; }
            foreach (var item in list.Items)
                if (item is YamlScalar s && SafePath(s) is { } p) home.Add(p); else if (item is not YamlScalar) Add(BadPortal, item, "A home view is a file path.");
            return home;
        }

        private Dictionary<string, string> Slots(YamlMapping portal, HashSet<string> mounted)
        {
            var slots = new Dictionary<string, string>();
            if (portal.Get("slots") is not { } node) return slots;
            if (node is not YamlMapping map) { Add(BadPortal, node, "`slots` is a mapping from a slot (header, footer, home, aside) to the app that fills it."); return slots; }
            foreach (var entry in map.Entries)
            {
                if (!SlotNames.Contains(entry.Key.Value)) { Add(BadPortal, entry.Key, $"`{entry.Key.Value}` is not a slot.", $"Use: {string.Join(", ", SlotNames)}."); continue; }
                if (entry.Value is not YamlScalar { Value.Length: > 0 } app) { Add(BadPortal, entry.Value, $"The `{entry.Key.Value}` slot names no app."); continue; }
                if (!mounted.Contains(app.Value)) { Add(BadPortal, app, $"`{app.Value}` fills the `{entry.Key.Value}` slot but is not mounted in this portal; add it to `mounts`."); continue; }
                slots[entry.Key.Value] = app.Value;
            }
            return slots;
        }

        private PortalAddresses Addresses(YamlMapping portal)
        {
            if (portal.Get("addresses") is not { } node) return Defaults().Addresses;
            if (node is not YamlMapping map) { Add(BadPortal, node, "`addresses` is a mapping: subdomain and domains."); return Defaults().Addresses; }
            CheckKeys(map, ["subdomain", "domains"], "`addresses`");
            string? sub = null;
            if (map.Get("subdomain") is YamlScalar s)
            {
                if (NameRule.IsMatch(s.Value) && !Reserved.Contains(s.Value)) sub = s.Value;
                else Add(BadPortal, s, $"`{s.Value}` is not a subdomain name: lower-case letters, digits and '-', not one the host keeps for itself.");
            }
            var domains = new List<string>();
            if (map.Get("domains") is { } d)
            {
                if (d is not YamlSequence list) Add(BadPortal, d, "`addresses.domains` is a list of host names.");
                else foreach (var item in list.Items)
                    if (item is YamlScalar h && HostName.IsMatch(h.Value)) { if (!domains.Contains(h.Value)) domains.Add(h.Value); }
                    else Add(BadPortal, item, $"`{(item as YamlScalar)?.Value}` is not a host name: lower-case, with dots, no scheme, port or path.");
            }
            return new PortalAddresses(sub, domains);
        }
    }
}
