using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;

namespace DbDataProxy.Server.Tls;

/// <summary>
/// Supplies the server's TLS certificate: a configured PFX, or a self-signed certificate that is generated on first
/// start, saved next to the service's data and reused until it nears expiry or the host names change.
/// </summary>
public static class CertificateProvider
{
    public const string SelfSignedFileName = "dbdataproxy-selfsigned.pfx";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public static X509Certificate2? Load(ProxyOptions options, string dataDirectory, ILogger logger, SecretsRuntime? secrets = null)
    {
        var tls = options.Tls;
        // The password may be a secret reference; resolve it for this load only.
        string? password = secrets?.ResolveSecret("Proxy:Tls:CertificatePassword", tls.CertificatePassword) ?? tls.CertificatePassword;
        switch (tls.Mode)
        {
            case TlsMode.None:
                logger.LogWarning("TLS is disabled; credentials and data travel unencrypted.");
                return null;

            case TlsMode.File:
                if (string.IsNullOrWhiteSpace(tls.CertificatePath))
                    throw new InvalidOperationException("Proxy:Tls:CertificatePath is required when Proxy:Tls:Mode is File.");
                return LoadPfx(tls.CertificatePath, password);

            default:
                return LoadOrCreateSelfSigned(tls, password, dataDirectory, logger);
        }
    }

    private static X509Certificate2 LoadOrCreateSelfSigned(TlsOptions tls, string? password, string dataDirectory, ILogger logger)
    {
        string path = tls.CertificatePath ?? Path.Combine(dataDirectory, SelfSignedFileName);
        var names = HostNames(tls);

        if (File.Exists(path))
        {
            try
            {
                var existing = LoadPfx(path, password);
                if (IsUsable(existing, names)) return existing;
                logger.LogInformation("Self-signed certificate at {Path} is expiring or its host names changed; regenerating.", path);
                existing.Dispose();
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "Could not read the self-signed certificate at {Path}; regenerating.", path);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var created = CreateSelfSigned(names, tls.SelfSignedValidityDays);
        File.WriteAllBytes(path, created.Export(X509ContentType.Pfx, password));
        string cerPath = Path.ChangeExtension(path, ".cer");
        File.WriteAllBytes(cerPath, created.Export(X509ContentType.Cert));
        logger.LogInformation(
            "Generated self-signed certificate {Thumbprint} for {Names}. Clients can trust it by importing {CerPath}.",
            created.Thumbprint, string.Join(", ", names), cerPath);
        return LoadPfx(path, password);
    }

    public static X509Certificate2 CreateSelfSigned(IReadOnlyCollection<string> names, int validityDays)
    {
        using var rsa = RSA.Create(2048);
        string commonName = names.First();
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(validityDays));
    }

    /// <summary>localhost, loopback, the machine name and any configured names, without duplicates.</summary>
    public static List<string> HostNames(TlsOptions tls)
    {
        var names = new List<string> { Environment.MachineName.ToLowerInvariant(), "localhost", "127.0.0.1", "::1" };
        try
        {
            string fqdn = Dns.GetHostEntry(string.Empty).HostName;
            if (!string.IsNullOrWhiteSpace(fqdn)) names.Add(fqdn.ToLowerInvariant());
        }
        catch (SocketException) { /* no DNS; the short name and localhost still work for most clients */ }
        names.AddRange(tls.Hostnames.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsUsable(X509Certificate2 cert, IReadOnlyCollection<string> names)
    {
        if (cert.NotAfter.ToUniversalTime() - DateTime.UtcNow < RenewBefore) return false;

        var sanExtension = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (sanExtension is null) return false;
        var present = sanExtension.EnumerateDnsNames().Concat(sanExtension.EnumerateIPAddresses().Select(i => i.ToString()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.All(n => present.Contains(IPAddress.TryParse(n, out var ip) ? ip.ToString() : n));
    }

    // Re-importing with an exportable, machine-independent key set avoids Windows SChannel "no credentials" failures
    // that affect ephemeral keys when used by a TLS server.
    private static X509Certificate2 LoadPfx(string path, string? password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
}
