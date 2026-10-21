using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DbDataKit.Hosting.Certificates.Windows;

/// <summary>
/// Named, persisted, non-exportable CNG keys — the private key behind every certificate this codebase
/// installs into the Windows store, self-signed or issued by a CA.
/// <para>
/// **The key is the certificate's key, not a staging copy.** A certificate in the store points at a
/// named key container; creating the key by name first, building or receiving the certificate over it,
/// and adding the pair to the store is the whole install. Nothing exports the key at any point, which
/// is what its <c>ExportPolicy = None</c> promises. Deleting the key deletes the certificate's private
/// key, so a key is deleted only when no certificate will ever use it (a denied or failed request, or
/// an install that threw before the store referenced it).
/// </para>
/// <para>
/// **Why a named key for an AD CS request in particular.** A CSR carries only the *public* key; the CA
/// never hands the private key back, so whatever process eventually retrieves the issued certificate
/// needs the *same* private key that signed the original request, still around. A request needing
/// certificate-manager approval can sit pending for hours or days, quite possibly outliving the CLI
/// invocation that submitted it — an in-memory RSA object cannot survive that, but a machine-wide CNG
/// key container, opened again by name, can. <see cref="PendingEnrollmentStore"/> is the other half: it
/// remembers which key name goes with which CA request id.
/// </para>
/// <para>
/// **Not tested against a real CA** — same honesty as <see cref="AdcsEnrollment"/> — but the key
/// lifecycle itself (create a named persisted key, open it back up by name, delete it) needs no CA at
/// all and is covered by a <c>Category=Windows</c> test.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class CertificateKeys
{
    private const string KeyNamePrefix = "DbDataSync-";

    /// <summary>A fresh key name, unique per certificate — for an enrollment also the correlation id
    /// <see cref="PendingEnrollmentStore"/> keys its record on.</summary>
    public static string NewKeyName() => $"{KeyNamePrefix}{Guid.NewGuid():N}";

    /// <summary>
    /// Creates and persists an RSA key under <paramref name="keyName"/>, with no export policy set — so
    /// nothing, including <see cref="CertificateStore.Install"/>, can ever export the private key of a
    /// certificate built over it.
    /// <para>
    /// <paramref name="machineKey"/> defaults to true — every real call site (<c>CertCommand</c>'s
    /// <c>enroll</c>/<c>renew</c>/<c>retrieve</c>) issues into <c>LocalMachine\My</c>, which already
    /// needs an elevated prompt the same way <c>dbdatasync service install</c> does, so a machine-wide key
    /// costs nothing there it wasn't already paying. It is <c>false</c> only in
    /// <c>CertificateKeysWindowsTests</c>, which — like the doc's own "install into
    /// <c>CurrentUser\My</c>" test — deliberately needs no elevation to run.
    /// </para>
    /// </summary>
    public static RSA Create(string keyName, bool machineKey = true)
    {
        var creationParameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyUsage = CngKeyUsages.AllUsages,
            KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
            ExportPolicy = CngExportPolicies.None,
        };
        creationParameters.Parameters.Add(new CngProperty(
            "Length", BitConverter.GetBytes(CertificateSpec.DefaultKeySizeBits), CngPropertyOptions.None));

        var cngKey = CngKey.Create(CngAlgorithm.Rsa, keyName, creationParameters);
        return new RSACng(cngKey);
    }

    /// <summary>Reopens a key created by <see cref="Create"/> — null if no such key exists, which is
    /// an ordinary outcome for an unrecognised or already-collected request id, not a reason to
    /// throw.</summary>
    public static RSA? Open(string keyName, bool machineKey = true)
    {
        var openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (!CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions))
            return null;

        var cngKey = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions);
        return new RSACng(cngKey);
    }

    /// <summary>Removes the persisted key — called once an enrollment resolves (issued, denied, or
    /// failed) so a pending key never outlives the request it was for.</summary>
    public static void Delete(string keyName, bool machineKey = true)
    {
        var openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (!CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions))
            return;

        using var cngKey = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions);
        cngKey.Delete();
    }
}
