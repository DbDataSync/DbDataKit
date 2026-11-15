namespace DbDataKit.Identity;

/// <summary>
/// Protects the signing keys with a key supplied from outside (AES-256-GCM), for homes that share one state database: every instance of a serving group is given the same key (from its secret store) and so
/// reads the same signing keys (plan 20 section 6, O06; 13c D31). The protected form is a version byte, the 12-byte nonce, the 16-byte tag and the ciphertext.
/// </summary>
public sealed class KeySigningKeyProtector : ISigningKeyProtector
{
    private readonly byte[] key;

    /// <param name="key">32 bytes.</param>
    public KeySigningKeyProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The key that protects the signing keys is 32 bytes.", nameof(key));
        this.key = (byte[])key.Clone();
    }

    /// <summary>A new random key, as base64: what a host stores in its secret store the first time.</summary>
    public static string NewKey() => DbDataKit.Web.Trust.GroupCipher.NewKey();

    /// <summary>The protector for a key in base64, as <see cref="NewKey"/> makes it. Throws <see cref="FormatException"/> naming what is wrong with any other text.</summary>
    public static KeySigningKeyProtector FromBase64(string text) => new(DbDataKit.Web.Trust.GroupCipher.Parse(text, "The key that protects the signing keys"));

    // the form is the suite's group-key form (DbDataKit.Web.Trust.GroupCipher), shared with the Gateway's and the Runner's group keys
    public byte[] Protect(byte[] privateKey) => DbDataKit.Web.Trust.GroupCipher.Protect(key, privateKey);

    // a wrong key fails in the decryption (the tag does not match): an instance given another key than the group's cannot sign
    public byte[] Unprotect(byte[] protectedKey) => DbDataKit.Web.Trust.GroupCipher.Unprotect(key, protectedKey, "A signing key is not in the form this protector writes.");
}
