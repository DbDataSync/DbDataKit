using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbData.Hosting.Certificates.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbdata-cert-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public class SelfSignedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CertificateProvider Provider(InMemorySecretSource secrets, FakeTime time, Action<string>? restrict = null) =>
        new(secrets, time: time, restrictFileAccess: restrict, fqdnLookup: _ => "box.corp.example");

    private static CertificateOptions Options(TempDir dir)
    {
        var o = new CertificateOptions { Mode = CertificateMode.SelfSigned, Path = dir.File("server.pfx") };
        o.AdditionalHostNames.Add("proxy.internal");
        return o;
    }

    [Fact]
    public async Task Creates_pfx_with_random_password_and_public_cer()
    {
        using var dir = new TempDir();
        var secrets = new InMemorySecretSource();
        var restricted = new List<string>();
        var cert = await Provider(secrets, new FakeTime(T0), restricted.Add).LoadAsync(Options(dir));

        Assert.True(File.Exists(dir.File("server.pfx")));
        Assert.True(File.Exists(dir.File("server.cer")));
        var password = Assert.Single(secrets.Values).Value;
        Assert.True(password.Length >= 40);
        Assert.Single(restricted); // ACL hook runs on the file before it is moved into place
        Assert.DoesNotContain(restricted, p => p.EndsWith("server.pfx"));

        // The PFX is not openable with an empty password.
        var bytes = File.ReadAllBytes(dir.File("server.pfx"));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(bytes, ""));
        Assert.NotNull(X509CertificateLoader.LoadPkcs12(bytes, password));

        // The .cer is public only.
        using var pub = X509CertificateLoader.LoadCertificateFromFile(dir.File("server.cer"));
        Assert.False(pub.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, pub.Thumbprint);
    }

    [Fact]
    public async Task Certificate_shape_rsa2048_sha256_with_expected_sans()
    {
        using var dir = new TempDir();
        var cert = await Provider(new InMemorySecretSource(), new FakeTime(T0)).LoadAsync(Options(dir));

        Assert.Equal(2048, cert.GetRSAPublicKey()!.KeySize);
        Assert.Equal("sha256RSA", cert.SignatureAlgorithm.FriendlyName);
        var (dns, ips) = SelfSignedCertificateGenerator.ReadSans(cert);
        Assert.Contains(Environment.MachineName.ToLowerInvariant(), dns);
        Assert.Contains("localhost", dns);
        Assert.Contains("box.corp.example", dns);
        Assert.Contains("proxy.internal", dns);
        Assert.Contains(IPAddress.Loopback, ips);
        Assert.Contains(IPAddress.IPv6Loopback, ips);
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    [Fact]
    public async Task Reuses_until_rotation_window_then_regenerates()
    {
        using var dir = new TempDir();
        var time = new FakeTime(T0);
        var provider = Provider(new InMemorySecretSource(), time);
        var o = Options(dir);

        var first = await provider.LoadAsync(o);
        var again = await provider.LoadAsync(o);
        Assert.Equal(first.Thumbprint, again.Thumbprint);

        time.Now = T0.AddDays(340); // 25 days left, inside the 30 day window
        var rotated = await provider.LoadAsync(o);
        Assert.NotEqual(first.Thumbprint, rotated.Thumbprint);
    }

    [Fact]
    public async Task Regenerates_when_required_names_change()
    {
        using var dir = new TempDir();
        var provider = Provider(new InMemorySecretSource(), new FakeTime(T0));
        var o = Options(dir);
        var first = await provider.LoadAsync(o);
        o.AdditionalHostNames.Add("new-name.example");
        var second = await provider.LoadAsync(o);
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        Assert.Contains("new-name.example", SelfSignedCertificateGenerator.ReadSans(second).Dns);
    }

    [Fact]
    public async Task Replaces_a_pfx_it_cannot_open()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("server.pfx"), "garbage");
        var cert = await Provider(new InMemorySecretSource(), new FakeTime(T0)).LoadAsync(Options(dir));
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public async Task Null_or_empty_password_from_the_callback_is_rejected()
    {
        using var dir = new TempDir();
        foreach (var bad in new string?[] { null, "" })
        {
            var source = new DelegateSecretSource((_, _) => bad!);
            var provider = new CertificateProvider(source, time: new FakeTime(T0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadAsync(Options(dir)));
        }

        Assert.False(File.Exists(dir.File("server.pfx")));
    }

    [Fact]
    public void Generated_passwords_are_unique_and_long()
    {
        var a = PfxPassword.Generate();
        Assert.NotEqual(a, PfxPassword.Generate());
        Assert.True(a.Length >= 43);
    }
}

public class ExpiryTests
{
    [Fact]
    public void Evaluates_states()
    {
        using var cert = SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
            "CN=t", ["t"], [IPAddress.Loopback], DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(60)));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(CertificateState.Valid, CertificateExpiry.Evaluate(cert, now, 30).State);
        Assert.Equal(CertificateState.ExpiringSoon, CertificateExpiry.Evaluate(cert, now.AddDays(40), 30).State);
        Assert.Equal(CertificateState.Expired, CertificateExpiry.Evaluate(cert, now.AddDays(61), 30).State);
        Assert.Equal(CertificateState.NotYetValid, CertificateExpiry.Evaluate(cert, now.AddDays(-20), 30).State);
        Assert.True(CertificateExpiry.Evaluate(cert, now.AddDays(40), 30).NeedsRotation);
    }

    [Fact]
    public async Task Readiness_probe_maps_state_to_health()
    {
        using var cert = SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
            "CN=t", ["t"], [IPAddress.Loopback], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10)));
        Assert.Equal(HealthStatus.Degraded, (await new CertificateReadinessProbe(() => cert, 30).CheckAsync(default)).Status);
        Assert.Equal(HealthStatus.Healthy, (await new CertificateReadinessProbe(() => cert, 5).CheckAsync(default)).Status);
        Assert.Equal(HealthStatus.Healthy, (await new CertificateReadinessProbe(() => null, 5).CheckAsync(default)).Status);
        var expired = new FakeTime(DateTimeOffset.UtcNow.AddDays(30));
        Assert.Equal(HealthStatus.Unhealthy, (await new CertificateReadinessProbe(() => cert, 5, expired).CheckAsync(default)).Status);
    }
}

public class ModeTests
{
    [Fact]
    public async Task File_mode_loads_pfx_with_password_from_secret_source()
    {
        using var dir = new TempDir();
        using var made = SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
            "CN=f", ["f"], [IPAddress.Loopback], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(100)));
        File.WriteAllBytes(dir.File("c.pfx"), made.Export(X509ContentType.Pfx, "pw-123"));
        var secrets = new InMemorySecretSource();
        secrets.Values["pfx"] = "pw-123";

        var cert = await new CertificateProvider(secrets).LoadAsync(new CertificateOptions { Mode = CertificateMode.File, Path = dir.File("c.pfx"), PasswordSecretName = "pfx" });
        Assert.Equal(made.Thumbprint, cert.Thumbprint);
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public async Task File_mode_loads_pem_pair()
    {
        using var dir = new TempDir();
        using var made = SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
            "CN=p", ["p"], [IPAddress.Loopback], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(100)));
        File.WriteAllText(dir.File("c.pem"), made.ExportCertificatePem());
        File.WriteAllText(dir.File("k.pem"), made.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        var cert = await new CertificateProvider(new InMemorySecretSource())
            .LoadAsync(new CertificateOptions { Mode = CertificateMode.File, Path = dir.File("c.pem"), KeyPath = dir.File("k.pem") });
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public async Task Enroll_mode_needs_an_enroller_and_passes_names()
    {
        var provider = new CertificateProvider(new InMemorySecretSource());
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.LoadAsync(new CertificateOptions { Mode = CertificateMode.Enroll }));

        var fake = new FakeEnroller();
        var withEnroller = new CertificateProvider(new InMemorySecretSource(), fake, fqdnLookup: _ => null);
        var o = new CertificateOptions { Mode = CertificateMode.Enroll, Template = "WebServer" };
        o.AdditionalHostNames.Add("svc.corp");
        await withEnroller.LoadAsync(o);
        Assert.Equal("WebServer", fake.Last!.Template);
        Assert.Contains("svc.corp", fake.Last.DnsNames);
    }

    private sealed class FakeEnroller : ICertificateEnroller
    {
        public EnrollRequest? Last { get; private set; }

        public Task<X509Certificate2> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
                "CN=e", ["e"], [IPAddress.Loopback], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10))));
        }
    }

    [Theory]
    [InlineData("AB CD:12", "ABCD12")]
    [InlineData("abcdef", "ABCDEF")]
    public void Thumbprint_normalisation(string input, string expected) =>
        Assert.Equal(expected, CertificateProvider.NormalizeThumbprint(input));

    [Fact]
    public async Task Store_mode_requires_a_selector_and_finds_current_user_cert()
    {
        var provider = new CertificateProvider(new InMemorySecretSource());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadAsync(new CertificateOptions { Mode = CertificateMode.Store }));

        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses the Windows CurrentUser certificate store");
        using var made = SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
            "CN=dbdatakit-store-test", ["dbdatakit-store-test"], [IPAddress.Loopback], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10)));
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(made);
        try
        {
            var found = await provider.LoadAsync(new CertificateOptions
            {
                Mode = CertificateMode.Store,
                StoreLocation = StoreLocation.CurrentUser,
                Thumbprint = made.Thumbprint.ToLowerInvariant(),
            });
            Assert.Equal(made.Thumbprint, found.Thumbprint);
        }
        finally
        {
            store.Remove(made);
        }
    }
}

public class PlainHttpGuardTests
{
    [Theory]
    [InlineData("http://localhost:5000", false)]
    [InlineData("http://127.0.0.1:5000", false)]
    [InlineData("http://[::1]:5000", false)]
    [InlineData("http://unix:/run/app.sock", false)]
    [InlineData("https://0.0.0.0:443", false)]
    [InlineData("http://0.0.0.0:80", true)]
    [InlineData("http://+:8080", true)]
    [InlineData("http://*:8080", true)]
    [InlineData("http://[::]:8080", true)]
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("http://myhost.corp:8080", true)]
    [InlineData("https://localhost:5001;http://+:80", true)]
    public void Detects_off_loopback_plain_http(string urls, bool violation)
    {
        Assert.Equal(violation, PlainHttpGuard.FindViolations([urls]).Count > 0);
        if (violation)
        {
            Assert.Throws<InvalidOperationException>(() => PlainHttpGuard.Enforce([urls], false));
        }

        PlainHttpGuard.Enforce([urls], allowInsecureOverride: true);
    }
}
