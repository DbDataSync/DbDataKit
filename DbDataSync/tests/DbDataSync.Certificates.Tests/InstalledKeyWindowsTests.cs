using DbDataKit.Hosting.Certificates.Windows;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace DbDataSync.Certificates.Tests;

/// <summary>
/// Phase 204P: a certificate in the store never has its private key exported. The first test is the
/// reported failure — installing a certificate whose key is non-exportable — which threw "Key not valid
/// for use in specified state" when <c>Install</c> round-tripped through a PFX.
/// <para>
/// Everything installs into <c>CurrentUser\My</c> with user-scoped keys, so none of it needs elevation.
/// The CA is replaced by a certificate built over a named key and then reduced to its public half, which
/// is exactly the shape <c>CopyWithPrivateKey</c> receives from <c>AdcsEnrollment</c>.
/// </para>
/// </summary>
[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class InstalledKeyWindowsTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), "ds-installed-key-" + Guid.NewGuid());
    private readonly List<X509Certificate2> _installed = [];
    private readonly List<string> _keyNames = [];

    public InstalledKeyWindowsTests() => Directory.CreateDirectory(_repoRoot);

    public void Dispose()
    {
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in _installed)
            {
                store.Remove(certificate);
                certificate.Dispose();
            }
        }

        foreach (var keyName in _keyNames)
            CertificateKeys.Delete(keyName, machineKey: false);

        GitTempDirectory.DeleteRecursively(_repoRoot);
    }

    private static CertificateSpec Spec() =>
        new($"dbdatasync-test-{Guid.NewGuid():N}.example.com", [$"dbdatasync-test-{Guid.NewGuid():N}.example.com"], 365, "DbDataSync Test");

    private string NewKey()
    {
        var name = CertificateKeys.NewKeyName();
        _keyNames.Add(name);
        return name;
    }

    /// <summary>What a CA hands back: the public certificate alone, for a key this machine still holds.</summary>
    private X509Certificate2 PublicOnlyCertificateFor(CertificateSpec spec, string keyName)
    {
        using var key = CertificateKeys.Create(keyName, machineKey: false);
        using var selfSigned = CertificateBuilder.CreateSelfSigned(spec, key);
        return X509CertificateLoader.LoadCertificate(selfSigned.RawData);
    }


    /// <summary>The premise of the whole phase, pinned: exporting a PFX from a certificate over a
    /// non-exportable key throws the reported "Key not valid for use in specified state", which is what
    /// <c>Install</c> used to do to every enrolled certificate.</summary>
    [WindowsOnlyFact]
    public void ACertificateOverANonExportableKey_CannotBeExportedToAPfx()
    {
        var keyName = NewKey();
        using var key = CertificateKeys.Create(keyName, machineKey: false);
        using var certificate = CertificateBuilder.CreateSelfSigned(Spec(), key);

        var ex = Assert.ThrowsAny<CryptographicException>(() => certificate.Export(X509ContentType.Pfx, "x"));
        Assert.Contains("not valid for use in specified state", ex.Message);
    }
    [WindowsOnlyFact]
    public void Install_ACertificateWhoseKeyIsNonExportable_Installs()
    {
        var spec = Spec();
        var keyName = NewKey();
        using var key = CertificateKeys.Create(keyName, machineKey: false);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec, key);

        var installed = CertificateStore.Install(certificate, StoreLocation.CurrentUser);
        _installed.Add(installed);

        var found = CertificateStore.FindByThumbprint(installed.Thumbprint, StoreLocation.CurrentUser);
        Assert.NotNull(found);
        Assert.True(found!.HasPrivateKey);
    }

    [WindowsOnlyFact]
    public void Install_KeepsTheKeyNonExportable_AndUsableForSigning()
    {
        var spec = Spec();
        var keyName = NewKey();
        using var key = CertificateKeys.Create(keyName, machineKey: false);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec, key);

        var installed = CertificateStore.Install(certificate, StoreLocation.CurrentUser);
        _installed.Add(installed);

        using var installedKey = installed.GetRSAPrivateKey()!;
        Assert.NotEmpty(installedKey.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.ThrowsAny<CryptographicException>(() => installedKey.ExportParameters(includePrivateParameters: true));
    }

    [WindowsOnlyFact]
    public void Install_ACertificateReceivedFromACa_WithItsKeyReattached_Installs()
    {
        var spec = Spec();
        var keyName = NewKey();
        using var caAnswer = PublicOnlyCertificateFor(spec, keyName);
        Assert.False(caAnswer.HasPrivateKey);

        using var key = CertificateKeys.Open(keyName, machineKey: false)!;
        using var withKey = caAnswer.CopyWithPrivateKey(key);
        var installed = CertificateStore.Install(withKey, StoreLocation.CurrentUser);
        _installed.Add(installed);

        Assert.True(CertificateStore.FindByThumbprint(installed.Thumbprint, StoreLocation.CurrentUser)!.HasPrivateKey);
        PrivateKeyAccess.Grant(installed, WindowsIdentity.GetCurrent().Name);
    }

    [WindowsOnlyFact]
    public void Install_ACertificateWithAnEphemeralKey_IsRefused_AndLeavesNothingInTheStore()
    {
        using var ephemeral = CertificateBuilder.CreateSelfSigned(Spec());

        Assert.Throws<ArgumentException>(() => CertificateStore.Install(ephemeral, StoreLocation.CurrentUser));

        Assert.Null(CertificateStore.FindByThumbprint(ephemeral.Thumbprint, StoreLocation.CurrentUser));
    }

    [WindowsOnlyFact]
    public void InstallSelfSigned_LeavesAnInstalledCertificateWithANonExportableKey()
    {
        var installed = StoreSelfSignedCertificate.Install(Spec(), StoreLocation.CurrentUser);
        _installed.Add(installed);

        using var key = installed.GetRSAPrivateKey()!;
        Assert.ThrowsAny<CryptographicException>(() => key.ExportParameters(includePrivateParameters: true));
        Assert.Equal("DbDataSync Test", CertificateStore.FindByThumbprint(installed.Thumbprint, StoreLocation.CurrentUser)!.FriendlyName);
    }

    private EnrollmentResult Issued(CertificateSpec spec, string keyName, string requestId = "42") =>
        new(EnrollmentOutcome.Issued, requestId, PublicOnlyCertificateFor(spec, keyName), "Certificate issued.");

    [WindowsOnlyFact]
    public void Complete_AnIssuedCertificate_IsInstalled_AndItsRecordAndNothingElseIsRemoved()
    {
        var spec = Spec();
        var keyName = NewKey();
        var result = Issued(spec, keyName);

        var completion = EnrollmentCompletionHandler.Complete(
            _repoRoot, result, keyName, spec, @"ca\Test CA", location: StoreLocation.CurrentUser);
        _installed.Add(completion.Installed!);

        Assert.Equal(CompletionOutcome.Installed, completion.Outcome);
        Assert.Null(PendingEnrollmentStore.Find(_repoRoot, "42"));
        // The key is the installed certificate's key, so it has to still be there.
        using var key = completion.Installed!.GetRSAPrivateKey()!;
        Assert.NotEmpty(key.SignData([1], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.NotNull(CertificateKeys.Open(keyName, machineKey: false));
    }

    [WindowsOnlyFact]
    public void Complete_WhenInstallingFails_KeepsTheRecordAndKey_AndNamesTheRetrieveCommand()
    {
        var spec = Spec();
        var keyName = NewKey();
        var result = Issued(spec, keyName, requestId: "77");

        var completion = EnrollmentCompletionHandler.Complete(
            _repoRoot, result, keyName, spec, @"ca\Test CA",
            grantKeyAccess: _ => throw new InvalidOperationException("no access for you"),
            location: StoreLocation.CurrentUser);

        Assert.Equal(CompletionOutcome.InstallFailed, completion.Outcome);
        Assert.Equal("77", completion.RequestId);
        Assert.Contains("dbdatasync config cert retrieve --request-id 77", completion.Message);
        Assert.Contains("no access for you", completion.Message);
        Assert.NotNull(PendingEnrollmentStore.Find(_repoRoot, "77"));
        Assert.NotNull(CertificateKeys.Open(keyName, machineKey: false));

        // The grant ran after the certificate was added; take it out again so the test leaves the store as it found it.
        var thumbprint = result.Certificate!.Thumbprint;
        if (CertificateStore.FindByThumbprint(thumbprint, StoreLocation.CurrentUser) is { } left)
            _installed.Add(left);
    }

    [WindowsOnlyFact]
    public void Complete_APendingRequest_IsSavedForRetrieve()
    {
        var spec = Spec();
        var keyName = NewKey();
        using var key = CertificateKeys.Create(keyName, machineKey: false);
        var pending = new EnrollmentResult(EnrollmentOutcome.Pending, "9", null, "pending approval");

        var completion = EnrollmentCompletionHandler.Complete(
            _repoRoot, pending, keyName, spec, @"ca\Test CA", location: StoreLocation.CurrentUser);

        Assert.Equal(CompletionOutcome.Pending, completion.Outcome);
        Assert.Equal(keyName, PendingEnrollmentStore.Find(_repoRoot, "9")!.KeyName);
    }

    [WindowsOnlyFact]
    public void Complete_ADeniedRequest_DeletesItsKey()
    {
        var spec = Spec();
        var keyName = NewKey();
        using (CertificateKeys.Create(keyName, machineKey: false)) { }
        var denied = new EnrollmentResult(EnrollmentOutcome.Denied, null, null, "denied");

        var completion = EnrollmentCompletionHandler.Complete(
            _repoRoot, denied, keyName, spec, @"ca\Test CA", location: StoreLocation.CurrentUser);

        Assert.Equal(CompletionOutcome.Rejected, completion.Outcome);
        Assert.Null(CertificateKeys.Open(keyName, machineKey: false));
    }
}
