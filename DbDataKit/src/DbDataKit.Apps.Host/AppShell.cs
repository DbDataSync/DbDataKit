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

    /// <summary>The shell's header: who is signed in and the menus of the sets the instance belongs to. Null where the app's manifest says <c>chrome: none</c>.</summary>
    public sealed record Header(string Person, IReadOnlyList<Menu> Menus);

    /// <summary>The longest route the shell takes from a frame or an address; a longer one is dropped.</summary>
    public const int MaximumRoute = 512;

    public const string ScriptPath = "/apps/-/shell.js";

    /// <summary>
    /// The token broker's side in the page (plan 26 section 3): a frame asks with <c>postMessage({dbdata: "app/1", type: "token.request", id})</c> and gets <c>{type: "token", id, token, expiresIn, audience}</c> or <c>{type: "error", id, message}</c>.
    /// Only the app's own frame is answered (the message's source is checked against it), and the request to the host carries a header a cross-site page cannot send.
    /// </summary>
    public const string Script = """
        (function () {
          var frame = document.getElementById("app-frame");
          var instance = frame.getAttribute("data-instance");
          var route = null;
          function valid(p) { return typeof p === "string" && p.length <= 512 && p.charAt(0) === "/" && p.charAt(1) !== "/" && !/[\u0000-\u001f\u007f]/.test(p); }
          function fromAddress() { var h = decodeURIComponent(location.hash.slice(1)); return valid(h) ? h : null; }
          function tell(path) { frame.contentWindow.postMessage({ dbdata: "app/1", type: "navigate", path: path }, "*"); }
          frame.addEventListener("load", function () { route = fromAddress(); if (route) tell(route); });
          window.addEventListener("popstate", function () { var p = fromAddress(); if (p && p !== route) { route = p; tell(p); } });
          window.addEventListener("message", function (event) {
            var m = event.data;
            if (event.source !== frame.contentWindow || !m || m.dbdata !== "app/1") return;
            if (m.type === "route") {
              if (valid(m.path) && m.path !== route) { route = m.path; history.pushState(null, "", "#" + encodeURI(m.path)); }
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

    public static AppResponse Page(string instance, string title, Header? header = null)
    {
        var bundle = AppSite.BasePath + Uri.EscapeDataString(instance) + "/" + AppSite.BundleFolder;
        var bar = header is null ? "" : HeaderHtml(header);
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>html,body{height:100%;margin:0}body{display:flex;flex-direction:column;font:14px system-ui,sans-serif}iframe{border:0;width:100%;flex:1;display:block}
            header{display:flex;gap:16px;align-items:center;padding:0 16px;min-height:44px;border-bottom:1px solid #8884;flex-wrap:wrap}header nav{display:flex;gap:12px;align-items:center;flex-wrap:wrap}header .who{margin-left:auto;opacity:.75}
            header a{color:inherit;text-decoration:none;padding:4px 0}header a[aria-current=page]{font-weight:600;border-bottom:2px solid currentColor}header .menu-title{opacity:.6}</style>
            </head><body>
            {{bar}}
            <iframe id="app-frame" data-instance="{{WebUtility.HtmlEncode(instance)}}" title="{{WebUtility.HtmlEncode(title)}}" sandbox="allow-scripts allow-forms allow-popups" src="{{WebUtility.HtmlEncode(bundle)}}"></iframe>
            <script src="{{ScriptPath}}"></script>
            </body></html>
            """;
        return new AppResponse(200, "text/html; charset=utf-8", new Dictionary<string, string>
        {
            // the shell's one script is the token broker; the app's own frame is the only frame
            ["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            ["Cache-Control"] = "no-store",
        }, Encoding.UTF8.GetBytes(html));
    }

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
