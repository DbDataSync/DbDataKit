using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataProxy.Client;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Tls;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataProxy.Tests.Flight;

public sealed class TlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbproxy-tls-" + Guid.NewGuid().ToString("N"));

    public TlsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ProxyOptions Options(params string[] extraHosts) => new()
    {
        DataDirectory = _dir,
        Tls = { Mode = TlsMode.SelfSigned, Hostnames = [.. extraHosts] },
    };

    [Fact]
    public void Generates_a_self_signed_certificate_on_first_use_and_reuses_it()
    {
        using var first = CertificateProvider.Load(Options(), _dir, NullLogger.Instance)!;
        using var second = CertificateProvider.Load(Options(), _dir, NullLogger.Instance)!;

        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.True(first.HasPrivateKey);
        Assert.True(File.Exists(Path.Combine(_dir, CertificateProvider.SelfSignedFileName)));
        Assert.True(File.Exists(Path.Combine(_dir, "dbdataproxy-selfsigned.cer")));
    }

    [Fact]
    public void Certificate_covers_localhost_the_machine_name_and_configured_names()
    {
        using var cert = CertificateProvider.Load(Options("proxy.example.com", "10.1.2.3"), _dir, NullLogger.Instance)!;
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        var dns = san.EnumerateDnsNames().ToList();
        var ips = san.EnumerateIPAddresses().Select(i => i.ToString()).ToList();

        Assert.Contains("localhost", dns);
        Assert.Contains(Environment.MachineName, dns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("proxy.example.com", dns);
        Assert.Contains("127.0.0.1", ips);
        Assert.Contains("10.1.2.3", ips);
    }

    [Fact]
    public void Certificate_is_regenerated_when_the_host_names_change()
    {
        using var first = CertificateProvider.Load(Options(), _dir, NullLogger.Instance)!;
        using var second = CertificateProvider.Load(Options("new-name.example.com"), _dir, NullLogger.Instance)!;
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void Corrupt_certificate_file_is_replaced()
    {
        File.WriteAllText(Path.Combine(_dir, CertificateProvider.SelfSignedFileName), "garbage");
        using var cert = CertificateProvider.Load(Options(), _dir, NullLogger.Instance)!;
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void Tls_none_means_no_certificate()
    {
        var options = new ProxyOptions { Tls = { Mode = TlsMode.None } };
        Assert.Null(CertificateProvider.Load(options, _dir, NullLogger.Instance));
    }

    [Fact]
    public async Task Server_accepts_tls_clients_that_trust_the_generated_certificate()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Tls:Mode"] = "SelfSigned",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
        };
        await using var app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await app.StartAsync(TestContext.Current.CancellationToken);

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("0.0.0.0", "127.0.0.1");
        Assert.StartsWith("https://", address);

        // Clients trust the server by importing the .cer the service writes next to its certificate.
        using var trusted = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_dir, "dbdataproxy-selfsigned.cer"));
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && cert.GetCertHashString() == trusted.GetCertHashString(),
            },
        };
        using var client = new ProxyFlightClient(address, new GrpcChannelOptions { HttpHandler = handler });
        await client.AuthenticateAsync("alice", "pw", TestContext.Current.CancellationToken);
        Assert.StartsWith("Bearer ", client.Authorization);
    }

    [Fact]
    public async Task Clients_that_do_not_trust_the_certificate_are_refused()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Tls:Mode"] = "SelfSigned",
        };
        await using var app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await app.StartAsync(TestContext.Current.CancellationToken);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("0.0.0.0", "127.0.0.1");

        using var client = new ProxyFlightClient(address);
        await Assert.ThrowsAnyAsync<Exception>(() => client.AuthenticateAsync("alice", "pw", TestContext.Current.CancellationToken));
    }

    /// <summary>A certificate with a private key in <c>CurrentUser\My</c>, removed again on dispose.</summary>
    private sealed class StoredCertificate : IDisposable
    {
        public X509Certificate2 Certificate { get; }

        public StoredCertificate(string commonName, int validDays)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(validDays));
            Certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.PersistKeySet);
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(Certificate);
        }

        public void Dispose()
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(Certificate);
            Certificate.Dispose();
        }
    }

    private ProxyOptions StoreOptions(string? thumbprint = null, string? subject = null) => new()
    {
        DataDirectory = _dir,
        Tls = { Mode = TlsMode.Store, CertificateThumbprint = thumbprint, CertificateSubject = subject, StoreLocation = StoreLocationOption.CurrentUser },
    };

    [Fact]
    public void Store_mode_loads_a_certificate_by_thumbprint_and_by_subject()
    {
        var name = "dbproxy-store-" + Guid.NewGuid().ToString("N");
        using var stored = new StoredCertificate(name, 30);

        using var byThumbprint = CertificateProvider.Load(StoreOptions(thumbprint: stored.Certificate.Thumbprint.ToLowerInvariant()), _dir, NullLogger.Instance)!;
        using var bySubject = CertificateProvider.Load(StoreOptions(subject: name), _dir, NullLogger.Instance)!;

        Assert.Equal(stored.Certificate.Thumbprint, byThumbprint.Thumbprint);
        Assert.Equal(stored.Certificate.Thumbprint, bySubject.Thumbprint);
        Assert.True(byThumbprint.HasPrivateKey);
    }

    [Fact]
    public void Store_mode_prefers_the_longest_lived_certificate_for_a_subject()
    {
        var name = "dbproxy-store-" + Guid.NewGuid().ToString("N");
        using var older = new StoredCertificate(name, 10);
        using var renewed = new StoredCertificate(name, 90);

        using var loaded = CertificateProvider.Load(StoreOptions(subject: name), _dir, NullLogger.Instance)!;

        Assert.Equal(renewed.Certificate.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public void Store_mode_without_a_matching_certificate_fails_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CertificateProvider.Load(StoreOptions(subject: "dbproxy-no-such-" + Guid.NewGuid().ToString("N")), _dir, NullLogger.Instance));

        Assert.Contains("store", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Store_mode_without_a_selector_fails_before_touching_the_store()
    {
        Assert.Throws<InvalidOperationException>(() => CertificateProvider.Load(StoreOptions(), _dir, NullLogger.Instance));
    }

    [Fact]
    public void Rotation_service_picks_up_a_renewed_store_certificate()
    {
        var name = "dbproxy-store-" + Guid.NewGuid().ToString("N");
        var options = StoreOptions(subject: name);
        var holder = new TlsCertificateHolder();
        using var first = new StoredCertificate(name, 10);
        holder.Set(CertificateProvider.Load(options, _dir, NullLogger.Instance));
        var service = new CertificateRotationService(holder, options, _dir, NullLogger<CertificateRotationService>.Instance);

        Assert.False(service.Check());

        using var renewed = new StoredCertificate(name, 90);
        Assert.True(service.Check());
        Assert.Equal(renewed.Certificate.Thumbprint, holder.Current!.Thumbprint);
    }
}
