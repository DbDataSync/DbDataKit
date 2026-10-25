using System.Text.RegularExpressions;

namespace DbDataKit.Web.Trust;

/// <summary>What a client sends to be paired: the code, who it is, and the public half of its token-signing key.</summary>
public sealed record PairingRequest(string? Code, string? ClientId, string? Name, ClientKind Kind, byte[]? PublicKey);

/// <summary>The result of a pairing. On success the client has been added; the caller is given the server's instance id and certificate fingerprint to pin. A refusal says nothing about why a code failed.</summary>
public sealed record PairingOutcome(PairedClient? Client, string InstanceId, string CertificateFingerprint, string? Refusal)
{
    public bool Ok => Client is not null;
}

/// <summary>
/// The server's half of the pairing exchange (plan 13, W12, steps 3 and 4): checks the code, then records the client with the ceiling the code carried. Pairing a client id that is already paired
/// replaces its entry (the client has a new key); nothing else about an existing pairing changes without <c>unpair</c>.
/// </summary>
public sealed partial class PairingService(string instanceId, string certificateFingerprint, PairedClientsFile clients, PairingCodes codes, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public PairingOutcome Pair(PairingRequest request)
    {
        PairingOutcome Refuse(string why) => new(null, instanceId, certificateFingerprint, why);

        // The shape is checked before the code is spent, so a malformed request does not use up a good code.
        if (request.ClientId is not { } id || !IdPattern().IsMatch(id)) return Refuse("the client id is missing or not valid (letters, digits and . _ : -, up to 64)");
        if (request.PublicKey is not { Length: ConsoleSigningKey.PublicKeyLength } key) return Refuse("the public key must be a 32-byte Ed25519 key");
        if (!Enum.IsDefined(request.Kind)) return Refuse("the client kind is not one of console, server, cli");
        if (request.Name is { } name && (name.Length > 100 || name.Any(char.IsControl))) return Refuse("the name is too long or has control characters");

        var grant = codes.Redeem(request.Code);
        if (grant is null) return Refuse("the pairing code was not accepted");

        var label = string.IsNullOrWhiteSpace(grant.Label) ? (string.IsNullOrWhiteSpace(request.Name) ? id : request.Name) : grant.Label;
        var client = new PairedClient(id, label, key, grant.Ceiling, request.Kind, _clock.GetUtcNow());
        clients.Upsert(client);
        return new PairingOutcome(client, instanceId, certificateFingerprint, null);
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex IdPattern();
}
