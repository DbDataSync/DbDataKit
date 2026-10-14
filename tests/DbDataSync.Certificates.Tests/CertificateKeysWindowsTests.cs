using System.Runtime.Versioning;

namespace DbDataSync.Certificates.Tests;

/// <summary>
/// The key-persistence primitive behind AD CS enrollment's pending-request handling — see
/// <see cref="CertificateKeys"/>'s own doc comment for why it exists. Needs no CA at all, unlike
/// <see cref="AdcsEnrollment"/> itself, so it gets real coverage even though the enrollment path around
/// it does not.
/// <para>
/// Every call passes <c>machineKey: false</c> — the same "no elevation needed" reasoning as the doc's
/// own <c>CurrentUser\My</c> store test: a machine-scoped CNG key needs an elevated prompt to create,
/// which real usage already has (every real call site targets <c>LocalMachine\My</c>) but a test
/// shouldn't have to.
/// </para>
/// </summary>
[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CertificateKeysWindowsTests
{
    [WindowsOnlyFact]
    public void Create_ThenOpen_ReturnsAUsableKeyOfTheRightSize()
    {
        var keyName = CertificateKeys.NewKeyName();
        using (CertificateKeys.Create(keyName, machineKey: false)) { }

        using var reopened = CertificateKeys.Open(keyName, machineKey: false);

        try
        {
            Assert.NotNull(reopened);
            Assert.Equal(CertificateSpec.DefaultKeySizeBits, reopened!.KeySize);
        }
        finally
        {
            CertificateKeys.Delete(keyName, machineKey: false);
        }
    }

    [WindowsOnlyFact]
    public void Open_UnknownKeyName_ReturnsNull()
    {
        var result = CertificateKeys.Open($"DbDataSync-Nonexistent-{Guid.NewGuid():N}", machineKey: false);

        Assert.Null(result);
    }

    [WindowsOnlyFact]
    public void Delete_ThenOpen_ReturnsNull()
    {
        var keyName = CertificateKeys.NewKeyName();
        using (CertificateKeys.Create(keyName, machineKey: false)) { }

        CertificateKeys.Delete(keyName, machineKey: false);

        Assert.Null(CertificateKeys.Open(keyName, machineKey: false));
    }

    [WindowsOnlyFact]
    public void Delete_OnAKeyThatWasNeverCreated_DoesNotThrow()
    {
        CertificateKeys.Delete($"DbDataSync-Nonexistent-{Guid.NewGuid():N}", machineKey: false);
    }
}
