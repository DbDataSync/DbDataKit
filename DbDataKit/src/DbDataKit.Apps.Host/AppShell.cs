using System.Net;
using System.Text;

namespace DbDataKit.Apps;

/// <summary>
/// The page at an instance's own address (plan 26, AP05b): the host's, not the app's. It is signed-in territory and runs nothing of the app; the app's document is a frame under it with the <c>sandbox</c> attribute, which
/// gives it an opaque origin, so it cannot read this page, the host's cookies or its storage (a document with a <c>sandbox</c> Content-Security-Policy gets the same from the other side).
/// </summary>
public static class AppShell
{
    public static AppResponse Page(string instance, string title)
    {
        var bundle = AppSite.BasePath + Uri.EscapeDataString(instance) + "/" + AppSite.BundleFolder;
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>html,body{height:100%;margin:0}iframe{border:0;width:100%;height:100%;display:block}</style>
            </head><body>
            <iframe id="app-frame" title="{{WebUtility.HtmlEncode(title)}}" sandbox="allow-scripts allow-forms allow-popups" src="{{WebUtility.HtmlEncode(bundle)}}"></iframe>
            </body></html>
            """;
        return new AppResponse(200, "text/html; charset=utf-8", new Dictionary<string, string>
        {
            // only the app's own frame loads; the shell runs no script until the token broker (AP05c)
            ["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            ["Cache-Control"] = "no-store",
        }, Encoding.UTF8.GetBytes(html));
    }
}
