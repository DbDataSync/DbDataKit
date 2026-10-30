using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataConsole.Server;

/// <summary>
/// Sign-in through an identity home (plan 13a U14): the person is sent to the home, comes back with a code, and gets this console's ordinary session as the user the home names. The console keeps no password or passkey
/// for them, so the same person is the same user id here as in every other console, and in the audit of every server they reach.
/// </summary>
public static class ConsoleFederation
{
    public const string SignInPath = "/api/identity/sign-in", CallbackPath = "/api/identity/callback";

    public static IEndpointRouteBuilder MapFederation(this IEndpointRouteBuilder app)
    {
        app.MapGet(SignInPath, async (HttpContext context, OpenIdClient client) =>
        {
            // Only a path of this console may be returned to: anything else would make this an open redirect.
            var returnUrl = context.Request.Query["returnUrl"].FirstOrDefault();
            if (returnUrl is not { Length: > 0 } || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.Contains('\\')) returnUrl = "/";
            try { return Results.Redirect((await client.BeginAsync(returnUrl, context.RequestAborted)).AbsoluteUri); }
            catch (OpenIdClientException e) { return Results.Text(e.Message, "text/plain", statusCode: StatusCodes.Status502BadGateway); }
        }).AllowAnonymous();

        app.MapGet(CallbackPath, async (HttpContext context, OpenIdClient client, FederatedUsers federated, SessionStore sessions, AuthOptions options) =>
        {
            var q = context.Request.Query;
            try
            {
                var (identity, returnUrl) = await client.CompleteAsync(q["state"], q["code"], q["error"], context.RequestAborted);
                if (federated.SignIn(identity) is not { Enabled: true } user)
                    return Results.Text("You are signed in to the identity home, but it gives you no access to this console.", "text/plain", statusCode: StatusCodes.Status403Forbidden);
                var session = sessions.Create(user.Id);
                context.Response.Cookies.Append(options.CookieName, session.Id, new CookieOptions
                {
                    HttpOnly = true,
                    // Lax, not Strict: the browser arrives here from the identity home, and a Strict cookie set on that navigation is withheld from the page it redirects to.
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Expires = session.ExpiresAtUtc,
                    Path = "/",
                });
                return Results.Redirect(returnUrl ?? "/");
            }
            catch (OpenIdClientException e) { return Results.Text(e.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest); }
            // the person's verified address is another account's: nobody is signed in and nothing is merged
            catch (AccountConflictException e) { return Results.Text(e.Message, "text/plain", statusCode: StatusCodes.Status409Conflict); }
        }).AllowAnonymous();
        return app;
    }
}
