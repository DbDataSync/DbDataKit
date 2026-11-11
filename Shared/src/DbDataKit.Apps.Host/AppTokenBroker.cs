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

    /// <summary>The scope a manifest's <c>models</c> grant: <c>model/entity:read</c> or <c>:write</c> for each entity it lists.</summary>
    public static IReadOnlyList<string> ScopeOf(AppManifest manifest) =>
        [.. manifest.Models.SelectMany(m => m.Entities.Where(e => e.Value != DataOperations.None).Select(e =>
            e.Value.HasFlag(DataOperations.ChangeRequest) && !e.Value.HasFlag(DataOperations.Write) ? TokenScope.ProposeEntry(m.Model, e.Key) : TokenScope.Entry(m.Model, e.Key, write: e.Value.HasFlag(DataOperations.Write))))];

    /// <summary>A token for the instance's own audience (<c>app:&lt;instance&gt;</c>): the apps host's data gateway (<see cref="AppDataGateway"/>) is what accepts it, and makes the token the Store sees.</summary>
    public AppToken Issue(AppSession session, string instance)
    {
        var roles = ServerRole.None;
        foreach (var name in session.RolesOn(instance))
            if (Enum.TryParse<ServerRole>(name, ignoreCase: true, out var role) && role != ServerRole.None) roles |= role;
        var audience = AppInstanceAudience.Of(instance);
        var request = new ConsoleTokenRequest(audience, session.Identity.UserId, session.Identity.Name, roles, App: instance);
        return new AppToken(issuer.Issue(request, LifetimeSeconds), LifetimeSeconds, audience);
    }
}
