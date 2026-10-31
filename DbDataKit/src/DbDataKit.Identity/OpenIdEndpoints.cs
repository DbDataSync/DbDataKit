using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Identity;

/// <summary>Where a host that serves the provider tells it who is signed in and where people sign in.</summary>
/// <param name="CurrentUserId">The home's user id of the signed-in person on this request (from the session cookie), or null.</param>
/// <param name="SignInPath">Where a person who is not signed in is sent; the original address goes in <c>returnUrl</c>.</param>
/// <param name="EndSession">Ends the host's own session for the person on this request (clears the cookie, deletes the session row), called by the end-session endpoint. The home's session is the host's: without this the endpoint ends only the refresh tokens.</param>
public sealed record OpenIdHostOptions(Func<HttpContext, string?> CurrentUserId, string SignInPath = "/sign-in", Func<HttpContext, Task>? EndSession = null);

public static class OpenIdEndpoints
{
    /// <summary>The provider's public endpoints: discovery, JWKS, authorize, token, userinfo and end-session.</summary>
    public static IEndpointRouteBuilder MapOpenIdProvider(this IEndpointRouteBuilder app, OpenIdProvider provider, SigningKeys keys, OpenIdHostOptions host)
    {
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(provider.Discovery()));
        app.MapGet("/identity/jwks", () => Results.Json(keys.Jwks()));

        app.MapGet("/identity/authorize", (HttpContext context) =>
        {
            var q = context.Request.Query;
            var userId = host.CurrentUserId(context);
            if (userId is null)
                return Results.Redirect($"{host.SignInPath}?returnUrl={Uri.EscapeDataString(context.Request.Path + context.Request.QueryString)}");
            var result = provider.Authorize(new AuthorizeRequest(q["client_id"], q["redirect_uri"], q["response_type"], q["scope"], q["state"], q["nonce"], q["code_challenge"], q["code_challenge_method"]), userId);
            return result.RedirectTo is { } to ? Results.Redirect(to) : Results.Text(result.Error!, "text/plain", statusCode: 400);
        });

        app.MapPost("/identity/token", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.HasFormContentType) return Results.Json(new { error = "invalid_request", error_description = "The request is a form." }, statusCode: 400);
            var f = context.Request.Form;
            string? id = f["client_id"], secret = f["client_secret"];
            // client_secret_basic as well as client_secret_post.
            if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var basic) && basic.Scheme == "Basic" && basic.Parameter is not null)
            {
                var pair = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(basic.Parameter)).Split(':', 2);
                if (pair.Length == 2) { id = Uri.UnescapeDataString(pair[0]); secret = Uri.UnescapeDataString(pair[1]); }
            }
            try
            {
                var t = provider.Exchange(new TokenRequest(f["grant_type"], f["code"], f["redirect_uri"], id, secret, f["code_verifier"], f["refresh_token"]));
                return Results.Json(new { access_token = t.AccessToken, token_type = "Bearer", expires_in = t.ExpiresIn, id_token = t.IdToken, scope = t.Scope, refresh_token = t.RefreshToken });
            }
            catch (OAuthException e) { return Results.Json(new { error = e.Error, error_description = e.Message }, statusCode: e.Status); }
        });

        app.MapGet("/identity/userinfo", (HttpContext context) =>
        {
            var bearer = AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var h) && h.Scheme == "Bearer" ? h.Parameter : null;
            return provider.UserInfo(bearer) is { } info ? Results.Json(info) : Results.Unauthorized();
        });

        // RP-Initiated Logout 1.0: GET with the parameters in the query, or POST with a form.
        async Task<IResult> EndSession(HttpContext context)
        {
            var q = context.Request.HasFormContentType && HttpMethods.IsPost(context.Request.Method) ? (IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>)context.Request.Form : context.Request.Query;
            string? Get(string key) => q.FirstOrDefault(p => p.Key == key).Value.FirstOrDefault();
            var result = provider.EndSession(new EndSessionRequest(Get("id_token_hint"), Get("client_id"), Get("post_logout_redirect_uri"), Get("state")));
            if (host.CurrentUserId(context) is { } current) provider.RevokeUser(current);
            if (host.EndSession is { } end) await end(context);
            return result.RedirectTo is { } to ? Results.Redirect(to) : Results.Text("You are signed out.", "text/plain");
        }
        app.MapGet("/identity/end-session", (Delegate)EndSession);
        app.MapPost("/identity/end-session", (Delegate)EndSession);
        return app;
    }
}
