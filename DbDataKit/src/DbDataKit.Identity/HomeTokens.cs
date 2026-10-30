using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

/// <summary>
/// The tokens a console sends to a server (plan 13 T01), made from what the home says: the subject is the home's user id and the roles are the ones the home holds for that user on that server
/// (and on each Build project of it). The console's own ceiling for the server still applies when the server validates the token.
/// </summary>
public static class HomeTokens
{
    /// <summary>The request to sign for <paramref name="userId"/> calling <paramref name="serverId"/>, or null when the user does not exist or is disabled.</summary>
    public static ConsoleTokenRequest? For(UserStore users, HomeDirectory directory, string userId, string serverId)
    {
        if (users.Get(userId) is not { Enabled: true } user) return null;
        var held = directory.RolesOn(user.Id, serverId);
        return new ConsoleTokenRequest(serverId, user.Id, user.DisplayName, held.Roles, held.Projects);
    }

    /// <summary>A signed token for the call, or null (see <see cref="For"/>).</summary>
    public static string? Issue(ConsoleTokenIssuer issuer, UserStore users, HomeDirectory directory, string userId, string serverId) =>
        For(users, directory, userId, serverId) is { } request ? issuer.Issue(request) : null;
}
