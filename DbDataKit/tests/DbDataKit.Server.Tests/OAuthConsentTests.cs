using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Connections.Http.Tests;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a IX03 on a real console against a fake OAuth provider: Authorize sends the person to the consent page, the callback keeps the refresh token as a secret, Test signs in with it, a provider that took consent back turns the connection unhealthy with a message, and Revoke
/// asks the provider and forgets the token. Neither the client secret nor a refresh token is in any answer, any row of the record or any file but the secret store.
/// </summary>
public sealed class OAuthConsentTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-oauth-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> bodies = [];
    private FakeOAuthServer oauth = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = "";
    private string viewer = "";

    private string Crm => $"schema: connection/2\nkind: http\nname: crm\nendpoint: {oauth.BaseAddress}\ntest_path: /data\nsign_in:\n  method: oauth2_authorization_code\n  authorize_url: {oauth.BaseAddress}/authorize\n  token_url: {oauth.BaseAddress}/token\n  revoke_url: {oauth.BaseAddress}/revoke\n  client_id: {FakeOAuthServer.ClientId}\n  client_secret_secret: secret:dbdatakit:server:connection:crm:client_secret\n  scope: offline_access\n  refresh_token_secret: secret:dbdatakit:server:connection:crm:refresh_token\n";

    public async ValueTask InitializeAsync()
    {
        oauth = new FakeOAuthServer();
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada", UserRole.Admin);
        viewer = SignIn("Vic", UserRole.Viewer);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await oauth.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SignIn(string name, UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> Call(HttpMethod method, string path, string? session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        bodies.Add(text);
        return (response, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private async Task<JsonNode> Row() => (await Call(HttpMethod.Get, "/api/outside-connections", admin)).Body!["connections"]!.AsArray().Single(c => (string)c!["name"]! == "crm")!;

    /// <summary>The person presses Authorize, signs in and agrees at the provider, and the browser is sent back to the console: the callback's answer.</summary>
    private async Task<HttpResponseMessage> ConsentAndReturn()
    {
        var (_, started) = await Call(HttpMethod.Post, "/api/outside-connections/crm/authorize", admin);
        var back = new Uri(oauth.Consent((string)started!["url"]!));
        return (await Call(HttpMethod.Get, back.PathAndQuery, admin)).Response;
    }

    [Fact]
    public async Task Consent_is_kept_as_a_secret_signs_in_goes_unhealthy_when_the_provider_revokes_and_revoke_forgets_the_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/crm", admin, new YamlBody(Crm))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/client_secret", admin, new SecretBody(FakeOAuthServer.ClientSecret))).Response.StatusCode);

        // before consent: Test says so, and the screen says the connection is not healthy
        var (_, before) = await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin);
        Assert.False((bool)before!["ok"]!);
        Assert.Contains("Consent has not been given", (string)before["message"]!);
        Assert.False((bool)(await Row())["health"]!["ok"]!);

        // only an administrator starts a consent; the address is the provider's, with the console's callback as where to return
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/outside-connections/crm/authorize", viewer)).Response.StatusCode);
        var (_, started) = await Call(HttpMethod.Post, "/api/outside-connections/crm/authorize", admin);
        var url = (string)started!["url"]!;
        Assert.StartsWith(oauth.BaseAddress + "/authorize?", url, StringComparison.Ordinal);
        Assert.EndsWith("/api/outside-connections/oauth/callback", (string)started["redirectUri"]!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/api/outside-connections/nope/authorize", admin)).Response.StatusCode);

        // the provider sends the person back: the refresh token is kept, the person lands on the Connections screen with the outcome
        var back = new Uri(oauth.Consent(url));
        var (callback, _) = await Call(HttpMethod.Get, back.PathAndQuery, admin);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.StartsWith("/connections?consent=ok&connection=crm", callback.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var refreshToken = Assert.Single(oauth.RefreshTokens.Keys);
        var row = await Row();
        var secret = row["secrets"]!.AsArray().Single(s => (string)s!["field"]! == "refresh_token")!;
        Assert.True((bool)secret["available"]!);
        Assert.True((bool)row["health"]!["ok"]!);

        // the same return again (a replay), and a return that was never started, are refused in words
        var replay = await Call(HttpMethod.Get, back.PathAndQuery, admin);
        Assert.Contains("consent=failed", replay.Response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("not%20started%20here", replay.Response.Headers.Location.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, back.PathAndQuery, viewer)).Response.StatusCode);

        var (_, works) = await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin);
        Assert.True((bool)works!["ok"]!, (string)works["message"]!);
        Assert.Equal("accepted", (string)works["signIn"]!);

        // the person takes consent back at the provider: Test says a person must give consent again, and the connection is unhealthy
        oauth.RevokeEverything();
        var (_, revoked) = await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin);
        Assert.False((bool)revoked!["ok"]!);
        Assert.Equal("rejected", (string)revoked["signIn"]!);
        Assert.False((bool)revoked["temporary"]!);
        Assert.Contains("consent again", (string)revoked["message"]!);
        var unhealthy = (await Row())["health"]!;
        Assert.False((bool)unhealthy["ok"]!);
        Assert.Contains("consent again", (string)unhealthy["message"]!);

        // consent again heals it
        Assert.StartsWith("/connections?consent=ok", (await ConsentAndReturn()).Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.True((bool)(await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin)).Body!["ok"]!);
        Assert.True((bool)(await Row())["health"]!["ok"]!);
        var current = oauth.RefreshTokens.Where(t => t.Value).Select(t => t.Key).Single();

        // revoke: the provider is told, the token is forgotten here, the connection is no longer authorised
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/outside-connections/crm/revoke", viewer)).Response.StatusCode);
        var (revokeStatus, revokedAnswer) = await Call(HttpMethod.Post, "/api/outside-connections/crm/revoke", admin);
        Assert.Equal(HttpStatusCode.OK, revokeStatus.StatusCode);
        Assert.Contains("told to revoke", (string)revokedAnswer!["message"]!);
        Assert.Contains(current, oauth.Revoked);
        Assert.False((bool)(await Row())["secrets"]!.AsArray().Single(s => (string)s!["field"]! == "refresh_token")!["available"]!);
        Assert.Contains("Consent has not been given", (string)(await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin)).Body!["message"]!);

        // the client secret and the refresh tokens are in no answer, no row of the record and no file but the secret store
        var (_, audit) = await Call(HttpMethod.Get, "/api/agents/audit?limit=1000", admin);
        var kinds = audit!.AsArray().Select(a => (string)a!["kind"]!).ToList();
        Assert.Contains("connection-consent-given", kinds);
        Assert.Contains("connection-consent-revoked", kinds);
        foreach (var canary in new[] { FakeOAuthServer.ClientSecret, refreshToken, current })
        {
            Assert.DoesNotContain(canary, audit.ToJsonString());
            Assert.DoesNotContain(canary, string.Join('\n', bodies));
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("secrets.yaml", StringComparison.Ordinal) && !f.Contains("keys") && !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains(".db-")))
                Assert.DoesNotContain(canary, await File.ReadAllTextAsync(file));
        }
    }

    [Fact]
    public async Task A_provider_that_denies_consent_or_gives_no_refresh_token_is_told_in_words_and_nothing_is_kept()
    {
        await Call(HttpMethod.Put, "/api/outside-connections/crm", admin, new YamlBody(Crm));
        await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/client_secret", admin, new SecretBody(FakeOAuthServer.ClientSecret));

        // the person says no at the provider: it returns error=access_denied with the state
        var (_, started) = await Call(HttpMethod.Post, "/api/outside-connections/crm/authorize", admin);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri((string)started!["url"]!).Query);
        var denied = await Call(HttpMethod.Get, $"/api/outside-connections/oauth/callback?error=access_denied&state={Uri.EscapeDataString(query["state"]!)}", admin);
        Assert.Contains("consent=failed", denied.Response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("access_denied", denied.Response.Headers.Location.OriginalString, StringComparison.Ordinal);
        Assert.Empty(oauth.RefreshTokens);

        // a provider that gives no refresh token: the connection could not sign in later, so it is not called consented
        oauth.GiveNoRefreshToken = true;
        var result = await ConsentAndReturn();
        Assert.Contains("consent=failed", result.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("refresh%20token", result.Headers.Location.OriginalString, StringComparison.Ordinal);
        Assert.False((bool)(await Row())["secrets"]!.AsArray().Single(s => (string)s!["field"]! == "refresh_token")!["available"]!);
    }
}
