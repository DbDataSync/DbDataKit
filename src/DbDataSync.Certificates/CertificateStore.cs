using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Certificates;

/// <summary>
/// The Windows certificate store — install, list, find. Every member here is genuinely Windows-only
/// (<see cref="X509Store"/> off Windows throws <see cref="PlatformNotSupportedException"/> the moment
/// it is opened), so every one is marked and every caller in this codebase checks
/// <see cref="OperatingSystem.IsWindows"/> first — see <c>CertCommand</c> and
/// <c>CertificateExpiryService</c>, which are the only two.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CertificateStore
{
    /// <summary>
    /// Installs a certificate into <c>&lt;location&gt;\My</c>. The certificate must already carry a
    /// private key that lives in a named CNG container (see <see cref="CertificateKeys"/>); nothing is
    /// exported, re-imported or created here, so a non-exportable key installs exactly like any other.
    /// <para>
    /// The store only has to be pointed at the key: adding a certificate whose key is a named container
    /// links the two, and <see cref="PrivateKeyAccess.Grant"/> later finds the key file by that name. A
    /// certificate whose key is ephemeral (an in-memory <c>RSA.Create</c>) would land in the store with
    /// no key behind it, so it is refused rather than installed half-working.
    /// </para>
    /// <para>
    /// Returns the certificate as the store now holds it, which is what proves it landed.
    /// </para>
    /// </summary>
    public static X509Certificate2 Install(X509Certificate2 certificate, StoreLocation location)
    {
        if (certificate.GetRSAPrivateKey() is not RSACng { Key.UniqueName: not null })
        {
            throw new ArgumentException(
                "The certificate's private key is not a named, persisted CNG key, so installing it would leave " +
                "the store holding a certificate with no key behind it. Create the key with CertificateKeys and " +
                "build or receive the certificate over it.",
                nameof(certificate));
        }

        // The store is opened first, before anything else can have a side effect. The caller's key
        // already exists on disk — under %ProgramData%\Microsoft\Crypto\RSA\MachineKeys for
        // LocalMachine, which is deliberately writable without elevation, while LocalMachine\My is not —
        // so a failure to reach the store has to surface here, where the caller still knows to delete the
        // key it made (see InstallSelfSigned), not after more has happened.
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);

        // The thumbprint is looked up again so the caller gets the store's own copy: it is the one that
        // is still valid after the input is disposed, and finding it is the check that Add took.
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
            .Cast<X509Certificate2>()
            .First();
    }

    /// <summary>
    /// Issues a self-signed certificate over a fresh named key and installs it — the store-side
    /// counterpart of <see cref="CertificateBuilder.CreateSelfSigned(CertificateSpec)"/>, which hands back an
    /// ephemeral key. If anything fails before the store references the certificate, the key is deleted so
    /// no attempt leaves an orphaned container behind.
    /// </summary>
    public static X509Certificate2 InstallSelfSigned(CertificateSpec spec, StoreLocation location)
    {
        var keyName = CertificateKeys.NewKeyName();
        var machineKey = location == StoreLocation.LocalMachine;
        var key = CertificateKeys.Create(keyName, machineKey);
        try
        {
            using (key)
            using (var certificate = CertificateBuilder.CreateSelfSigned(spec, key))
            {
                return Install(certificate, location);
            }
        }
        catch
        {
            CertificateKeys.Delete(keyName, machineKey);
            throw;
        }
    }

    /// <summary>Every certificate in <c>&lt;location&gt;\My</c> whose Enhanced Key Usage includes
    /// server authentication — the ones <c>dbdatasync config cert list</c> shows, since a certificate without
    /// this EKU cannot be bound for TLS regardless of what else is in the store.</summary>
    public static IReadOnlyList<X509Certificate2> ListServerAuthCertificates(StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);

        return [.. store.Certificates.Where(HasServerAuthEku)];
    }

    public static X509Certificate2? FindByThumbprint(string thumbprint, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);

        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>
    /// The certificate a subject-name lookup finds — how Kestrel itself resolves
    /// <c>Kestrel:Certificates:Default:Subject</c>, and so how <c>dbdatasync config cert status</c> and
    /// <see cref="CertificateExpiryService"/> find the certificate that is actually bound. When more
    /// than one certificate shares the subject (a renewal that has not yet replaced the old one in
    /// config), the one with the latest <c>NotAfter</c> wins — the same "prefer the newest" rule
    /// Kestrel's own certificate resolver applies.
    /// </summary>
    public static X509Certificate2? FindBySubject(string subjectCommonName, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);

        var matches = store.Certificates.Find(
            X509FindType.FindBySubjectName, subjectCommonName, validOnly: false);

        return matches.Count == 0
            ? null
            : matches.OrderByDescending(c => c.NotAfter).First();
    }

    private static bool HasServerAuthEku(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension eku)
                continue;

            foreach (var oid in eku.EnhancedKeyUsages)
            {
                if (oid.Value == CertificateSpec.ServerAuthenticationEku)
                    return true;
            }
        }

        return false;
    }
}
