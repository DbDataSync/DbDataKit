using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates.Tests;

public sealed class ProtectedPasswordFileTests
{
    [Fact]
    public void GetOrCreate_generates_once_and_reads_back()
    {
        using var dir = new TempDir();
        var store = new ProtectedPasswordFile(dir.File("x.pwd"), []);
        var calls = 0;

        var first = store.GetOrCreatePassword("pfx", () => { calls++; return "secret-1"; });
        var second = new ProtectedPasswordFile(dir.File("x.pwd"), []).GetOrCreatePassword("pfx", () => { calls++; return "other"; });

        Assert.Equal("secret-1", first);
        Assert.Equal("secret-1", second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void File_written_under_other_format_identifiers_counts_as_missing()
    {
        using var dir = new TempDir();
        new ProtectedPasswordFile(dir.File("x.pwd"), [], "product-a-v1", "A").Write("secret");

        Assert.False(new ProtectedPasswordFile(dir.File("x.pwd"), [], "product-b-v1", "B").TryRead(out _));
        Assert.True(new ProtectedPasswordFile(dir.File("x.pwd"), [], "product-a-v1", "A").TryRead(out var back));
        Assert.Equal("secret", back);
    }

    [Fact]
    public void Password_is_not_in_the_clear_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows-only; elsewhere the file is plain text with mode 0600.");
        using var dir = new TempDir();
        new ProtectedPasswordFile(dir.File("x.pwd"), []).Write("secret-value");
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

        var store = new ProtectedPasswordFile(dir.File("c.pfx.pwd"), []);
        Assert.True(ProtectedPasswordFile.MigrateUnprotectedPfx(pfx, store, []));

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(pfx, ""));
        Assert.True(store.TryRead(out var password));
        using var reopened = X509CertificateLoader.LoadPkcs12FromFile(pfx, password);
        Assert.Equal(thumbprint, reopened.Thumbprint);

        Assert.False(ProtectedPasswordFile.MigrateUnprotectedPfx(pfx, store, []), "second call is a no-op");
    }

    [Fact]
    public void Migrate_ignores_a_missing_or_unreadable_pfx()
    {
        using var dir = new TempDir();
        var store = new ProtectedPasswordFile(dir.File("c.pfx.pwd"), []);
        Assert.False(ProtectedPasswordFile.MigrateUnprotectedPfx(dir.File("missing.pfx"), store, []));

        File.WriteAllText(dir.File("bad.pfx"), "not a pfx");
        Assert.False(ProtectedPasswordFile.MigrateUnprotectedPfx(dir.File("bad.pfx"), store, []));
        Assert.False(store.Exists);
    }
}
