using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.Security.Acl;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DbDataKit.Secrets.Store;

/// <summary>
/// One entry of <c>secrets.yaml</c>. <see cref="Value"/> is base64 of a 96-bit nonce, the ciphertext and the 128-bit tag.
/// </summary>
public sealed class SecretEntry
{
    [YamlMember(Alias = "v")] public int Version { get; set; } = 1;
    [YamlMember(Alias = "key")] public string Key { get; set; } = "";
    [YamlMember(Alias = "value")] public string Value { get; set; } = "";
    [YamlMember(Alias = "updated")] public string Updated { get; set; } = "";
}

internal sealed class SecretFile
{
    [YamlMember(Alias = "version")] public int Version { get; set; } = 1;
    [YamlMember(Alias = "secrets")] public SortedDictionary<string, SecretEntry> Secrets { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Name, last write and key of a stored secret; never its value.</summary>
public sealed record SecretInfo(SecretName Name, DateTimeOffset Updated, string KeyId);

/// <summary>
/// The shared secret store: <c>&lt;data&gt;/secrets/secrets.yaml</c>, every value AES-256-GCM encrypted with a fresh random nonce and the secret's <b>name as associated data</b>, so a value copied to
/// another name does not decrypt. The data key comes from an <see cref="IKeySource"/> (a key ring, or a key the platform provides); the key id is kept beside each value so keys can be rotated.
/// Writes go to a restricted temporary file that replaces the store atomically, under a lock that holds across processes. It answers <c>secret:</c> references in the resolver chain.
/// </summary>
public sealed partial class EncryptedFileSecretStore : ISecretStore, ISecretProvider, ISecretAdmin
{
    public const string FileName = "secrets.yaml";
    private const int NonceLength = 12, TagLength = 16;

    internal static readonly ISerializer Serializer = new SerializerBuilder().WithQuotingNecessaryStrings().DisableAliases().Build();
    internal static readonly IDeserializer Deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    private readonly string _directory;
    private readonly IKeySource _keys;
    private readonly string[] _identities;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">The store directory (<c>&lt;data&gt;/secrets</c>), created restricted if it is not there.</param>
    /// <param name="keys">Where the data keys come from.</param>
    /// <param name="windowsIdentities">Who may reach the files on Windows (default: the current user, SYSTEM and Administrators); ignored elsewhere.</param>
    /// <param name="clock">For the time written beside each value.</param>
    public EncryptedFileSecretStore(string directory, IKeySource keys, IEnumerable<string>? windowsIdentities = null, TimeProvider? clock = null)
    {
        _directory = Path.GetFullPath(directory);
        _keys = keys;
        _identities = (windowsIdentities ?? ["CurrentUser", "SYSTEM", "Administrators"]).ToArray();
        _clock = clock ?? TimeProvider.System;
    }

    public string Name => "secret-store";

    public string FilePath => Path.Combine(_directory, FileName);

    public bool CanHandle(SecretRef reference) => reference.Kind == SecretRefKind.Secret && SecretName.TryParse(reference.Target, out _);

    public async ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default)
    {
        var name = SecretName.Parse(reference.Target);
        var file = await ReadAsync(ct);
        return file.Secrets.TryGetValue(name.ToString(), out var entry) ? Decrypt(name, entry) : null;
    }

    public async Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default)
    {
        await using var _ = await LockAsync(ct);
        var file = await ReadAsync(ct);
        file.Secrets[name.ToString()] = Encrypt(name, value);
        await WriteAsync(file, ct);
    }

    public async Task DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        await using var _ = await LockAsync(ct);
        var file = await ReadAsync(ct);
        if (file.Secrets.Remove(name.ToString())) await WriteAsync(file, ct);
    }

    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var info in await ListInfoAsync(prefix, ct)) yield return info.Name;
    }

    /// <summary>The stored names with when each was written and the key it uses (for <c>secret list</c>). No values.</summary>
    public async Task<IReadOnlyList<SecretInfo>> ListInfoAsync(string prefix = "", CancellationToken ct = default)
    {
        var file = await ReadAsync(ct);
        return file.Secrets.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => new SecretInfo(SecretName.Parse(p.Key), DateTimeOffset.TryParse(p.Value.Updated, out var when) ? when : default, p.Value.Key)).ToList();
    }

    public async Task<bool> ExistsAsync(SecretName name, CancellationToken ct = default) => (await ReadAsync(ct)).Secrets.ContainsKey(name.ToString());

    // ---- encryption ----

    private const int KeyLengthBytes = 32;

    private SecretEntry Encrypt(SecretName name, SecretValue value)
    {
        var keyId = _keys.ActiveKeyId;
        var key = _keys.GetKey(keyId);
        try { return Seal(name, value, key, keyId); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private SecretValue Decrypt(SecretName name, SecretEntry entry)
    {
        byte[] key;
        try { key = _keys.GetKey(entry.Key); }
        catch (KeyNotFoundException ex) { throw new SecretStoreException($"The secret '{name}' was written with key '{entry.Key}', which this store does not have. {ex.Message}", ex); }
        try { return Open(name, entry, key, entry.Key, FilePath, wrongKeyHint: "It was changed, moved from another name, or written with a different key."); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Encrypts a value with AES-256-GCM: a fresh nonce, the name as associated data. <paramref name="keyLabel"/> is what is recorded as the entry's key.</summary>
    private SecretEntry Seal(SecretName name, SecretValue value, byte[] key, string keyLabel)
    {
        var plain = Encoding.UTF8.GetBytes(value.Reveal().ToArray());
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(key, TagLength)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name.ToString()));
            var sealedValue = new byte[NonceLength + cipher.Length + TagLength];
            nonce.CopyTo(sealedValue, 0);
            cipher.CopyTo(sealedValue, NonceLength);
            tag.CopyTo(sealedValue, NonceLength + cipher.Length);
            return new SecretEntry { Version = 1, Key = keyLabel, Value = Convert.ToBase64String(sealedValue), Updated = _clock.GetUtcNow().ToString("O") };
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static SecretValue Open(SecretName name, SecretEntry entry, byte[] key, string keyLabel, string where, string wrongKeyHint)
    {
        if (entry.Version != 1) throw new SecretStoreException($"The secret '{name}' is stored in format {entry.Version}; this build reads format 1.");
        byte[] sealedValue;
        try { sealedValue = Convert.FromBase64String(entry.Value); }
        catch (FormatException ex) { throw new SecretStoreException($"The secret '{name}' in '{where}' is not valid base64.", ex); }
        if (sealedValue.Length < NonceLength + TagLength) throw new SecretStoreException($"The secret '{name}' in '{where}' is too short to be an encrypted value.");

        var plain = new byte[sealedValue.Length - NonceLength - TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(sealedValue.AsSpan(0, NonceLength), sealedValue.AsSpan(NonceLength, plain.Length), sealedValue.AsSpan(NonceLength + plain.Length, TagLength), plain, Encoding.UTF8.GetBytes(name.ToString()));
            return SecretValue.From(Encoding.UTF8.GetString(plain));
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreException($"The secret '{name}' in '{where}' does not decrypt with key '{keyLabel}'. {wrongKeyHint}", ex);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // ---- the file ----

    private async Task<SecretFile> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return new SecretFile();
        string text;
        for (var attempt = 0; ; attempt++)
        {
            try { text = await File.ReadAllTextAsync(FilePath, ct); break; }
            catch (FileNotFoundException) { return new SecretFile(); }
            catch (IOException) when (attempt < 20) { await Task.Delay(10, ct); }      // a writer's rename is replacing the file at this instant (a sharing violation on Windows)
        }
        try
        {
            var file = Deserializer.Deserialize<SecretFile>(text) ?? new SecretFile();
            if (file.Version != 1) throw new SecretStoreException($"'{FilePath}' is secret store format {file.Version}; this build reads format 1.");
            file.Secrets ??= new SortedDictionary<string, SecretEntry>(StringComparer.Ordinal);
            foreach (var name in file.Secrets.Keys)
                if (!SecretName.TryParse(name, out _)) throw new SecretStoreException($"'{FilePath}' holds '{name}', which is not a secret name.");
            return file;
        }
        catch (YamlDotNet.Core.YamlException ex) { throw new SecretStoreException($"'{FilePath}' is not a secret store file: {ex.Message}", ex); }
    }

    private async Task WriteAsync(SecretFile file, CancellationToken ct)
    {
        var text = "# DbDataKit secret store: every value is encrypted, and useless without the key (see secrets/keys, or secrets.keyFile). Do not edit; use the `secret` commands.\n" + Serializer.Serialize(file);
        var temp = FilePath + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".tmp";
        try
        {
            AclHelper.WriteAllBytesRestricted(temp, Encoding.UTF8.GetBytes(text), _identities);
            await ReplaceAsync(temp, ct);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>
    /// Renames the temp file over the store. On Windows that fails with "access denied" or a sharing violation while anything has the destination open (a reader mid-read,
    /// an indexer, a virus scanner), so it is retried briefly; elsewhere a rename replaces the file whatever has it open and a failure is real.
    /// </summary>
    private async Task ReplaceAsync(string temp, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, FilePath, overwrite: true); return; }
            catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException && attempt < 100)
            {
                await Task.Delay(20, ct);
            }
        }
    }

    // ---- the lock: one writer at a time, in this process and in any other ----

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);

    private async Task<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            AclHelper.EnsureRestrictedDirectory(_directory, _identities);
            var path = Path.Combine(_directory, "secrets.lock");
            // The lock is polled, so a waiter is not served in turn: it waits against a deadline, not a count of attempts, which a crowd of writers on a slow disk can use up while each of them makes progress.
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                    return new Release(stream, _gate);
                }
                catch (IOException) when (deadline.Elapsed < LockWait) { await Task.Delay(Random.Shared.Next(10, 40), ct); }
                // Windows answers "access denied", not a sharing violation, to an open of a file whose last holder has just closed it with the delete still pending
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && deadline.Elapsed < LockWait) { await Task.Delay(Random.Shared.Next(10, 40), ct); }
                catch (IOException ex) { throw new SecretStoreException($"Could not lock the secret store '{_directory}' for writing: another process is holding it.", ex); }
            }
        }
        catch { _gate.Release(); throw; }
    }

    private sealed class Release(FileStream stream, SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { stream.Dispose(); gate.Release(); return ValueTask.CompletedTask; }
    }
}
