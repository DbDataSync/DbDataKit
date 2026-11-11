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
    public const string CatalogPath = "/apps/catalog";
    public const string CallbackPath = "/apps/-/callback";
    public const string SessionCookie = "dbdata_apps";

    public static IEndpointRouteBuilder MapApps(this IEndpointRouteBuilder app, AppInstanceStore instances, AppSite site, OpenIdClient client, AppSessions sessions, AppTokenBroker broker, AppDataGateway? data = null, AppStorage? storage = null, AppApprovalStore? approvals = null, Func<long>? storageCapBytes = null)
    {
        app.MapGet("/apps/{instance}/_app/{**path}", async (HttpContext context, string instance, string? path) =>
        {
            // The code that would run on the host's origin when opened by its address (a trusted portal's, or an app of origin `own`) is not served without an approval for these exact bytes. A sandboxed document is served as before:
            // its policy then carries nothing the administrator did not approve, and it has no origin to abuse.
            if (instances.Resolve(instance) is { Manifest: { Trust: AppTrust.Trusted } and ({ Kind: AppKind.Portal } or { Origin: AppOrigin.Own }) } resolved && Unapproved(approvals, resolved) is { } why) { await Text(context, 403, why); return; }
            await Write(context, site.Get(instance, path ?? ""));
        });

        // what the app can reach and who approved it; for anyone who may open the app
        app.MapGet("/apps/{instance}/_about", async (HttpContext context, string instance) =>
        {
            if (instances.Resolve(instance) is not { } resolved) { await Text(context, 404, "No such app."); return; }
            if (await sessions.FindAsync(context.Request.Cookies[SessionCookie], context.RequestAborted) is not { } session)
            {
                context.Response.Redirect((await client.BeginAsync(context.Request.Path.Value!, context.RequestAborted, AppAddressing.CallbackFor(context))).AbsoluteUri);
                return;
            }
            if (session.RolesOn(instance).Count == 0) { await Text(context, 403, "The identity home gives you no role on this app."); return; }
            await Write(context, AppAbout.Page(resolved, approvals?.ForApp(resolved.Manifest.Name) ?? [], TitleOf(resolved)));
        });

        app.MapGet(AppShell.ScriptPath, async (HttpContext context) => await Write(context, AppShell.ScriptResponse()));

        // The shell page asks for a token for its frame. A cross-site page cannot send the header (it would need a CORS preflight this host never answers), and the cookie is not sent from another site anyway.
        app.MapPost("/apps/{instance}/_token", async (HttpContext context, string instance) =>
        {
            if (context.Request.Headers["X-DbData-Shell"] != "1") { await Text(context, 400, "Not a request from the shell page."); return; }
            if (instances.Resolve(instance) is not { } resolved) { await Text(context, 404, "No such app."); return; }
            if (await sessions.FindAsync(context.Request.Cookies[SessionCookie], context.RequestAborted) is not { } session) { await Text(context, 401, "Sign in first."); return; }
            if (session.RolesOn(instance).Count == 0) { await Text(context, 403, "The identity home gives you no role on this app."); return; }
            if (Unapproved(approvals, resolved) is { } why) { await Text(context, 403, why); return; }
            var token = broker.Issue(session, instance);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { token = token.Token, expiresIn = token.ExpiresInSeconds, audience = token.Audience });
        });

        if (storage is not null)
        {
            // The shell page relays a sandboxed app's storage calls (it has no browser storage of its own); same guard as the token request.
            app.MapPost("/apps/{instance}/_storage", async (HttpContext context, string instance) =>
            {
                if (context.Request.Headers["X-DbData-Shell"] != "1") { await Text(context, 400, "Not a request from the shell page."); return; }
                if (instances.Resolve(instance) is not { } resolved) { await Text(context, 404, "No such app."); return; }
                if (await sessions.FindAsync(context.Request.Cookies[SessionCookie], context.RequestAborted) is not { } session) { await Text(context, 401, "Sign in first."); return; }
                if (session.RolesOn(instance).Count == 0) { await Text(context, 403, "The identity home gives you no role on this app."); return; }
                if (Unapproved(approvals, resolved) is { } why) { await Text(context, 403, why); return; }
                await AppStorageEndpoint.HandleAsync(context, storage, resolved, session, storageCapBytes?.Invoke());
            });
        }

        if (data is not null)
        {
            app.MapMethods("/apps/{instance}/_data/{**path}", ["GET", "POST", "OPTIONS"], async (HttpContext context, string instance, string? path) =>
            {
                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    await Write(context, new AppResponse(204, "text/plain", AppDataGateway.Cors, []));
                    return;
                }
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                await Write(context, await data.ForwardAsync(instance, path ?? "", context.Request.Method, context.Request.QueryString.Value ?? "", context.Request.Headers.Authorization, body.ToArray(), context.RequestAborted));
            });
        }

        // The instances the signed-in person may open, for a portal that builds its tiles from a feed (plan 26 section 5). The session cookie decides who is asking; there is no list for the anonymous.
        app.MapGet(CatalogPath, async (HttpContext context) =>
        {
            if (await sessions.FindAsync(context.Request.Cookies[SessionCookie], context.RequestAborted) is not { } session) { await Text(context, 401, "Sign in first."); return; }
            var entries = instances.List().Where(i => session.RolesOn(i.Name).Count > 0)
                .Select(i => instances.Resolve(i.Name)).OfType<ResolvedInstance>()
                .Select(r => new { name = r.Instance.Name, title = TitleOf(r), description = r.Manifest.Description, app = r.Instance.App, version = r.AppVersion.Version, channel = DbDataKit.State.DefinitionStore.Production, launch = LaunchUrl(r.Instance.Name) });
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { instances = entries });
        });

        app.MapGet(CallbackPath, async (HttpContext context) =>
        {
            var q = context.Request.Query;
            try
            {
                var result = await client.CompleteAsync(q["state"], q["code"], q["error"], context.RequestAborted);
                var session = sessions.Create(result.Identity, result.Grant.RefreshToken);
                context.Response.Cookies.Append(SessionCookie, session.Id, new CookieOptions
                {
                    HttpOnly = true, Secure = true, Path = AppAddressing.Of(context)?.Binding is null ? "/apps/" : "/", Expires = session.ExpiresAtUtc,
                    // None: a portal on another site frames an instance (AP07), and the frame must carry the person's session. Nothing the cookie authorises changes state without the shell's own header (a cross-site page cannot send it: that
                    // needs a CORS preflight this host never answers), and who may frame the page is `frame-ancestors`. (Lax would do for the sign-in alone: the browser arrives from the home, and Strict is withheld on that navigation.)
                    SameSite = SameSiteMode.None,
                });
                context.Response.Redirect(SafeReturn(result.ReturnUrl, AppAddressing.Of(context)?.Binding is not null));
            }
            catch (OpenIdClientException e)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(e.Message);
            }
        });

        app.MapGet("/apps/{instance}/{**path}", async (HttpContext context, string instance, string? path) =>
        {
            var resolved = instances.Resolve(instance);
            // a portal has pages under it (its mounts); an app has none
            var portal = resolved?.Manifest.Kind == AppKind.Portal ? resolved : null;
            var mount = portal is null || string.IsNullOrEmpty(path) ? null : PortalChrome.MountAt(portal.Manifest.Portal!, path);
            if (!string.IsNullOrEmpty(path) && mount is null) { await Text(context, 404, "No such page."); return; }
            if (!context.Request.Path.Value!.EndsWith('/')) { context.Response.Redirect(AppAddressing.PublicPath(context) + "/" + context.Request.QueryString); return; }
            if (resolved is null) { await Text(context, 404, "No such app."); return; }
            // `acme~preview`: the instance as another channel has it (plan 21); a portal has no preview, an app's is read only
            var previewing = AppInstanceName.Split(instance).Channel != DbDataKit.State.DefinitionStore.Production;
            if (previewing && portal is not null) { await Text(context, 404, "A portal has no preview."); return; }
            if (await sessions.FindAsync(context.Request.Cookies[SessionCookie], context.RequestAborted) is not { } session)
            {
                // at a bound name the person returns to the name's own address, not to the instance's subpath
                var bound = AppAddressing.Of(context)?.Binding is not null;
                var back = bound ? AppAddressing.PublicPath(context) + "?launch" : portal is null ? LaunchUrl(instance) : context.Request.Path.Value!;
                context.Response.Redirect((await client.BeginAsync(back, context.RequestAborted, AppAddressing.CallbackFor(context))).AbsoluteUri);
                return;
            }

            // A portal with code of its own runs on this host's origin, so only with an approval for these exact bytes (AP16): then its page is its own document, which holds the session, asks for tokens and frames what it mounts
            // (its calls carry its approval: the token broker and the data gateway). Without one the person gets the default portal (the suite's own shell) and the reason, never the code.
            string? notice = previewing ? $"Preview of {AppInstanceName.Split(instance).Name} ({AppInstanceName.Split(instance).Channel} channel, version {resolved.AppVersion.Version}). It reads the instance's data and keeps no change; its storage is its own." : null;
            if (portal is { Manifest: { Trust: AppTrust.Trusted, Entry: not null } })
            {
                if (Unapproved(approvals, portal) is not { } notRun) { await Write(context, site.PortalPage(instance)); return; }
                notice = $"{notRun} Showing the default portal instead.";
            }

            // a portal's home is for anyone signed in; what it offers is filtered by their roles. An app, or a mount, needs a role on the app.
            var shown = mount is null ? resolved : instances.Resolve(mount.App);
            if (shown is null) { await Text(context, 404, "No such app."); return; }
            if (portal is null || mount is not null)
            {
                if (session.RolesOn(shown.Instance.Name).Count == 0) { await Text(context, 403, "You are signed in, but the identity home gives you no role on this app."); return; }
            }

            // (the unapproved code portal itself is the default portal's home, not something to refuse)
            if ((notice is null || shown != resolved) && Unapproved(approvals, shown) is { } unapproved) { await Text(context, 403, unapproved); return; }

            // ?embed serves the app without the header, for a page that frames it; only the origins the instance names may do that
            var embed = context.Request.Query.ContainsKey("embed");
            var origins = AppInstanceStore.EmbedOrigins(shown.Instance.Settings.GetValueOrDefault(AppInstanceStore.EmbedSetting), out _) ?? [];

            // the portal that frames this page: the one being visited, or the one the instance's `portal` setting names
            var framing = notice is not null ? null : portal ?? (shown.Instance.Settings.GetValueOrDefault(PortalChrome.PortalSetting) is { } named && instances.Resolve(named) is { Manifest.Kind: AppKind.Portal } named2 ? named2 : null);
            AppShell.Header? header = shown.Manifest.Chrome == AppChrome.None || embed ? null
                : framing is not null ? new AppShell.Header(PersonOf(session), [], PortalChrome.Look(instances, framing, session, mount?.App))
                : HeaderOf(instances, session, mount?.App ?? instance);
            if (notice is not null) header = (header ?? new AppShell.Header(PersonOf(session), [])) with { Notice = notice };
            if (portal is not null && mount is null)
                await Write(context, AppShell.PortalHome(TitleOf(resolved), header ?? new AppShell.Header(PersonOf(session), [], PortalChrome.Look(instances, portal, session, null)), PortalChrome.Tiles(instances, portal, session), origins, notice is null ? HomeViewHtml.Render(instances, portal, session) : null));
            else
                await Write(context, AppShell.Page(mount is null ? instance : shown.Instance.Name, TitleOf(shown), header, origins));
        });
        return app;
    }

    /// <summary>The person and the menus of the sets the instance belongs to, each listing only what this person may open (their roles at the home decide), so a menu never advertises a link that ends in a refusal.</summary>
    private static string PersonOf(AppSession session) => session.Identity.Name ?? session.Identity.Email ?? session.Identity.UserId;

    /// <summary>The stable address that signs a person in and opens the instance.</summary>
    public static string LaunchUrl(string instance) => $"/apps/{Uri.EscapeDataString(instance)}/?launch";

    public static AppShell.Header HeaderOf(AppInstanceStore instances, AppSession session, string instance)
    {
        var menus = new List<AppShell.Menu>();
        foreach (var set in instances.NavigationSets().Where(s => s.Entries.Any(e => e.Instance == instance)))
        {
            var links = new List<AppShell.MenuLink>();
            foreach (var entry in set.Entries)
            {
                var current = entry.Instance == instance;
                if (!current && session.RolesOn(entry.Instance).Count == 0) continue;
                if (instances.Resolve(entry.Instance) is not { } member) continue;
                links.Add(new AppShell.MenuLink(entry.Instance, entry.Label ?? TitleOf(member), entry.Icon, current));
            }
            menus.Add(new AppShell.Menu(set.Title, links));
        }
        return new AppShell.Header(PersonOf(session), menus);
    }

    /// <summary>Why a trusted app may not run (it asked to be trusted and no approval stands for this version), or null. A sandboxed app always runs. Without an approval store nothing is trusted.</summary>
    private static string? Unapproved(AppApprovalStore? approvals, ResolvedInstance resolved) =>
        resolved.Manifest.Trust == AppTrust.Sandbox ? null
            : approvals is null ? $"'{resolved.Manifest.Name}' asks to be trusted and this host keeps no approvals."
            : approvals.Gate(resolved) is { Allowed: false } gate ? gate.Refusal : null;

    private static string TitleOf(ResolvedInstance resolved) => resolved.Instance.Title ?? resolved.Manifest.Title ?? resolved.Instance.Name;

    /// <summary>Only a path of this host's apps may be returned to; anything else would make the callback an open redirect.</summary>
    private static string SafeReturn(string? returnUrl, bool atBoundName = false) =>
        returnUrl is { Length: > 0 } && (returnUrl.StartsWith("/apps/", StringComparison.Ordinal) || (atBoundName && returnUrl.StartsWith('/'))) && !returnUrl.Contains('\\') && !returnUrl.StartsWith("//") ? returnUrl : "/";

    private static async Task Write(HttpContext context, AppResponse response)
    {
        foreach (var (name, value) in response.Headers) context.Response.Headers[name] = value;
        context.Response.StatusCode = response.Status;
        // a 204 has no content, and no content type to describe it
        if (response.Status is 204 or 304) return;
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
