using DbDataKit.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Apps;

/// <summary>
/// The routes of the apps host (plan 26, AP05b): <c>/apps/&lt;instance&gt;/</c> is the shell page, for a person signed in through the identity home who holds a role on <c>app:&lt;instance&gt;</c>;
/// <c>/apps/&lt;instance&gt;/_app/...</c> is the app's bundle; <c>/apps/-/callback</c> is where the home sends the person back.
/// <para>
/// The bundle is not behind the sign-in: it is the sandboxed frame's document, a request from an opaque origin that carries no cookie by design. It is code the group published, not data; what it reads goes through
/// the store with a token the shell hands it (AP05c) and the person's roles.
/// </para>
/// </summary>
public static class AppsEndpoints
{
    public const string CallbackPath = "/apps/-/callback";
    public const string SessionCookie = "dbdata_apps";

    public static IEndpointRouteBuilder MapApps(this IEndpointRouteBuilder app, AppInstanceStore instances, AppSite site, OpenIdClient client, AppSessions sessions, AppTokenBroker broker)
    {
        app.MapGet("/apps/{instance}/_app/{**path}", async (HttpContext context, string instance, string? path) => await Write(context, site.Get(instance, path ?? "")));

        app.MapGet(AppShell.ScriptPath, async (HttpContext context) => await Write(context, AppShell.ScriptResponse()));

        // The shell page asks for a token for its frame. A cross-site page cannot send the header (it would need a CORS preflight this host never answers), and the cookie is not sent from another site anyway.
        app.MapPost("/apps/{instance}/_token", async (HttpContext context, string instance) =>
        {
            if (context.Request.Headers["X-DbData-Shell"] != "1") { await Text(context, 400, "Not a request from the shell page."); return; }
            if (instances.Resolve(instance) is null) { await Text(context, 404, "No such app."); return; }
            if (sessions.Find(context.Request.Cookies[SessionCookie]) is not { } session) { await Text(context, 401, "Sign in first."); return; }
            if (session.RolesOn(instance).Count == 0) { await Text(context, 403, "The identity home gives you no role on this app."); return; }
            var token = broker.Issue(session, instance);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { token = token.Token, expiresIn = token.ExpiresInSeconds, audience = token.Audience });
        });

        app.MapGet(CallbackPath, async (HttpContext context) =>
        {
            var q = context.Request.Query;
            try
            {
                var result = await client.CompleteAsync(q["state"], q["code"], q["error"], context.RequestAborted);
                var session = sessions.Create(result.Identity);
                context.Response.Cookies.Append(SessionCookie, session.Id, new CookieOptions
                {
                    HttpOnly = true, Secure = true, Path = "/apps/", Expires = session.ExpiresAtUtc,
                    // Lax: the browser arrives from the identity home, and a Strict cookie set on that navigation is withheld from the page it redirects to
                    SameSite = SameSiteMode.Lax,
                });
                context.Response.Redirect(SafeReturn(result.ReturnUrl));
            }
            catch (OpenIdClientException e)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(e.Message);
            }
        });

        app.MapGet("/apps/{instance}/{**path}", async (HttpContext context, string instance, string? path) =>
        {
            if (!string.IsNullOrEmpty(path)) { await Text(context, 404, "No such page."); return; }
            if (!context.Request.Path.Value!.EndsWith('/')) { context.Response.Redirect($"/apps/{Uri.EscapeDataString(instance)}/"); return; }
            var resolved = instances.Resolve(instance);
            if (resolved is null) { await Text(context, 404, "No such app."); return; }
            if (sessions.Find(context.Request.Cookies[SessionCookie]) is not { } session)
            {
                context.Response.Redirect((await client.BeginAsync($"/apps/{Uri.EscapeDataString(instance)}/", context.RequestAborted)).AbsoluteUri);
                return;
            }
            if (session.RolesOn(instance).Count == 0)
            {
                await Text(context, 403, "You are signed in, but the identity home gives you no role on this app.");
                return;
            }
            await Write(context, AppShell.Page(instance, TitleOf(resolved)));
        });
        return app;
    }

    private static string TitleOf(ResolvedInstance resolved) => resolved.Instance.Title ?? resolved.Manifest.Title ?? resolved.Instance.Name;

    /// <summary>Only a path of this host's apps may be returned to; anything else would make the callback an open redirect.</summary>
    private static string SafeReturn(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl.StartsWith("/apps/", StringComparison.Ordinal) && !returnUrl.Contains('\\') && !returnUrl.StartsWith("//") ? returnUrl : "/";

    private static async Task Write(HttpContext context, AppResponse response)
    {
        foreach (var (name, value) in response.Headers) context.Response.Headers[name] = value;
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        await context.Response.Body.WriteAsync(response.Body);
    }

    private static async Task Text(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync(message);
    }
}
