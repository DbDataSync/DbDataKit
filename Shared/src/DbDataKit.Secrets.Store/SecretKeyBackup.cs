using System.Security.Cryptography;
using System.Text;
using DbDataKit.Security.Acl;
using YamlDotNet.Serialization;

namespace DbDataKit.Secrets.Store;

internal sealed class SecretKeyFile
{
    [YamlMember(Alias = "format")] public string Format { get; set; } = SecretKeyBackup.FormatName;
    [YamlMember(Alias = "version")] public int Version { get; set; } = SecretKeyBackup.FormatVersion;
    [YamlMember(Alias = "kdf")] public string Kdf { get; set; } = "pbkdf2-sha256";
    [YamlMember(Alias = "iterations")] public int Iterations { get; set; }
    [YamlMember(Alias = "salt")] public string Salt { get; set; } = "";
    [YamlMember(Alias = "active")] public string Active { get; set; } = "";
    [YamlMember(Alias = "keys")] public SortedDictionary<string, SealedKey> Keys { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class SealedKey
{
    [YamlMember(Alias = "nonce")] public string Nonce { get; set; } = "";
    [YamlMember(Alias = "ciphertext")] public string Ciphertext { get; set; } = "";
    [YamlMember(Alias = "tag")] public string Tag { get; set; } = "";
}

/// <summary>What a key restore did.</summary>
public sealed record SecretKeyRestoreResult(IReadOnlyList<string> Restored, IReadOnlyList<string> Kept, string ActiveKeyId);

/// <summary>
/// The backup and restore of the secrets key (plan 22a GP21, file format <c>secret-key/1</c>): the data keys of the key ring, each sealed with AES-256-GCM under a key derived from a passphrase (PBKDF2-SHA256, the same rounds and
/// minimum length as <c>secret export</c>), the key's id as associated data so a key moved to another id fails. The file holds the keys and the id of the active one and <b>nothing about the machine</b>, so it restores on any host: <see cref="RestoreAsync"/>
/// stores each key under this host's own protector (a key that DPAPI bound to another machine or user is thereby <b>re-keyed</b> to this one). It never goes in git and the passphrase is kept apart from it.
/// A wrong passphrase, a changed file or a key moved to another id fails the restore <b>before anything is stored</b>; a key id the ring already has is kept, never replaced.
/// </summary>
public static class SecretKeyBackup
{
    public const string FormatName = "dbdata-secret-key";

    /// <summary>The file format's version (docs/formats.json: <c>secret-key</c>).</summary>
    public const int FormatVersion = 1;

    /// <summary>The message a store gives when a key file cannot be read on this host: what to do next.</summary>
    public const string RekeyHint = "If this machine or account is new, restore the key from its backup with `secret key-restore <file>` (made earlier by `secret key-backup`); without a backup the secrets it protected cannot be read, and the way on is to set them again (`secret set`) under a new key.";

    /// <summary>Writes every key of the ring to <paramref name="path"/> under <paramref name="passphrase"/>; the file is created restricted. Returns the number of keys.</summary>
    public static int Backup(KeyRing ring, string path, SecretValue passphrase)
    {
        RequirePassphrase(passphrase);
        var ids = ring.KeyIds();
        if (ids.Count == 0) throw new SecretStoreException($"The key ring '{ring.DirectoryPath}' has no key to back up.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = Derive(passphrase, salt, SecretExport.Iterations);
        var file = new SecretKeyFile { Iterations = SecretExport.Iterations, Salt = Convert.ToBase64String(salt), Active = ring.ActiveKeyIdOrNull ?? "" };
        try
        {
            foreach (var id in ids)
            {
                var key = ring.GetKey(id);
                try
                {
                    var nonce = RandomNumberGenerator.GetBytes(12);
                    var cipher = new byte[key.Length];
                    var tag = new byte[16];
                    using var aes = new AesGcm(derived, 16);
                    aes.Encrypt(nonce, key, cipher, tag, Encoding.UTF8.GetBytes(id));
                    file.Keys[id] = new SealedKey { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(cipher), Tag = Convert.ToBase64String(tag) };
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(derived); }

        var text = "# DbDataKit secret key backup: the data keys of the secret store, each encrypted under a key derived from the passphrase it was made with. Restore it with `secret key-restore`. Never put it in git; keep the passphrase apart from it.\n"
            + EncryptedFileSecretStore.Serializer.Serialize(file);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        AclHelper.WriteAllBytesRestricted(path, Encoding.UTF8.GetBytes(text), AclHelper.DefaultIdentities(Path.GetFullPath(Path.Combine(ring.DirectoryPath, "..", ".."))));
        return file.Keys.Count;
    }

    /// <summary>Reads a backup and stores its keys in <paramref name="ring"/>. The backup's active key becomes the ring's active key only when the ring has none (a restore into a ring that is in use changes what new values are encrypted with only if you ask for it with <paramref name="activate"/>).</summary>
    public static Task<SecretKeyRestoreResult> RestoreAsync(KeyRing ring, string path, SecretValue passphrase, bool activate = false, CancellationToken ct = default)
    {
        RequirePassphrase(passphrase);
        if (!File.Exists(path)) throw new FileNotFoundException($"The key backup '{path}' does not exist.", path);
        SecretKeyFile file;
        try { file = EncryptedFileSecretStore.Deserializer.Deserialize<SecretKeyFile>(File.ReadAllText(path)) ?? throw new SecretStoreException($"'{path}' is empty."); }
        catch (YamlDotNet.Core.YamlException ex) { throw new SecretStoreException($"'{path}' is not a secret key backup: {ex.Message}", ex); }
        if (file.Format != FormatName || file.Version != FormatVersion) throw new SecretStoreException($"'{path}' is not a secret key backup this build reads (format '{file.Format}', version {file.Version}).");
        if (file.Kdf != "pbkdf2-sha256") throw new SecretStoreException($"'{path}' uses the key derivation '{file.Kdf}'; only pbkdf2-sha256 is read.");
        if (file.Iterations < SecretExport.Iterations || file.Iterations > SecretExport.MaximumIterations)
            throw new SecretStoreException($"'{path}' says {file.Iterations} PBKDF2 rounds; a backup has {SecretExport.Iterations} to {SecretExport.MaximumIterations}.");
        byte[] salt;
        try { salt = Convert.FromBase64String(file.Salt); }
        catch (FormatException ex) { throw new SecretStoreException($"'{path}' has a salt that is not base64.", ex); }
        if (salt.Length < 16) throw new SecretStoreException($"'{path}' has a salt of {salt.Length} bytes; a backup has at least 16.");
        if (file.Keys.Count == 0) throw new SecretStoreException($"'{path}' holds no key.");

        var derived = Derive(passphrase, salt, file.Iterations);
        var opened = new List<(string Id, byte[] Key)>();
        try
        {
            foreach (var (id, sealedKey) in file.Keys)
            {
                try
                {
                    var key = new byte[KeyRing.KeyLength];
                    var cipher = Convert.FromBase64String(sealedKey.Ciphertext);
                    if (cipher.Length != KeyRing.KeyLength) throw new SecretStoreException($"'{path}': the key '{id}' is not {KeyRing.KeyLength * 8} bits.");
                    using var aes = new AesGcm(derived, 16);
                    aes.Decrypt(Convert.FromBase64String(sealedKey.Nonce), cipher, Convert.FromBase64String(sealedKey.Tag), key, Encoding.UTF8.GetBytes(id));
                    opened.Add((id, key));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
                {
                    throw new SecretStoreException($"'{path}': the key '{id}' could not be opened. Is the passphrase the one the backup was made with? Has the file been changed?", ex);
                }
            }

            var ringHadActive = ring.ActiveKeyIdOrNull is not null;
            var restored = new List<string>();
            var kept = new List<string>();
            foreach (var (id, key) in opened)
            {
                if (ring.KeyIds().Contains(id, StringComparer.Ordinal)) { kept.Add(id); continue; }
                ring.AddKey(id, key, activate: false);
                restored.Add(id);
            }

            var active = file.Active.Length > 0 && file.Keys.ContainsKey(file.Active) ? file.Active : opened[0].Id;
            if (activate || !ringHadActive) ring.Activate(active);
            return Task.FromResult(new SecretKeyRestoreResult(restored, kept, ring.ActiveKeyId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            foreach (var (_, key) in opened) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void RequirePassphrase(SecretValue passphrase)
    {
        if (passphrase.Length < SecretExport.MinimumPassphraseLength)
            throw new ArgumentException($"The passphrase must be at least {SecretExport.MinimumPassphraseLength} characters: the backup file can be guessed at for as long as anyone likes.", nameof(passphrase));
    }

    private static byte[] Derive(SecretValue passphrase, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(passphrase.Reveal().ToArray());
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeyRing.KeyLength); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
