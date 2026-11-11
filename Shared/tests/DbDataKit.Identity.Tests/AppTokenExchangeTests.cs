using System.Security.Cryptography;
using System.Text;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// Plan 26 AP19, the host's half: the apps host signs a person in with its own client and trades that person's refresh token at the home for the access token of one app instance (RFC 8693). The tests run the home's provider
/// on its real state, keys and clients (the fixture of the other identity tests) and check what a module of the serving layer checks of the token.
/// </summary>
public sealed class AppTokenExchangeTests : IDisposable
{
    private const string Redirect = "https://apps.example.invalid/apps/-/callback";
    private const string Issuer = "https://home.example.invalid";

    private readonly HomeFixture _home = new();
    private readonly Grants _grants = new();
    private readonly Clock _clock = new();
    private readonly HashSet<string> _hosts = [];
    private readonly OpenIdProvider _provider;
    private readonly OidcClient _host;
    private readonly string _hostSecret;
    private readonly UserRecord _ada, _bob, _cy;

    public AppTokenExchangeTests()
    {
        (_host, var secret) = _home.Clients.Register("Apps host", [Redirect], confidential: true);
        _hostSecret = secret!;
        _hosts.Add(_host.ClientId);
        _provider = new OpenIdProvider(_home.Database, _home.Users, _home.Directory, _home.Clients, _home.Keys, Issuer, _clock, _grants, appHosts: _hosts.Contains);
        _grants.Scopes["acme"] = [TokenScope.Entry("sales", "order", write: true), TokenScope.Entry("sales", "customer", write: false)];
        _grants.Scopes["acme~preview"] = [TokenScope.Entry("sales", "order", write: true), TokenScope.Entry("sales", "invoice", write: true)];
        _grants.Scopes["globex"] = [TokenScope.Entry("sales", "order", write: true)];
        foreach (var app in new[] { "acme", "globex" }) _home.Clients.RegisterApp(app, [Redirect]);
        _ada = _home.Users.CreateUser("Ada", "ada@example.invalid", UserRole.Viewer);   // may write
        _bob = _home.Users.CreateUser("Bob", "bob@example.invalid", UserRole.Viewer);   // may only read
        _cy = _home.Users.CreateUser("Cy", "cy@example.invalid", UserRole.Viewer);      // no role on the apps
        foreach (var app in new[] { "acme", "globex" })
        {
            _home.Directory.AssignApp(AssignmentSubject.User(_ada.Id), app, ServerRole.Author);
            _home.Directory.AssignApp(AssignmentSubject.User(_bob.Id), app, ServerRole.Viewer);
        }
    }

    public void Dispose() => _home.Dispose();

    private sealed class Grants : IAppGrants
    {
        public Dictionary<string, IReadOnlyList<string>> Scopes { get; } = [];

        public IReadOnlyList<string>? ScopeOf(string instance) => Scopes.GetValueOrDefault(instance);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private string SignIn(UserRecord who, OidcClient? client = null, string? secret = null)
    {
        client ??= _host;
        secret ??= client == _host ? _hostSecret : null;
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var result = _provider.Authorize(new AuthorizeRequest(client.ClientId, Redirect, "code", "openid profile offline_access", "s", "n", challenge, "S256"), who.Id);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(result.RedirectTo!).Query)["code"] ?? throw new InvalidOperationException(result.RedirectTo);
        return _provider.Exchange(new TokenRequest("authorization_code", code, Redirect, client.ClientId, secret, verifier)).RefreshToken!;
    }

    private TokenResponse Exchange(string refresh, string audience, string? clientId = null, string? secret = null, string? type = OpenIdProvider.RefreshTokenType) =>
        _provider.Exchange(new TokenRequest(OpenIdProvider.TokenExchangeGrant, null, null, clientId ?? _host.ClientId, clientId is null ? _hostSecret : secret, null, SubjectToken: refresh, Audience: audience, SubjectTokenType: type));

    private OAuthException Refused(Action action) => Assert.Throws<OAuthException>(action);

    [Fact]
    public void The_exchange_gives_the_instances_token_for_the_person_narrowed_by_the_roles_they_hold_on_it()
    {
        var validator = new AppTokenValidator(_provider);

        var ada = validator.Validate(Exchange(SignIn(_ada), "app:acme").AccessToken, "acme");
        Assert.True(ada.Ok);
        Assert.Equal(("acme", _ada.Id, "Ada"), (ada.Token!.Instance, ada.Token.SubjectId, ada.Token.SubjectName));
        Assert.Contains("author", ada.Token.Roles);
        Assert.True(ada.Token.Allows("sales", "order", write: true));
        Assert.True(ada.Token.Allows("sales", "customer", write: false));
        Assert.False(ada.Token.Allows("sales", "customer", write: true));
        Assert.False(ada.Token.Allows("sales", "invoice", write: false));   // a model or entity the manifest does not name

        var bob = validator.Validate(Exchange(SignIn(_bob), "app:acme").AccessToken, "acme").Token!;
        Assert.True(bob.Allows("sales", "order", write: false));
        Assert.False(bob.Allows("sales", "order", write: true));              // a person who may only read gets no write
        Assert.Equal(_bob.Id, bob.SubjectId);
    }

    [Fact]
    public void The_token_is_the_instances_and_not_the_hosts_or_the_homes_and_instance_B_refuses_instance_As()
    {
        var refresh = SignIn(_ada);
        var forAcme = Exchange(refresh, "app:acme").AccessToken;
        var forGlobex = Exchange(refresh, "app:globex").AccessToken;
        var validator = new AppTokenValidator(_provider);

        Assert.Equal("app:acme", _provider.Verify(forAcme, "at+jwt")!["aud"]);
        Assert.Equal(AppTokenFailure.WrongAudience, validator.Validate(forAcme, "globex").Failure);
        Assert.Equal(AppTokenFailure.WrongAudience, validator.Validate(forGlobex, "acme").Failure);
        // the client of the token is the instance's own, so the host's own client never appears in what a module accepts
        Assert.Equal(_home.Clients.AppClientOf("acme")!.ClientId, validator.Validate(forAcme, "acme").Token!.ClientId);
        Assert.NotEqual(_host.ClientId, validator.Validate(forAcme, "acme").Token!.ClientId);
        // the token the same client gets for itself (the refresh grant) is the home's, not an instance's: no instance accepts it
        var own = _provider.Exchange(new TokenRequest("refresh_token", null, null, _host.ClientId, _hostSecret, null, RefreshToken: SignIn(_ada))).AccessToken;
        Assert.Equal(AppTokenFailure.WrongAudience, validator.Validate(own, "acme").Failure);
    }

    [Fact]
    public void A_preview_is_the_instances_client_and_roles_with_the_scope_of_the_previews_manifest()
    {
        var token = new AppTokenValidator(_provider).Validate(Exchange(SignIn(_ada), "app:acme~preview").AccessToken, "acme~preview").Token!;

        Assert.True(token.Allows("sales", "invoice", write: true));    // the preview's manifest names the invoice, production's does not
        Assert.False(token.Allows("sales", "customer", write: false));
        Assert.Contains("author", token.Roles);                         // the roles are those on `acme`
        Assert.Equal(AppTokenFailure.WrongAudience, new AppTokenValidator(_provider).Validate(Exchange(SignIn(_ada), "app:acme~preview").AccessToken, "acme").Failure);
        Refused(() => Exchange(SignIn(_cy), "app:acme~preview"));       // and a person with no role on `acme` has none on its preview
    }

    [Fact]
    public void The_exchange_does_not_use_up_the_refresh_token_which_goes_on_rotating_and_a_replaced_one_is_refused_without_ending_the_sign_in()
    {
        var first = SignIn(_ada);
        Exchange(first, "app:acme");
        Exchange(first, "app:acme");
        Exchange(first, "app:globex");

        // the host's own refresh trades the token for the next; the exchange only ever works with the one in force
        var refreshed = _provider.Exchange(new TokenRequest("refresh_token", null, null, _host.ClientId, _hostSecret, null, RefreshToken: first));
        Assert.Equal("invalid_grant", Refused(() => Exchange(first, "app:acme")).Error);
        Exchange(refreshed.RefreshToken!, "app:acme");
        // a replaced token presented to the exchange is no reason to end the person's sign-in: the next token still refreshes
        var again = _provider.Exchange(new TokenRequest("refresh_token", null, null, _host.ClientId, _hostSecret, null, RefreshToken: refreshed.RefreshToken));
        Assert.NotNull(again.RefreshToken);
    }

    [Fact]
    public void The_exchange_is_refused_in_the_words_of_the_standard_for_everything_it_should_not_do()
    {
        var refresh = SignIn(_ada);

        Assert.Equal(("invalid_client", 401), (Refused(() => Exchange(refresh, "app:acme", _host.ClientId, "wrong")).Error, Refused(() => Exchange(refresh, "app:acme", _host.ClientId, "wrong")).Status));
        Assert.Equal("invalid_request", Refused(() => Exchange("", "app:acme")).Error);
        Assert.Equal("invalid_request", Refused(() => Exchange(refresh, "app:acme", type: "urn:ietf:params:oauth:token-type:access_token")).Error);
        Assert.Equal("invalid_target", Refused(() => Exchange(refresh, "acme")).Error);                     // not an app audience
        Assert.Equal("invalid_target", Refused(() => Exchange(refresh, "app:")).Error);
        Assert.Equal("invalid_target", Refused(() => Exchange(refresh, "app:initech")).Error);              // no client for it here
        Assert.Equal("invalid_target", Refused(() => Exchange(refresh, "app:~preview")).Error);
        Assert.Equal("invalid_grant", Refused(() => Exchange("not-a-token", "app:acme")).Error);

        // a person with no role on the instance, and an instance with no approved version
        Assert.Equal(("access_denied", 403), (Refused(() => Exchange(SignIn(_cy), "app:acme")).Error, Refused(() => Exchange(SignIn(_cy), "app:acme")).Status));
        _grants.Scopes.Remove("globex");
        Assert.Equal("invalid_grant", Refused(() => Exchange(refresh, "app:globex")).Error);
        Exchange(refresh, "app:acme");   // none of that used the token up
    }

    [Fact]
    public void Only_the_client_the_home_names_as_an_apps_host_may_exchange_and_only_its_own_refresh_tokens()
    {
        var other = _home.Clients.Register("Console", [Redirect], confidential: true);
        var theirs = SignIn(_ada, other.Client, other.Secret);
        var mine = SignIn(_ada);

        // not named an apps host: refused, whatever it presents
        Assert.Equal("unauthorized_client", Refused(() => Exchange(theirs, "app:acme", other.Client.ClientId, other.Secret)).Error);
        _hosts.Add(other.Client.ClientId);
        Exchange(theirs, "app:acme", other.Client.ClientId, other.Secret);
        // a refresh token belongs to the client that got it: the host's cannot be traded by another, nor the other's by the host
        Assert.Equal("invalid_grant", Refused(() => Exchange(mine, "app:acme", other.Client.ClientId, other.Secret)).Error);
        Assert.Equal("invalid_grant", Refused(() => Exchange(theirs, "app:acme")).Error);
        // the instance's own public client may not (it is not named)
        Assert.Equal("unauthorized_client", Refused(() => Exchange(mine, "app:acme", _home.Clients.AppClientOf("acme")!.ClientId, null)).Error);
        // and a home whose host named no apps host refuses everyone
        _hosts.Clear();
        Assert.Equal("unauthorized_client", Refused(() => Exchange(mine, "app:acme")).Error);
        Assert.DoesNotContain(OpenIdProvider.TokenExchangeGrant, GrantsOf(new OpenIdProvider(_home.Database, _home.Users, _home.Directory, _home.Clients, _home.Keys, Issuer)));
        Assert.Contains(OpenIdProvider.TokenExchangeGrant, GrantsOf(_provider));
    }

    private static string[] GrantsOf(OpenIdProvider provider) => (string[])provider.Discovery().GetType().GetProperty("grant_types_supported")!.GetValue(provider.Discovery())!;

    [Fact]
    public void A_public_host_client_is_the_registration_the_suite_makes_and_works_the_same()
    {
        var (publicHost, _) = _home.Clients.Register("Public apps host", [Redirect], confidential: false);
        _hosts.Add(publicHost.ClientId);
        var refresh = SignIn(_ada, publicHost);
        Assert.True(new AppTokenValidator(_provider).Validate(Exchange(refresh, "app:acme", publicHost.ClientId).AccessToken, "acme").Ok);
    }

    [Fact]
    public void A_revoked_expired_or_disabled_sign_in_gives_no_token_and_ending_the_person_ends_it()
    {
        var revoked = SignIn(_ada);
        _provider.RevokeUser(_ada.Id);
        Assert.Equal("invalid_grant", Refused(() => Exchange(revoked, "app:acme")).Error);

        var old = SignIn(_bob);
        Exchange(old, "app:acme");
        _clock.Now += OpenIdProvider.RefreshLifetime + TimeSpan.FromMinutes(1);
        Assert.Equal("invalid_grant", Refused(() => Exchange(old, "app:acme")).Error);
        _clock.Now = DateTimeOffset.UtcNow;

        var disabled = SignIn(_bob);
        _home.Users.SetEnabled(_bob.Id, false);
        Assert.Equal("invalid_grant", Refused(() => Exchange(disabled, "app:acme")).Error);
        Assert.Equal("invalid_grant", Refused(() => _provider.Exchange(new TokenRequest("refresh_token", null, null, _host.ClientId, _hostSecret, null, RefreshToken: disabled))).Error);   // the sign-in is over for good
    }

    [Fact]
    public async Task The_hosts_client_gets_the_token_over_the_wire_and_says_why_when_it_does_not()
    {
        var handler = new HomeHandler(_provider, _home.Keys);
        var client = new OpenIdClient(new OpenIdClientOptions(Issuer, _host.ClientId, _hostSecret, Redirect), new HttpClient(handler));
        var refresh = SignIn(_ada);

        var (access, seconds) = await client.ExchangeForAppAsync(refresh, "acme", default);
        Assert.Equal(600, seconds);
        Assert.True(new AppTokenValidator(_provider).Validate(access, "acme").Ok);
        Assert.Contains(OpenIdProvider.TokenExchangeGrant, handler.Grants);

        // the refresh was not used up: the same client refreshes it next
        Assert.NotNull((await client.RefreshAsync(refresh, default)).Grant.RefreshToken);
        var stale = await Assert.ThrowsAsync<OpenIdClientException>(() => client.ExchangeForAppAsync(refresh, "acme", default));
        Assert.True(stale.HomeRefused);
        Assert.Equal("invalid_grant", stale.Error);

        var denied = await Assert.ThrowsAsync<OpenIdClientException>(() => client.ExchangeForAppAsync(SignIn(_cy), "acme", default));
        Assert.False(denied.HomeRefused);
        Assert.Equal("access_denied", denied.Error);

        _hosts.Clear();
        var notAllowed = await Assert.ThrowsAsync<OpenIdClientException>(() => client.ExchangeForAppAsync(SignIn(_ada), "acme", default));
        Assert.Equal(("unauthorized_client", false), (notAllowed.Error, notAllowed.HomeRefused));

        handler.Unreachable = true;
        var down = await Assert.ThrowsAsync<OpenIdClientException>(() => client.ExchangeForAppAsync(refresh, "acme", default));
        Assert.Null(down.Error);
        Assert.False(down.HomeRefused);
    }
}
