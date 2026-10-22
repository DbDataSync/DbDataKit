namespace DbDataKit.Secrets.Store;

/// <summary>What the store offers beyond reading and writing a secret: its inventory (names and dates, never values), rotation, and the passphrase export. The <c>secret</c> commands use it.</summary>
public interface ISecretAdmin
{
    Task<IReadOnlyList<SecretInfo>> ListInfoAsync(string prefix = "", CancellationToken ct = default);

    Task<KeyRotationResult> RotateKeyAsync(Action<string>? checkpoint = null, CancellationToken ct = default);

    Task<int> ExportAsync(string path, SecretValue passphrase, string prefix = "", CancellationToken ct = default);

    Task<SecretImportResult> ImportAsync(string path, SecretValue passphrase, bool overwrite = false, CancellationToken ct = default);
}
