using DbDataKit.Config.Git;

namespace DbDataKit.Web.Auth;

/// <summary>
/// Which product's sign-in this is. Everything that has to be told apart between products on the same machine derives from the name, so two products never share a
/// cookie, a configuration section or a claim: <c>DbDataSync</c> reads <c>DbDataSync:Auth</c> and sets the cookie <c>dbdatasync.session</c>.
/// </summary>
public sealed record AuthProduct(string Name)
{
    private string Lower => Name.ToLowerInvariant();

    /// <summary>The configuration section the options are read from.</summary>
    public string Section => $"{Name}:Auth";

    /// <summary>The name of the session cookie.</summary>
    public string SessionCookie => $"{Lower}.session";

    /// <summary>The short-lived cookies that hold a passkey registration's and a passkey sign-in's challenge between the two calls.</summary>
    public string PasskeyRegistrationCookie => $"{Lower}.passkey-registration";

    public string PasskeyAssertionCookie => $"{Lower}.passkey-assertion";

    /// <summary>The authentication scheme the session handler is registered under.</summary>
    public string SchemeName => $"{Name}Session";

    /// <summary>The claim that carries the signed-in user's id.</summary>
    public string UserIdClaim => $"{Lower}:userId";

    /// <summary>The command that prints a fresh first-run invitation.</summary>
    public string InviteCommand => $"{Lower} invite";

    /// <summary>What a change is attributed to when nobody is signed in: an unauthenticated deployment, or the server acting on its own.</summary>
    public GitAuthor SystemAuthor { get; } = new(Name, $"{Name.ToLowerInvariant()}@localhost");
}
