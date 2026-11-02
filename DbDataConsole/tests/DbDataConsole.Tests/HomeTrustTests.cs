using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataConsole.Server;

namespace DbDataConsole.Tests;

/// <summary>Plan 20 O06: <c>--identity-home-cert</c>, how a console trusts a serving layer's own certificate (or the CA that issued it) for its identity home, and nothing else.</summary>
public sealed class HomeTrustTests
{
    private static X509Certificate2 SelfSigned(string name, bool ca = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        if (ca) request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void The_named_certificate_is_trusted_as_itself_and_as_an_issuer_and_no_other_certificate_or_name_is()
    {
        var home = SelfSigned("home.example");
        // the machine's trust still counts
        Assert.True(HomeTrust.Accepts(home, SelfSigned("other.example"), SslPolicyErrors.None));
        // the home's own self-signed certificate, which the machine does not trust
        Assert.True(HomeTrust.Accepts(home, X509CertificateLoader.LoadCertificate(home.RawData), SslPolicyErrors.RemoteCertificateChainErrors));
        // another self-signed certificate is not the home's
        Assert.False(HomeTrust.Accepts(home, SelfSigned("home.example"), SslPolicyErrors.RemoteCertificateChainErrors));
        // the right certificate for the wrong name is refused
        Assert.False(HomeTrust.Accepts(home, X509CertificateLoader.LoadCertificate(home.RawData), SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));

        // a CA: a certificate it issued is trusted, one it did not is not
        var ca = SelfSigned("Home CA", ca: true);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issued = new CertificateRequest("CN=home.example", leafKey, HashAlgorithmName.SHA256)
            .Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
        Assert.True(HomeTrust.Accepts(ca, issued, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(HomeTrust.Accepts(SelfSigned("Another CA", ca: true), issued, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
