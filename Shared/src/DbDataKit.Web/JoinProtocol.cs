using System.Text;
using System.Text.Json.Nodes;

namespace DbDataKit.Web.Trust;

/// <summary>
/// What a joining server sends the console: who it is, where the console can call it back, and a <see cref="Proof"/> that it holds the token, never the token.
/// </summary>
/// <param name="JoinId">A random id of this attempt; the console's callback names it, so only the process that asked can confirm.</param>
/// <param name="TokenKey">The base64url public key made from the token (<see cref="JoinTokens.VerifierFor"/>): how the console finds the token's record.</param>
/// <param name="Proof">The base64url signature of <see cref="JoinProof.Request"/> with the key made from the token.</param>
/// <param name="Product">The joining product in lower case.</param>
/// <param name="Group">The group the server already belongs to; null when it belongs to none and takes the token's.</param>
/// <param name="InstanceId">The server's instance id.</param>
/// <param name="Name">The name to list the server under; the instance id when null.</param>
/// <param name="Address">The https address the console calls the server at.</param>
/// <param name="CertificateFingerprint">The SHA-256 of the certificate the server presents at <see cref="Address"/> (64 lower-case hex digits), which the console pins.</param>
/// <param name="PublicKey">The server's own Ed25519 public key; it signs the callback's answer.</param>
/// <param name="Generation">The server's compatibility generation (<see cref="SuiteCompatibility"/>).</param>
/// <param name="ProgramVersion">The server's program version.</param>
/// <param name="Listeners">The listen settings the server is configured with, by their name in its settings file (<c>listen</c>, <c>public.listen</c>, <c>management.listen</c> of a
/// Gateway); the console hands the first instance's to every further instance of its group. Null from a server that has none to report.</param>
/// <param name="GroupKey">The key the server's group already protects its values with, when the server holds one and the console may hold none yet (a group that
/// made its own key before it joined a console); the console keeps it as the group's key only when it has none for the group. Null otherwise.</param>
public sealed record JoinRequest(
    string? JoinId, string? TokenKey, string? Proof, string? Product, string? Group, string? InstanceId, string? Name, string? Address,
    string? CertificateFingerprint, string? PublicKey, int? Generation = null, string? ProgramVersion = null,
    Dictionary<string, string[]>? Listeners = null, string? GroupKey = null);

/// <summary>The console's answer to a join it accepted: who the console is, the key its tokens are signed with, and what it may do on the server.</summary>
/// <param name="Ceiling">The role names of the console's ceiling on the server.</param>
/// <param name="Group">The group the server is in now; null for none.</param>
/// <param name="ConsoleId">The console's id, the issuer of its tokens.</param>
/// <param name="ConsoleName">The console's name.</param>
/// <param name="ConsolePublicKey">The base64url Ed25519 key the console's tokens are signed with.</param>
/// <param name="InstanceId">The server's instance id, as the console recorded it.</param>
/// <param name="GroupKey">The key of the server's group (base64 of 32 bytes), which the server keeps in its own secret store; null when its product keeps no group key.</param>
/// <param name="Listeners">The listen settings of the group's first instance, for a further instance of the group to listen on the same ports; null otherwise.</param>
/// <param name="Link">The link of the server's group with its partner group, when the console made one; null otherwise.</param>
public sealed record JoinAnswer(string ConsoleId, string ConsoleName, string ConsolePublicKey, string[] Ceiling, string? Group, string InstanceId,
    string? GroupKey = null, Dictionary<string, string[]>? Listeners = null, JsonObject? Link = null);

/// <summary>The console's callback to the joining server, made before the token is spent.</summary>
public sealed record JoinCallbackRequest(string? JoinId, string? ConsoleId, string? ConsoleThumbprint);

/// <summary>The joining server's answer to the callback: <see cref="Signature"/> is <see cref="JoinProof.Callback"/> signed with its own key.</summary>
public sealed record JoinCallbackAnswer(string? JoinId, string? Signature);

/// <summary>
/// The texts the two signatures of a join cover. The request's covers the console's certificate as the joining server pinned it, so a proof made for one
/// console is refused by another; the callback's covers the address and the certificate the console reached, so only the process that sent the join
/// can confirm it.
/// </summary>
public static class JoinProof
{
    public const string Version = "dbdata-join-v1";

    public static byte[] Request(string consoleFingerprint, JoinRequest request) => Text(
        Version + "-request", consoleFingerprint, request.JoinId, request.Product, request.Group ?? "", request.InstanceId, request.Name ?? "",
        request.Address, request.CertificateFingerprint, request.PublicKey);

    public static byte[] Callback(string joinId, string consoleId, string consoleFingerprint, string address, string certificateFingerprint) =>
        Text(Version + "-callback", joinId, consoleId, consoleFingerprint, address, certificateFingerprint);

    private static byte[] Text(params string?[] parts) => Encoding.UTF8.GetBytes(string.Join('\n', parts.Select(p => p ?? "")));
}

/// <summary>A certificate thumbprint as the join command writes it: <c>sha256:</c> and 64 hex digits.</summary>
public static class Thumbprints
{
    public const string Prefix = "sha256:";

    /// <summary>The 64 lower-case hex digits of <paramref name="text"/>, or null when it is not a thumbprint.</summary>
    public static string? Parse(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = trimmed[Prefix.Length..].Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    public static string Format(string fingerprint) => Prefix + fingerprint.ToLowerInvariant();
}
