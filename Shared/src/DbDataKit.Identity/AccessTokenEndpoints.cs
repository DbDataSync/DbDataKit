using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Identity;

/// <summary>What a person asks for to make a token: its name and, optionally, how many days it lasts (the home's default when absent).</summary>
public sealed record AccessTokenRequest(string? Name, int? Days);

/// <summary>
/// A person's own personal access tokens at the home (plan 13a U20; plan 16, "Sign-in decisions" item 6): list, make (the secret answered once) and revoke, signed in at the home. A request signed in with a token cannot make
/// another one (a leaked token would otherwise outlive its own expiry), and nobody sees or revokes anyone else's here: that is the administrators', at the command line and the admin API.
/// </summary>
public static class AccessTokenEndpoints
{
    public const string Path = "/api/access-tokens";

    internal static void Map(WebApplication app)
    {
        var group = app.MapGroup(Path).RequireAuthorization(Policies.Viewer);
        var currentId = (HttpContext c) => c.User.FindFirst(c.RequestServices.GetRequiredService<AuthOptions>().Product.UserIdClaim)?.Value;

        group.MapGet("", (HttpContext c, AccessTokens tokens) =>
            currentId(c) is { } me ? Results.Ok(tokens.List(me).Select(View)) : Results.Unauthorized());

        group.MapPost("", (HttpContext c, AccessTokens tokens, AccessTokenRequest request) =>
        {
            if (currentId(c) is not { } me) return Results.Unauthorized();
            if (c.User.HasClaim(PasswordCredentials.MethodClaim, PasswordCredentials.Method))
                return Results.Json(new { error = "A token cannot be made with a token. Sign in at the identity home to make one." }, statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var made = tokens.Create(me, request.Name ?? "", createdBy: me, lifetime: request.Days is { } d ? TimeSpan.FromDays(d) : null);
                return Results.Ok(new { token = made.Secret, record = View(made.Record) });
            }
            catch (AccessTokenException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapDelete("{id}", (HttpContext c, AccessTokens tokens, string id) =>
            currentId(c) is not { } me ? Results.Unauthorized()
            : tokens.Revoke(id, ownerId: me) ? Results.NoContent()
            : Results.NotFound(new { error = "You have no token with that id that is not already revoked." }));
    }

    internal static object View(AccessTokenRecord t) => new
    {
        id = t.Id, name = t.Name, createdAtUtc = t.CreatedAtUtc, expiresAtUtc = t.ExpiresAtUtc, revokedAtUtc = t.RevokedAtUtc, lastUsedAtUtc = t.LastUsedAtUtc,
    };
}
