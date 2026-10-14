using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Cli;

/// <summary>
/// The HTTP client behind every "is it answering?" probe in the tool — <c>dbdatasync health</c>, the readiness
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
    public HealthProbeClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    CertificateProblem = Describe(certificate, errors);
                    return true;
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
