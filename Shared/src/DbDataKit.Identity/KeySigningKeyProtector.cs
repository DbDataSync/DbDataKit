using System.Security.Cryptography;

namespace DbDataKit.Identity;

/// <summary>
/// Protects the signing keys with a key supplied from outside (AES-256-GCM), for homes that share one state database: every instance of a serving group is given the same key (from its secret store) and so
/// reads the same signing keys (plan 20 section 6, O06; 13c D31). The protected form is a version byte, the 12-byte nonce, the 16-byte tag and the ciphertext.
/// </summary>
public sealed class KeySigningKeyProtector : ISigningKeyProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12, TagSize = 16;
    private readonly byte[] key;

    /// <param name="key">32 bytes.</param>
    public KeySigningKeyProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The key that protects the signing keys is 32 bytes.", nameof(key));
        this.key = (byte[])key.Clone();
    }

    /// <summary>A new random key, as base64: what a host stores in its secret store the first time.</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The protector for a key in base64, as <see cref="NewKey"/> makes it. Throws <see cref="FormatException"/> naming what is wrong with any other text.</summary>
    public static KeySigningKeyProtector FromBase64(string text)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text.Trim()); }
        catch (FormatException) { throw new FormatException("The key that protects the signing keys is not base64."); }
        if (bytes.Length != 32) throw new FormatException($"The key that protects the signing keys is 32 bytes; this one is {bytes.Length}.");
        return new KeySigningKeyProtector(bytes);
    }

    public byte[] Protect(byte[] privateKey)
    {
        var output = new byte[1 + NonceSize + TagSize + privateKey.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, privateKey, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize));
        return output;
    }

    public byte[] Unprotect(byte[] protectedKey)
    {
        if (protectedKey.Length < 1 + NonceSize + TagSize || protectedKey[0] != Version)
            throw new CryptographicException("A signing key is not in the form this protector writes.");
        var plain = new byte[protectedKey.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        // a wrong key fails here (the tag does not match): an instance given another key than the group's cannot sign
        aes.Decrypt(protectedKey.AsSpan(1, NonceSize), protectedKey.AsSpan(1 + NonceSize + TagSize), protectedKey.AsSpan(1 + NonceSize, TagSize), plain);
        return plain;
    }
}
