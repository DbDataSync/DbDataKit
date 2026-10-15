using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting.Certificates;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataKit.Security.Acl;

namespace DbDataProxy.Server.Tls;

/// <summary>
/// Supplies the server's TLS certificate: a configured PFX, or a self-signed certificate that is generated on first
/// start, saved next to the service's data and reused until it nears expiry or the host names change.
/// The generated PFX is encrypted with a random password kept in a protected file (see
/// <see cref="ProtectedPasswordFile"/>), restricted to the service identities, and rotated before it expires.
/// </summary>
public static class CertificateProvider
{
    public const string SelfSignedFileName = "dbdataproxy-selfsigned.pfx";
    public const string PasswordFileSuffix = ".pwd";
    public const int RenewBeforeDays = 30;

    public static X509Certificate2? Load(ProxyOptions options, string dataDirectory, ILogger logger, TimeProvider? time = null, string? serviceName = null)
    {
        var tls = options.Tls;
        switch (tls.Mode)
        {
            case TlsMode.None:
                logger.LogWarning("TLS is disabled; credentials and data travel unencrypted.");
                return null;

            case TlsMode.File:
                if (string.IsNullOrWhiteSpace(tls.CertificatePath))
                    throw new InvalidOperationException("Proxy:Tls:CertificatePath is required when Proxy:Tls:Mode is File.");
                return LoadPfx(tls.CertificatePath, tls.CertificatePassword);

            default:
                return LoadOrCreateSelfSigned(tls, dataDirectory, logger, time, serviceName ?? ProxyHost.ServiceName);
        }
    }

    /// <summary>Everything the shared certificate provider and the <c>cert</c> command need for the self-signed mode.</summary>
    public sealed record SelfSignedSetup(CertificateOptions Options, ICertificateSecretSource Secrets, DbDataKit.Hosting.Certificates.CertificateProvider Provider, string PfxPath, string PasswordFile, bool PasswordConfigured);

    public static SelfSignedSetup CreateSetup(TlsOptions tls, string dataDirectory, string serviceName, TimeProvider? time = null)
    {
        string path = tls.CertificatePath ?? Path.Combine(dataDirectory, SelfSignedFileName);
        var identities = ProxyAcl.KeyFileIdentities(serviceName);
        string passwordFile = path + PasswordFileSuffix;

        // An explicitly configured password is honoured (the PFX of an existing deployment keeps loading); the
        // protected random password is only used when none is configured.
        bool configured = !string.IsNullOrEmpty(tls.CertificatePassword);
        ICertificateSecretSource secrets = configured
            ? new DelegateSecretSource((_, _) => tls.CertificatePassword!)
            : new ProtectedPasswordFile(passwordFile, identities);

        var options = new CertificateOptions
        {
            Mode = DbDataKit.Hosting.Certificates.CertificateMode.SelfSigned,
            Path = path,
            ValidityDays = tls.SelfSignedValidityDays,
            RenewBeforeDays = RenewBeforeDays,
        };
        foreach (var host in tls.Hostnames.Where(h => !string.IsNullOrWhiteSpace(h))) options.AdditionalHostNames.Add(host.Trim());

        var provider = new DbDataKit.Hosting.Certificates.CertificateProvider(
            secrets, time: time, restrictFileAccess: file => AclHelper.Restrict(file, identities));
        return new SelfSignedSetup(options, secrets, provider, path, passwordFile, configured);
    }

    private static X509Certificate2 LoadOrCreateSelfSigned(TlsOptions tls, string dataDirectory, ILogger logger, TimeProvider? time, string serviceName)
    {
        var setup = CreateSetup(tls, dataDirectory, serviceName, time);
        var identities = ProxyAcl.KeyFileIdentities(serviceName);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(setup.PfxPath));
        if (directory is not null && !Directory.Exists(directory))
            AclHelper.EnsureRestrictedDirectory(directory, identities);

        MigrateUnprotectedPfx(setup, logger, identities);

        var before = File.Exists(setup.PfxPath) ? File.GetLastWriteTimeUtc(setup.PfxPath) : (DateTime?)null;
        var certificate = setup.Provider.LoadAsync(setup.Options).GetAwaiter().GetResult();
        if (before is null || File.GetLastWriteTimeUtc(setup.PfxPath) != before)
        {
            logger.LogInformation(
                "Generated self-signed certificate {Thumbprint}, valid until {NotAfter:u}. Clients can trust it by importing {CerPath}.",
                certificate.Thumbprint, certificate.NotAfter.ToUniversalTime(), Path.ChangeExtension(setup.PfxPath, ".cer"));
        }

        if (setup.PasswordConfigured)
            logger.LogWarning("Proxy:Tls:CertificatePassword protects the self-signed certificate; remove it to let the proxy keep a random password in a protected file instead.");
        return certificate;
    }

    /// <summary>
    /// Earlier versions wrote the PFX with an empty password. Re-encrypt such a file with a random protected password
    /// (same key and thumbprint, so clients that trust the .cer are unaffected) the first time it is seen.
    /// </summary>
    public static bool MigrateUnprotectedPfx(SelfSignedSetup setup, ILogger logger, IReadOnlyList<string> identities)
    {
        if (setup.PasswordConfigured || !File.Exists(setup.PfxPath) || setup.Secrets is not ProtectedPasswordFile store || store.Exists) return false;

        X509Certificate2 legacy;
        try
        {
            legacy = X509CertificateLoader.LoadPkcs12FromFile(setup.PfxPath, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            return false; // unreadable: the provider replaces it
        }

        using (legacy)
        {
            string password = PfxPassword.Generate();
            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(setup.PfxPath))!, $".{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, legacy.Export(X509ContentType.Pfx, password));
                AclHelper.Restrict(temp, identities);
                store.Write(password);
                File.Move(temp, setup.PfxPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            logger.LogInformation("Re-encrypted {Path} with a random password kept in a protected file (it had an empty password).", setup.PfxPath);
            return true;
        }
    }

    /// <summary>When the self-signed certificate in use needs replacing at <paramref name="now"/>.</summary>
    public static bool NeedsRotation(X509Certificate2 certificate, TimeProvider? time = null) =>
        CertificateExpiry.Evaluate(certificate, (time ?? TimeProvider.System).GetUtcNow(), RenewBeforeDays).NeedsRotation;

    // Re-importing with an exportable, machine-independent key set avoids Windows SChannel "no credentials" failures
    // that affect ephemeral keys when used by a TLS server.
    private static X509Certificate2 LoadPfx(string path, string? password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
}
