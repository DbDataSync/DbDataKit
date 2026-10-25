using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Web.Trust;

/// <summary>
/// Puts the token check in front of a server's management API (plan 13, W12): the caller's signed token, in the <see cref="TokenHeader"/> header, is validated by <see cref="ConsoleTokenValidator"/> for every request
/// under the protected prefix, and the result is kept on the request for the endpoint, which decides what the caller may do (<see cref="ManagementAuthorization"/>). Pairing and the version header are outside the prefix,
/// because a client that is not paired yet has no token.
/// <para>
/// A refused request gets a 401 with no body: the caller is told it was refused, never why (the reason, <see cref="TokenResult.Detail"/>, goes to the server's log through <c>refused</c>).
/// </para>
/// </summary>
public static class ManagementAuthentication
{
    /// <summary>The header the token travels in; a header rather than a cookie, so it works through reverse proxies and cannot be sent by a web page on another site.</summary>
    public const string TokenHeader = "X-DbData-Token";

    /// <summary>The path every authenticated management call lives under.</summary>
    public const string ApiPrefix = "/api/";

    private const string ItemKey = "dbdata.management.token";

    public static IApplicationBuilder UseManagementAuthentication(this IApplicationBuilder app, ConsoleTokenValidator validator, Action<TokenResult>? refused = null, string prefix = ApiPrefix) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(prefix.TrimEnd('/'), StringComparison.Ordinal) && !context.Request.Path.Value!.StartsWith(prefix, StringComparison.Ordinal))
            {
                await next();
                return;
            }

            var result = validator.Validate(context.Request.Headers[TokenHeader].FirstOrDefault());
            if (!result.Ok)
            {
                refused?.Invoke(result);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Items[ItemKey] = result.Token;
            await next();
        });

    /// <summary>The caller's validated token. Only for a request under the protected prefix, where the middleware has run; throws otherwise, because an endpoint outside it has no caller to ask about.</summary>
    public static ValidatedToken ManagementToken(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var token) && token is ValidatedToken validated
            ? validated
            : throw new InvalidOperationException("This request was not authenticated: map the endpoint under the management API prefix and call UseManagementAuthentication.");
}
