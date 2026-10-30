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
public sealed record OpenIdClientOptions(string Authority, string ClientId, string? ClientSecret, string RedirectUri);

/// <summary>Who the home says a person is: the stable user id, the name, the groups, and the roles held on every server and app (<c>dbdata_roles</c>: <c>server:&lt;id&gt;</c>, <c>server:&lt;id&gt;/&lt;project&gt;</c>, <c>app:&lt;id&gt;</c>).</summary>
public sealed record HomeIdentity(string UserId, string? Name, string? Email, IReadOnlyList<string> Groups, IReadOnlyDictionary<string, IReadOnlyList<string>> Roles, bool EmailVerified = false);

/// <summary>A sign-in that cannot be completed; the message is ours and never holds a token or a code.</summary>
public sealed class OpenIdClientException(string message) : Exception(message);

/// <summary>
/// The relying party of the identity home (plan 13a U14): what a second console, an app host or an agent uses to sign people in through the home instead of keeping users of its own. It sends the browser to the home with a
/// PKCE challenge and a nonce, and when the browser comes back it trades the code for tokens, checks the id token (ES256 against the home's published keys, issuer, audience, expiry, nonce) and reads who the person is.
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
            ("response_type", "code"), ("client_id", options.ClientId), ("redirect_uri", options.RedirectUri), ("scope", "openid profile email"),
            ("state", state), ("nonce", nonce), ("code_challenge", challenge), ("code_challenge_method", "S256"),
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        return new Uri(discovery.AuthorizationEndpoint + (discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?") + query);
    }

    /// <summary>Completes a sign-in from the home's redirect (<paramref name="state"/> and <paramref name="code"/>, or <paramref name="error"/>). A state works once.</summary>
    public async Task<(HomeIdentity Identity, string? ReturnUrl)> CompleteAsync(string? state, string? code, string? error, CancellationToken ct)
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
        return (await ValidateAsync(idToken, attempt.Nonce, discovery, ct), attempt.ReturnUrl);
    }

    private async Task<HomeIdentity> ValidateAsync(string? jwt, string nonce, Discovery discovery, CancellationToken ct)
    {
        var parts = (jwt ?? "").Split('.');
        if (parts.Length != 3) throw new OpenIdClientException("The identity home sent no id token.");
        try
        {
            var header = JsonSerializer.Deserialize<JsonElement>(Base64Url.DecodeFromChars(parts[0]));
            if (header.GetProperty("alg").GetString() != "ES256") throw new OpenIdClientException("The id token is not signed with ES256.");
            var kid = header.GetProperty("kid").GetString();
            var jwks = await http.GetFromJsonAsync<JsonElement>(discovery.JwksUri, ct);
            var jwk = jwks.GetProperty("keys").EnumerateArray().FirstOrDefault(k => k.GetProperty("kid").GetString() == kid);
            if (jwk.ValueKind != JsonValueKind.Object) throw new OpenIdClientException("The id token is signed with a key the identity home does not publish.");
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!), Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!) },
            });
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new OpenIdClientException("The id token's signature is not valid.");

            var claims = JsonSerializer.Deserialize<JsonElement>(Base64Url.DecodeFromChars(parts[1]));
            if (claims.GetProperty("iss").GetString() != discovery.Issuer || discovery.Issuer.TrimEnd('/') != _authority) throw new OpenIdClientException("The id token is from another issuer.");
            if (claims.GetProperty("aud").GetString() != options.ClientId) throw new OpenIdClientException("The id token is for another client.");
            if (claims.GetProperty("exp").GetInt64() <= _clock.GetUtcNow().ToUnixTimeSeconds()) throw new OpenIdClientException("The id token has expired.");
            if (!claims.TryGetProperty("nonce", out var n) || n.GetString() != nonce) throw new OpenIdClientException("The id token answers another sign-in (nonce).");

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
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new OpenIdClientException("The id token could not be read.");
        }
    }

    private sealed record Discovery(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri);

    private Discovery? _discovery;

    private async Task<Discovery> DiscoveryAsync(CancellationToken ct)
    {
        if (_discovery is { } known) return known;
        JsonElement d;
        try { d = await http.GetFromJsonAsync<JsonElement>(_authority + "/.well-known/openid-configuration", ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { throw new OpenIdClientException("The identity home could not be reached."); }
        try { return _discovery = new Discovery(d.GetProperty("issuer").GetString()!, d.GetProperty("authorization_endpoint").GetString()!, d.GetProperty("token_endpoint").GetString()!, d.GetProperty("jwks_uri").GetString()!); }
        catch (KeyNotFoundException) { throw new OpenIdClientException("The identity home's discovery document is incomplete."); }
    }

    private static string Random(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
}
