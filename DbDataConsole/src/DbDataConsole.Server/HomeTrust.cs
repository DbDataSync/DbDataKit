using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace DbDataConsole.Server;

/// <summary>
/// How the console trusts its identity home's certificate (plan 20 O06): the machine's trust, and, when <c>--identity-home-cert</c> names one, that certificate as well, either as the home's own (a serving layer's
/// self-signed certificate, compared exactly) or as a CA that issued the home's (a chain built on it alone). The host name is checked in every case.
/// </summary>
public static class HomeTrust
{
    public static X509Certificate2 Load(string path) => X509CertificateLoader.LoadCertificateFromFile(path);

    public static HttpMessageHandler Handler(string? trustedCertificate)
    {
        if (trustedCertificate is null) return new HttpClientHandler();
        var trusted = Load(trustedCertificate);
        return new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => Accepts(trusted, certificate, errors) };
    }

    /// <summary>Whether a home's certificate is acceptable: valid by the machine's trust, or the trusted one itself, or issued by it; never with a name that does not match.</summary>
    public static bool Accepts(X509Certificate2 trusted, X509Certificate2? presented, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (presented is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        if (presented.RawDataMemory.Span.SequenceEqual(trusted.RawDataMemory.Span)) return true;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trusted);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(presented);
    }
}
