using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Certificates.Tests;

/// <summary>
/// Phase 130, tier 2 — <see cref="ManagedSelfSignedCertificate"/>'s pieces: generation (SANs, validity),
/// storage (path, encryption, permissions, git-ignore) and renewal through the shared provider. The renewal
/// threshold itself is the shared <c>CertificateExpiry</c> rule, tested with the shared library. No Windows
/// dependency anywhere here — the whole point of this class.
/// </summary>
public sealed class ManagedSelfSignedCertificateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-managed-selfsigned-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void Generate_CarriesTheHostAndLocalhostAsSans_AndTheRequestedValidity()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 90);

        var dnsNames = CertificateSanReader.GetDnsNames(certificate);
        Assert.Contains("dbdatasync.example.com", dnsNames);
        Assert.Contains("localhost", dnsNames);

        var validity = certificate.NotAfter - certificate.NotBefore;
        Assert.InRange(validity.TotalDays, 89.9, 90.1);
    }

    [Fact]
    public void Generate_HostAlreadyLocalhost_DoesNotDuplicateTheSan()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("localhost", validityDays: 30);

        var dnsNames = CertificateSanReader.GetDnsNames(certificate);
        Assert.Single(dnsNames, name => string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Write_ProducesAFileLoadableWithNoPassword_AtTheWellKnownPath()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);

        ManagedSelfSignedCertificate.Write(_root, certificate);

        var path = ManagedSelfSignedCertificate.PfxPath(_root);
        Assert.True(File.Exists(path));

        using var reloaded = X509CertificateLoader.LoadPkcs12FromFile(path, ManagedSelfSignedCertificate.PasswordFor(_root, path));
        Assert.Equal(certificate.Thumbprint, reloaded.Thumbprint);
    }

    [Fact]
    public void Write_EncryptsThePfx_AndKeepsItsPasswordInAFileBesideIt()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);
        ManagedSelfSignedCertificate.Write(_root, certificate);

        var path = ManagedSelfSignedCertificate.PfxPath(_root);
        Assert.Equal(path + ".pwd", ManagedSelfSignedCertificate.PasswordPath(_root));
        Assert.True(File.Exists(ManagedSelfSignedCertificate.PasswordPath(_root)));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(path, password: null));
        Assert.NotNull(ManagedSelfSignedCertificate.PasswordFor(_root, path));
    }

    [Fact]
    public void PasswordFor_AnyOtherPath_IsNull()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);
        ManagedSelfSignedCertificate.Write(_root, certificate);

        Assert.Null(ManagedSelfSignedCertificate.PasswordFor(_root, Path.Combine(_root, "other.pfx")));
    }

    [Fact]
    public void LoadOrRenew_NothingThere_CreatesAnEncryptedCertificate()
    {
        using var certificate = ManagedSelfSignedCertificate.LoadOrRenew(_root, "dbdatasync.example.com", 90, out var renewed);

        Assert.True(renewed);
        Assert.Contains("dbdatasync.example.com", CertificateSanReader.GetDnsNames(certificate));
        Assert.Contains("localhost", CertificateSanReader.GetDnsNames(certificate));
        Assert.Contains("tls/", File.ReadAllLines(Path.Combine(_root, ".gitignore")));
    }

    [Fact]
    public void LoadOrRenew_CurrentCertificate_IsLeftAlone()
    {
        using var first = ManagedSelfSignedCertificate.LoadOrRenew(_root, "dbdatasync.example.com", 90, out _);
        using var second = ManagedSelfSignedCertificate.LoadOrRenew(_root, "dbdatasync.example.com", 90, out var renewed);

        Assert.False(renewed);
        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void LoadOrRenew_WithinThirtyDaysOfExpiry_ReplacesIt()
    {
        using var first = ManagedSelfSignedCertificate.LoadOrRenew(_root, "dbdatasync.example.com", 90, out _);

        using var second = ManagedSelfSignedCertificate.LoadOrRenew(
            _root, "dbdatasync.example.com", 90, out var renewed, new FixedTime(DateTimeOffset.UtcNow.AddDays(70)));

        Assert.True(renewed);
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void LoadOrRenew_AFileFromBeforeThePasswordExisted_IsReplaced()
    {
        // The unencrypted layout earlier versions wrote: no password file, empty-password PFX. Not migrated —
        // replaced, with a new key, the first time the renewal check runs.
        using var legacy = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 90);
        Directory.CreateDirectory(Path.GetDirectoryName(ManagedSelfSignedCertificate.PfxPath(_root))!);
        File.WriteAllBytes(ManagedSelfSignedCertificate.PfxPath(_root), legacy.Export(X509ContentType.Pfx));

        using var replaced = ManagedSelfSignedCertificate.LoadOrRenew(_root, "dbdatasync.example.com", 90, out var renewed);

        Assert.True(renewed);
        Assert.NotEqual(legacy.Thumbprint, replaced.Thumbprint);
        Assert.NotNull(ManagedSelfSignedCertificate.PasswordFor(_root, ManagedSelfSignedCertificate.PfxPath(_root)));
    }

    [Fact]
    public void Renew_ReplacesTheCertificateNow()
    {
        using var first = ManagedSelfSignedCertificate.Renew(_root, "dbdatasync.example.com", 90);
        using var second = ManagedSelfSignedCertificate.Renew(_root, "dbdatasync.example.com", 90);

        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        var path = ManagedSelfSignedCertificate.PfxPath(_root);
        using var stored = X509CertificateLoader.LoadPkcs12FromFile(path, ManagedSelfSignedCertificate.PasswordFor(_root, path));
        Assert.Equal(second.Thumbprint, stored.Thumbprint);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Write_OnNonWindows_RestrictsTheFileToTheOwner()
    {
        if (OperatingSystem.IsWindows())
            return; // 0600 is a Unix permission concept — nothing to assert on Windows.

        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);
        ManagedSelfSignedCertificate.Write(_root, certificate);

        var mode = File.GetUnixFileMode(ManagedSelfSignedCertificate.PfxPath(_root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Write_AddsTlsToGitignore_OnceEvenAcrossMultipleWrites()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);

        ManagedSelfSignedCertificate.Write(_root, certificate);
        ManagedSelfSignedCertificate.Write(_root, certificate);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Single(lines, line => line == "tls/");
    }

    [Fact]
    public void Write_PreservesAnAlreadyExistingGitignore()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "state.db\n");
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);

        ManagedSelfSignedCertificate.Write(_root, certificate);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Contains("state.db", lines);
        Assert.Contains("tls/", lines);
    }

    [Fact]
    public void PfxPath_IsStableAndNormalized_ForTheSameRepoRoot()
    {
        var first = ManagedSelfSignedCertificate.PfxPath(_root);
        var second = ManagedSelfSignedCertificate.PfxPath(_root + Path.DirectorySeparatorChar);

        Assert.Equal(first, second);
        Assert.EndsWith(Path.Combine("tls", "dbdatasync.pfx"), first);
    }
}
