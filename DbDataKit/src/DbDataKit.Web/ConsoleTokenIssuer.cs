using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Web.Trust;

/// <summary>
/// Signs the short-lived token a console sends with every call to a server: <c>base64url(header).base64url(claims).base64url(signature)</c>, the header <c>{"alg":"EdDSA","typ":"dbdata-console","kid":&lt;fingerprint&gt;}</c>.
/// Claims: <c>iss</c> the console, <c>aud</c> the server, <c>sub</c> and <c>name</c>, <c>kind</c> (user or console), <c>roles</c>, <c>projects</c>, <c>jti</c> (unique), <c>iat</c> and <c>exp</c> (seconds).
/// It travels in a header, not a cookie, so it works through reverse proxies.
/// </summary>
public sealed class ConsoleTokenIssuer(string consoleId, ConsoleSigningKey key, TimeProvider? clock = null)
{
    public const int DefaultLifetimeSeconds = 300;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string ConsoleId => consoleId;

    public string Issue(ConsoleTokenRequest request, int lifetimeSeconds = DefaultLifetimeSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Audience);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        if (lifetimeSeconds is < 1 or > ConsoleTokenValidator.MaximumLifetimeSeconds) throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds), $"A token lives 1 to {ConsoleTokenValidator.MaximumLifetimeSeconds} seconds.");

        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = consoleId,
            ["aud"] = request.Audience,
            ["sub"] = request.SubjectId,
            ["name"] = request.SubjectName,
            ["kind"] = request.IsConsole ? "console" : "user",
            ["roles"] = ServerRoles.Names(ServerRoles.Close(request.Roles)),
            ["projects"] = (request.Projects ?? new Dictionary<string, ServerRole>()).ToDictionary(p => p.Key, p => ServerRoles.Names(ServerRoles.Close(p.Value))),
            ["jti"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ["iat"] = now,
            ["exp"] = now + lifetimeSeconds,
        };
        var header = new Dictionary<string, object?> { ["alg"] = "EdDSA", ["typ"] = "dbdata-console", ["kid"] = key.Fingerprint };
        var signingInput = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return signingInput + "." + Base64Url.Encode(key.Sign(Encoding.ASCII.GetBytes(signingInput)));
    }
}
