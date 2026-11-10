using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Cli;

/// <summary>
/// The HTTP client behind every "is it answering?" probe in the tool — <c>dbdatasync health check</c>, the readiness
/// binding check, and the one an update waits on before it decides whether to keep the new version.
/// <para>
/// **It accepts any server certificate, and reports what was wrong with it.** This is the same call
/// <see cref="HealthCommand"/>'s own comment makes about sessions, one layer down: a probe that demands anything
/// beyond "is it serving" is a probe that reports a healthy application as unreachable. A self-signed certificate,
/// or one whose name does not match the URL the console is configured on, is a real misconfiguration — but it is
/// not this service being down, and an update that rolls a working version back over it has destroyed an upgrade
/// to report a fault in a certificate. Nothing here authenticates a remote party: the request carries no
/// credentials, and the answer is <c>{"status":"ok"}</c>.
/// </para>
/// <para>
/// The certificate's problem comes back **alongside** the answer rather than instead of it, so a caller reports
/// both — the service is up, and the certificate still needs fixing.
/// </para>
/// </summary>
internal sealed class HealthProbeClient : IDisposable
{
    /// <param name="timeout">How long one request may take.</param>
    /// <param name="pinned">The public certificate the server made for itself (<c>tls/dbdatasync.cer</c>): the one
    /// certificate that is trusted on top of the machine's own trust, and only when the server presents exactly it.
    /// Null trusts nothing extra.</param>
    /// <param name="strict">When true a certificate that neither validates nor is the pinned one fails the request
    /// (<see cref="HttpRequestException"/>), as an ordinary client would; the update and readiness probes leave it false.</param>
    public HealthProbeClient(TimeSpan timeout, X509Certificate2? pinned = null, bool strict = false)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (pinned is not null && certificate is not null
                        && string.Equals(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
                            pinned.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), StringComparison.OrdinalIgnoreCase)
                        && (errors & SslPolicyErrors.RemoteCertificateNotAvailable) == 0)
                    {
                        // The very certificate the server wrote for itself: the chain and the name are not asked about it.
                        CertificateProblem = null;
                        return true;
                    }

                    CertificateProblem = Describe(certificate, errors);
                    return !strict || errors == SslPolicyErrors.None;
                },
            },
        };

        Client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    public HttpClient Client { get; }

    /// <summary>What was wrong with the server's certificate; null when there was nothing wrong with it, and also
    /// when no TLS handshake has happened at all — a plain-HTTP URL, or a request that never connected.</summary>
    public string? CertificateProblem { get; private set; }

    public void Dispose() => Client.Dispose();

    /// <summary>
    /// The probe <c>dbdatasync health check</c> uses. An https address on this machine (loopback) trusts the certificate the server
    /// wrote in <paramref name="root"/> (<c>tls/dbdatasync.cer</c>) and nothing else is switched off: a server presenting another
    /// certificate there is reported as before (it answers, the certificate is named), because it is this machine's own port.
    /// An https address anywhere else gets normal validation and fails on a certificate that does not validate.
    /// </summary>
    public static HealthProbeClient ForCheck(string url, string root, TimeSpan timeout)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
            return new HealthProbeClient(timeout);

        if (!address.IsLoopback)
            return new HealthProbeClient(timeout, pinned: null, strict: true);

        X509Certificate2? pinned = null;
        try
        {
            var file = Path.ChangeExtension(DbDataSync.Certificates.ManagedSelfSignedCertificate.PfxPath(root), ".cer");
            if (File.Exists(file))
                pinned = X509CertificateLoader.LoadCertificateFromFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // an unreadable copy pins nothing; the probe then reports the certificate as it always did
        }

        return new HealthProbeClient(timeout, pinned);
    }

    private static string? Describe(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return null;

        var faults = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            faults.Add("the server offered none");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            faults.Add("its name does not match this URL");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            faults.Add("it is not trusted here — a self-signed certificate is not, unless a copy of it is installed in this machine's Trusted Root store");

        var subject = certificate?.Subject is { Length: > 0 } text ? $" ({text})" : "";
        return $"the TLS certificate{subject} did not validate: {string.Join("; ", faults)}";
    }
}
