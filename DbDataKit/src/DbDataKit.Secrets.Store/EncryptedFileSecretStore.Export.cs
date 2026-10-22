using System.Security.Cryptography;
using System.Text;
using DbDataKit.Security.Acl;
using YamlDotNet.Serialization;

namespace DbDataKit.Secrets.Store;

internal sealed class SecretExportFile
{
    [YamlMember(Alias = "format")] public string Format { get; set; } = SecretExport.FormatName;
    [YamlMember(Alias = "version")] public int Version { get; set; } = 1;
    [YamlMember(Alias = "kdf")] public string Kdf { get; set; } = "pbkdf2-sha256";
    [YamlMember(Alias = "iterations")] public int Iterations { get; set; }
    [YamlMember(Alias = "salt")] public string Salt { get; set; } = "";
    [YamlMember(Alias = "secrets")] public SortedDictionary<string, SecretEntry> Secrets { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What an import did.</summary>
public sealed record SecretImportResult(int Imported, int Skipped, int Overwritten);

/// <summary>The parameters of the passphrase export, for tests and the documentation.</summary>
public static class SecretExport
{
    public const string FormatName = "dbdata-secrets-export";

    /// <summary>PBKDF2-HMAC-SHA256 rounds (the figure OWASP gives for it); an export that says fewer is refused on import.</summary>
    public const int Iterations = 600_000;

    /// <summary>A longer passphrase is not a courtesy: the export file can be taken away and guessed at for as long as anyone likes.</summary>
    public const int MinimumPassphraseLength = 12;

    internal const int MaximumIterations = 10_000_000;
}

public sealed partial class EncryptedFileSecretStore
{
    /// <summary>
    /// Writes every secret (or those whose name starts with <paramref name="prefix"/>) to <paramref name="path"/>, each encrypted under a key derived from <paramref name="passphrase"/>
    /// (PBKDF2-SHA256, <see cref="SecretExport.Iterations"/> rounds, a random salt), with the secret's name as associated data. The store's own key is never in the file, so the export reads on
    /// any machine and says nothing about this one. The file is created restricted. Returns how many secrets it holds.
    /// </summary>
    public async Task<int> ExportAsync(string path, SecretValue passphrase, string prefix = "", CancellationToken ct = default)
    {
        RequirePassphrase(passphrase);
        var file = await ReadAsync(ct);
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Derive(passphrase, salt, SecretExport.Iterations);
        var export = new SecretExportFile { Iterations = SecretExport.Iterations, Salt = Convert.ToBase64String(salt) };
        try
        {
            foreach (var (name, entry) in file.Secrets)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var secretName = SecretName.Parse(name);
                using var value = Decrypt(secretName, entry);
                export.Secrets[name] = Seal(secretName, value, key, "export");
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        var text = "# DbDataKit secret export: every value is encrypted under a key derived from the passphrase it was exported with. Import it with `secret import`.\n" + Serializer.Serialize(export);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        AclHelper.WriteAllBytesRestricted(path, Encoding.UTF8.GetBytes(text), _identities);
        return export.Secrets.Count;
    }

    /// <summary>
    /// Reads an export made by <see cref="ExportAsync"/> and stores each secret under this store's own key. A wrong passphrase, a changed file or a value moved to another name fails the import
    /// <b>before anything is stored</b>. A name already in the store is kept unless <paramref name="overwrite"/> is set.
    /// </summary>
    public async Task<SecretImportResult> ImportAsync(string path, SecretValue passphrase, bool overwrite = false, CancellationToken ct = default)
    {
        RequirePassphrase(passphrase);
        if (!File.Exists(path)) throw new FileNotFoundException($"The export file '{path}' does not exist.", path);
        SecretExportFile export;
        try { export = Deserializer.Deserialize<SecretExportFile>(await File.ReadAllTextAsync(path, ct)) ?? throw new SecretStoreException($"'{path}' is empty."); }
        catch (YamlDotNet.Core.YamlException ex) { throw new SecretStoreException($"'{path}' is not a secret export file: {ex.Message}", ex); }
        if (export.Format != SecretExport.FormatName || export.Version != 1) throw new SecretStoreException($"'{path}' is not a secret export this build reads (format '{export.Format}', version {export.Version}).");
        if (export.Kdf != "pbkdf2-sha256") throw new SecretStoreException($"'{path}' uses the key derivation '{export.Kdf}'; only pbkdf2-sha256 is read.");
        if (export.Iterations < SecretExport.Iterations || export.Iterations > SecretExport.MaximumIterations)
            throw new SecretStoreException($"'{path}' says {export.Iterations} PBKDF2 rounds; an export has {SecretExport.Iterations} to {SecretExport.MaximumIterations}. A weaker one was not made by this tool.");
        byte[] salt;
        try { salt = Convert.FromBase64String(export.Salt); }
        catch (FormatException ex) { throw new SecretStoreException($"'{path}' has a salt that is not base64.", ex); }
        if (salt.Length < 16) throw new SecretStoreException($"'{path}' has a salt of {salt.Length} bytes; an export has at least 16.");

        var key = Derive(passphrase, salt, export.Iterations);
        var plain = new List<(SecretName Name, SecretValue Value)>();
        try
        {
            foreach (var (name, entry) in export.Secrets)
            {
                if (!SecretName.TryParse(name, out var secretName)) throw new SecretStoreException($"'{path}' holds '{name}', which is not a secret name.");
                plain.Add((secretName, Open(secretName, entry, key, "export", path, wrongKeyHint: "Is the passphrase the one the file was exported with?")));
            }

            await using var _ = await LockAsync(ct);
            var file = await ReadAsync(ct);
            int imported = 0, skipped = 0, overwritten = 0;
            foreach (var (name, value) in plain)
            {
                var exists = file.Secrets.ContainsKey(name.ToString());
                if (exists && !overwrite) { skipped++; continue; }
                file.Secrets[name.ToString()] = Encrypt(name, value);
                if (exists) overwritten++; else imported++;
            }
            if (imported + overwritten > 0) await WriteAsync(file, ct);
            return new SecretImportResult(imported, skipped, overwritten);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            foreach (var (_, value) in plain) value.Dispose();
        }
    }

    private static void RequirePassphrase(SecretValue passphrase)
    {
        if (passphrase.Length < SecretExport.MinimumPassphraseLength)
            throw new ArgumentException($"The passphrase must be at least {SecretExport.MinimumPassphraseLength} characters: the export file can be guessed at for as long as anyone likes.", nameof(passphrase));
    }

    private static byte[] Derive(SecretValue passphrase, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(passphrase.Reveal().ToArray());
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeyLengthBytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
