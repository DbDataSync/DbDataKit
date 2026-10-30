using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Identity;

/// <summary>An authorization request as the provider needs it (the query of GET /authorize).</summary>
public sealed record AuthorizeRequest(string? ClientId, string? RedirectUri, string? ResponseType, string? Scope, string? State, string? Nonce, string? CodeChallenge, string? CodeChallengeMethod);

/// <summary>The answer to an authorization request: a URI to send the browser to, or, when the client or its redirect URI cannot be trusted, a message to show instead (never a redirect).</summary>
public sealed record AuthorizeResult(string? RedirectTo, string? Error);

public sealed record TokenRequest(string? GrantType, string? Code, string? RedirectUri, string? ClientId, string? ClientSecret, string? CodeVerifier);

public sealed record TokenResponse(string AccessToken, string IdToken, int ExpiresIn, string Scope);

/// <summary>A token request that is refused, in the words of RFC 6749 section 5.2.</summary>
public sealed class OAuthException(string error, string description, int status = 400) : Exception(description)
{
    public string Error { get; } = error;
    public int Status { get; } = status;
}

/// <summary>
/// The OpenID Connect provider of the identity home: the authorization code flow with PKCE (S256 only), id tokens and access tokens signed ES256, discovery and userinfo. Signing a person in is the host's (it hands the
/// signed-in user id to <see cref="Authorize"/>); this issues the code and trades it for tokens that carry the home's user id as <c>sub</c>, the groups, and the roles held on every server and app (<c>dbdata_roles</c>).
/// </summary>
public sealed class OpenIdProvider(StateDatabase database, UserStore users, HomeDirectory directory, OidcClients clients, SigningKeys keys, string issuer, TimeProvider? clock = null)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    private static readonly string[] KnownScopes = ["openid", "profile", "email"];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Issuer { get; } = issuer.TrimEnd('/');

    public object Discovery() => new
    {
        issuer = Issuer,
        authorization_endpoint = Issuer + "/identity/authorize",
        token_endpoint = Issuer + "/identity/token",
        userinfo_endpoint = Issuer + "/identity/userinfo",
        jwks_uri = Issuer + "/identity/jwks",
        response_types_supported = new[] { "code" },
        grant_types_supported = new[] { "authorization_code" },
        subject_types_supported = new[] { "public" },
        id_token_signing_alg_values_supported = new[] { "ES256" },
        scopes_supported = KnownScopes,
        token_endpoint_auth_methods_supported = new[] { "client_secret_post", "none" },
        code_challenge_methods_supported = new[] { "S256" },
        claims_supported = new[] { "sub", "name", "email", "groups", "dbdata_roles", "nonce" },
    };

    /// <summary>Checks the request and, for the user who is signed in, makes a code and the URI that carries it back to the client.</summary>
    public AuthorizeResult Authorize(AuthorizeRequest request, string signedInUserId)
    {
        // Until the client and the redirect URI are known good, nothing is redirected: an attacker's redirect URI must never receive an error either.
        var client = request.ClientId is null ? null : clients.Find(request.ClientId);
        if (client is null) return new(null, "The client is not known.");
        if (request.RedirectUri is null || !client.RedirectUris.Contains(request.RedirectUri, StringComparer.Ordinal)) return new(null, "The redirect URI is not one this client registered.");

        string Back(string error, string description) => Redirect(request.RedirectUri, ("error", error), ("error_description", description), ("state", request.State));
        if (request.ResponseType != "code") return new(Back("unsupported_response_type", "Only response_type=code is supported."), null);
        var scopes = (request.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid")) return new(Back("invalid_scope", "The scope must include openid."), null);
        if (string.IsNullOrEmpty(request.CodeChallenge) || request.CodeChallengeMethod != "S256") return new(Back("invalid_request", "PKCE is required: a code_challenge with code_challenge_method=S256."), null);
        if (request.CodeChallenge.Length is < 43 or > 128) return new(Back("invalid_request", "The code_challenge is not a SHA-256 hash in base64url."), null);
        if (users.Get(signedInUserId) is not { Enabled: true }) return new(Back("access_denied", "The user cannot sign in."), null);

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO AuthorizationCodes (CodeHash, ClientId, UserId, RedirectUri, CodeChallenge, Nonce, Scope, ExpiresAtUtc)
                VALUES ($hash, $client, $user, $redirect, $challenge, $nonce, $scope, $expires);
                """);
            cmd.Bind(database, "hash", Hash(code));
            cmd.Bind(database, "client", client.ClientId);
            cmd.Bind(database, "user", signedInUserId);
            cmd.Bind(database, "redirect", request.RedirectUri);
            cmd.Bind(database, "challenge", request.CodeChallenge);
            cmd.Bind(database, "nonce", (object?)request.Nonce ?? DBNull.Value);
            cmd.Bind(database, "scope", string.Join(' ', scopes.Where(KnownScopes.Contains)));
            cmd.Bind(database, "expires", (_clock.GetUtcNow() + CodeLifetime).ToString("O"));
            cmd.ExecuteNonQuery();
        });
        return new(Redirect(request.RedirectUri, ("code", code), ("state", request.State)), null);
    }

    /// <summary>Trades a code for tokens. Anything wrong is an <see cref="OAuthException"/>; a code works once, whatever happens after it is presented.</summary>
    public TokenResponse Exchange(TokenRequest request)
    {
        if (request.GrantType != "authorization_code") throw new OAuthException("unsupported_grant_type", "Only authorization_code is supported.");
        if (request.ClientId is null || !clients.Authenticates(request.ClientId, request.ClientSecret)) throw new OAuthException("invalid_client", "The client could not be authenticated.", 401);
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.CodeVerifier)) throw new OAuthException("invalid_request", "code and code_verifier are required.");

        var grant = Consume(Hash(request.Code)) ?? throw new OAuthException("invalid_grant", "The code is not valid.");
        if (grant.ClientId != request.ClientId || grant.RedirectUri != request.RedirectUri) throw new OAuthException("invalid_grant", "The code was not issued to this client and redirect URI.");
        if (grant.ExpiresAt < _clock.GetUtcNow()) throw new OAuthException("invalid_grant", "The code has expired.");
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(grant.CodeChallenge))) throw new OAuthException("invalid_grant", "The code_verifier does not match the code_challenge.");
        var user = users.Get(grant.UserId);
        if (user is not { Enabled: true }) throw new OAuthException("invalid_grant", "The user cannot sign in.");

        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var scopes = grant.Scope.Split(' ');
        var claims = new Dictionary<string, object?> { ["iss"] = Issuer, ["sub"] = user.Id, ["aud"] = request.ClientId, ["iat"] = now, ["exp"] = now + (long)TokenLifetime.TotalSeconds };
        if (grant.Nonce is not null) claims["nonce"] = grant.Nonce;
        if (scopes.Contains("profile")) claims["name"] = user.DisplayName;
        if (scopes.Contains("email") && user.Email is not null)
        {
            claims["email"] = user.Email;
            // Only an address an administrator entered (or the domain gave) is vouched for; one the person typed themselves is not, and a relying party must not link accounts on it (plan 13a U16).
            claims["email_verified"] = user.EmailVerified;
        }
        claims["groups"] = directory.GroupsOf(user.Id).Select(g => g.Name).ToList();
        claims["dbdata_roles"] = directory.AllRoles(user.Id);
        var access = new Dictionary<string, object?> { ["iss"] = Issuer, ["sub"] = user.Id, ["aud"] = Issuer, ["client_id"] = request.ClientId, ["scope"] = grant.Scope, ["iat"] = now, ["exp"] = now + (long)TokenLifetime.TotalSeconds };
        return new(Sign(access, "at+jwt"), Sign(claims, "JWT"), (int)TokenLifetime.TotalSeconds, grant.Scope);
    }

    /// <summary>The claims of a userinfo request (the access token), or null when the token is not one this home issued or has expired.</summary>
    public IReadOnlyDictionary<string, object?>? UserInfo(string? accessToken)
    {
        var claims = Verify(accessToken, "at+jwt");
        if (claims is null || claims.GetValueOrDefault("aud") as string != Issuer || users.Get((string)claims["sub"]!) is not { Enabled: true } user) return null;
        var scopes = ((string?)claims.GetValueOrDefault("scope") ?? "").Split(' ');
        var info = new Dictionary<string, object?> { ["sub"] = user.Id };
        if (scopes.Contains("profile")) info["name"] = user.DisplayName;
        if (scopes.Contains("email") && user.Email is not null)
        {
            info["email"] = user.Email;
            info["email_verified"] = user.EmailVerified;
        }
        info["groups"] = directory.GroupsOf(user.Id).Select(g => g.Name).ToList();
        info["dbdata_roles"] = directory.AllRoles(user.Id);
        return info;
    }

    /// <summary>The claims of a token this home signed, when its signature, issuer, type and expiry are right; otherwise null.</summary>
    public IReadOnlyDictionary<string, object?>? Verify(string? jwt, string expectedType)
    {
        var parts = (jwt ?? "").Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64Url.DecodeFromChars(parts[0]))!;
            if (header["alg"].GetString() != "ES256" || header["typ"].GetString() != expectedType) return null;
            var key = keys.Find(header["kid"].GetString() ?? "");
            if (key is null || !key.Private.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64Url.DecodeFromChars(parts[1]))!;
            if (raw["iss"].GetString() != Issuer || raw["exp"].GetInt64() <= _clock.GetUtcNow().ToUnixTimeSeconds()) return null;
            return raw.ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? (object?)p.Value.GetString() : p.Value.Clone());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return null; }
    }

    private string Sign(Dictionary<string, object?> claims, string type)
    {
        var key = keys.Current();
        var header = new Dictionary<string, object?> { ["alg"] = "ES256", ["typ"] = type, ["kid"] = key.Kid };
        var input = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
        return input + "." + Base64Url.EncodeToString(key.Private.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private sealed record Grant(string ClientId, string UserId, string RedirectUri, string CodeChallenge, string? Nonce, string Scope, DateTimeOffset ExpiresAt);

    /// <summary>Reads a code and marks it used in one statement-pair, so two requests with the same code cannot both succeed.</summary>
    private Grant? Consume(string codeHash) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using (var mark = database.Command(connection, "UPDATE AuthorizationCodes SET UsedAtUtc = $at WHERE CodeHash = $hash AND UsedAtUtc IS NULL;"))
        {
            mark.Bind(database, "at", _clock.GetUtcNow().ToString("O"));
            mark.Bind(database, "hash", codeHash);
            if (mark.ExecuteNonQuery() != 1) return null;
        }
        using var cmd = database.Command(connection, "SELECT ClientId, UserId, RedirectUri, CodeChallenge, Nonce, Scope, ExpiresAtUtc FROM AuthorizationCodes WHERE CodeHash = $hash;");
        cmd.Bind(database, "hash", codeHash);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Grant(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)))
            : null;
    });

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    private static string Redirect(string uri, params (string Key, string? Value)[] query)
    {
        var separator = uri.Contains('?') ? '&' : '?';
        return uri + separator + string.Join('&', query.Where(q => q.Value is not null).Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value!)}"));
    }
}
