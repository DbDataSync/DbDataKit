using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Certificates;

/// <summary>
/// Holds the random password of a generated PFX in a file only the service identities can read. On Windows the
/// value is encrypted with DPAPI (machine scope) so the file is useless when copied off the machine; elsewhere the
/// file is plain text with mode 0600. A file that cannot be decrypted (restored on another machine) counts as
/// missing, so a new password and certificate are generated.
/// <para>
/// <paramref name="header"/> and <paramref name="entropy"/> identify the on-disk format and bind the DPAPI blob to a
/// product; a product that already wrote files under its own values passes them again so those files keep loading.
/// </para>
/// </summary>
public class ProtectedPasswordFile(
    string path, IReadOnlyList<string> identities,
    string header = "dbdatakit-secret-v1", string entropy = "DbDataKit.Tls.PfxPassword.v1") : ICertificateSecretSource
{
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(entropy);

    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    public string GetOrCreatePassword(string name, Func<string> generate)
    {
        if (TryRead(out var existing)) return existing;
        var created = generate();
        Write(created);
        return created;
    }

    public bool TryRead(out string password)
    {
        password = "";
        if (!File.Exists(Path)) return false;
        try
        {
            var lines = File.ReadAllLines(Path);
            if (lines.Length < 3 || lines[0] != header) return false;
            switch (lines[1])
            {
                case "dpapi-machine" when OperatingSystem.IsWindows():
                    password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(lines[2]), _entropy, DataProtectionScope.LocalMachine));
                    break;
                case "plain":
                    password = lines[2];
                    break;
                default:
                    return false;
            }

            return password.Length > 0;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
        {
            return false;
        }
    }

    public void Write(string password)
    {
        string kind, value;
        if (OperatingSystem.IsWindows())
        {
            kind = "dpapi-machine";
            value = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), _entropy, DataProtectionScope.LocalMachine));
        }
        else
        {
            kind = "plain";
            value = password;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        AclHelper.WriteAllBytesRestricted(Path, Encoding.UTF8.GetBytes($"{header}\n{kind}\n{value}\n"), identities);
    }

    /// <summary>
    /// Earlier versions wrote the PFX with an empty password. Re-encrypts such a file with a random password kept in
    /// <paramref name="store"/> (same key and thumbprint, so clients that trust the .cer are unaffected). Does nothing
    /// when the store already holds a password, or when the file is missing or unreadable (the provider replaces it).
    /// </summary>
    /// <returns>True when the file was re-encrypted.</returns>
    public static bool MigrateUnprotectedPfx(string pfxPath, ProtectedPasswordFile store, IReadOnlyList<string> identities)
    {
        if (!File.Exists(pfxPath) || store.Exists) return false;

        X509Certificate2 legacy;
        try
        {
            legacy = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            return false;
        }

        using (legacy)
        {
            string password = PfxPassword.Generate();
            string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(pfxPath))!, $".{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, legacy.Export(X509ContentType.Pfx, password));
                AclHelper.Restrict(temp, identities);
                store.Write(password);
                File.Move(temp, pfxPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            return true;
        }
    }
}
