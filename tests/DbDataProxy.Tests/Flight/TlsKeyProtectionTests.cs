using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SelfSignedCertificateGenerator = DbDataKit.Hosting.Certificates.SelfSignedCertificateGenerator;
using DbDataKit.Security.Acl;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Tls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DbDataProxy.Tests.Flight;

/// <summary>Finding H4: the generated PFX is protected, restricted, migrated from older versions and rotated.</summary>
public sealed class TlsKeyProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbproxy-tlskey-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Pfx => Path.Combine(_root, CertificateProvider.SelfSignedFileName);

    private string PasswordFile => Pfx + CertificateProvider.PasswordFileSuffix;

    private ProxyOptions Options(string? password = null) => new()
    {
        DataDirectory = _root,
        Tls = { Mode = TlsMode.SelfSigned, CertificatePassword = password },
    };

    [Fact]
    public void Generated_pfx_is_not_openable_with_an_empty_password_and_opens_with_the_stored_one()
    {
        using var cert = CertificateProvider.Load(Options(), _root, NullLogger.Instance)!;

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(Pfx, null));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(Pfx, ""));

        var store = new ProtectedPasswordFile(PasswordFile, []);
        Assert.True(store.TryRead(out var password));
        Assert.True(password.Length >= 40, "the password is random and long");
        using var reopened = X509CertificateLoader.LoadPkcs12FromFile(Pfx, password);
        Assert.Equal(cert.Thumbprint, reopened.Thumbprint);
    }

    [Fact]
    public void Password_file_does_not_contain_the_password_in_the_clear_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows-only; elsewhere the file is plain text with mode 0600.");
        using var cert = CertificateProvider.Load(Options(), _root, NullLogger.Instance)!;
        Assert.True(new ProtectedPasswordFile(PasswordFile, []).TryRead(out var password));
        Assert.DoesNotContain(password, File.ReadAllText(PasswordFile));
        Assert.Contains("dpapi-machine", File.ReadAllText(PasswordFile));
    }

    [Fact]
    public void Pfx_and_password_file_are_mode_0600_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        if (OperatingSystem.IsWindows()) return; // unreachable after the skip; lets the platform analyzer see the guard
        using var cert = CertificateProvider.Load(Options(), _root, NullLogger.Instance)!;
        Assert.Equal(AclHelper.DefaultFileMode, File.GetUnixFileMode(Pfx));
        Assert.Equal(AclHelper.DefaultFileMode, File.GetUnixFileMode(PasswordFile));
    }

    [Fact]
    public void Key_files_and_a_directory_the_proxy_creates_are_restricted_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        Assert.False(Directory.Exists(_root));
        using var cert = CertificateProvider.Load(Options(), _root, NullLogger.Instance)!;

        var identities = ProxyAcl.KeyFileIdentities("DbDataProxy");
        Assert.True(AclHelper.Verify(Pfx, identities).InSync, string.Join("; ", AclHelper.Verify(Pfx, identities).Drift));
        Assert.True(AclHelper.Verify(PasswordFile, identities).InSync);
        Assert.True(AclHelper.Verify(_root, identities).InSync, string.Join("; ", AclHelper.Verify(_root, identities).Drift));

        var acl = new FileSystemAclService();
        foreach (var path in new[] { Pfx, PasswordFile, _root })
        {
            var observed = acl.Read(path)!;
            Assert.True(observed.InheritanceProtected, path);
            Assert.DoesNotContain(observed.Aces, a => a.Allow && IsBroadGroup(a.Sid));
            // The service identity (virtual account, even before the service exists) and Administrators have access.
            Assert.Contains(observed.Aces, a => a.Allow && a.Sid == DbDataKit.Hosting.WindowsService.ServiceSid.FromName("DbDataProxy"));
            Assert.Contains(observed.Aces, a => a.Allow && a.Sid == "S-1-5-32-544");
        }
    }

    private static bool IsBroadGroup(string sid) => sid is "S-1-5-32-545" or "S-1-1-0" or "S-1-5-11";

    [Fact]
    public void Existing_pfx_with_an_empty_password_is_reencrypted_keeping_the_same_certificate()
    {
        Directory.CreateDirectory(_root);
        using var legacy = DbDataKit.Hosting.Certificates.SelfSignedCertificateGenerator.Create(
            DbDataKit.Hosting.Certificates.SelfSignedCertificateGenerator.Discover([], TimeProvider.System, 825));
        File.WriteAllBytes(Pfx, legacy.Export(X509ContentType.Pfx)); // what earlier versions wrote: no password
        string thumbprint = legacy.Thumbprint;

        using var loaded = CertificateProvider.Load(Options(), _root, NullLogger.Instance)!;

        Assert.Equal(thumbprint, loaded.Thumbprint); // clients that trust the .cer are unaffected
        Assert.True(File.Exists(PasswordFile));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(Pfx, null));
    }

    [Fact]
    public void A_configured_certificate_password_keeps_working_and_no_password_file_is_written()
    {
        Directory.CreateDirectory(_root);
        using var existing = DbDataKit.Hosting.Certificates.SelfSignedCertificateGenerator.Create(
            DbDataKit.Hosting.Certificates.SelfSignedCertificateGenerator.Discover([], TimeProvider.System, 825));
        File.WriteAllBytes(Pfx, existing.Export(X509ContentType.Pfx, "old-config-password"));

        using var loaded = CertificateProvider.Load(Options("old-config-password"), _root, NullLogger.Instance)!;

        Assert.Equal(existing.Thumbprint, loaded.Thumbprint);
        Assert.False(File.Exists(PasswordFile));
    }

    [Fact]
    public void Certificate_is_rotated_before_it_expires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var first = CertificateProvider.Load(Options(), _root, NullLogger.Instance, time)!;
        using var again = CertificateProvider.Load(Options(), _root, NullLogger.Instance, time)!;
        Assert.Equal(first.Thumbprint, again.Thumbprint);

        time.Advance(TimeSpan.FromDays(825 - 20)); // inside the 30 day renewal window
        Assert.True(CertificateProvider.NeedsRotation(first, time));
        using var rotated = CertificateProvider.Load(Options(), _root, NullLogger.Instance, time)!;

        Assert.NotEqual(first.Thumbprint, rotated.Thumbprint);
        Assert.True(rotated.NotAfter.ToUniversalTime() > first.NotAfter.ToUniversalTime());
        Assert.False(CertificateProvider.NeedsRotation(rotated, time));
    }

    [Fact]
    public void Rotation_service_swaps_the_certificate_the_server_presents()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = Options();
        var holder = new TlsCertificateHolder();
        holder.Set(CertificateProvider.Load(options, _root, NullLogger.Instance, time));
        var service = new CertificateRotationService(holder, options, _root, NullLogger<CertificateRotationService>.Instance, time);
        string before = holder.Current!.Thumbprint;

        Assert.False(service.Check()); // plenty of validity left
        Assert.Equal(before, holder.Current.Thumbprint);

        time.Advance(TimeSpan.FromDays(800));
        Assert.True(service.Check());
        Assert.NotEqual(before, holder.Current.Thumbprint);
        Assert.False(service.Check()); // fresh certificate: nothing more to do
    }

    [Fact]
    public void Lost_password_file_means_a_new_certificate_instead_of_a_failed_start()
    {
        using var first = CertificateProvider.Load(Options(), _root, NullLogger.Instance)!;
        File.Delete(PasswordFile);
        using var second = CertificateProvider.Load(Options(), _root, NullLogger.Instance)!;
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        Assert.True(File.Exists(PasswordFile));
    }

    [Fact]
    public void Host_names_and_validity_behaviour_are_unchanged()
    {
        var options = Options();
        options.Tls.Hostnames = ["proxy.example.com", "10.1.2.3"];
        options.Tls.SelfSignedValidityDays = 100;
        using var cert = CertificateProvider.Load(options, _root, NullLogger.Instance)!;
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("proxy.example.com", san.EnumerateDnsNames());
        Assert.Contains("10.1.2.3", san.EnumerateIPAddresses().Select(i => i.ToString()));
        Assert.Contains("::1", san.EnumerateIPAddresses().Select(i => i.ToString()));
        Assert.InRange((cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays, 99, 101);
        Assert.True(cert.HasPrivateKey);
    }
}

/// <summary><c>cert show|ensure|renew|check</c>.</summary>
public sealed class CertCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbproxy-certcli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private ProxyOptions Options(TlsMode mode = TlsMode.SelfSigned) => new() { DataDirectory = _root, Tls = { Mode = mode } };

    private static (int Code, string Out, string Error) Run(ProxyOptions options, string dir, params string[] args)
    {
        var console = new DbDataKit.Hosting.Cli.BufferConsole();
        int code = CertCli.Run(args, options, dir, console);
        return (code, console.OutWriter.ToString(), console.ErrorWriter.ToString());
    }

    [Fact]
    public void Show_never_creates_ensure_does_and_renew_replaces()
    {
        var options = Options();
        Assert.Equal(1, Run(options, _root, "show").Code);
        Assert.False(Directory.Exists(_root));

        var ensured = Run(options, _root, "ensure");
        Assert.Equal(0, ensured.Code);
        Assert.True(File.Exists(Path.Combine(_root, CertificateProvider.SelfSignedFileName)));

        var shown = Run(options, _root, "show");
        Assert.Equal(0, shown.Code);
        Assert.Contains("Thumbprint", shown.Out);
        Assert.Equal(0, Run(options, _root, "check").Code);

        var renewed = Run(options, _root, "renew");
        Assert.Equal(0, renewed.Code);
        Assert.NotEqual(shown.Out, Run(options, _root, "show").Out);
    }

    [Fact]
    public void Show_reads_a_pfx_from_an_earlier_version_without_changing_it()
    {
        Directory.CreateDirectory(_root);
        using var legacy = SelfSignedCertificateGenerator.Create(SelfSignedCertificateGenerator.Discover([], TimeProvider.System, 825));
        string path = Path.Combine(_root, CertificateProvider.SelfSignedFileName);
        byte[] bytes = legacy.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, bytes);

        var shown = Run(Options(), _root, "show");

        Assert.Equal(0, shown.Code);
        Assert.Contains(legacy.Thumbprint, shown.Out);
        Assert.Contains("empty password", shown.Out);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + CertificateProvider.PasswordFileSuffix));

        Assert.Equal(0, Run(Options(), _root, "ensure").Code); // ensure protects it, same certificate
        Assert.Contains(legacy.Thumbprint, Run(Options(), _root, "show").Out);
        Assert.True(File.Exists(path + CertificateProvider.PasswordFileSuffix));
    }

    [Fact]
    public void File_and_none_modes_are_handled_without_touching_anything()
    {
        Assert.Equal(1, Run(Options(TlsMode.None), _root, "show").Code);
        Assert.Equal(0, Run(Options(TlsMode.None), _root, "check").Code);

        Directory.CreateDirectory(_root);
        using var cert = SelfSignedCertificateGenerator.Create(SelfSignedCertificateGenerator.Discover([], TimeProvider.System, 825));
        string path = Path.Combine(_root, "supplied.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "pw"));
        var options = Options(TlsMode.File);
        options.Tls.CertificatePath = path;
        options.Tls.CertificatePassword = "pw";

        Assert.Contains(cert.Thumbprint, Run(options, _root, "show").Out);
        Assert.Equal(2, Run(options, _root, "renew").Code);
    }

    [Fact]
    public void Unknown_verb_prints_usage()
    {
        var result = Run(Options(), _root, "bogus");
        Assert.Equal(2, result.Code);
        Assert.Contains("cert <status|show|ensure|renew|check>", result.Error);
    }
}
