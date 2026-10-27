using System.Text.Json.Nodes;

namespace DbDataKit.Web.Trust;

/// <summary>
/// <c>POST /api/v1/pairing-codes</c> (plan 13, T11): an administrator, through a console, asks the server for a single-use code that lets <b>another server</b> pair with it as a publisher, so the console can broker the pairing of two of its servers without
/// anyone typing a code. The code carries the publisher ceiling and nothing else; a code for a console or a wider ceiling is still made on the server's own machine with <c>server pair</c>, which is the proof that the person owns it.
/// The body is optional: <c>{"name": "label", "minutes": 10}</c> (1 to 60 minutes).
/// </summary>
public static class PairingCodeIssuer
{
    public const string Path = "/api/v1/pairing-codes";
    public const int MaxMinutes = 60;

    /// <summary>The answer for the body of a request from a caller who holds <paramref name="allowed"/>: <c>{code, expires, max_role}</c>, or why not.</summary>
    public static (int Status, JsonObject Body) Issue(PairingCodes codes, bool allowed, string? body)
    {
        if (!allowed) return (403, new JsonObject { ["error"] = "This needs the admin role." });
        string? name = null;
        var lifetime = PairingCodes.DefaultLifetime;
        if (!string.IsNullOrWhiteSpace(body))
        {
            JsonObject? parsed;
            try { parsed = JsonNode.Parse(body) as JsonObject; }
            catch (System.Text.Json.JsonException) { parsed = null; }
            if (parsed is null) return (400, new JsonObject { ["error"] = "The body is a JSON object, such as {\"name\": \"build\", \"minutes\": 10}." });
            name = parsed["name"]?.GetValue<string>();
            if (name is { } n && (n.Length > 100 || n.Any(char.IsControl))) return (400, new JsonObject { ["error"] = "The name is too long or has control characters." });
            if (parsed["minutes"] is { } m)
            {
                if (m is not JsonValue v || !v.TryGetValue<int>(out var minutes) || minutes is < 1 or > MaxMinutes) return (400, new JsonObject { ["error"] = $"minutes is a whole number from 1 to {MaxMinutes}." });
                lifetime = TimeSpan.FromMinutes(minutes);
            }
        }
        var code = codes.Create(ServerRole.Publisher, name, lifetime);
        return (200, new JsonObject
        {
            ["code"] = code.Code,
            ["expires"] = code.ExpiresAt.UtcDateTime.ToString("O"),
            ["max_role"] = new JsonArray([.. ServerRoles.Names(ServerRole.Publisher).Select(r => (JsonNode)r)]),
        });
    }
}
