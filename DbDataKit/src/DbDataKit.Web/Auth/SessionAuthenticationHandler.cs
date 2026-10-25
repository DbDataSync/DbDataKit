using System.Security.Claims;
using System.Text.Encodings.Web;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DbDataKit.Web.Auth;

/// <summary>
/// Turns the session cookie into a principal.
/// <para>
/// A cookie rather than a JWT: there is no second service to present a token to, and a cookie backed
/// by a row is revocable — signing somebody out or disabling them takes effect on their next request
/// rather than whenever a token would have expired. The row is re-read every request for exactly that
/// reason.
/// </para>
/// <para>
/// This is the *only* authentication scheme the app uses. Windows negotiation happens on one
/// endpoint, which mints a session; every other request — including SignalR's — is a session cookie.
/// That keeps one answer to "who is this" rather than one per method.
/// </para>
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionStore sessions,
    UserStore users,
    AuthOptions authOptions,
    ConsoleTokenValidator? tokens = null)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A console's call carries a signed token naming the person (plan 13, U08), and no cookie: the server that is managed from a console has no users of its own, and the roles are the token's.
        if (tokens is not null && Request.Headers[ManagementAuthentication.TokenHeader].FirstOrDefault() is { Length: > 0 } token)
            return Task.FromResult(FromToken(tokens.Validate(token)));

        var sessionId = Request.Cookies[authOptions.CookieName];
        if (string.IsNullOrEmpty(sessionId))
            return Task.FromResult(NetworkFallback());

        // SignalR cannot set headers on a WebSocket handshake, but it does send cookies — which is
        // the other reason this is a cookie scheme rather than a bearer one.
        var user = sessions.Resolve(sessionId, users);
        if (user is null)
            return Task.FromResult(AuthenticateResult.Fail("The session is not valid."));

        return Task.FromResult(AuthenticateResult.Success(Ticket(user)));
    }

    /// <summary>
    /// No session cookie at all — phase 164's replacement for the old blanket <c>Auth:Disabled</c>: a
    /// role-scoped, network-trust fallback rather than one switch that granted Admin to every request
    /// from anywhere. Admin is checked first (the same "both checked, admin wins" precedent
    /// <see cref="WindowsSignIn.Resolve"/> already uses), then Viewer; neither configured means this
    /// falls through to <see cref="AuthenticateResult.NoResult"/>, same as today, requiring a real
    /// sign-in.
    /// </summary>
    private AuthenticateResult NetworkFallback()
    {
        var remote = Context.Connection.RemoteIpAddress;
        // A null remote address is a connection this process cannot attribute — an in-memory test
        // server, or a transport that does not report one — treated as loopback, the same convention
        // RunnerStateGuard's own loopback check already uses and for the same reason: the alternative
        // is that nothing reaches it at all.
        var isLoopback = remote is null || System.Net.IPAddress.IsLoopback(remote);

        if (isLoopback && authOptions.NetworkAdmin == AdminNetworkTrust.Loopback)
            return AuthenticateResult.Success(Anonymous(UserRole.Admin));

        if (authOptions.NetworkViewer == ViewerNetworkTrust.Remote
            || (isLoopback && authOptions.NetworkViewer == ViewerNetworkTrust.Loopback))
        {
            return AuthenticateResult.Success(Anonymous(UserRole.Viewer));
        }

        return AuthenticateResult.NoResult();
    }

    /// <summary>
    /// The token's person as a principal: the product's two roles from the roles the token holds here (the lower of the person's and the console's ceiling): an administrator where it holds admin, a viewer where it holds
    /// anything that reads. A token that holds none is not a way in. The reason a token is refused is not given to the caller.
    /// </summary>
    private AuthenticateResult FromToken(TokenResult result)
    {
        if (!result.Ok) return AuthenticateResult.Fail("The token is not valid.");
        var held = ManagementAuthorization.Effective(result.Token!);
        var role = ServerRoles.Allows(held, ServerRole.Admin) ? UserRole.Admin : ServerRoles.Allows(held, ServerRole.Viewer) ? UserRole.Viewer : (UserRole?)null;
        if (role is null) return AuthenticateResult.Fail("The token holds no role here.");

        var token = result.Token!;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.SubjectId),
            new(authOptions.Product.UserIdClaim, token.SubjectId),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(token.SubjectName) ? token.SubjectId : token.SubjectName),
            new(ClaimTypes.Role, role.Value.ToString()),
        };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }

    private AuthenticationTicket Ticket(UserRecord user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(authOptions.Product.UserIdClaim, user.Id),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(ClaimTypes.Email, user.Email));

        return new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name);
    }

    /// <summary>
    /// A network-trust fallback grant: nobody has a name, which is exactly what "trusted by network,
    /// not by sign-in" means. Config commits fall back to the system identity, because attributing them
    /// to a person nobody identified would be a lie.
    /// </summary>
    private AuthenticationTicket Anonymous(UserRole role)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
    }
}
