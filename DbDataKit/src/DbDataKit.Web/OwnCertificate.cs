using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The certificate a server presents on its own address, learnt by connecting to it (plan 13, U08). Pairing binds to this certificate's fingerprint, and a server that is given its certificate by the platform
/// (HTTP.sys, a store, a managed file it did not load itself) can read it back the way a client will see it. The answer is cached once it is known; asking before the server listens answers null.
/// </summary>
public sealed class OwnCertificate(Func<Uri?> address, string? configured = null)
{
    private string? _known = string.IsNullOrWhiteSpace(configured) ? null : configured;

    /// <summary>The fingerprint, or null while the server cannot be reached on an https address.</summary>
    public string? Fingerprint => _known ??= Probe();

    private string? Probe()
    {
        if (address() is not { Scheme: "https" } url) return null;
        try
        {
            // 127.0.0.1 and [::1] reach a server that listens on "any" address; a name the server was told is its own is tried as it is
            var host = url.HostNameType == UriHostNameType.Dns && url.Host != "localhost" && url.Host != "*" && url.Host != "+" ? url.Host : "localhost";
            using var tcp = new TcpClient();
            tcp.Connect(host, url.Port);
            string? seen = null;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is not null) seen = PairingProof.CertificateFingerprint(certificate);
                return true;   // only reading it: nothing is sent over this connection
            });
            ssl.AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = host });
            return seen;
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or InvalidOperationException)
        {
            return null;
        }
    }
}
