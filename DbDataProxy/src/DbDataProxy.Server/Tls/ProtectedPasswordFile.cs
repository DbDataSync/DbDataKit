using System.Security.Cryptography;
using System.Text;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Security.Acl;

namespace DbDataProxy.Server.Tls;

/// <summary>
/// Holds the random password of the generated PFX in a file only the service identities can read. On Windows the
/// value is encrypted with DPAPI (machine scope) so the file is useless when copied off the machine; elsewhere the
/// file is plain text with mode 0600. A file that cannot be decrypted (restored on another machine) counts as
/// missing, so a new password and certificate are generated.
/// </summary>
public sealed class ProtectedPasswordFile(string path, IReadOnlyList<string> identities) : ICertificateSecretSource
{
    private const string Header = "dbdataproxy-secret-v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DbDataProxy.Tls.PfxPassword.v1");

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
            if (lines.Length < 3 || lines[0] != Header) return false;
            switch (lines[1])
            {
                case "dpapi-machine" when OperatingSystem.IsWindows():
                    password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(lines[2]), Entropy, DataProtectionScope.LocalMachine));
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
            value = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));
        }
        else
        {
            kind = "plain";
            value = password;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        AclHelper.WriteAllBytesRestricted(Path, Encoding.UTF8.GetBytes($"{Header}\n{kind}\n{value}\n"), identities);
    }
}
