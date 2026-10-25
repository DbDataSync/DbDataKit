using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Web.Trust;

/// <summary>
/// A client replacing its signing key on a server it is paired with (plan 13, W12): the new public key, signed by the old key (it is the paired client asking) and by the new one (it holds what it registers), over the
/// server's instance id, the client's id, a time and a nonce. The server needs nothing else, and no pairing code.
/// </summary>
public sealed record KeyRotationRequest(string ClientId, byte[] NewPublicKey, long Timestamp, string Nonce, byte[] SignedByOld, byte[] SignedByNew)
{
    public static byte[] Message(string instanceId, string clientId, byte[] newPublicKey, long timestamp, string nonce) =>
        Encoding.UTF8.GetBytes(string.Join('\n', "dbdata-rotate-v1", instanceId, clientId, Base64Url.Encode(newPublicKey), timestamp.ToString(CultureInfo.InvariantCulture), nonce));

    /// <summary>Makes the request for <paramref name="next"/> to replace <paramref name="current"/> on the server <paramref name="instanceId"/>.</summary>
    public static KeyRotationRequest Create(string instanceId, string clientId, ConsoleSigningKey current, ConsoleSigningKey next, DateTimeOffset now)
    {
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var message = Message(instanceId, clientId, next.PublicKey, now.ToUnixTimeSeconds(), nonce);
        return new KeyRotationRequest(clientId, next.PublicKey, now.ToUnixTimeSeconds(), nonce, current.Sign(message), next.Sign(message));
    }
}

public enum RotationFailure { None, UnknownClient, BadShape, Stale, Replayed, BadOldSignature, BadNewSignature }

/// <summary>The outcome of a rotation. <see cref="Detail"/> is for the server's log; the caller is only told it was refused.</summary>
public sealed record RotationResult(RotationFailure Failure, string? Detail = null, PairedClient? Client = null)
{
    public bool Ok => Failure == RotationFailure.None;
}

/// <summary>
/// The server's half: checks a <see cref="KeyRotationRequest"/> and, if it is good, replaces the client's key and nothing else (its name, kind, ceiling and date of pairing stay). Tokens signed by the old key stop
/// being accepted on the next call; tokens signed by the new one are accepted at once. Asking again for the key the server already has, signed by that key, changes nothing and succeeds, so a console that
/// could not tell whether its request arrived can send it again.
/// </summary>
public sealed class KeyRotationService(string instanceId, PairedClientsFile clients, IReplayCache replay, TimeProvider? clock = null)
{
    public const int SkewSeconds = 60;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public RotationResult Rotate(KeyRotationRequest request)
    {
        if (request.NewPublicKey is not { Length: ConsoleSigningKey.PublicKeyLength } || string.IsNullOrEmpty(request.Nonce) || request.Nonce.Length > 64 || string.IsNullOrEmpty(request.ClientId))
            return new RotationResult(RotationFailure.BadShape, "the request is incomplete");
        if (clients.Find(request.ClientId) is not { } client) return new RotationResult(RotationFailure.UnknownClient, $"'{request.ClientId}' is not paired");

        var now = _clock.GetUtcNow();
        if (Math.Abs(now.ToUnixTimeSeconds() - request.Timestamp) > SkewSeconds) return new RotationResult(RotationFailure.Stale, $"the request is from {DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(request.Timestamp, 0, 253_402_300_799)):O}");

        var message = KeyRotationRequest.Message(instanceId, request.ClientId, request.NewPublicKey, request.Timestamp, request.Nonce);
        // the new key may already be the one on record (a request that arrived but whose answer was lost): then the holder of that key is asking for what is already so, and nothing changes
        if (client.PublicKey.AsSpan().SequenceEqual(request.NewPublicKey))
            return ConsoleSigningKey.Verify(request.NewPublicKey, message, request.SignedByNew)
                ? new RotationResult(RotationFailure.None, "already on record", client)
                : new RotationResult(RotationFailure.BadNewSignature, "not signed by the key on record");
        if (!ConsoleSigningKey.Verify(client.PublicKey, message, request.SignedByOld)) return new RotationResult(RotationFailure.BadOldSignature, $"not signed by the key on record for '{client.Id}'");
        if (!ConsoleSigningKey.Verify(request.NewPublicKey, message, request.SignedByNew)) return new RotationResult(RotationFailure.BadNewSignature, "not signed by the new key");
        if (!replay.TryAdd(client.Id, "rotate:" + request.Nonce, now.AddSeconds(2 * SkewSeconds))) return new RotationResult(RotationFailure.Replayed, "the request was already used");

        var rotated = client with { PublicKey = request.NewPublicKey };
        clients.Upsert(rotated);
        return new RotationResult(RotationFailure.None, null, rotated);
    }
}

public static class KeyRotationEndpoint
{
    private const int MaxBody = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed class Body
    {
        public string? ClientId { get; set; }
        public string? NewPublicKey { get; set; }
        public long Timestamp { get; set; }
        public string? Nonce { get; set; }
        public string? SignedByOld { get; set; }
        public string? SignedByNew { get; set; }
    }

    public static IEndpointRouteBuilder MapKeyRotation(this IEndpointRouteBuilder app, KeyRotationService service)
    {
        app.MapPost(ManagementApi.RotationPath, async (HttpContext http) =>
        {
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxBody;
            Body? body;
            try { body = await http.Request.ReadFromJsonAsync<Body>(Json, http.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException) { return Results.Json(new { error = "the request is not valid JSON" }, Json, statusCode: 400); }
            var (newKey, old, @new) = (Decode(body?.NewPublicKey), Decode(body?.SignedByOld), Decode(body?.SignedByNew));
            if (body is null || newKey is null || old is null || @new is null) return Results.Json(new { error = "the request is incomplete" }, Json, statusCode: 400);

            var result = service.Rotate(new KeyRotationRequest(body.ClientId ?? "", newKey, body.Timestamp, body.Nonce ?? "", old, @new));
            if (!result.Ok)
            {
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("DbDataKit.Web.Pairing").LogWarning("A key rotation was refused ({Failure}): {Reason}", result.Failure, result.Detail);
                return Results.Json(new { error = "the key rotation was not accepted" }, Json, statusCode: StatusCodes.Status403Forbidden);
            }
            return Results.Json(new { rotated = true, fingerprint = Fingerprints.Of(newKey) }, Json);
        });
        return app;
    }

    private static byte[]? Decode(string? text) => text is null ? null : Base64Url.Decode(text);
}
