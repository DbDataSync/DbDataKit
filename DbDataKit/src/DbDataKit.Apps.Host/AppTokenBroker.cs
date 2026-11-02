using DbDataKit.Web.Trust;

namespace DbDataKit.Apps;

/// <summary>A token the shell hands the sandboxed app, with the seconds it lives.</summary>
public sealed record AppToken(string Token, int ExpiresInSeconds, string Audience);

/// <summary>
/// Makes the token a sandboxed app uses for data (plan 26 section 3): short-lived, for one person and one instance (its audience is <c>app:&lt;instance&gt;</c>, so another instance's validator refuses it), carrying the roles the identity
/// home gave the person on that instance and no others. The app never holds the person's cookie or the home's tokens; it asks the shell, which asks here.
/// </summary>
public sealed class AppTokenBroker(ConsoleTokenIssuer issuer)
{
    public const int LifetimeSeconds = 300;

    public AppToken Issue(AppSession session, string instance)
    {
        var roles = ServerRole.None;
        foreach (var name in session.RolesOn(instance))
            if (Enum.TryParse<ServerRole>(name, ignoreCase: true, out var role) && role != ServerRole.None) roles |= role;
        var audience = AppInstanceAudience.Of(instance);
        return new AppToken(issuer.Issue(new ConsoleTokenRequest(audience, session.Identity.UserId, session.Identity.Name, roles), LifetimeSeconds), LifetimeSeconds, audience);
    }
}
