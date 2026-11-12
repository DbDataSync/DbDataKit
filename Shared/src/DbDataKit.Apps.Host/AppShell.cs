using System.Net;
using System.Text;

namespace DbDataKit.Apps;

/// <summary>
/// The page at an instance's own address (plan 26, AP05b): the host's, not the app's. It is signed-in territory and runs nothing of the app (its one script is the token broker); the app's document is a frame under it with the <c>sandbox</c> attribute, which
/// gives it an opaque origin, so it cannot read this page, the host's cookies or its storage (a document with a <c>sandbox</c> Content-Security-Policy gets the same from the other side).
/// </summary>
public static class AppShell
{
    /// <summary>One link in a menu: where it goes and what it says. <see cref="Current"/> marks the instance being shown.</summary>
    public sealed record MenuLink(string Instance, string Label, string? Icon, bool Current);

    /// <summary>A navigation set as the signed-in person sees it: only the members they may open (plus the current instance).</summary>
    public sealed record Menu(string Title, IReadOnlyList<MenuLink> Links);

    /// <summary>One entry of a portal's menu, already filtered to what the person may open. <see cref="Current"/> marks the entry (or, for a group, a child) of the page being shown.</summary>
    public sealed record NavItem(NavKind Kind, string? Label, string? Icon, string? Href, bool Current, IReadOnlyList<NavItem> Children);

    /// <summary>A portal's identity, theme, layout and menu as the page draws them (see <see cref="PortalChrome"/>).</summary>
    public sealed record PortalLook(string Title, string? LogoUrl, string? FaviconUrl, string? FooterText, string? SupportLink, string ThemeCss, string ThemeJson,
        bool SideMenu, bool Footer, PortalWidth Width, bool ShowPerson, IReadOnlyList<NavItem> Navigation, IReadOnlyDictionary<string, SlotApp>? Slots = null);

    /// <summary>An app a portal mounts in one of its slots (header, footer, aside, home): the instance and its title. Only apps the person holds a role on are here.</summary>
    public sealed record SlotApp(string Instance, string Title);

    /// <summary>The shell's header: who is signed in and the menus of the sets the instance belongs to. Null where the app's manifest says <c>chrome: none</c>. With a <see cref="Portal"/> the portal's menu and look replace the sets.</summary>
    public sealed record Header(string Person, IReadOnlyList<Menu> Menus, PortalLook? Portal = null, string? Notice = null);

    /// <summary>The longest route the shell takes from a frame or an address; a longer one is dropped.</summary>
    public const int MaximumRoute = 512;

    public const string ScriptPath = "/apps/-/shell.js";

    /// <summary>
    /// The token broker's side in the page (plan 26 section 3): a frame asks with <c>postMessage({dbdata: "app/1", type: "token.request", id})</c> and gets <c>{type: "token", id, token, expiresIn, audience}</c> or <c>{type: "error", id, message}</c>.
    /// Only the app's own frame is answered (the message's source is checked against it), and the request to the host carries a header a cross-site page cannot send.
    /// </summary>
    public static readonly string Script = ScriptTemplate.Replace("__TOKENS__", CustomPages.TokensJson(), StringComparison.Ordinal);

    private const string ScriptTemplate = """
        (function () {
          var route = null;
          function valid(p) { return typeof p === "string" && p.length <= 512 && p.charAt(0) === "/" && p.charAt(1) !== "/" && !/[\u0000-\u001f\u007f]/.test(p); }
          function fromAddress() { var h = decodeURIComponent(location.hash.slice(1)); return valid(h) ? h : null; }
          // the page's frames: #app-frame is the page's app (it owns the address); each frame.slot is a portal slot, an app of its own with its own token
          var frames = Array.prototype.slice.call(document.querySelectorAll("iframe[data-instance]"));
          var main = document.getElementById("app-frame");
          function tell(path) { if (main) main.contentWindow.postMessage({ dbdata: "app/1", type: "navigate", path: path }, "*"); }
          // A custom page's frame (plan 32 DBR33): the page code says views:hello, this script fetches its rows as the person (the host's route, never the frame) and answers views:init. Only the SDK's shapes are read, only from this frame's window.
          var TOKENS = __TOKENS__;
          function fits(type, v) { return type === "integer" ? typeof v === "number" && isFinite(v) && Math.floor(v) === v : type === "number" ? typeof v === "number" && isFinite(v) : type === "boolean" ? typeof v === "boolean" : (typeof v === "string" || typeof v === "number" || typeof v === "boolean"); }
          function notice(frame, text) {
            var p = frame.previousElementSibling && frame.previousElementSibling.hasAttribute("data-view-notice") ? frame.previousElementSibling : null;
            if (!p) { p = document.createElement("p"); p.setAttribute("data-view-notice", ""); p.setAttribute("role", "alert"); p.style.cssText = "margin:0;padding:8px 16px;background:#fd03;border-bottom:1px solid #8884"; frame.parentNode.insertBefore(p, frame); }
            p.textContent = text;
          }
          function viewOf(frame) {
            if (!frame.__view) {
              var declared = {}; try { declared = JSON.parse(frame.getAttribute("data-params") || "{}"); } catch (e) {}
              frame.__view = { declared: declared, values: null, started: false };
              setTimeout(function () { if (!frame.__view.started) notice(frame, "This page did not start. Its app may not register the view '" + frame.getAttribute("data-export") + "'."); }, 15000);
            }
            return frame.__view;
          }
          function query(values) {
            var parts = [];
            Object.keys(values).sort().forEach(function (k) { [].concat(values[k]).forEach(function (one) { parts.push(encodeURIComponent(k) + "=" + encodeURIComponent(String(one))); }); });
            return parts.join("&");
          }
          function props(frame, rows) {
            var scheme = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
            return { data: rows.data || {}, params: rows.params || {}, theme: { scheme: scheme, tokens: TOKENS[scheme] }, size: { width: frame.clientWidth || 800, height: "auto" }, mode: "screen", links: {} };
          }
          function rows(frame) {
            var st = viewOf(frame);
            var q = st.values ? query(st.values) : location.search.slice(1);
            return fetch(frame.getAttribute("data-rows") + (q ? "?" + q : ""), { credentials: "same-origin", headers: { "X-DbData-Shell": "1" } })
              .then(function (r) { return r.json().then(function (b) { return r.ok ? b : Promise.reject(new Error(b.message || ("The host refused the page's rows (" + r.status + ")."))); }); })
              .then(function (b) { st.values = b.params || {}; var n = frame.previousElementSibling; if (n && n.hasAttribute("data-view-notice")) n.remove(); return b; });
          }
          function viewMessage(frame, m) {
            var st = viewOf(frame);
            switch (m.type) {
              case "views:hello":
                st.started = true;
                rows(frame).then(function (b) { frame.contentWindow.postMessage({ type: "views:init", name: frame.getAttribute("data-export"), props: props(frame, b) }, "*"); }).catch(function (e) { notice(frame, String(e.message || e)); });
                return;
              case "views:ready": frame.setAttribute("data-state", "ready"); return;
              case "views:error": if (typeof m.message === "string") notice(frame, m.message.slice(0, 2000)); frame.setAttribute("data-state", "error"); return;
              case "views:size": if (typeof m.height === "number" && isFinite(m.height) && m.height >= 0 && m.height < 1e7) frame.style.flex = "1 0 " + Math.ceil(m.height) + "px"; return;
              case "views:setParam": {
                var d = st.declared[m.name];
                if (typeof m.name !== "string" || !d || !st.values) { notice(frame, "The page asked to set `" + m.name + "`, which it does not declare."); return; }
                var v = m.value, list = v === null ? [] : Array.isArray(v) ? v : [v];
                if ((!d.many && Array.isArray(v)) || !list.every(function (x) { return fits(d.type, x); })) { notice(frame, "The page asked to set `" + m.name + "` to a value that is not a " + d.type + (d.many ? " list" : "") + "."); return; }
                if (list.length) st.values[m.name] = d.many ? list : list[0]; else delete st.values[m.name];
                history.replaceState(null, "", location.pathname + (Object.keys(st.values).length ? "?" + query(st.values) : "") + location.hash);
                rows(frame).then(function (b) { frame.contentWindow.postMessage({ type: "views:props", props: props(frame, b) }, "*"); }).catch(function (e) { notice(frame, String(e.message || e)); });
                return;
              }
            }
          }
          frames.forEach(function (frame) {
            frame.addEventListener("load", function () {
              var theme = frame.getAttribute("data-theme");
              if (theme) frame.contentWindow.postMessage({ dbdata: "app/1", type: "theme", tokens: JSON.parse(theme) }, "*");
              if (frame === main) { route = fromAddress(); if (route) tell(route); }
              else { var start = frame.getAttribute("data-route"); if (valid(start)) frame.contentWindow.postMessage({ dbdata: "app/1", type: "navigate", path: start }, "*"); }
            });
          });
          window.addEventListener("popstate", function () { var p = fromAddress(); if (p && p !== route) { route = p; tell(p); } });
          window.addEventListener("message", function (event) {
            var m = event.data;
            var frame = frames.filter(function (f) { return f.contentWindow === event.source; })[0];
            if (frame && m && frame.hasAttribute("data-view-page") && typeof m.type === "string" && m.type.indexOf("views:") === 0) { viewMessage(frame, m); return; }
            if (!frame || !m || m.dbdata !== "app/1") return;
            var instance = frame.getAttribute("data-instance");
            if (m.type === "route") {
              // only the page's own app moves the address; a slot's route is its own business
              if (frame === main && valid(m.path) && m.path !== route) { route = m.path; history.pushState(null, "", "#" + encodeURI(m.path)); }
              return;
            }
            if (m.type === "storage" && typeof m.id === "string") {
              fetch("/apps/" + encodeURIComponent(instance) + "/_storage", { method: "POST", credentials: "same-origin", headers: { "X-DbData-Shell": "1", "Content-Type": "application/json" },
                body: JSON.stringify({ op: m.op, scope: m.scope, key: m.key, value: m.value, ifVersion: m.ifVersion, prefix: m.prefix }) })
                .then(function (r) { return r.json().then(function (b) { return r.ok ? b : Promise.reject(new Error(b.message || ("The host refused the call (" + r.status + ")."))); }); })
                .then(function (b) { var reply = { dbdata: "app/1", id: m.id, type: "storage.result", result: b }; frame.contentWindow.postMessage(reply, "*"); })
                .catch(function (e) { frame.contentWindow.postMessage({ dbdata: "app/1", id: m.id, type: "error", message: String(e.message || e) }, "*"); });
              return;
            }
            if (m.type !== "token.request" || typeof m.id !== "string") return;
            function reply(body) { body.dbdata = "app/1"; body.id = m.id; frame.contentWindow.postMessage(body, "*"); }
            fetch("/apps/" + encodeURIComponent(instance) + "/_token", { method: "POST", credentials: "same-origin", headers: { "X-DbData-Shell": "1" } })
              .then(function (r) { return r.ok ? r.json() : Promise.reject(new Error("The host refused a token (" + r.status + ").")); })
              .then(function (t) { reply({ type: "token", token: t.token, expiresIn: t.expiresIn, audience: t.audience }); })
              .catch(function (e) { reply({ type: "error", message: String(e.message || e) }); });
          });
        })();
        """;

    public static AppResponse ScriptResponse() => new(200, "text/javascript; charset=utf-8", new Dictionary<string, string>
    {
        ["X-Content-Type-Options"] = "nosniff", ["Cache-Control"] = "no-cache",
    }, Encoding.UTF8.GetBytes(Script));

    public static AppResponse Page(string instance, string title, Header? header = null, IReadOnlyList<string>? embedOrigins = null)
    {
        var bundle = AppSite.BasePath + Uri.EscapeDataString(instance) + "/" + AppSite.BundleFolder;
        var theme = header?.Portal is { } look ? $" data-theme=\"{WebUtility.HtmlEncode(look.ThemeJson)}\"" : "";
        var frame = $"<iframe id=\"app-frame\" data-instance=\"{WebUtility.HtmlEncode(instance)}\"{theme} title=\"{WebUtility.HtmlEncode(title)}\" sandbox=\"allow-scripts allow-forms allow-popups\" src=\"{WebUtility.HtmlEncode(bundle)}\"></iframe>";
        return Respond(Document(title, header, frame), embedOrigins);
    }

    /// <summary>
    /// A custom page of an app (plan 32 DBR33): the shell's page with the app's own document as its one frame, mounted on the page's view (<c>#view=&lt;Export&gt;&amp;mode=screen</c>, which no server sees). The frame has the app's sandbox and is the instance's, so the
    /// shell script gives it a token if it asks; the script also plays the host's side of the SDK's conversation for it (<c>data-view-page</c>): it fetches the rows from <c>data-rows</c> as the person and tells the frame, so the code never fetches.
    /// The parameters are in the page's address; <c>data-params</c> is the declaration a <c>setParam</c> is checked against.
    /// </summary>
    public static AppResponse ViewPage(string instance, string title, CustomView view, Header? header = null, IReadOnlyList<string>? embedOrigins = null)
    {
        var bundle = AppSite.BasePath + Uri.EscapeDataString(instance) + "/" + AppSite.BundleFolder + "#view=" + Uri.EscapeDataString(view.Export) + "&mode=screen";
        var frame = $"<iframe id=\"view-frame\" data-instance=\"{WebUtility.HtmlEncode(instance)}\" data-view-page=\"{WebUtility.HtmlEncode(view.Name)}\" data-export=\"{WebUtility.HtmlEncode(view.Export)}\" data-rows=\"{WebUtility.HtmlEncode(CustomPages.RowsAddress(instance, view.Name))}\" data-params=\"{WebUtility.HtmlEncode(CustomPages.DeclarationJson(view))}\" title=\"{WebUtility.HtmlEncode(title)}\" sandbox=\"allow-scripts allow-forms allow-popups\" src=\"{WebUtility.HtmlEncode(bundle)}\"></iframe>";
        return Respond(Document(title, header, frame), embedOrigins);
    }

    /// <summary>One app on a portal's home: where it opens, and what to call it.</summary>
    public sealed record Tile(string Title, string? Description, string Url);

    /// <summary>A portal's own page (plan 26, AP11, AP12): its chrome and a tile for each app mounted in it that this person may open.</summary>
    public static AppResponse PortalHome(string title, Header header, IReadOnlyList<Tile> tiles, IReadOnlyList<string>? embedOrigins = null, string? content = null) =>
        Respond(Document(title, header, (header.Portal is { } look ? SlotFrame(look, "home") : "") + (content ?? TilesHtml(tiles))), embedOrigins);

    /// <summary>The tiles of a home: a grid of titles and descriptions that open the apps, or a note that nothing is open to the person yet. Everything is encoded.</summary>
    public static string TilesHtml(IReadOnlyList<Tile> tiles) => tiles.Count == 0
        ? "<p class=\"note\">Nothing here is open to you yet.</p>"
        : "<ul class=\"tiles\" style=\"list-style:none;margin:0;padding:16px;display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:12px\">"
          + string.Concat(tiles.Select(t => $"<li style=\"border:1px solid #8884;border-radius:8px;padding:12px\"><a href=\"{WebUtility.HtmlEncode(t.Url)}\" style=\"color:inherit;font-weight:600;text-decoration:none\">{WebUtility.HtmlEncode(t.Title)}</a>"
              + (string.IsNullOrWhiteSpace(t.Description) ? "" : $"<p style=\"margin:6px 0 0;opacity:.75\">{WebUtility.HtmlEncode(t.Description)}</p>") + "</li>"))
          + "</ul>";

    private static AppResponse Respond(string html, IReadOnlyList<string>? embedOrigins) =>
        new(200, "text/html; charset=utf-8", new Dictionary<string, string>
        {
            // the shell's one script is the token broker; the app's own frame is the only frame; a portal's logo and favicon are files of its bundle
            ["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'; img-src 'self'; style-src 'unsafe-inline'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors " + (embedOrigins is { Count: > 0 } ? string.Join(' ', embedOrigins) : "'none'"),
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            ["Cache-Control"] = "no-store",
        }, Encoding.UTF8.GetBytes(html));

    private const string BaseStyle = "html,body{height:100%;margin:0}body{display:flex;flex-direction:column;font:14px system-ui,sans-serif}iframe{border:0;width:100%;flex:1;display:block}";

    private static string Document(string title, Header? header, string content)
    {
        if (header?.Portal is { } look) return PortalDocument(title, header, look, content);
        var bar = header is null ? "" : HeaderHtml(header) + NoticeHtml(header);
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>{{BaseStyle}}
            header{display:flex;gap:16px;align-items:center;padding:0 16px;min-height:44px;border-bottom:1px solid #8884;flex-wrap:wrap}header nav{display:flex;gap:12px;align-items:center;flex-wrap:wrap}header .who{margin-left:auto;opacity:.75}
            header a{color:inherit;text-decoration:none;padding:4px 0}header a[aria-current=page]{font-weight:600;border-bottom:2px solid currentColor}header .menu-title{opacity:.6}</style>
            </head><body>
            {{bar}}
            {{content}}
            <script src="{{ScriptPath}}"></script>
            </body></html>
            """;
    }

    private static string PortalDocument(string title, Header header, PortalLook look, string content)
    {
        var width = look.Width switch { PortalWidth.Narrow => "720px", PortalWidth.Medium => "960px", PortalWidth.Wide => "1280px", _ => "none" };
        var nav = NavHtml(look.Navigation, look.SideMenu);
        var head = new StringBuilder("<header class=\"portal\">");
        if (look.LogoUrl is not null) head.Append("<img class=\"logo\" alt=\"\" src=\"").Append(WebUtility.HtmlEncode(look.LogoUrl)).Append("\">");
        head.Append("<span class=\"portal-title\">").Append(WebUtility.HtmlEncode(look.Title)).Append("</span>");
        if (!look.SideMenu) head.Append(nav);
        head.Append(SlotFrame(look, "header"));
        if (look.ShowPerson) head.Append("<span class=\"who\">").Append(WebUtility.HtmlEncode(header.Person)).Append("</span>");
        head.Append("</header>");
        var footer = new StringBuilder();
        if (look.Footer)
        {
            footer.Append("<footer class=\"portal\">").Append(WebUtility.HtmlEncode(look.FooterText ?? "")).Append(SlotFrame(look, "footer"));
            if (look.SupportLink is { } support) footer.Append(" <a href=\"").Append(WebUtility.HtmlEncode(support)).Append("\" rel=\"noopener noreferrer\">Support</a>");
            footer.Append("</footer>");
        }
        var favicon = look.FaviconUrl is null ? "" : $"<link rel=\"icon\" href=\"{WebUtility.HtmlEncode(look.FaviconUrl)}\">";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>{{favicon}}
            <style>:root{--portal-width:{{width}}}{{look.ThemeCss}}
            {{BaseStyle}}
            body{background:var(--sui-surface,Canvas);color:var(--sui-text,CanvasText);font-family:var(--sui-font,system-ui,sans-serif)}
            header.portal{display:flex;gap:16px;align-items:center;padding:0 16px;min-height:48px;border-bottom:1px solid #8884;flex-wrap:wrap}header.portal .who{margin-left:auto;opacity:.75}.logo{height:28px}.portal-title{font-weight:600}
            ul{list-style:none;margin:0;padding:0}nav.portal ul{display:flex;gap:12px;align-items:center;flex-wrap:wrap}nav.portal.side ul{flex-direction:column;align-items:stretch;gap:4px}nav.portal.side{padding:12px 16px;border-right:1px solid #8884;min-width:160px}
            nav.portal a{color:var(--sui-accent,inherit);text-decoration:none;padding:4px 0}nav.portal a[aria-current=page]{font-weight:600;border-bottom:2px solid currentColor}nav.portal .group>span{opacity:.6}nav.portal .group ul{padding-left:12px}nav.portal [role=separator]{border-top:1px solid #8884;margin:4px 0}
            .row{display:flex;flex:1;min-height:0}main{flex:1;display:flex;flex-direction:column;max-width:var(--portal-width);margin:0 auto;width:100%;min-width:0}.note{padding:16px}
            iframe.slot{flex:none;width:auto}iframe.slot-header{height:40px;width:260px}iframe.slot-footer{height:36px;width:100%;display:block}iframe.slot-aside{width:280px;border-left:1px solid #8884}iframe.slot-home{height:320px;width:100%;flex:none}
            footer.portal{padding:8px 16px;border-top:1px solid #8884;opacity:.75}footer.portal a{color:inherit}</style>
            </head><body class="{{(look.SideMenu ? "side" : "top")}}">
            {{head}}{{NoticeHtml(header)}}
            <div class="row">{{(look.SideMenu ? nav : "")}}<main>
            {{content}}
            </main>{{SlotFrame(look, "aside")}}</div>
            {{footer}}
            <script src="{{ScriptPath}}"></script>
            </body></html>
            """;
    }

    /// <summary>A slot's app as a sandboxed frame of its own: the same sandbox, the same broker (its token is for its own instance only), the portal's theme. It cannot reach this page, the session or the app next to it.</summary>
    private static string SlotFrame(PortalLook look, string slot) =>
        look.Slots is { } slots && slots.TryGetValue(slot, out var app)
            ? $"<iframe class=\"slot slot-{slot}\" data-instance=\"{WebUtility.HtmlEncode(app.Instance)}\" data-theme=\"{WebUtility.HtmlEncode(look.ThemeJson)}\" title=\"{WebUtility.HtmlEncode(app.Title)}\" sandbox=\"allow-scripts allow-forms allow-popups\" src=\"{WebUtility.HtmlEncode(AppSite.BasePath + Uri.EscapeDataString(app.Instance) + "/" + AppSite.BundleFolder)}\"></iframe>"
            : "";

    private static string NavHtml(IReadOnlyList<NavItem> items, bool sideMenu)
    {
        if (items.Count == 0) return "";
        var html = new StringBuilder($"<nav class=\"portal{(sideMenu ? " side" : "")}\" aria-label=\"Menu\">");
        Append(html, items);
        return html.Append("</nav>").ToString();
    }

    private static void Append(StringBuilder html, IReadOnlyList<NavItem> items)
    {
        html.Append("<ul>");
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case NavKind.Divider:
                    html.Append("<li role=\"separator\"></li>");
                    break;
                case NavKind.Group:
                    html.Append("<li class=\"group\"><span>").Append(WebUtility.HtmlEncode(item.Label)).Append("</span>");
                    Append(html, item.Children);
                    html.Append("</li>");
                    break;
                default:
                    html.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(item.Href)).Append('"');
                    if (item.Icon is not null) html.Append(" data-icon=\"").Append(WebUtility.HtmlEncode(item.Icon)).Append('"');
                    if (item.Current) html.Append(" aria-current=\"page\"");
                    if (item.Kind == NavKind.Link) html.Append(" rel=\"noopener noreferrer\"");
                    html.Append('>').Append(WebUtility.HtmlEncode(item.Label)).Append("</a></li>");
                    break;
            }
        }
        html.Append("</ul>");
    }

    /// <summary>Why the page is not what was asked for (a trusted portal without an approval is shown as the default portal), said where the person reads it.</summary>
    private static string NoticeHtml(Header header) =>
        header.Notice is { Length: > 0 } notice ? $"<p class=\"notice\" role=\"status\" style=\"margin:0;padding:8px 16px;background:#fd03;border-bottom:1px solid #8884\">{WebUtility.HtmlEncode(notice)}</p>" : "";

    private static string HeaderHtml(Header header)
    {
        var html = new StringBuilder("<header>");
        foreach (var menu in header.Menus)
        {
            html.Append("<nav aria-label=\"").Append(WebUtility.HtmlEncode(menu.Title)).Append("\"><span class=\"menu-title\">").Append(WebUtility.HtmlEncode(menu.Title)).Append("</span>");
            foreach (var link in menu.Links)
            {
                html.Append("<a href=\"").Append(AppSite.BasePath).Append(Uri.EscapeDataString(link.Instance)).Append("/\"");
                if (link.Icon is not null) html.Append(" data-icon=\"").Append(WebUtility.HtmlEncode(link.Icon)).Append('"');
                if (link.Current) html.Append(" aria-current=\"page\"");
                html.Append('>').Append(WebUtility.HtmlEncode(link.Label)).Append("</a>");
            }
            html.Append("</nav>");
        }
        return html.Append("<span class=\"who\">").Append(WebUtility.HtmlEncode(header.Person)).Append("</span></header>").ToString();
    }
}
