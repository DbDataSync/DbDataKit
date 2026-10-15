using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates;

public sealed record SelfSignedRequest(
    string Subject,
    IReadOnlyList<string> DnsNames,
    IReadOnlyList<IPAddress> IpAddresses,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

public static class SelfSignedCertificateGenerator
{
    /// <summary>Machine name, FQDN (when resolvable), localhost, loopback addresses, plus extras.</summary>
    public static SelfSignedRequest Discover(IEnumerable<string> extraNames, TimeProvider time, int validityDays, Func<string, string?>? fqdnLookup = null)
    {
        var names = new List<string> { Environment.MachineName.ToLowerInvariant(), "localhost" };
        var fqdn = (fqdnLookup ?? DefaultFqdn)(Environment.MachineName);
        if (!string.IsNullOrWhiteSpace(fqdn)) names.Add(fqdn.ToLowerInvariant());
        names.AddRange(extraNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToLowerInvariant()));

        var dns = new List<string>();
        var ips = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        foreach (var n in names.Distinct(StringComparer.Ordinal))
        {
            if (IPAddress.TryParse(n, out var ip))
            {
                if (!ips.Contains(ip)) ips.Add(ip);
            }
            else
            {
                dns.Add(n);
            }
        }

        var now = time.GetUtcNow();
        return new SelfSignedRequest($"CN={dns[0]}", dns, ips, now.AddMinutes(-5), now.AddDays(validityDays));
    }

    /// <summary>RSA 2048, SHA-256, serverAuth, not a CA. The returned certificate holds the private key.</summary>
    public static X509Certificate2 Create(SelfSignedRequest request)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(request.Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in request.DnsNames) san.AddDnsName(d);
        foreach (var ip in request.IpAddresses) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(request.NotBefore, request.NotAfter);
        // Round-trip so the key is attached in a form that can be exported again.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    /// <summary>DNS and IP SANs of a certificate.</summary>
    public static (IReadOnlySet<string> Dns, IReadOnlySet<IPAddress> Ips) ReadSans(X509Certificate2 cert)
    {
        var dns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<IPAddress>();
        var ext = cert.Extensions["2.5.29.17"];
        if (ext is X509SubjectAlternativeNameExtension san)
        {
            foreach (var d in san.EnumerateDnsNames()) dns.Add(d);
            foreach (var i in san.EnumerateIPAddresses()) ips.Add(i);
        }

        return (dns, ips);
    }

    private static string? DefaultFqdn(string host)
    {
        try
        {
            var e = Dns.GetHostEntry(host);
            return e.HostName.Contains('.') ? e.HostName : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

public enum CertificateState
{
    Valid,
    ExpiringSoon,
    Expired,
    NotYetValid,
}

public sealed record CertificateStatus(CertificateState State, int DaysRemaining, DateTimeOffset NotAfter, string Thumbprint)
{
    public bool NeedsRotation => State != CertificateState.Valid;
}

public static class CertificateExpiry
{
    public static CertificateStatus Evaluate(X509Certificate2 cert, DateTimeOffset now, int renewBeforeDays)
    {
        var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var notBefore = new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var days = (int)Math.Floor((notAfter - now).TotalDays);
        var state = now < notBefore ? CertificateState.NotYetValid
            : now >= notAfter ? CertificateState.Expired
            : days < renewBeforeDays ? CertificateState.ExpiringSoon
            : CertificateState.Valid;
        return new CertificateStatus(state, days, notAfter, cert.Thumbprint);
    }
}

/// <summary>Readiness component: Healthy, Degraded when rotation is due, Unhealthy when expired.</summary>
public sealed class CertificateReadinessProbe(Func<X509Certificate2?> current, int renewBeforeDays, TimeProvider? time = null) : IReadinessProbe
{
    public string Name => "certificate";

    public Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken)
    {
        var cert = current();
        if (cert is null) return Task.FromResult(new ComponentHealth(Name, HealthStatus.Healthy, "no TLS certificate in use"));
        var s = CertificateExpiry.Evaluate(cert, (time ?? TimeProvider.System).GetUtcNow(), renewBeforeDays);
        var status = s.State switch
        {
            CertificateState.Valid => HealthStatus.Healthy,
            CertificateState.ExpiringSoon => HealthStatus.Degraded,
            _ => HealthStatus.Unhealthy,
        };
        return Task.FromResult(new ComponentHealth(Name, status, $"{s.State}, {s.DaysRemaining} day(s) remaining"));
    }
}
