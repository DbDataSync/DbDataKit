using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Http;

/// <summary>Keeps the refresh token an OAuth 2 provider rotated (some give a new one with every refresh and forget the old). The console writes it to its secret store; a reference that is not a store's (an environment variable) cannot be written and the new token is then not kept.</summary>
public interface IRefreshTokenStore
{
    Task SaveAsync(string reference, string refreshToken, CancellationToken ct);
}

/// <summary>What <see cref="OAuthAuthorizationCode.Begin"/> gives: the address to send the person to, and what the console keeps until they come back (the state that ties the return to this request, and the PKCE verifier).</summary>
public sealed record ConsentRequest(string Url, string State, string CodeVerifier);

/// <summary>What the provider gave for a consent: the refresh token to keep (a secret), the access token for now and how long it lasts. A provider that gives no refresh token (the scope did not ask for offline access) is <see cref="RefreshToken"/> = null.</summary>
public sealed record ConsentGrant(string? RefreshToken, string AccessToken, TimeSpan Lifetime);

/// <summary>A source of access tokens for the sign-in: the cached one, or a new one when the clock passed it or a caller found it refused.</summary>
internal interface ITokenSource
{
    /// <summary>How many times the token address was called: what a test counts.</summary>
    int Fetches { get; }

    Task<(string Token, bool Fresh)> GetAsync(string? stale, CancellationToken ct);
}

/// <summary>The token address, shared by the grants that call it (client credentials, authorization code, refresh): one place that signs the client in, sends the form and reads the answer.</summary>
internal static class TokenEndpoint
{
    public const string GrantRevoked = "oauth-grant-revoked";

    public const string GrantRevokedText = "The authorization was revoked or has expired: a person must give consent again (Authorize on the Connections screen).";

    public static async Task<JsonElement> PostAsync(SignInSpec spec, ISecretResolver secrets, HttpMessageInvoker invoker, string address, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var clientSecret = spec.ClientSecretSecret is null ? null : await SecretText.ReadAsync(secrets, spec.ClientSecretSecret, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, address);
        if (clientSecret is null) form.Add(new("client_id", spec.ClientId!));   // a public client: PKCE stands in for the secret
        else if (spec.ClientAuth == "body") { form.Add(new("client_id", spec.ClientId!)); form.Add(new("client_secret", clientSecret)); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(spec.ClientId!)}:{Uri.EscapeDataString(clientSecret)}")));
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await invoker.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // invalid_grant: the refresh token (or the code) is no longer good: consent was withdrawn, the token expired or was replaced. A person must give consent again; asking again would only repeat it.
            if ((int)response.StatusCode is 400 or 401 && ErrorOf(text) == "invalid_grant")
                throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Login, GrantRevoked, HttpFailures.Engine));
            throw new ConnectionFailureException(HttpFailures.ClassifyToken(response.StatusCode));
        }
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Login, "oauth-no-token", HttpFailures.Engine)); }
    }

    public static ConsentGrant Read(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("access_token", out var token) && token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 } text)
        {
            var seconds = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0 ? n : 300;
            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rt ? rt : null;
            return new ConsentGrant(refresh, text, TimeSpan.FromSeconds(seconds));
        }

        throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Login, "oauth-no-token", HttpFailures.Engine));
    }

    private static string? ErrorOf(string text)
    {
        try { using var d = JsonDocument.Parse(text); return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null; }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// OAuth 2 authorization code with PKCE (plan 27a IX03): the three steps the console takes. <see cref="Begin"/> builds the address a person is sent to; <see cref="ExchangeAsync"/> turns the code the provider returns into the refresh token that is then kept as a secret;
/// <see cref="RevokeAsync"/> tells the provider to forget a token. What the connection does afterwards (an access token for each request, refreshed when it expires) is <see cref="AuthorizationCodeTokenSource"/>.
/// </summary>
public static class OAuthAuthorizationCode
{
    /// <summary>The address of the consent page: <c>response_type=code</c>, the client, where the provider returns the person (<paramref name="redirectUri"/>, the console's own callback), the scope, a random <c>state</c> and the PKCE challenge (S256). The verifier stays with the console.</summary>
    public static ConsentRequest Begin(SignInSpec spec, string redirectUri)
    {
        var state = Random(24);
        var verifier = Random(48);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new List<(string, string)> { ("response_type", "code"), ("client_id", spec.ClientId!), ("redirect_uri", redirectUri) };
        if (!string.IsNullOrEmpty(spec.Scope)) query.Add(("scope", spec.Scope));
        query.Add(("state", state));
        query.Add(("code_challenge", challenge));
        query.Add(("code_challenge_method", "S256"));
        var url = spec.AuthorizeUrl! + "?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2)));
        return new ConsentRequest(url, state, verifier);
    }

    /// <summary>Exchanges the code for the tokens. A refusal of the client is a permanent login failure; a code that is not good (used twice, expired) is <c>oauth-grant-revoked</c>.</summary>
    public static async Task<ConsentGrant> ExchangeAsync(SignInSpec spec, ISecretResolver secrets, HttpMessageInvoker invoker, string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri), new("code_verifier", codeVerifier) };
        return TokenEndpoint.Read(await TokenEndpoint.PostAsync(spec, secrets, invoker, spec.TokenUrl!, form, ct).ConfigureAwait(false));
    }

    /// <summary>Asks the provider to revoke <paramref name="token"/> (RFC 7009) when the connection names a <c>revoke_url</c>; false when it does not (the token is then only forgotten here). A failure of the call is thrown classified.</summary>
    public static async Task<bool> RevokeAsync(SignInSpec spec, ISecretResolver secrets, HttpMessageInvoker invoker, string token, CancellationToken ct = default)
    {
        if (spec.RevokeUrl is null) return false;
        var form = new List<KeyValuePair<string, string>> { new("token", token), new("token_type_hint", "refresh_token") };
        var clientSecret = spec.ClientSecretSecret is null ? null : await SecretText.ReadAsync(secrets, spec.ClientSecretSecret, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, spec.RevokeUrl);
        if (clientSecret is null) form.Add(new("client_id", spec.ClientId!));
        else if (spec.ClientAuth == "body") { form.Add(new("client_id", spec.ClientId!)); form.Add(new("client_secret", clientSecret)); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(spec.ClientId!)}:{Uri.EscapeDataString(clientSecret)}")));
        request.Content = new FormUrlEncodedContent(form);
        using var response = await invoker.SendAsync(request, ct).ConfigureAwait(false);
        // RFC 7009: a token already gone is a success too (200); only a refusal of the client or a failure of the system is not
        if (!response.IsSuccessStatusCode) throw new ConnectionFailureException(HttpFailures.ClassifyToken(response.StatusCode));
        return true;
    }

    private static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Gets and keeps the access token of OAuth 2 authorization code (plan 27a IX03) from the refresh token the console's consent stored as a secret. The token is cached until shortly before it expires; <b>an expired or refused token is refreshed once</b> however many callers meet it
/// together. A provider that rotates refresh tokens has the new one written back (<see cref="IRefreshTokenStore"/>) before the access token is used. When the provider says the grant is no longer good (<c>invalid_grant</c>: consent was revoked, or the token expired) the failure is permanent
/// (<see cref="TokenEndpoint.GrantRevoked"/>), <paramref name="consentLost"/> is told once (the connection is unhealthy until a person gives consent again), and the provider is not asked again with the same refresh token.
/// </summary>
internal sealed class AuthorizationCodeTokenSource(string connection, SignInSpec spec, ISecretResolver secrets, HttpMessageInvoker invoker, TimeProvider time, IRefreshTokenStore? store, Action<string, string>? consentLost, Action<string>? refreshed) : ITokenSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;
    private string? _refusedRefreshToken;

    public int Fetches { get; private set; }

    public async Task<(string Token, bool Fresh)> GetAsync(string? stale, CancellationToken ct)
    {
        if (_token is { } cached && cached != stale && time.GetUtcNow() < _expires - OAuthTokenSource.Skew) return (cached, false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } current && current != stale && time.GetUtcNow() < _expires - OAuthTokenSource.Skew) return (current, false);
            var refresh = await SecretText.ReadAsync(secrets, spec.RefreshTokenSecret!, ct).ConfigureAwait(false);
            if (refresh == _refusedRefreshToken) throw Revoked();   // refused already: a person has not given consent again
            ConsentGrant grant;
            try
            {
                var form = new List<KeyValuePair<string, string>> { new("grant_type", "refresh_token"), new("refresh_token", refresh) };
                if (!string.IsNullOrEmpty(spec.Scope)) form.Add(new("scope", spec.Scope));
                Fetches++;
                grant = TokenEndpoint.Read(await TokenEndpoint.PostAsync(spec, secrets, invoker, spec.TokenUrl!, form, ct).ConfigureAwait(false));
            }
            catch (ConnectionFailureException e) when (e.Failure.Code == TokenEndpoint.GrantRevoked)
            {
                (_token, _refusedRefreshToken) = (null, refresh);
                var failure = Revoked();
                consentLost?.Invoke(connection, TokenEndpoint.GrantRevokedText);
                throw failure;
            }

            if (grant.RefreshToken is { } rotated && rotated != refresh && store is not null)
            {
                try { await store.SaveAsync(spec.RefreshTokenSecret!, rotated, ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { /* kept nowhere that can be written: the old token stays; the next refresh may be refused by a provider that rotates */ }
            }

            (_token, _expires) = (grant.AccessToken, time.GetUtcNow() + grant.Lifetime);
            refreshed?.Invoke(connection);
            return (grant.AccessToken, true);
        }
        finally { _gate.Release(); }
    }

    private static ConnectionFailureException Revoked() => new(new ConnectionFailure(ConnectionFailureKind.Login, TokenEndpoint.GrantRevoked, HttpFailures.Engine));
}
