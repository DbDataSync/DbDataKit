using System.Net;
using DbDataKit.Secrets;
using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.Connections.Http.Tests;

/// <summary>
/// Plan 27a IX03 against a fake OAuth provider on a real socket: consent (state and PKCE) gives a refresh token that is kept as a secret; an access token is refreshed after it expires; a provider that rotates has the new refresh token kept; and a provider that revoked the grant turns the
/// connection unhealthy with a message and is not asked again. The client secret is in no failure, message or test result.
/// </summary>
public sealed class OAuthAuthorizationCodeTests
{
    private const string Redirect = "https://console.example/api/outside-connections/oauth/callback";

    /// <summary>The secrets of the test as a dictionary the resolver reads, and the store that writes a rotated refresh token to it.</summary>
    private sealed class Vault : IRefreshTokenStore
    {
        public Dictionary<string, string> Values { get; } = new() { ["CLIENT_SECRET"] = FakeOAuthServer.ClientSecret };

        public List<string> Saved { get; } = [];

        public ISecretResolver Resolver => new SecretResolverChain([new EnvSecretProvider(name => Values.GetValueOrDefault(name))]);

        public Task SaveAsync(string reference, string refreshToken, CancellationToken ct)
        {
            Values[reference["env:".Length..]] = refreshToken;
            Saved.Add(reference);
            return Task.CompletedTask;
        }
    }

    private static ConnectionDefinition Connection(FakeOAuthServer oauth, string extra = "")
    {
        var yaml = $"schema: connection/2\nkind: http\nname: crm\nendpoint: {oauth.BaseAddress}\ntest_path: /data\nsign_in:\n  method: oauth2_authorization_code\n  authorize_url: {oauth.BaseAddress}/authorize\n  token_url: {oauth.BaseAddress}/token\n  revoke_url: {oauth.BaseAddress}/revoke\n  client_id: {FakeOAuthServer.ClientId}\n  client_secret_secret: env:CLIENT_SECRET\n  scope: offline_access read\n  refresh_token_secret: env:REFRESH_TOKEN\n{extra}";
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/crm.yml");
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        return result.Definition!;
    }

    /// <summary>What the console does: send the person to the consent page, take what the provider returns, check the state, exchange the code.</summary>
    private static async Task<ConsentGrant> GiveConsent(FakeOAuthServer oauth, ConnectionDefinition d, Vault vault)
    {
        var request = OAuthAuthorizationCode.Begin(d.SignIn!, Redirect);
        var back = new Uri(oauth.Consent(request.Url));
        var query = System.Web.HttpUtility.ParseQueryString(back.Query);
        Assert.Equal(request.State, query["state"]);
        using var invoker = HttpConnections.TokenInvoker(d);
        var grant = await OAuthAuthorizationCode.ExchangeAsync(d.SignIn!, vault.Resolver, invoker, query["code"]!, Redirect, request.CodeVerifier);
        if (grant.RefreshToken is { } token) vault.Values["REFRESH_TOKEN"] = token;
        return grant;
    }

    [Fact]
    public async Task The_consent_address_has_the_state_the_pkce_challenge_and_the_scope()
    {
        await using var oauth = new FakeOAuthServer();
        var d = Connection(oauth);
        var a = OAuthAuthorizationCode.Begin(d.SignIn!, Redirect);
        var b = OAuthAuthorizationCode.Begin(d.SignIn!, Redirect);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(a.Url).Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal(FakeOAuthServer.ClientId, q["client_id"]);
        Assert.Equal(Redirect, q["redirect_uri"]);
        Assert.Equal("offline_access read", q["scope"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(a.State, q["state"]);
        Assert.NotEqual(a.State, b.State);
        Assert.NotEqual(a.CodeVerifier, b.CodeVerifier);
        Assert.DoesNotContain(a.CodeVerifier, a.Url, StringComparison.Ordinal);   // only the challenge travels
    }

    [Fact]
    public async Task Consent_gives_a_refresh_token_and_the_connection_signs_in_with_it()
    {
        await using var oauth = new FakeOAuthServer();
        var vault = new Vault();
        var d = Connection(oauth);
        var grant = await GiveConsent(oauth, d, vault);
        Assert.NotNull(grant.RefreshToken);

        using var connection = HttpConnections.Create(d, vault.Resolver);
        Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode);
        Assert.Equal(1, connection.TokenRequests);   // the access token is cached
    }

    [Fact]
    public async Task A_code_that_was_used_or_a_wrong_verifier_is_refused()
    {
        await using var oauth = new FakeOAuthServer();
        var vault = new Vault();
        var d = Connection(oauth);
        var request = OAuthAuthorizationCode.Begin(d.SignIn!, Redirect);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(oauth.Consent(request.Url)).Query)["code"]!;
        using var invoker = HttpConnections.TokenInvoker(d);

        var wrong = await Assert.ThrowsAsync<ConnectionFailureException>(() => OAuthAuthorizationCode.ExchangeAsync(d.SignIn!, vault.Resolver, invoker, code, Redirect, "not-the-verifier"));
        Assert.Equal(TokenEndpoint.GrantRevoked, wrong.Failure.Code);

        // the code is single use, even when the first try failed
        var again = await Assert.ThrowsAsync<ConnectionFailureException>(() => OAuthAuthorizationCode.ExchangeAsync(d.SignIn!, vault.Resolver, invoker, code, Redirect, request.CodeVerifier));
        Assert.False(again.Failure.IsTemporary);
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_once_from_the_stored_refresh_token()
    {
        await using var oauth = new FakeOAuthServer { AccessTokenSeconds = 120 };
        var vault = new Vault();
        var d = Connection(oauth);
        await GiveConsent(oauth, d, vault);
        var clock = new FakeTimeProvider();
        using var connection = HttpConnections.Create(d, vault.Resolver, new HttpConnectionOptions(Time: clock));

        Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode);
        Assert.Equal(1, oauth.RefreshCalls);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode);
        Assert.Equal(1, oauth.RefreshCalls);   // still good (30 s are kept in hand)
        clock.Advance(TimeSpan.FromSeconds(61));
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode)));
        Assert.Equal(2, oauth.RefreshCalls);   // refreshed once, however many callers met the expiry together
    }

    [Fact]
    public async Task A_provider_that_rotates_has_the_new_refresh_token_kept_and_used_next_time()
    {
        await using var oauth = new FakeOAuthServer { AccessTokenSeconds = 60, Rotate = true };
        var vault = new Vault();
        var d = Connection(oauth);
        await GiveConsent(oauth, d, vault);
        var first = vault.Values["REFRESH_TOKEN"];
        var clock = new FakeTimeProvider();
        using var connection = HttpConnections.Create(d, vault.Resolver, new HttpConnectionOptions(Time: clock, RefreshTokens: vault));

        Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode);
        Assert.NotEqual(first, vault.Values["REFRESH_TOKEN"]);
        Assert.Single(vault.Saved);
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode);   // the old one no longer works: the new one was used
        Assert.Equal(2, vault.Saved.Count);
    }

    [Fact]
    public async Task A_revoked_grant_is_a_permanent_failure_that_turns_the_connection_unhealthy_and_is_not_asked_again()
    {
        await using var oauth = new FakeOAuthServer { AccessTokenSeconds = 60 };
        var vault = new Vault();
        var d = Connection(oauth);
        await GiveConsent(oauth, d, vault);
        var clock = new FakeTimeProvider();
        var lost = new List<(string Name, string Message)>();
        var options = new HttpConnectionOptions(Time: clock, ConsentLost: (name, message) => lost.Add((name, message)));
        using var connection = HttpConnections.Create(d, vault.Resolver, options);
        Assert.Equal(HttpStatusCode.OK, (await connection.Client.GetAsync("data")).StatusCode);

        oauth.RevokeEverything();   // the person took consent back at the provider
        clock.Advance(TimeSpan.FromMinutes(5));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => connection.Client.GetAsync("data"));
        var classified = HttpFailures.Classify(failure);
        Assert.Equal(ConnectionFailureKind.Login, classified.Kind);
        Assert.Equal(TokenEndpoint.GrantRevoked, classified.Code);
        Assert.False(classified.IsTemporary);
        Assert.Equal([("crm", TokenEndpoint.GrantRevokedText)], lost);

        var calls = oauth.RefreshCalls;
        await Assert.ThrowsAnyAsync<Exception>(() => connection.Client.GetAsync("data"));
        Assert.Equal(calls, oauth.RefreshCalls);   // the same refresh token is not offered again
        Assert.Single(lost);

        // the Test says it in words, and that a person must give consent again; nothing of the client secret is in it
        var test = await HttpConnectionTester.TestAsync(d, vault.Resolver, new HttpConnectionOptions(Time: clock));
        Assert.False(test.Ok);
        Assert.Equal("rejected", test.SignIn);
        Assert.Equal(ConnectionFailureKind.Login, test.Kind);
        Assert.False(test.Temporary);
        Assert.Contains("consent again", test.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeOAuthServer.ClientSecret, test.Message + failure.Message, StringComparison.Ordinal);

        // consent given again (a new refresh token) makes it work without a restart of anything
        await GiveConsent(oauth, d, vault);
        using var healed = HttpConnections.Create(d, vault.Resolver, options);
        Assert.Equal(HttpStatusCode.OK, (await healed.Client.GetAsync("data")).StatusCode);
    }

    [Fact]
    public async Task Test_before_any_consent_says_consent_has_not_been_given()
    {
        await using var oauth = new FakeOAuthServer();
        var vault = new Vault();   // no REFRESH_TOKEN
        var test = await HttpConnectionTester.TestAsync(Connection(oauth), vault.Resolver);
        Assert.False(test.Ok);
        Assert.Contains("Consent has not been given", test.Message, StringComparison.Ordinal);
        Assert.Null(test.Reached);
    }

    [Fact]
    public async Task Test_with_consent_works()
    {
        await using var oauth = new FakeOAuthServer();
        var vault = new Vault();
        var d = Connection(oauth);
        await GiveConsent(oauth, d, vault);
        var test = await HttpConnectionTester.TestAsync(d, vault.Resolver);
        Assert.True(test.Ok, test.Message);
        Assert.Equal("accepted", test.SignIn);
    }

    [Fact]
    public async Task A_provider_that_gives_no_refresh_token_is_seen_as_such()
    {
        await using var oauth = new FakeOAuthServer { GiveNoRefreshToken = true };
        var grant = await GiveConsent(oauth, Connection(oauth), new Vault());
        Assert.Null(grant.RefreshToken);
        Assert.NotEmpty(grant.AccessToken);
    }

    [Fact]
    public async Task Revoke_tells_the_provider_and_without_a_revoke_address_says_it_did_not()
    {
        await using var oauth = new FakeOAuthServer();
        var vault = new Vault();
        var d = Connection(oauth);
        await GiveConsent(oauth, d, vault);
        var token = vault.Values["REFRESH_TOKEN"];
        using var invoker = HttpConnections.TokenInvoker(d);

        Assert.True(await OAuthAuthorizationCode.RevokeAsync(d.SignIn!, vault.Resolver, invoker, token));
        Assert.Equal([token], oauth.Revoked);
        var without = Connection(oauth).SignIn!;
        without.RevokeUrl = null;
        Assert.False(await OAuthAuthorizationCode.RevokeAsync(without, vault.Resolver, invoker, token));
        Assert.Single(oauth.Revoked);
    }

    [Theory]
    [InlineData("authorize_url", "sign_in:\n  method: oauth2_authorization_code\n  token_url: https://x.example/t\n  client_id: c\n  refresh_token_secret: env:R\n")]
    [InlineData("refresh_token_secret", "sign_in:\n  method: oauth2_authorization_code\n  authorize_url: https://x.example/a\n  token_url: https://x.example/t\n  client_id: c\n")]
    [InlineData("belongs to", "sign_in:\n  method: api_key\n  key_secret: env:K\n  revoke_url: https://x.example/r\n")]
    [InlineData("not a secret reference", "sign_in:\n  method: oauth2_authorization_code\n  authorize_url: https://x.example/a\n  token_url: https://x.example/t\n  client_id: c\n  refresh_token_secret: 1//plain-refresh-token\n")]
    public void The_loader_refuses_a_sign_in_that_is_not_complete_or_names_a_token_in_plain_sight(string expected, string signIn)
    {
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind($"schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com\n{signIn}", "connections/crm.yml");
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, x => x.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }
}
