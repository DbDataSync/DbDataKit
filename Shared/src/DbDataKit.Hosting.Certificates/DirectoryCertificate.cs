using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates;

/// <summary>
/// The self-signed certificate a server that has nothing else keeps in its own directory (plan 13, R01 and U07): <c>tls.pfx</c>, with its password in <c>tls-pfx-password</c> beside it, readable only by the server's account.
/// Made on first use and read after. A client pins its fingerprint when it pairs, so nothing else about the certificate needs to be trusted.
/// </summary>
public static class DirectoryCertificate
{
    public const string PfxFileName = "tls.pfx";
    public const string PasswordFileName = "tls-pfx-password";

    /// <summary>Loads the certificate in <paramref name="directory"/>, making it (and the password file) when there is none.</summary>
    public static X509Certificate2 Load(string directory)
    {
        Directory.CreateDirectory(directory);
        var passwordFile = Path.Combine(directory, PasswordFileName);
        var provider = new CertificateProvider(
            new DelegateSecretSource((_, generate) =>
            {
                if (File.Exists(passwordFile)) return File.ReadAllText(passwordFile).Trim();
                var made = generate();
                File.WriteAllText(passwordFile, made);
                Restrict(passwordFile);
                return made;
            }),
            restrictFileAccess: Restrict);
        var options = new CertificateOptions { Mode = CertificateMode.SelfSigned, Path = Path.Combine(directory, PfxFileName) };
        return provider.LoadAsync(options).GetAwaiter().GetResult();
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
