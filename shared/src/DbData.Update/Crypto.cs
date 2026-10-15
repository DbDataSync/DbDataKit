using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DbData.Update;

public enum VerificationFailure
{
    MalformedSignature, UnknownKey, BadSignature, MalformedManifest, HashMismatch, SizeMismatch,
    UntrustedUrl, WrongProduct, VersionMismatch, UnsafeArchive, KeyRotationRejected, Download,
}

public sealed class UpdateVerificationException(VerificationFailure failure, string message) : Exception(message)
{
    public VerificationFailure Failure { get; } = failure;
}

/// <summary>Raw ed25519 primitives (RFC 8032) via BouncyCastle's managed implementation. .NET 10's BCL has no ed25519.</summary>
public static class Ed25519
{
    public const int KeySize = 32, SignatureSize = 64;

    public static (byte[] Seed, byte[] PublicKey) GenerateKeyPair()
    {
        var priv = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (priv.GetEncoded(), priv.GeneratePublicKey().GetEncoded());
    }

    public static byte[] PublicKeyFromSeed(byte[] seed)
        => new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();

    public static byte[] Sign(byte[] seed, ReadOnlySpan<byte> data)
    {
        if (seed.Length != KeySize) throw new ArgumentException("Seed must be 32 bytes.", nameof(seed));
        var s = new Ed25519Signer();
        s.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        var d = data.ToArray();
        s.BlockUpdate(d, 0, d.Length);
        return s.GenerateSignature();
    }

    public static bool Verify(byte[] publicKey, ReadOnlySpan<byte> data, byte[] signature)
    {
        if (publicKey.Length != KeySize || signature.Length != SignatureSize) return false;
        try
        {
            var s = new Ed25519Signer();
            s.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            var d = data.ToArray();
            s.BlockUpdate(d, 0, d.Length);
            return s.VerifySignature(signature);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
}

/// <summary>Detached signature file: one line "dbdata-sig/1 &lt;keyId&gt; &lt;base64&gt;".</summary>
public static class SignatureFile
{
    private const string Magic = "dbdata-sig/1";

    public static string Format(string keyId, byte[] signature) => $"{Magic} {keyId} {Convert.ToBase64String(signature)}\n";

    public static (string KeyId, byte[] Signature) Parse(string text)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != Magic) throw new UpdateVerificationException(VerificationFailure.MalformedSignature, "Signature file is malformed.");
        try { return (parts[1], Convert.FromBase64String(parts[2])); }
        catch (FormatException) { throw new UpdateVerificationException(VerificationFailure.MalformedSignature, "Signature is not valid base64."); }
    }
}

/// <summary>Signing helper used by CI tooling and tests. The seed is the private key; keep it in a protected CI environment.</summary>
public static class ManifestSigner
{
    public static string Sign(byte[] manifestBytes, byte[] seed, string keyId) => SignatureFile.Format(keyId, Ed25519.Sign(seed, manifestBytes));

    public static string Hex(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);

    public static string Sha256Hex(byte[] data) => Hex(SHA256.HashData(data));
}

public sealed record TrustedKey(string Id, byte[] PublicKey);

/// <summary>
/// The set of public keys a product trusts. Constructed from keys embedded in the product/installer.
/// Rotation: a key manifest (envelope) signed by a currently trusted key adds and retires keys.
/// </summary>
public sealed class TrustStore
{
    private readonly Dictionary<string, byte[]> _keys;
    public int Sequence { get; }
    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    public TrustStore(IEnumerable<TrustedKey> keys, int sequence = 0)
    {
        _keys = keys.ToDictionary(k => k.Id, k => k.PublicKey, StringComparer.Ordinal);
        Sequence = sequence;
    }

    public static TrustStore FromBase64(string keyId, string publicKeyBase64) => new([new TrustedKey(keyId, Convert.FromBase64String(publicKeyBase64))]);

    public bool Contains(string keyId) => _keys.ContainsKey(keyId);

    /// <summary>Verify <paramref name="data"/> against a detached signature file text. Throws on any failure.</summary>
    public void VerifyOrThrow(ReadOnlySpan<byte> data, string signatureFileText)
    {
        var (id, sig) = SignatureFile.Parse(signatureFileText);
        if (!_keys.TryGetValue(id, out var pub)) throw new UpdateVerificationException(VerificationFailure.UnknownKey, $"Signing key '{id}' is not trusted.");
        if (!Ed25519.Verify(pub, data, sig)) throw new UpdateVerificationException(VerificationFailure.BadSignature, "Signature does not match the manifest.");
    }

    /// <summary>Apply a signed key manifest. Returns the new store; throws if not signed by a currently trusted key or not newer.</summary>
    public TrustStore Rotate(byte[] envelopeJson)
    {
        KeyEnvelope? env;
        try { env = JsonSerializer.Deserialize<KeyEnvelope>(envelopeJson, ReleaseManifest.JsonOptions); }
        catch (JsonException) { env = null; }
        if (env?.Payload is null || env.KeyId is null || env.Signature is null)
            throw new UpdateVerificationException(VerificationFailure.KeyRotationRejected, "Key manifest is malformed.");
        byte[] payload, sig;
        try { payload = Convert.FromBase64String(env.Payload); sig = Convert.FromBase64String(env.Signature); }
        catch (FormatException) { throw new UpdateVerificationException(VerificationFailure.KeyRotationRejected, "Key manifest is not valid base64."); }
        if (!_keys.TryGetValue(env.KeyId, out var signer) || !Ed25519.Verify(signer, payload, sig))
            throw new UpdateVerificationException(VerificationFailure.KeyRotationRejected, "Key manifest is not signed by a trusted key.");
        KeySetPayload? p;
        try { p = JsonSerializer.Deserialize<KeySetPayload>(payload, ReleaseManifest.JsonOptions); }
        catch (JsonException) { p = null; }
        if (p is null || p.Schema != "keys/1") throw new UpdateVerificationException(VerificationFailure.KeyRotationRejected, "Key manifest payload is malformed.");
        if (p.Sequence <= Sequence) throw new UpdateVerificationException(VerificationFailure.KeyRotationRejected, "Key manifest is not newer than the current one (replay).");
        var next = new Dictionary<string, byte[]>(_keys, StringComparer.Ordinal);
        foreach (var r in p.Retired ?? []) next.Remove(r);
        foreach (var k in p.Keys ?? [])
        {
            byte[] pub;
            try { pub = Convert.FromBase64String(k.PublicKey ?? ""); } catch (FormatException) { pub = []; }
            if (string.IsNullOrEmpty(k.Id) || pub.Length != Ed25519.KeySize)
                throw new UpdateVerificationException(VerificationFailure.KeyRotationRejected, "Key manifest contains an invalid key.");
            next[k.Id] = pub;
        }
        if (next.Count == 0) throw new UpdateVerificationException(VerificationFailure.KeyRotationRejected, "Key manifest would leave no trusted keys.");
        return new TrustStore(next.Select(kv => new TrustedKey(kv.Key, kv.Value)), p.Sequence);
    }

    /// <summary>Build a key manifest envelope signed by <paramref name="signerSeed"/> (the previous key). Used by tooling and tests.</summary>
    public static byte[] CreateKeyManifest(int sequence, IEnumerable<TrustedKey> keys, IEnumerable<string> retired, byte[] signerSeed, string signerId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new KeySetPayload
        {
            Schema = "keys/1", Sequence = sequence,
            Keys = keys.Select(k => new KeyEntry { Id = k.Id, PublicKey = Convert.ToBase64String(k.PublicKey) }).ToList(),
            Retired = retired.ToList(),
        }, ReleaseManifest.JsonOptions);
        var env = new KeyEnvelope { Payload = Convert.ToBase64String(payload), KeyId = signerId, Signature = Convert.ToBase64String(Ed25519.Sign(signerSeed, payload)) };
        return JsonSerializer.SerializeToUtf8Bytes(env, ReleaseManifest.JsonOptions);
    }

    private sealed class KeyEnvelope { public string? Payload { get; set; } public string? KeyId { get; set; } public string? Signature { get; set; } }
    private sealed class KeyEntry { public string? Id { get; set; } public string? PublicKey { get; set; } }
    private sealed class KeySetPayload
    {
        public string? Schema { get; set; }
        public int Sequence { get; set; }
        public List<KeyEntry>? Keys { get; set; }
        public List<string>? Retired { get; set; }
    }
}

public sealed record VerifiedManifest(ReleaseManifest Manifest, string Sha256, byte[] RawBytes);

public static class ReleaseVerifier
{
    public const int MaxManifestBytes = 1024 * 1024;

    /// <summary>Signature first, parse second: nothing from an unauthenticated manifest is interpreted.</summary>
    public static VerifiedManifest VerifyManifest(byte[] manifestBytes, string signatureText, TrustStore trust, string? expectedProduct = null)
    {
        if (manifestBytes.Length is 0 or > MaxManifestBytes) throw new UpdateVerificationException(VerificationFailure.MalformedManifest, "Manifest size is out of range.");
        trust.VerifyOrThrow(manifestBytes, signatureText);
        ReleaseManifest m;
        try { m = ReleaseManifest.Parse(manifestBytes); }
        catch (FormatException ex) { throw new UpdateVerificationException(VerificationFailure.MalformedManifest, ex.Message); }
        if (expectedProduct is not null && !string.Equals(m.Product, expectedProduct, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException(VerificationFailure.WrongProduct, $"Manifest is for '{m.Product}', expected '{expectedProduct}'.");
        return new VerifiedManifest(m, ManifestSigner.Sha256Hex(manifestBytes), manifestBytes);
    }

    public static async Task VerifyFileAsync(string path, ReleaseArtifact artifact, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new UpdateVerificationException(VerificationFailure.Download, "Artifact file is missing.");
        if (info.Length != artifact.Size) throw new UpdateVerificationException(VerificationFailure.SizeMismatch, $"Artifact size {info.Length} != expected {artifact.Size}.");
        await using var fs = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
        if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException(VerificationFailure.HashMismatch, "Artifact sha256 does not match the signed manifest.");
    }
}
