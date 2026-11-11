using System.Security.Claims;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Authentication;

namespace DbDataKit.Web.Auth;

/// <summary>
/// A console's token as a principal: the one rule the session handler (a host that signs people in) and a host that has no sign-in of its own (DbDataSync, plan 13a X06) share.
/// </summary>
public static class ConsoleTokenAuthentication
{
    /// <summary>
    /// The product's two roles from the roles the token holds here (the lower of the person's and the console's ceiling): an administrator where it holds admin, a viewer where it holds anything that reads. A token that
    /// holds none is not a way in. The reason a token is refused is not given to the caller.
    /// </summary>
    public static AuthenticateResult Authenticate(TokenResult result, AuthProduct product, string scheme)
    {
        if (!result.Ok) return AuthenticateResult.Fail("The token is not valid.");
        var held = ManagementAuthorization.Effective(result.Token!);
        var role = ServerRoles.Allows(held, ServerRole.Admin) ? UserRole.Admin : ServerRoles.Allows(held, ServerRole.Viewer) ? UserRole.Viewer : (UserRole?)null;
        if (role is null) return AuthenticateResult.Fail("The token holds no role here.");

        var token = result.Token!;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.SubjectId),
            new(product.UserIdClaim, token.SubjectId),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(token.SubjectName) ? token.SubjectId : token.SubjectName),
            new(ClaimTypes.Role, role.Value.ToString()),
        };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)), scheme));
    }
}
