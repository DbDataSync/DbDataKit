using DbDataKit.Config.Git;
using DbDataSync.Api.Configuration;

namespace DbDataSync.Api.Auth;

/// <summary>
/// DbDataSync's side of the shared session sign-in (<c>DbDataKit.Web.Auth</c>, plan 13, U05): the product identity, and the calls that read the shared options from Sync's
/// configuration. Static extensions, so the code that has always said <c>AuthOptions.FromConfiguration(configuration)</c> or <c>CurrentUser.SystemAuthor</c> still does.
/// </summary>
public static class SyncAuth
{
    /// <summary>Sync reads <c>DbDataSync:Auth</c> and sets the cookie <c>dbdatasync.session</c>, as it always has.</summary>
    public static AuthProduct Product { get; } = new("DbDataSync");

    private static readonly PasskeyOptions NoPasskeys = new()
    {
        RelyingPartyId = "localhost",
        RelyingPartyName = "DbDataSync",
        Mode = FeatureMode.Disabled,
        Origins = new HashSet<string>(),
    };

    extension(AuthOptions)
    {
        public static AuthOptions FromConfiguration(IConfiguration configuration) => AuthOptions.FromConfiguration(configuration, Product);

        public static string SessionCookie => Product.SessionCookie;
    }

    extension(PasskeyOptions)
    {
        /// <summary>
        /// Sync has no passkeys of its own (plan 13, X05): people sign in at the console, and a console's call reaches Sync as a signed token. The shared sign-in code still asks
        /// for the options, so this is the fixed answer: passkeys off, nothing read from configuration. The <c>Auth:Passkeys:*</c> settings and <c>App:AlternateUrls</c> (their
        /// only reader) are refused by <see cref="RemovedSettings"/>, so a file that still sets one is not ignored in silence.
        /// </summary>
        public static PasskeyOptions Disabled => NoPasskeys;
    }

    extension(CurrentUser)
    {
        public static GitAuthor SystemAuthor => Product.SystemAuthor;
    }

    extension(SessionAuthenticationHandler)
    {
        public static string SchemeName => Product.SchemeName;
    }
}
