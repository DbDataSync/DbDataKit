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

    extension(AuthOptions)
    {
        public static AuthOptions FromConfiguration(IConfiguration configuration) => AuthOptions.FromConfiguration(configuration, Product);

        public static string SessionCookie => Product.SessionCookie;
    }

    extension(PasskeyOptions)
    {
        /// <param name="configuration">Where the <c>Auth:Passkeys</c> section is read from.</param>
        /// <param name="apiOptions">Already resolved <see cref="ApiOptions.Url"/> and <see cref="ApiOptions.AlternateUrls"/>.</param>
        public static PasskeyOptions FromConfiguration(IConfiguration configuration, ApiOptions apiOptions) =>
            PasskeyOptions.FromConfiguration(
                configuration, Product, apiOptions.Url, apiOptions.AlternateUrls,
                // Nothing configures App:Url or App:AlternateUrls at all: a fresh clone or `dotnet run` with no server.yaml yet
                // (ApiOptions.Url is then its own DefaultUrl).
                apiOptions.Url == ApiOptions.DefaultUrl ? ["https://localhost:5080", "http://localhost:5173"] : []);
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
