using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Identity;

/// <param name="Authority">The home's address (its issuer).</param>
/// <param name="ClientId">The id the home registered this application under.</param>
/// <param name="ClientSecret">The secret, for a confidential client; null for a public one.</param>
/// <param name="RedirectUri">Where the home sends the browser back: this application's callback, as registered.</param>
/// <param name="PostLogoutRedirectUri">Where the home sends the browser back after it ends the person's session, as registered; null: the home shows its own page.</param>
public sealed record OpenIdClientOptions(string Authority, string ClientId, string? ClientSecret, string RedirectUri, string? PostLogoutRedirectUri = null);

/// <summary>What the home gave besides who the person is: the refresh token (the means to ask again, and to be told no) and the id token (what proves to the home, at sign-out, whose session to end). Both are secrets.</summary>
public sealed record HomeGrant(string? RefreshToken, string? IdToken);

/// <summary>A completed sign-in: the person, where they were going, and the grant.</summary>
public sealed record HomeSignIn(HomeIdentity Identity, string? ReturnUrl, HomeGrant Grant);

/// <summary>Who the home says a person is: the stable user id, the name, the groups, and the roles held on every server and app (<c>dbdata_roles</c>: <c>server:&lt;id&gt;</c>, <c>server:&lt;id&gt;/&lt;project&gt;</c>, <c>app:&lt;id&gt;</c>).</summary>
public sealed record HomeIdentity(string UserId, string? Name, string? Email, IReadOnlyList<string> Groups, IReadOnlyDictionary<string, IReadOnlyList<string>> Roles, bool EmailVerified = false);

/// <summary>A sign-in that cannot be completed; the message is ours and never holds a token or a code.</summary>
public sealed class OpenIdClientException(string message, bool homeRefused = false) : Exception(message)
{
    /// <summary>True when the home answered and said no (the refresh token is revoked, expired or used, or the person is disabled): the sign-in is over. False when the home could not be asked.</summary>
    public bool HomeRefused { get; } = homeRefused;
}

/// <summary>
/// The relying party of the identity home (plan 13a U14): what a second console, an app host or an agent uses to sign people in through the home instead of keeping users of its own. It sends the browser to the home with a
/// PKCE challenge and a nonce, and when the browser comes back it trades the code for tokens, checks the id token (ES256, or RS256 for another provider, against the home's published keys, issuer, audience, expiry, nonce) and reads who the person is.
/// </summary>
public sealed class OpenIdClient(OpenIdClientOptions options, HttpClient http, TimeProvider? clock = null)
{
    private static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private readonly string _authority = options.Authority.TrimEnd('/');

    private sealed record Attempt(string Verifier, string Nonce, DateTimeOffset Expires, string? ReturnUrl);

    /// <summary>Where to send the browser, and the state to remember until it comes back (<paramref name="returnUrl"/> is handed back by <see cref="CompleteAsync"/>).</summary>
    public async Task<Uri> BeginAsync(string? returnUrl, CancellationToken ct)
    {
        var discovery = await DiscoveryAsync(ct);
        var now = _clock.GetUtcNow();
        foreach (var old in _attempts.Where(a => a.Value.Expires < now).Select(a => a.Key)) _attempts.TryRemove(old, out _);

        var verifier = Random(48);
        var state = Random(24);
        var nonce = Random(24);
        _attempts[state] = new Attempt(verifier, nonce, now + AttemptLifetime, returnUrl);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = string.Join('&', new (string, string)[]
        {
            ("response_type", "code"), ("client_id", options.ClientId), ("redirect_uri", options.RedirectUri), ("scope", "openid profile email offline_access"),
            ("state", state), ("nonce", nonce), ("code_challenge", challenge), ("code_challenge_method", "S256"),
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        return new Uri(discovery.AuthorizationEndpoint + (discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?") + query);
    }

    /// <summary>Completes a sign-in from the home's redirect (<paramref name="state"/> and <paramref name="code"/>, or <paramref name="error"/>). A state works once.</summary>
    public async Task<HomeSignIn> CompleteAsync(string? state, string? code, string? error, CancellationToken ct)
    {
        if (state is null || !_attempts.TryRemove(state, out var attempt) || attempt.Expires < _clock.GetUtcNow()) throw new OpenIdClientException("This sign-in was not started here, or it has taken too long. Start again.");
        if (!string.IsNullOrEmpty(error)) throw new OpenIdClientException($"The identity home refused the sign-in ({error}).");
        if (string.IsNullOrEmpty(code)) throw new OpenIdClientException("The identity home sent no code.");

        var discovery = await DiscoveryAsync(ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = options.RedirectUri, ["client_id"] = options.ClientId, ["code_verifier"] = attempt.Verifier,
        };
        if (options.ClientSecret is not null) form["client_secret"] = options.ClientSecret;
        using var response = await http.PostAsync(discovery.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) throw new OpenIdClientException($"The identity home did not accept the code ({(int)response.StatusCode}).");
        var token = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var idToken = token.TryGetProperty("id_token", out var t) ? t.GetString() : null;
        var identity = await ValidateAsync(idToken, attempt.Nonce, discovery, ct);
        return new HomeSignIn(identity, attempt.ReturnUrl, new HomeGrant(token.TryGetProperty("refresh_token", out var r) ? r.GetString() : null, idToken));
    }

    /// <summary>
    /// Asks the home who the person is now, with the refresh token from the sign-in (or the last refresh: each is used once and answered with the next). The roles are the home's current word. When the home says no,
    /// <see cref="OpenIdClientException.HomeRefused"/> is true; when it cannot be reached or answers with an error of its own, it is false and the caller decides how long to go on trusting the last answer.
    /// </summary>
    public async Task<(HomeIdentity Identity, HomeGrant Grant)> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            var discovery = await DiscoveryAsync(ct);
            var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = options.ClientId };
            if (options.ClientSecret is not null) form["client_secret"] = options.ClientSecret;
            using var response = await http.PostAsync(discovery.TokenEndpoint, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                string? error = null;
                try { error = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString(); } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                throw new OpenIdClientException($"The identity home did not refresh the sign-in ({(int)response.StatusCode}).", homeRefused: error == "invalid_grant");
            }
            var token = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var idToken = token.TryGetProperty("id_token", out var t) ? t.GetString() : null;
            var identity = await ValidateAsync(idToken, null, discovery, ct);
            return (identity, new HomeGrant(token.TryGetProperty("refresh_token", out var r) ? r.GetString() : null, idToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new OpenIdClientException("The identity home could not be reached.");
        }
    }

    /// <summary>Where to send the browser to end the person's session at the home (OpenID Connect RP-Initiated Logout), or null when the home publishes no end-session endpoint.</summary>
    public async Task<Uri?> EndSessionUriAsync(string? idTokenHint, string? state, CancellationToken ct)
    {
        var discovery = await DiscoveryAsync(ct);
        if (discovery.EndSessionEndpoint is not { } endpoint) return null;
        var query = new List<(string, string)>();
        if (idTokenHint is not null) query.Add(("id_token_hint", idTokenHint));
        query.Add(("client_id", options.ClientId));
        if (options.PostLogoutRedirectUri is { } back) query.Add(("post_logout_redirect_uri", back));
        if (state is not null) query.Add(("state", state));
        return new Uri(endpoint + (endpoint.Contains('?') ? "&" : "?") + string.Join('&', query.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}")));
    }

    private async Task<HomeIdentity> ValidateAsync(string? jwt, string? nonce, Discovery discovery, CancellationToken ct)
    {
        var parts = (jwt ?? "").Split('.');
        if (parts.Length != 3) throw new OpenIdClientException("The identity home sent no id token.");
        try
        {
            var header = JsonSerializer.Deserialize<JsonElement>(Base64Url.DecodeFromChars(parts[0]));
            var alg = header.GetProperty("alg").GetString();
            if (alg is not ("ES256" or "RS256")) throw new OpenIdClientException("The id token is not signed with ES256 or RS256.");
            var kid = header.GetProperty("kid").GetString();
            var jwks = await http.GetFromJsonAsync<JsonElement>(discovery.JwksUri, ct);
            var jwk = jwks.GetProperty("keys").EnumerateArray().FirstOrDefault(k => k.GetProperty("kid").GetString() == kid);
            if (jwk.ValueKind != JsonValueKind.Object) throw new OpenIdClientException("The id token is signed with a key the identity home does not publish.");
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var signature = Base64Url.DecodeFromChars(parts[2]);
            // the home signs with ES256; an Entra tenant (or any other OpenID Connect provider a home signs people in through, plan 13a U18) with RS256. The key must be of the algorithm the header names and the key's own type says.
            bool valid;
            if (alg == "ES256" && jwk.GetProperty("kty").GetString() == "EC")
            {
                using var key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!), Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!) },
                });
                valid = key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            else if (alg == "RS256" && jwk.GetProperty("kty").GetString() == "RSA")
            {
                using var key = RSA.Create(new RSAParameters { Modulus = Base64Url.DecodeFromChars(jwk.GetProperty("n").GetString()!), Exponent = Base64Url.DecodeFromChars(jwk.GetProperty("e").GetString()!) });
                valid = key.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            else throw new OpenIdClientException("The id token's algorithm does not match its key.");
            if (!valid) throw new OpenIdClientException("The id token's signature is not valid.");

            var claims = JsonSerializer.Deserialize<JsonElement>(Base64Url.DecodeFromChars(parts[1]));
            if (claims.GetProperty("iss").GetString() != discovery.Issuer || discovery.Issuer.TrimEnd('/') != _authority) throw new OpenIdClientException("The id token is from another issuer.");
            if (claims.GetProperty("aud").GetString() != options.ClientId) throw new OpenIdClientException("The id token is for another client.");
            if (claims.GetProperty("exp").GetInt64() <= _clock.GetUtcNow().ToUnixTimeSeconds()) throw new OpenIdClientException("The id token has expired.");
            // a refresh answers no sign-in request, so it carries no nonce
            if (nonce is not null && (!claims.TryGetProperty("nonce", out var n) || n.GetString() != nonce)) throw new OpenIdClientException("The id token answers another sign-in (nonce).");

            var roles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (claims.TryGetProperty("dbdata_roles", out var held))
                foreach (var p in held.EnumerateObject()) roles[p.Name] = [.. p.Value.EnumerateArray().Select(e => e.GetString()!)];
            return new HomeIdentity(
                claims.GetProperty("sub").GetString()!,
                claims.TryGetProperty("name", out var name) ? name.GetString() : null,
                claims.TryGetProperty("email", out var email) ? email.GetString() : null,
                claims.TryGetProperty("groups", out var groups) ? [.. groups.EnumerateArray().Select(e => e.GetString()!)] : [],
                roles,
                // only a JSON true counts (not the string "true", not a missing claim): an address the home does not vouch for links nothing
                claims.TryGetProperty("email_verified", out var verified) && verified.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or CryptographicException)
        {
            throw new OpenIdClientException("The id token could not be read.");
        }
    }

    private sealed record Discovery(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri, string? EndSessionEndpoint);

    private Discovery? _discovery;

    private async Task<Discovery> DiscoveryAsync(CancellationToken ct)
    {
        if (_discovery is { } known) return known;
        JsonElement d;
        try { d = await http.GetFromJsonAsync<JsonElement>(_authority + "/.well-known/openid-configuration", ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { throw new OpenIdClientException("The identity home could not be reached."); }
        try { return _discovery = new Discovery(d.GetProperty("issuer").GetString()!, d.GetProperty("authorization_endpoint").GetString()!, d.GetProperty("token_endpoint").GetString()!, d.GetProperty("jwks_uri").GetString()!, d.TryGetProperty("end_session_endpoint", out var end) ? end.GetString() : null); }
        catch (KeyNotFoundException) { throw new OpenIdClientException("The identity home's discovery document is incomplete."); }
    }

    private static string Random(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
}
