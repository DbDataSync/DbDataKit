using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Identity;

/// <summary>
/// Checks the identity home's tokens in a process that does not run the home (plan 26 AP19): a host that only has the home's address. The home publishes its public keys at <c>/identity/jwks</c>; this keeps them, checks a token's
/// signature, <c>typ</c>, issuer and expiry exactly as <see cref="OpenIdProvider.Verify"/> does where the home runs, and answers the same claims, so <see cref="AppTokenValidator"/> takes <see cref="Verify"/> as its check and the instance's
/// audience, the roles and the scope are read the same way. Only ES256 is accepted (the home's key), and only a key the home published: an unknown key id is looked for in the home's list again (at most once per
/// <see cref="Retry"/>, so a flood of tokens with invented ids cannot make this host ask the home for each), never accepted.
/// </summary>
public sealed class JwksTokenVerifier(string issuer, HttpClient http, TimeProvider? clock = null)
{
    /// <summary>The longest this host waits between two asks of the home for its keys.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    private readonly string _issuer = issuer.TrimEnd('/');
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _asking = new(1, 1);
    private volatile Dictionary<string, ECParameters> _keys = [];
    private DateTimeOffset _askedAt = DateTimeOffset.MinValue;

    /// <summary>Where the home's keys are: <c>&lt;issuer&gt;/identity/jwks</c>.</summary>
    public string JwksUri => _issuer + "/identity/jwks";

    /// <summary>Asks the home for its keys if this host has none for the token's key id and did not ask lately. Call before <see cref="Verify"/> on a token that may be signed with a key made since the last ask.</summary>
    public async Task PrepareAsync(string? jwt, CancellationToken cancellationToken)
    {
        if (KidOf(jwt) is not { } kid || _keys.ContainsKey(kid)) return;
        await _asking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys.ContainsKey(kid) || _clock.GetUtcNow() - _askedAt < Retry) return;
            _askedAt = _clock.GetUtcNow();
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _asking.Release(); }
    }

    /// <summary>Reads the home's keys now. A home that cannot be reached leaves the keys this host holds as they are.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await http.GetFromJsonAsync<JsonElement>(JwksUri, cancellationToken).ConfigureAwait(false);
            var found = new Dictionary<string, ECParameters>(StringComparer.Ordinal);
            foreach (var jwk in document.GetProperty("keys").EnumerateArray())
            {
                if (jwk.GetProperty("kty").GetString() != "EC" || jwk.GetProperty("crv").GetString() != "P-256" || jwk.GetProperty("kid").GetString() is not { Length: > 0 } kid) continue;
                found[kid] = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!), Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!) },
                };
            }
            _keys = found;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or TaskCanceledException) { }
    }

    /// <summary>The claims of a token the home signed with a key it publishes, when its type, issuer and expiry are right; otherwise null. The same answer <see cref="OpenIdProvider.Verify"/> gives where the home runs.</summary>
    public IReadOnlyDictionary<string, object?>? Verify(string? jwt, string expectedType)
    {
        var parts = (jwt ?? "").Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64Url.DecodeFromChars(parts[0]))!;
            if (header["alg"].GetString() != "ES256" || header["typ"].GetString() != expectedType) return null;
            if (!_keys.TryGetValue(header["kid"].GetString() ?? "", out var parameters)) return null;
            using var key = ECDsa.Create(parameters);
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64Url.DecodeFromChars(parts[1]))!;
            if (raw["iss"].GetString()?.TrimEnd('/') != _issuer || raw["exp"].GetInt64() <= _clock.GetUtcNow().ToUnixTimeSeconds()) return null;
            return raw.ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? (object?)p.Value.GetString() : p.Value.Clone());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException or CryptographicException) { return null; }
    }

    private static string? KidOf(string? jwt)
    {
        var parts = (jwt ?? "").Split('.');
        if (parts.Length != 3) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64Url.DecodeFromChars(parts[0]))?.GetValueOrDefault("kid").GetString(); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }
}
