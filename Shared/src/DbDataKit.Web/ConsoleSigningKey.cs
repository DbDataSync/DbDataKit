using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace DbDataKit.Web.Trust;

/// <summary>A console's Ed25519 key pair. The private half signs tokens; only the 32-byte public half is ever given to a server.</summary>
public sealed class ConsoleSigningKey
{
    public const int PublicKeyLength = 32, SeedLength = 32;

    private readonly Ed25519PrivateKeyParameters _private;

    private ConsoleSigningKey(Ed25519PrivateKeyParameters key)
    {
        _private = key;
        PublicKey = key.GeneratePublicKey().GetEncoded();
    }

    public static ConsoleSigningKey Generate() => FromSeed(RandomNumberGenerator.GetBytes(SeedLength));

    /// <summary>The key made from its 32-byte seed (what a console keeps in its protected store).</summary>
    public static ConsoleSigningKey FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength) throw new ArgumentException($"An Ed25519 seed is {SeedLength} bytes.", nameof(seed));
        return new ConsoleSigningKey(new Ed25519PrivateKeyParameters(seed.ToArray(), 0));
    }

    public byte[] PublicKey { get; }

    /// <summary>A short, stable name for the public key (the first 16 hex digits of its SHA-256), shown when pairing and in the token header.</summary>
    public string Fingerprint => Fingerprints.Of(PublicKey);

    /// <summary>The seed to store. Treat it like a password.</summary>
    public byte[] ExportSeed() => _private.GetEncoded();

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        var bytes = message.ToArray();
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != 64) return false;
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
            var bytes = message.ToArray();
            signer.BlockUpdate(bytes, 0, bytes.Length);
            return signer.VerifySignature(signature.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
}

public static class Fingerprints
{
    public static string Of(ReadOnlySpan<byte> publicKey) => Convert.ToHexString(SHA256.HashData(publicKey))[..16].ToLowerInvariant();
}
