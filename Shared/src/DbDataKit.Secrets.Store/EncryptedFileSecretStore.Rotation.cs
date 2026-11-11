using DbDataKit.Security.Acl;
using System.Text;

namespace DbDataKit.Secrets.Store;

/// <summary>What a rotation did.</summary>
/// <param name="NewKeyId">The key every value is now under.</param>
/// <param name="RemovedKeyIds">The keys deleted.</param>
/// <param name="Reencrypted">How many values were rewritten (0 when this run only finished an earlier one).</param>
/// <param name="Resumed">True when an interrupted rotation was found and continued.</param>
public sealed record KeyRotationResult(string NewKeyId, IReadOnlyList<string> RemovedKeyIds, int Reencrypted, bool Resumed);

public sealed partial class EncryptedFileSecretStore
{
    private string RotationStatePath => Path.Combine(_directory, "rotation.state");

    /// <summary>True when a rotation was started and not finished: <see cref="RotateKeyAsync"/> continues it.</summary>
    public bool RotationPending => File.Exists(RotationStatePath);

    /// <summary>
    /// Rotates the data key: a new key becomes the active one, every value is decrypted with its key and encrypted again under the new one, and then the old keys are deleted. It can be stopped
    /// anywhere and run again. The steps are ordered so that a stop leaves a store that reads: the target key is written down first (<c>rotation.state</c>), all values move in <b>one</b>
    /// atomic replacement of <c>secrets.yaml</c>, and the old keys go last, only after no value uses them. The next run reads the state file, rewrites any value still on another key, and removes
    /// what is left. Only a key ring can rotate; a key provided from outside is rotated by the platform that provides it.
    /// </summary>
    /// <param name="checkpoint">Called with the name of each step reached (<c>key-created</c>, <c>values-rewritten</c>, <c>old-keys-removed</c>), for tests that stop the rotation there.</param>
    /// <param name="ct">Cancels the wait for the store lock and the reads; a rotation that is cancelled part-way is continued by the next run.</param>
    public async Task<KeyRotationResult> RotateKeyAsync(Action<string>? checkpoint = null, CancellationToken ct = default)
    {
        if (_keys is not KeyRing ring)
            throw new NotSupportedException("The key of this store is provided from outside (secrets.keyFile); rotate it where it is provided, and the store reads the new one after `secret import` or a re-set of the values.");

        await using var _ = await LockAsync(ct);
        var resumed = RotationPending;
        string target;
        if (resumed)
        {
            target = File.ReadAllText(RotationStatePath, Encoding.UTF8).Trim();
            if (!ring.KeyIds().Contains(target, StringComparer.Ordinal))
                throw new SecretStoreException($"A rotation to key '{target}' was started, but that key is not in the ring '{ring.DirectoryPath}'. Delete '{RotationStatePath}' if it was started by mistake.");
        }
        else
        {
            target = ring.CreateKey(activate: false);
            AclHelper.WriteAllBytesRestricted(RotationStatePath, Encoding.UTF8.GetBytes(target), _identities);
        }
        checkpoint?.Invoke("key-created");
        if (!string.Equals(ring.ActiveKeyId, target, StringComparison.Ordinal)) ring.Activate(target);

        var file = await ReadAsync(ct);
        var moved = 0;
        foreach (var (key, entry) in file.Secrets.ToList())
        {
            if (string.Equals(entry.Key, target, StringComparison.Ordinal)) continue;
            var name = SecretName.Parse(key);
            using var value = Decrypt(name, entry);
            file.Secrets[key] = Encrypt(name, value);
            moved++;
        }
        if (moved > 0) await WriteAsync(file, ct);
        checkpoint?.Invoke("values-rewritten");

        var removed = new List<string>();
        foreach (var id in ring.KeyIds())
        {
            if (string.Equals(id, target, StringComparison.Ordinal)) continue;
            ring.DeleteKey(id);
            removed.Add(id);
        }
        checkpoint?.Invoke("old-keys-removed");
        File.Delete(RotationStatePath);
        return new KeyRotationResult(target, removed, moved, resumed);
    }
}
