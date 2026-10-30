using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Identity;

/// <summary>Where a host that serves the provider tells it who is signed in and where people sign in.</summary>
/// <param name="CurrentUserId">The home's user id of the signed-in person on this request (from the session cookie), or null.</param>
/// <param name="SignInPath">Where a person who is not signed in is sent; the original address goes in <c>returnUrl</c>.</param>
public sealed record OpenIdHostOptions(Func<HttpContext, string?> CurrentUserId, string SignInPath = "/sign-in");

public static class OpenIdEndpoints
{
    /// <summary>The provider's public endpoints: discovery, JWKS, authorize, token and userinfo.</summary>
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
                var t = provider.Exchange(new TokenRequest(f["grant_type"], f["code"], f["redirect_uri"], id, secret, f["code_verifier"]));
                return Results.Json(new { access_token = t.AccessToken, token_type = "Bearer", expires_in = t.ExpiresIn, id_token = t.IdToken, scope = t.Scope });
            }
            catch (OAuthException e) { return Results.Json(new { error = e.Error, error_description = e.Message }, statusCode: e.Status); }
        });

        app.MapGet("/identity/userinfo", (HttpContext context) =>
        {
            var bearer = AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var h) && h.Scheme == "Bearer" ? h.Parameter : null;
            return provider.UserInfo(bearer) is { } info ? Results.Json(info) : Results.Unauthorized();
        });
        return app;
    }
}
