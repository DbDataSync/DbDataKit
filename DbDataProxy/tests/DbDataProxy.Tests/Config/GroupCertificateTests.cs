using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.State;
using DbDataProxy.Server.Tls;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Config;

/// <summary>
/// Plan 20 O04: a group of self-signed instances presents one certificate, kept in its state encrypted with the group's key. The first instance's becomes the group's; the others serve it; when it is due, the first
/// instance to renew puts its renewed one there and the others take that one rather than their own.
/// </summary>
public sealed class GroupCertificateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbproxy-groupcert-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_directory);
    }

    private static X509Certificate2 Certificate(string name, int days)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static bool Due(X509Certificate2 certificate) => certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30);

    [Fact]
    public void The_first_certificate_is_the_groups_until_it_is_due_and_the_first_renewal_is_the_one_every_instance_takes()
    {
        Directory.CreateDirectory(_directory);
        var state = ProxyState.Open(_directory);
        var options = new StaticOptions(new ProxyOptions { DataDirectory = _directory });
        var (a, b) = (new GroupCertificate(state, new GroupSessionKey(state, options)), new GroupCertificate(state, new GroupSessionKey(state, options)));

        var first = a.Choose(() => Certificate("a", 365), Due);
        Assert.Equal(first.Thumbprint, b.Choose(() => Certificate("b", 365), Due).Thumbprint);   // b serves a's, not its own

        // a's renews it once it is due; b, checking after, takes the renewed one and not one of its own
        a.Replace(Certificate("a-old", 10));
        var renewed = a.Choose(() => Certificate("a-renewed", 365), Due);
        Assert.Equal("CN=a-renewed", renewed.Subject);
        Assert.Equal(renewed.Thumbprint, b.Choose(() => Certificate("b-renewed", 365), Due).Thumbprint);

        // what an operator renews on purpose replaces it at once
        var forced = Certificate("forced", 365);
        b.Replace(forced);
        Assert.Equal(forced.Thumbprint, a.Choose(() => Certificate("never", 365), Due).Thumbprint);
        Assert.False(a.Enabled);   // a SQLite state is one instance's: the host does not ask
    }

    private sealed class StaticOptions(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;
        public ProxyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }
}
