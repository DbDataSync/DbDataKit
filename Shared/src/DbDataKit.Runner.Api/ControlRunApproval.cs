using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.Web.Trust;

namespace DbDataKit.Runner.Api;

/// <summary>What a person approved, and which run it is for: the task, the version it runs (null: the one the channel serves), the exact inputs and the digest of the statements the person was shown.</summary>
/// <param name="Audience">The Runner's instance id: an approval is for one Runner.</param>
/// <param name="Task">The task the run starts.</param>
/// <param name="Version">The version of the package the run is pinned to, or null for the served one.</param>
/// <param name="Inputs">The inputs of the run, all of them: a plan that travels as an input is part of what was approved.</param>
/// <param name="StatementsSha256">The SHA-256 of the exact text of the statements the person approved, as 64 hexadecimal digits.</param>
/// <param name="ApproverId">The signed-in person's id.</param>
/// <param name="ApproverName">The person's name, for the audit.</param>
public sealed record ControlRunRequest(string Audience, TaskRef Task, string? Version, IReadOnlyDictionary<string, string> Inputs, string StatementsSha256, string ApproverId, string? ApproverName);

/// <summary>Why a Runner did not accept the control service's approval of a run (for the audit; the caller is told it was refused).</summary>
public enum ControlApprovalFailure
{
    Missing,
    Malformed,
    UnknownIssuer,
    NotAConsole,
    NotTheCaller,
    BadSignature,
    WrongAudience,
    Expired,
    LifetimeTooLong,
    WrongRun,
    Replayed,
}

/// <summary>The outcome of checking an approval: the person and the statements' digest when it holds, the failure and a sentence when it does not.</summary>
public sealed record ControlApprovalResult(ControlApprovalFailure? Failure, string Detail, ControlApprovalRecord? Approval)
{
    public bool Approved => Failure is null;

    public static ControlApprovalResult Refused(ControlApprovalFailure failure, string detail) => new(failure, detail, null);

    public static ControlApprovalResult Success(ControlApprovalRecord approval) => new(null, "", approval);
}

/// <summary>
/// The control service's request to run a DDL task (a task with the capability <see cref="TaskCapabilities.Ddl"/>): <c>base64url(header).base64url(claims).base64url(signature)</c> with the header
/// <c>{"alg":"EdDSA","typ":"dbdata-control-run","kid":&lt;fingerprint&gt;}</c>, signed with the console's key, the one its management tokens are signed with, so a Runner checks it against the console it is paired with and needs no other
/// credential. The claims: <c>iss</c> the console, <c>aud</c> the Runner's instance id, <c>task</c> (<c>package/channel/task</c>), <c>version</c> (empty for the served one), <c>inputs</c> (the digest of every input, <see cref="InputsDigest"/>),
/// <c>statements</c> (the digest of the exact statements the person approved), <c>sub</c> and <c>name</c> (the person), <c>jti</c>, <c>iat</c> and <c>exp</c>. It is carried in the header <see cref="Header"/> of the call that starts the run and
/// is good for one run of exactly that task, version and inputs within two minutes.
/// </summary>
public static class ControlRunApprovals
{
    public const string Header = "X-DbDataKit-Control-Approval";

    public const string TokenType = "dbdata-control-run";

    public const int LifetimeSeconds = 120;

    /// <summary>The longest an approval may live from issue to expiry, whatever its console wrote.</summary>
    public const int MaximumLifetimeSeconds = 300;

    /// <summary>The clock difference between a console and a Runner that is forgiven.</summary>
    public const int SkewSeconds = 30;

    /// <summary>An approval longer than this is refused unread.</summary>
    public const int MaximumLength = 4096;

    /// <summary>The SHA-256 of the exact text <paramref name="statements"/>, as the approval carries it.</summary>
    public static string StatementsDigest(string statements) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(statements))).ToLowerInvariant();

    /// <summary>
    /// The SHA-256 of the inputs, independent of their order: each name and value as length-prefixed UTF-8, names in ordinal order. A run started with an input the person did not see has another digest.
    /// </summary>
    public static string InputsDigest(IReadOnlyDictionary<string, string> inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, value) in inputs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Append(hash, name);
            Append(hash, value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        static void Append(IncrementalHash hash, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
    }

    /// <summary>Signs <paramref name="request"/> as the console <paramref name="consoleId"/>; valid for <see cref="LifetimeSeconds"/> from <paramref name="clock"/>'s now.</summary>
    public static string Sign(string consoleId, ConsoleSigningKey key, ControlRunRequest request, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Audience);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApproverId);
        if (request.StatementsSha256.Length != 64 || !request.StatementsSha256.All(Uri.IsHexDigit)) throw new ArgumentException("The statements' digest is 64 hexadecimal digits.", nameof(request));
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = consoleId,
            ["aud"] = request.Audience,
            ["task"] = request.Task.ToString(),
            ["version"] = request.Version ?? "",
            ["inputs"] = InputsDigest(request.Inputs),
            ["statements"] = request.StatementsSha256.ToLowerInvariant(),
            ["sub"] = request.ApproverId,
            ["name"] = request.ApproverName,
            ["jti"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ["iat"] = now,
            ["exp"] = now + LifetimeSeconds,
        };
        var header = new Dictionary<string, object?> { ["alg"] = "EdDSA", ["typ"] = TokenType, ["kid"] = key.Fingerprint };
        var signingInput = B64.Encode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + B64.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return signingInput + "." + B64.Encode(key.Sign(Encoding.ASCII.GetBytes(signingInput)));
    }
}

/// <summary>
/// What a Runner does with the approval a run of a DDL task carries: parse it, find the console that issued it among its paired clients (a console, never another kind of client), check the signature against that console's key, that the console is
/// the one that made the call, the audience, the times and the lifetime, that it names exactly this task, version and inputs, and last that it has not been used before. Nothing is remembered before the signature has been verified.
/// </summary>
public sealed class ControlRunApprovalVerifier(string instanceId, IPairedClients clients, IReplayCache replay, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Checks <paramref name="approval"/> (the value of the header) for the run <paramref name="task"/> of <paramref name="version"/> with <paramref name="inputs"/>, called with <paramref name="caller"/>'s token.</summary>
    public ControlApprovalResult Verify(string? approval, ValidatedToken caller, TaskRef task, string? version, IReadOnlyDictionary<string, string> inputs)
    {
        if (string.IsNullOrEmpty(approval)) return ControlApprovalResult.Refused(ControlApprovalFailure.Missing, "the call carries no approval from the control service");
        if (approval.Length > ControlRunApprovals.MaximumLength) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "approval too long");
        var parts = approval.Split('.');
        if (parts.Length != 3) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "not three parts");
        var (headerBytes, claimBytes, signature) = (B64.Decode(parts[0]), B64.Decode(parts[1]), B64.Decode(parts[2]));
        if (headerBytes is null || claimBytes is null || signature is null) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "a part is not base64url");

        JsonDocument header, claims;
        try { header = JsonDocument.Parse(headerBytes); claims = JsonDocument.Parse(claimBytes); }
        catch (JsonException) { return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "a part is not JSON"); }
        using (header)
        using (claims)
        {
            if (header.RootElement.ValueKind != JsonValueKind.Object || claims.RootElement.ValueKind != JsonValueKind.Object)
                return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "a part is not an object");
            if (Str(header.RootElement, "alg") != "EdDSA" || Str(header.RootElement, "typ") != ControlRunApprovals.TokenType)
                return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "not an EdDSA control approval");

            var root = claims.RootElement;
            if (Str(root, "iss") is not { } issuer) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "iss");
            if (clients.Find(issuer) is not { } console) return ControlApprovalResult.Refused(ControlApprovalFailure.UnknownIssuer, $"'{issuer}' is not paired");
            if (console.Kind != ClientKind.Console) return ControlApprovalResult.Refused(ControlApprovalFailure.NotAConsole, $"'{console.Name}' is a paired client but not a console");
            if (!ConsoleSigningKey.Verify(console.PublicKey, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature))
                return ControlApprovalResult.Refused(ControlApprovalFailure.BadSignature, $"the signature does not match the key of console '{issuer}'");

            // from here on the approval is genuinely the console's; the rest is whether it is for this call
            if (!string.Equals(caller.Client.Id, console.Id, StringComparison.Ordinal))
                return ControlApprovalResult.Refused(ControlApprovalFailure.NotTheCaller, $"the call was made by '{caller.Client.Name}', the approval by '{console.Name}'");
            if (!string.Equals(Str(root, "aud"), instanceId, StringComparison.Ordinal)) return ControlApprovalResult.Refused(ControlApprovalFailure.WrongAudience, $"approval is for '{Str(root, "aud")}'");
            if (!Seconds(root, "iat", out var issuedAt) || !Seconds(root, "exp", out var expires)) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "iat or exp");
            var now = _clock.GetUtcNow();
            if (expires.AddSeconds(ControlRunApprovals.SkewSeconds) <= now) return ControlApprovalResult.Refused(ControlApprovalFailure.Expired, $"expired at {expires:O}");
            if (issuedAt.AddSeconds(-ControlRunApprovals.SkewSeconds) > now) return ControlApprovalResult.Refused(ControlApprovalFailure.Expired, $"issued at {issuedAt:O}");
            if (expires <= issuedAt || (expires - issuedAt).TotalSeconds > ControlRunApprovals.MaximumLifetimeSeconds) return ControlApprovalResult.Refused(ControlApprovalFailure.LifetimeTooLong, "lifetime beyond the maximum");

            if (!string.Equals(Str(root, "task"), task.ToString(), StringComparison.Ordinal)) return ControlApprovalResult.Refused(ControlApprovalFailure.WrongRun, $"approval is for the task '{Str(root, "task")}'");
            if (!string.Equals(Str(root, "version") ?? "", version ?? "", StringComparison.Ordinal)) return ControlApprovalResult.Refused(ControlApprovalFailure.WrongRun, "approval is for another version");
            if (!string.Equals(Str(root, "inputs"), ControlRunApprovals.InputsDigest(inputs), StringComparison.Ordinal)) return ControlApprovalResult.Refused(ControlApprovalFailure.WrongRun, "the inputs are not the ones that were approved");
            if (Str(root, "statements") is not { Length: 64 } statements || !statements.All(Uri.IsHexDigit)) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "statements");
            if (Str(root, "sub") is not { } approver) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "sub");
            if (Str(root, "jti") is not { Length: >= 8 and <= 64 } jti) return ControlApprovalResult.Refused(ControlApprovalFailure.Malformed, "jti");

            if (!replay.TryAdd(console.Id, "control-run:" + jti, expires.AddSeconds(ControlRunApprovals.SkewSeconds)))
                return ControlApprovalResult.Refused(ControlApprovalFailure.Replayed, $"approval {jti} was already used");
            return ControlApprovalResult.Success(new ControlApprovalRecord(Str(root, "name") is { } name ? $"{name} ({approver})" : approver, statements));
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text : null;

    private static bool Seconds(JsonElement element, string name, out DateTimeOffset when)
    {
        when = default;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) return false;
        if (seconds is < 0 or > 253_402_300_799) return false;
        when = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}

/// <summary>Unpadded URL-safe base64 for the parts of an approval.</summary>
file static class B64
{
    public static string Encode(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    /// <summary>The bytes of <paramref name="text"/>, or null when it is not unpadded URL-safe base64.</summary>
    public static byte[]? Decode(string text)
    {
        try { return System.Buffers.Text.Base64Url.DecodeFromChars(text); }
        catch (FormatException) { return null; }
    }
}
