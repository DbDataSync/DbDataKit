using System.Text;
using System.Text.Json;

namespace DbDataKit.Web.Trust;

/// <summary>
/// What a server does with the token on every call: parse it, check the algorithm is EdDSA (and nothing else), find the console that issued it among its paired consoles, check the signature against that
/// console's key, then the audience (this server's instance id), the times, the lifetime, the console's ceiling and, last, that the token id has not been used before. Each refusal has a
/// <see cref="TokenFailure"/> for the server's log; the caller is only told it was refused. Nothing is cached before the signature has been verified, so garbage cannot fill the replay cache.
/// </summary>
public sealed class ConsoleTokenValidator(string instanceId, IPairedConsoles consoles, IReplayCache replay, TimeProvider? clock = null)
{
    /// <summary>The longest a token may live from issue to expiry, whatever its console wrote.</summary>
    public const int MaximumLifetimeSeconds = 600;

    /// <summary>The clock difference between a console and a server that is forgiven.</summary>
    public const int SkewSeconds = 30;

    /// <summary>A token longer than this is refused unread (a user with many project roles still fits many times over).</summary>
    public const int MaximumTokenLength = 8192;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public TokenResult Validate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return TokenResult.Refused(TokenFailure.Malformed, "no token");
        if (token.Length > MaximumTokenLength) return TokenResult.Refused(TokenFailure.Malformed, "token too long");
        var parts = token.Split('.');
        if (parts.Length != 3) return TokenResult.Refused(TokenFailure.Malformed, "not three parts");
        var (headerBytes, claimBytes, signature) = (Base64Url.Decode(parts[0]), Base64Url.Decode(parts[1]), Base64Url.Decode(parts[2]));
        if (headerBytes is null || claimBytes is null || signature is null) return TokenResult.Refused(TokenFailure.Malformed, "a part is not base64url");

        JsonDocument header, claims;
        try { header = JsonDocument.Parse(headerBytes); claims = JsonDocument.Parse(claimBytes); }
        catch (JsonException) { return TokenResult.Refused(TokenFailure.Malformed, "a part is not JSON"); }
        using (header)
        using (claims)
        {
            if (header.RootElement.ValueKind != JsonValueKind.Object || claims.RootElement.ValueKind != JsonValueKind.Object)
                return TokenResult.Refused(TokenFailure.Malformed, "a part is not an object");
            var alg = Str(header.RootElement, "alg");
            if (alg != "EdDSA") return TokenResult.Refused(TokenFailure.UnsupportedAlgorithm, $"algorithm '{alg}'");

            var root = claims.RootElement;
            var issuer = Str(root, "iss");
            if (issuer is null) return TokenResult.Refused(TokenFailure.MissingClaim, "iss");
            var console = consoles.Find(issuer);
            if (console is null) return TokenResult.Refused(TokenFailure.UnknownIssuer, $"console '{issuer}' is not paired");

            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!ConsoleSigningKey.Verify(console.PublicKey, signingInput, signature))
                return TokenResult.Refused(TokenFailure.BadSignature, $"signature does not match the key of console '{issuer}'");

            // from here on the token is genuinely the console's; the rest is whether it is acceptable
            var (audience, subject, jti) = (Str(root, "aud"), Str(root, "sub"), Str(root, "jti"));
            if (audience is null) return TokenResult.Refused(TokenFailure.MissingClaim, "aud");
            if (subject is null) return TokenResult.Refused(TokenFailure.MissingClaim, "sub");
            if (jti is null || jti.Length is < 8 or > 64) return TokenResult.Refused(TokenFailure.MissingClaim, "jti");
            if (!Seconds(root, "iat", out var issuedAt)) return TokenResult.Refused(TokenFailure.MissingClaim, "iat");
            if (!Seconds(root, "exp", out var expires)) return TokenResult.Refused(TokenFailure.MissingClaim, "exp");

            if (!string.Equals(audience, instanceId, StringComparison.Ordinal)) return TokenResult.Refused(TokenFailure.WrongAudience, $"token is for '{audience}'");

            var now = _clock.GetUtcNow();
            if (expires.AddSeconds(SkewSeconds) <= now) return TokenResult.Refused(TokenFailure.Expired, $"expired at {expires:O}");
            if (issuedAt.AddSeconds(-SkewSeconds) > now) return TokenResult.Refused(TokenFailure.NotYetValid, $"issued at {issuedAt:O}");
            if (expires <= issuedAt || (expires - issuedAt).TotalSeconds > MaximumLifetimeSeconds) return TokenResult.Refused(TokenFailure.LifetimeTooLong, "lifetime beyond the maximum");

            if (!Roles(root, "roles", required: true, out var roles)) return TokenResult.Refused(TokenFailure.MissingClaim, "roles");
            var projects = new Dictionary<string, ServerRole>(StringComparer.Ordinal);
            if (root.TryGetProperty("projects", out var projectElement))
            {
                if (projectElement.ValueKind != JsonValueKind.Object) return TokenResult.Refused(TokenFailure.MissingClaim, "projects");
                foreach (var project in projectElement.EnumerateObject())
                {
                    if (!Roles(projectElement, project.Name, required: true, out var projectRoles)) return TokenResult.Refused(TokenFailure.MissingClaim, "projects");
                    projects[project.Name] = projectRoles;
                }
            }

            var above = ServerRoles.Above(roles, console.Ceiling);
            foreach (var projectRoles in projects.Values) above |= ServerRoles.Above(projectRoles, console.Ceiling);
            if (above != ServerRole.None)
                return TokenResult.Refused(TokenFailure.AboveCeiling, $"console '{console.Name}' may grant {string.Join(",", ServerRoles.Names(console.Ceiling))} here, the token claims {string.Join(",", ServerRoles.Names(above))} beyond that");

            if (!replay.TryAdd(console.Id, jti, expires.AddSeconds(SkewSeconds)))
                return TokenResult.Refused(TokenFailure.Replayed, $"token {jti} was already used");

            var isConsole = Str(root, "kind") == "console";
            return TokenResult.Success(new ValidatedToken(console, subject, Str(root, "name"), isConsole, ServerRoles.Close(roles), projects, jti, issuedAt, expires));
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool Seconds(JsonElement element, string name, out DateTimeOffset when)
    {
        when = default;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) return false;
        if (seconds is < 0 or > 253_402_300_799) return false;
        when = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private static bool Roles(JsonElement element, string name, bool required, out ServerRole roles)
    {
        roles = ServerRole.None;
        if (!element.TryGetProperty(name, out var value)) return !required;
        if (value.ValueKind != JsonValueKind.Array) return false;
        var names = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            names.Add(item.GetString()!);
        }
        return ServerRoles.TryParse(names, out roles);
    }
}
