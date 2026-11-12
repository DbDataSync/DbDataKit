using System.Security.Cryptography;
using System.Text;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>
/// What a publisher signs and how (plan 30 decision 5): the message is <c>dbdatakit-definition/1</c>, the kind, the name, the SHA-256 of the manifest and the path and SHA-256 of each file in path order, one per line, so a changed manifest,
/// file, kind or name is a different message. The signature text is <c>ed25519:&lt;key id&gt;:&lt;base64url signature&gt;</c>; the key is the Ed25519 key of a <see cref="ConsoleSigningKey"/> (the one a client pairs with, as the update system signs releases).
/// </summary>
public static class DefinitionSigning
{
    public const string Scheme = "ed25519";

    public static byte[] Message(string kind, string name, string manifest, IEnumerable<ArtifactRef> artifacts)
    {
        var text = new StringBuilder("dbdatakit-definition/1\n").Append(kind).Append('\n').Append(name).Append('\n').Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)))).Append('\n');
        foreach (var file in artifacts.OrderBy(a => a.Path, StringComparer.Ordinal)) text.Append(file.Path).Append('\t').Append(file.Sha256.ToLowerInvariant()).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public static string Sign(ConsoleSigningKey key, string kind, string name, string manifest, IEnumerable<ArtifactRef> artifacts) =>
        $"{Scheme}:{key.Fingerprint}:{Url.Encode(key.Sign(Message(kind, name, manifest, artifacts)))}";
}

/// <summary>Verifies a version's signature against the group's trust store: the key must be one the kind trusts and has not revoked, and the signature must hold over the message.</summary>
public sealed class TrustStoreVerifier(DefinitionTrustStore trust) : IVersionSignatureVerifier
{
    public SignatureVerdict Verify(VersionSignatureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Signature)) return new SignatureVerdict(SignatureState.Unsigned);
        var parts = request.Signature.Split(':');
        if (parts.Length != 3 || parts[0] != DefinitionSigning.Scheme) return new SignatureVerdict(SignatureState.Invalid, null, $"the signature is not written ed25519:<key id>:<signature>");
        var key = trust.Find(request.Kind, parts[1]);
        if (key is null) return new SignatureVerdict(SignatureState.Invalid, parts[1], $"the key {parts[1]} is not one the group trusts for {request.Kind}");
        if (key.Revoked) return new SignatureVerdict(SignatureState.Invalid, parts[1], $"the key {parts[1]} was revoked for {request.Kind}");
        var publicKey = Url.Decode(key.PublicKey);
        var signature = Url.Decode(parts[2]);
        if (publicKey is null || signature is null || !ConsoleSigningKey.Verify(publicKey, DefinitionSigning.Message(request.Kind, request.Name, request.Manifest, request.Artifacts), signature))
            return new SignatureVerdict(SignatureState.Invalid, parts[1], $"the signature does not hold over the {request.Kind} '{request.Name}' (the manifest or a file changed after it was signed)");
        return new SignatureVerdict(SignatureState.Verified, parts[1]);
    }
}

/// <summary>
/// Whether a kind needs signed versions in a group (plan 30 PB03, default taken: owner delegated 2026-11-09): required for every kind unless an administrator set it off for the kind, or marked the group <c>test</c>.
/// Kept in the group's settings (<c>signatures/&lt;kind&gt;</c> is <c>required</c> or <c>optional</c>; <c>group/mode</c> is <c>test</c> for a test group), so every instance reads the same.
/// </summary>
public sealed class SignaturePolicy(IGroupSettings settings)
{
    public bool Required(string kind) => settings.Get($"signatures/{kind}") switch { "required" => true, "optional" => false, _ => !IsTestGroup };

    public bool IsTestGroup => settings.Get("group/mode") == "test";

    public void Set(string kind, bool required) => Put($"signatures/{kind}", required ? "required" : "optional");

    public void MarkTestGroup(bool test) => Put("group/mode", test ? "test" : "production");

    private void Put(string name, string value)
    {
        var current = settings.Get(name);
        if (current is null) settings.Put(name, value);
        else if (current != value) settings.Put(name, value, current);
    }
}

/// <summary>Unpadded URL-safe base64, as the keys and signatures are written.</summary>
public static class Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? Decode(string text)
    {
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 0 => padded, 2 => padded + "==", 3 => padded + "=", _ => null! };
        if (padded is null) return null;
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return null; }
    }
}
