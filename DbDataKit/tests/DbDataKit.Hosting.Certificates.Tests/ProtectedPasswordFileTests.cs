using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates.Tests;

public sealed class ProtectedPasswordFileTests
{
    // Windows restricts files to a named set of identities and refuses an empty one; elsewhere the list is ignored.
    private static readonly string[] Identities = ["CurrentUser", "SYSTEM", "Administrators"];

    [Fact]
    public void GetOrCreate_generates_once_and_reads_back()
    {
        using var dir = new TempDir();
        var store = new ProtectedPasswordFile(dir.File("x.pwd"), Identities);
        var calls = 0;

        var first = store.GetOrCreatePassword("pfx", () => { calls++; return "secret-1"; });
        var second = new ProtectedPasswordFile(dir.File("x.pwd"), Identities).GetOrCreatePassword("pfx", () => { calls++; return "other"; });

        Assert.Equal("secret-1", first);
        Assert.Equal("secret-1", second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void File_written_under_other_format_identifiers_counts_as_missing()
    {
        using var dir = new TempDir();
        new ProtectedPasswordFile(dir.File("x.pwd"), Identities, "product-a-v1", "A").Write("secret");

        Assert.False(new ProtectedPasswordFile(dir.File("x.pwd"), Identities, "product-b-v1", "B").TryRead(out _));
        Assert.True(new ProtectedPasswordFile(dir.File("x.pwd"), Identities, "product-a-v1", "A").TryRead(out var back));
        Assert.Equal("secret", back);
    }

    [Fact]
    public void Password_is_not_in_the_clear_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows-only; elsewhere the file is plain text with mode 0600.");
        using var dir = new TempDir();
        new ProtectedPasswordFile(dir.File("x.pwd"), Identities).Write("secret-value");
        Assert.DoesNotContain("secret-value", File.ReadAllText(dir.File("x.pwd")));
    }

    [Fact]
    public void Migrate_reencrypts_an_empty_password_pfx_keeping_the_thumbprint()
    {
        using var dir = new TempDir();
        var pfx = dir.File("c.pfx");
        string thumbprint;
        using (var cert = SelfSignedCertificateGenerator.Create(new SelfSignedRequest("CN=t", ["t"], [System.Net.IPAddress.Loopback], DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90))))
        {
            thumbprint = cert.Thumbprint;
            File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, ""));
        }

        var store = new ProtectedPasswordFile(dir.File("c.pfx.pwd"), Identities);
        Assert.True(ProtectedPasswordFile.MigrateUnprotectedPfx(pfx, store, Identities));

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(pfx, ""));
        Assert.True(store.TryRead(out var password));
        using var reopened = X509CertificateLoader.LoadPkcs12FromFile(pfx, password);
        Assert.Equal(thumbprint, reopened.Thumbprint);

        Assert.False(ProtectedPasswordFile.MigrateUnprotectedPfx(pfx, store, Identities), "second call is a no-op");
    }

    [Fact]
    public void Migrate_ignores_a_missing_or_unreadable_pfx()
    {
        using var dir = new TempDir();
        var store = new ProtectedPasswordFile(dir.File("c.pfx.pwd"), Identities);
        Assert.False(ProtectedPasswordFile.MigrateUnprotectedPfx(dir.File("missing.pfx"), store, Identities));

        File.WriteAllText(dir.File("bad.pfx"), "not a pfx");
        Assert.False(ProtectedPasswordFile.MigrateUnprotectedPfx(dir.File("bad.pfx"), store, Identities));
        Assert.False(store.Exists);
    }
}

public sealed class DnsNameTests
{
    [Fact]
    public void ReadDnsNames_returns_the_san_dns_names_in_order()
    {
        using var cert = SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
            "CN=b", ["b.example", "a.example", "localhost"], [System.Net.IPAddress.Loopback],
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30)));

        Assert.Equal(["b.example", "a.example", "localhost"], SelfSignedCertificateGenerator.ReadDnsNames(cert));
    }

    [Fact]
    public void ReadDnsNames_is_empty_without_a_san_extension()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=none", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Empty(SelfSignedCertificateGenerator.ReadDnsNames(cert));
    }
}
