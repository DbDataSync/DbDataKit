using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting.Certificates.Windows;

namespace DbDataSync.Certificates;

/// <summary>
/// Issues a self-signed certificate over a fresh named key and installs it in the Windows store — the store-side
/// counterpart of <see cref="CertificateBuilder.CreateSelfSigned(CertificateSpec)"/>, which hands back an ephemeral
/// key. If anything fails before the store references the certificate, the key is deleted so no attempt leaves an
/// orphaned container behind.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StoreSelfSignedCertificate
{
    public static X509Certificate2 Install(CertificateSpec spec, StoreLocation location)
    {
        var keyName = CertificateKeys.NewKeyName();
        var machineKey = location == StoreLocation.LocalMachine;
        var key = CertificateKeys.Create(keyName, machineKey);
        try
        {
            using (key)
            using (var certificate = CertificateBuilder.CreateSelfSigned(spec, key))
            {
                return CertificateStore.Install(certificate, location);
            }
        }
        catch
        {
            CertificateKeys.Delete(keyName, machineKey);
            throw;
        }
    }
}
