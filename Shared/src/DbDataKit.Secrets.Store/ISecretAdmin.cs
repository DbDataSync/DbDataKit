namespace DbDataKit.Secrets.Store;

/// <summary>What the store offers beyond reading and writing a secret: its inventory (names and dates, never values), rotation, and the passphrase export. The <c>secret</c> commands use it.</summary>
public interface ISecretAdmin
{
    Task<IReadOnlyList<SecretInfo>> ListInfoAsync(string prefix = "", CancellationToken ct = default);

    Task<KeyRotationResult> RotateKeyAsync(Action<string>? checkpoint = null, CancellationToken ct = default);

    Task<int> ExportAsync(string path, SecretValue passphrase, string prefix = "", CancellationToken ct = default);

    /// <summary>Writes the data keys under a passphrase (plan 22a GP21, <c>secret-key/1</c>); returns how many. Only a key ring has keys of its own.</summary>
    int BackupKeys(string path, SecretValue passphrase);

    /// <summary>Stores the keys of a backup in this store's ring under this host's key protection; a key the ring has is kept.</summary>
    Task<SecretKeyRestoreResult> RestoreKeysAsync(string path, SecretValue passphrase, bool activate = false, CancellationToken ct = default);

    Task<SecretImportResult> ImportAsync(string path, SecretValue passphrase, bool overwrite = false, CancellationToken ct = default);
}
