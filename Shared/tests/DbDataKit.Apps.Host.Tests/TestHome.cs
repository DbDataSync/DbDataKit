using System.Security.Cryptography;
using System.Text;
using DbDataKit.Identity;
using DbDataKit.Identity.Tests;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Apps.Tests;

/// <summary>
/// A real identity home for the apps host's tests (plan 26 AP19): the home's own state, provider and keys (the fixture of the identity tests), the apps host's client registered as the one that may exchange tokens, and each
/// instance's client. A test gets an app's token the way the host does, in two steps: the person signs in through the host's client (the authorization code with PKCE, a refresh token), and the refresh token is traded for the instance's
/// access token (RFC 8693). Nothing here signs a token but the home.
/// </summary>
internal sealed class TestHome : IDisposable
{
    private const string Redirect = "https://apps.example.invalid/apps/-/callback";

    private readonly HomeFixture fixture = new();

    public OpenIdProvider Provider { get; }

    public OidcClient Host { get; }

    /// <summary>The host client's secret; null for a public client.</summary>
    public string? HostSecret { get; }

    public TestHome(IAppGrants grants, bool confidential = true, bool hostMayExchange = true, string issuer = "https://home.example.invalid")
    {
        (Host, HostSecret) = fixture.Clients.Register("Apps host", [Redirect], confidential);
        var hostId = Host.ClientId;
        Provider = new OpenIdProvider(fixture.Database, fixture.Users, fixture.Directory, fixture.Clients, fixture.Keys, issuer, apps: grants,
            appHosts: hostMayExchange ? id => id == hostId : null);
    }

    public AppTokenValidator Validator => new(Provider);

    public OidcClients Clients => fixture.Clients;

    public SigningKeys Keys => fixture.Keys;

    /// <summary>The home registers the instance's client, as the apps host does when an instance is saved.</summary>
    public void Instance(string name) => fixture.Clients.RegisterApp(name, [Redirect]);

    /// <summary>A person of the home with the given roles on app instances.</summary>
    public UserRecord Person(string name, params (string Instance, ServerRole Role)[] roles)
    {
        var user = fixture.Users.CreateUser(name, name.ToLowerInvariant() + "@example.invalid", UserRole.Viewer);
        foreach (var (instance, role) in roles) fixture.Directory.AssignApp(AssignmentSubject.User(user.Id), instance, role);
        return user;
    }

    public void Assign(UserRecord user, string instance, ServerRole role) => fixture.Directory.AssignApp(AssignmentSubject.User(user.Id), instance, role);

    public void Disable(UserRecord user) => fixture.Users.SetEnabled(user.Id, false);

    /// <summary>The person signs in through the host's client: the refresh token the host would hold in the session.</summary>
    public string SignIn(UserRecord who)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var result = Provider.Authorize(new AuthorizeRequest(Host.ClientId, Redirect, "code", "openid profile email offline_access", "s", "n", challenge, "S256"), who.Id);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(result.RedirectTo!).Query)["code"] ?? throw new InvalidOperationException(result.RedirectTo);
        return Provider.Exchange(new TokenRequest("authorization_code", code, Redirect, Host.ClientId, HostSecret, verifier)).RefreshToken!;
    }

    /// <summary>The exchange: the access token of <paramref name="instance"/> for the person whose refresh token it is.</summary>
    public TokenResponse Exchange(string refreshToken, string instance, string? clientId = null, string? secret = null) =>
        Provider.Exchange(new TokenRequest(OpenIdProvider.TokenExchangeGrant, null, null, clientId ?? Host.ClientId, clientId is null ? HostSecret : secret, null,
            SubjectToken: refreshToken, Audience: "app:" + instance, SubjectTokenType: OpenIdProvider.RefreshTokenType));

    /// <summary>Sign in and exchange in one: the token the shell would hand an app of the instance.</summary>
    public string Token(UserRecord who, string instance) => Exchange(SignIn(who), instance).AccessToken;

    public void Dispose() => fixture.Dispose();
}
