using System.Net;
using System.Text;

namespace DbDataKit.Apps;

/// <summary>
/// The page at an instance's own address (plan 26, AP05b): the host's, not the app's. It is signed-in territory and runs nothing of the app (its one script is the token broker); the app's document is a frame under it with the <c>sandbox</c> attribute, which
/// gives it an opaque origin, so it cannot read this page, the host's cookies or its storage (a document with a <c>sandbox</c> Content-Security-Policy gets the same from the other side).
/// </summary>
public static class AppShell
{
    public const string ScriptPath = "/apps/-/shell.js";

    /// <summary>
    /// The token broker's side in the page (plan 26 section 3): a frame asks with <c>postMessage({dbdata: "app/1", type: "token.request", id})</c> and gets <c>{type: "token", id, token, expiresIn, audience}</c> or <c>{type: "error", id, message}</c>.
    /// Only the app's own frame is answered (the message's source is checked against it), and the request to the host carries a header a cross-site page cannot send.
    /// </summary>
    public const string Script = """
        (function () {
          var frame = document.getElementById("app-frame");
          var instance = frame.getAttribute("data-instance");
          window.addEventListener("message", function (event) {
            var m = event.data;
            if (event.source !== frame.contentWindow || !m || m.dbdata !== "app/1" || m.type !== "token.request" || typeof m.id !== "string") return;
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

    public static AppResponse Page(string instance, string title)
    {
        var bundle = AppSite.BasePath + Uri.EscapeDataString(instance) + "/" + AppSite.BundleFolder;
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>html,body{height:100%;margin:0}iframe{border:0;width:100%;height:100%;display:block}</style>
            </head><body>
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
}
