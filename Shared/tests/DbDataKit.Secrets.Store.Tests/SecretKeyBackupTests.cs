using DbDataKit.Secrets.Store;
using Xunit;

namespace DbDataKit.Secrets.Store.Tests;

/// <summary>A protector that binds a key to "a machine": what DPAPI does, so a key file made on another one cannot be read (plan 22a GP21).</summary>
internal sealed class MachineBoundProtector(string machine) : IKeyProtector
{
    public byte Kind => (byte)(machine == "old-pc" ? 9 : 10);

    public string Description => $"the machine '{machine}'";

    public byte[] Protect(ReadOnlySpan<byte> key) => [.. System.Text.Encoding.UTF8.GetBytes(machine + ":"), .. key.ToArray()];

    public byte[] Unprotect(ReadOnlySpan<byte> blob)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(machine + ":");
        if (!blob.StartsWith(prefix)) throw new System.Security.Cryptography.CryptographicException("not protected for this machine");
        return blob[prefix.Length..].ToArray();
    }
}

/// <summary>Plan 22a GP21: the secrets key, backed up under a passphrase and restored (<c>secret-key/1</c>).</summary>
public class SecretKeyBackupTests
{
    private const string Passphrase = "correct horse battery staple";
    private static SecretName N(string name) => new("sync", "connection", name);

    private static async Task<(EncryptedFileSecretStore Store, KeyRing Ring)> Store(TempDir t, string folder, IKeyProtector? protector = null, params (SecretName Name, string Value)[] values)
    {
        var directory = System.IO.Path.Combine(t.Path, folder);
        var ring = new KeyRing(System.IO.Path.Combine(directory, "keys"), protector);
        ring.EnsureActiveKey();
        var store = new EncryptedFileSecretStore(directory, ring);
        foreach (var (name, value) in values) await store.SetAsync(name, SecretValue.From(value));
        return (store, ring);
    }

    [Fact]
    public async Task A_backup_restores_into_an_empty_ring_and_the_copied_secrets_file_reads_back()
    {
        using var t = new TempDir();
        var (source, ring) = await Store(t, "a", null, (N("one"), "first"), (N("two"), "second"));
        var keyId = ring.ActiveKeyId;
        var path = System.IO.Path.Combine(t.Path, "backup", "secrets.key.yaml");
        Assert.Equal(1, source.BackupKeys(path, SecretValue.From(Passphrase)));

        // the machine is lost: the secrets file survives (copied elsewhere), the key ring does not
        var restoredDirectory = System.IO.Path.Combine(t.Path, "b");
        Directory.CreateDirectory(restoredDirectory);
        File.Copy(System.IO.Path.Combine(t.Path, "a", "secrets.yaml"), System.IO.Path.Combine(restoredDirectory, "secrets.yaml"));
        var newRing = new KeyRing(System.IO.Path.Combine(restoredDirectory, "keys"));
        var result = await SecretKeyBackup.RestoreAsync(newRing, path, SecretValue.From(Passphrase));
        Assert.Equal([keyId], result.Restored);
        Assert.Equal(keyId, result.ActiveKeyId);

        var target = new EncryptedFileSecretStore(restoredDirectory, newRing);
        using var one = await target.TryResolveAsync(N("one").ToRef());
        Assert.Equal("first", one!.RevealString());
        using var two = await target.TryResolveAsync(N("two").ToRef());
        Assert.Equal("second", two!.RevealString());
    }

    [Fact]
    public async Task A_wrong_passphrase_a_changed_file_and_a_key_moved_to_another_id_are_refused_before_anything_is_stored()
    {
        using var t = new TempDir();
        var (source, ring) = await Store(t, "a");
        var path = System.IO.Path.Combine(t.Path, "k.yaml");
        source.BackupKeys(path, SecretValue.From(Passphrase));

        var empty = new KeyRing(System.IO.Path.Combine(t.Path, "b", "keys"));
        var wrong = await Assert.ThrowsAsync<SecretStoreException>(() => SecretKeyBackup.RestoreAsync(empty, path, SecretValue.From("another passphrase entirely")));
        Assert.Contains("passphrase", wrong.Message);
        Assert.Empty(empty.KeyIds());

        var text = File.ReadAllText(path);
        var id = ring.ActiveKeyId;
        File.WriteAllText(path, text.Replace(id + ":", "ffffffffffff:"));          // the key under another id: its associated data no longer fits
        await Assert.ThrowsAsync<SecretStoreException>(() => SecretKeyBackup.RestoreAsync(empty, path, SecretValue.From(Passphrase)));
        Assert.Empty(empty.KeyIds());

        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(text, "(ciphertext: )(.)", m => m.Groups[1].Value + (m.Groups[2].Value == "A" ? "B" : "A")));
        await Assert.ThrowsAnyAsync<Exception>(() => SecretKeyBackup.RestoreAsync(empty, path, SecretValue.From(Passphrase)));
        Assert.Empty(empty.KeyIds());

        Assert.Throws<ArgumentException>(() => source.BackupKeys(System.IO.Path.Combine(t.Path, "short.yaml"), SecretValue.From("too short")));
    }

    [Fact]
    public async Task The_backup_file_holds_no_key_bytes_no_passphrase_and_two_backups_differ_and_a_key_the_ring_has_is_kept()
    {
        using var t = new TempDir();
        var (source, ring) = await Store(t, "a");
        var one = System.IO.Path.Combine(t.Path, "1.yaml");
        var two = System.IO.Path.Combine(t.Path, "2.yaml");
        source.BackupKeys(one, SecretValue.From(Passphrase));
        source.BackupKeys(two, SecretValue.From(Passphrase));
        var text = File.ReadAllText(one);
        Assert.Contains("format: dbdata-secret-key", text);
        Assert.Contains("iterations: 600000", text);
        Assert.DoesNotContain(Passphrase, text);
        var rawKey = Convert.ToBase64String(ring.GetKey(ring.ActiveKeyId));
        Assert.DoesNotContain(rawKey, text);
        Assert.NotEqual(text, File.ReadAllText(two));

        var again = await SecretKeyBackup.RestoreAsync(ring, one, SecretValue.From(Passphrase));        // restoring into the ring that has the key replaces nothing
        Assert.Empty(again.Restored);
        Assert.Equal([ring.ActiveKeyId], again.Kept);
        Assert.Single(ring.KeyIds());
    }

    [Fact]
    public async Task A_key_that_another_machine_protected_cannot_be_read_says_how_to_restore_and_a_restore_re_keys_it_to_this_machine()
    {
        using var t = new TempDir();
        var (oldStore, oldRing) = await Store(t, "a", new MachineBoundProtector("old-pc"), (N("one"), "first"));
        var path = System.IO.Path.Combine(t.Path, "k.yaml");
        oldStore.BackupKeys(path, SecretValue.From(Passphrase));

        // the key files are copied to the new machine: they cannot be read there, and the message says what to do
        var newDirectory = System.IO.Path.Combine(t.Path, "b");
        var newRing = new KeyRing(System.IO.Path.Combine(newDirectory, "keys"), new MachineBoundProtector("new-pc"));
        Directory.CreateDirectory(System.IO.Path.Combine(newDirectory, "keys"));
        File.Copy(System.IO.Path.Combine(t.Path, "a", "secrets.yaml"), System.IO.Path.Combine(newDirectory, "secrets.yaml"));
        var copied = System.IO.Path.Combine(oldRing.DirectoryPath, oldRing.ActiveKeyId + ".key");
        var stuck = new KeyRing(oldRing.DirectoryPath, new MachineBoundProtector("new-pc"));
        var refused = Assert.Throws<InvalidDataException>(() => stuck.GetKey(oldRing.ActiveKeyId));
        Assert.Contains("secret key-restore", refused.Message);
        Assert.True(File.Exists(copied));

        var result = await SecretKeyBackup.RestoreAsync(newRing, path, SecretValue.From(Passphrase));
        Assert.Single(result.Restored);
        var target = new EncryptedFileSecretStore(newDirectory, newRing);
        using var one = await target.TryResolveAsync(N("one").ToRef());
        Assert.Equal("first", one!.RevealString());
    }
}
