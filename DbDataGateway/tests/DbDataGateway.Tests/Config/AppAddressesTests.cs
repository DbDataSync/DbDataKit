using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 26 AP08 at the Gateway: a name is verified before it is accepted (a subdomain is the instance's under the apps domain; a custom domain resolves to this host or a listed address and has a certificate for it), and each binding is a redirect URI of the
/// instance's OpenID client when added and gone when removed.
/// </summary>
public sealed class AppAddressesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "gw-addresses-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, string> secrets = [];
    private readonly GatewayOptions options = new() { Apps = { Domain = "apps.test", Addresses = ["203.0.113.10"] } };
    private readonly Dictionary<string, IPAddress[]> dns = new() { ["sales.contoso.test"] = [IPAddress.Parse("203.0.113.10")], ["elsewhere.contoso.test"] = [IPAddress.Parse("198.51.100.7")], ["here.contoso.test"] = [IPAddress.Parse("10.1.2.3")] };
    private readonly AppAddressVerifier verifier;

    public AppAddressesTests()
    {
        Directory.CreateDirectory(folder);
        verifier = new AppAddressVerifier(() => options, reference => secrets.GetValueOrDefault(reference),
            (host, _) => dns.TryGetValue(host, out var found) ? Task.FromResult<IReadOnlyList<IPAddress>>(found) : throw new System.Net.Sockets.SocketException(11001),
            () => [IPAddress.Parse("10.1.2.3")]);
        secrets["secret:cert-sales"] = Pfx("sales.contoso.test", "pw");
        secrets["secret:cert-sales-pw"] = "pw";
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static string Pfx(string name, string password, bool expired = false, bool withKey = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(expired ? -30 : -1), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 30));
        return Convert.ToBase64String(withKey ? made.Export(X509ContentType.Pfx, password) : made.Export(X509ContentType.Cert));
    }

    private static AppInstance Instance(string name, params AppAddress[] addresses) => new(name, "notes") { Addresses = addresses };

    private static AppAddress Custom(string host, string certificate = "secret:cert-sales", string? password = "secret:cert-sales-pw") => new(host, AppAddressKind.Custom, "/", certificate, password);

    private Task<string?> Verify(AppInstance instance) => verifier.VerifyAsync(instance, CancellationToken.None);

    [Fact]
    public async Task A_subdomain_is_the_instances_under_the_apps_domain_and_needs_the_domain()
    {
        Assert.Null(await Verify(Instance("acme", new AppAddress("acme.apps.test", AppAddressKind.Subdomain))));
        Assert.Contains("is not it", await Verify(Instance("acme", new AppAddress("other.apps.test", AppAddressKind.Subdomain))));
        Assert.Contains("is not it", await Verify(Instance("acme", new AppAddress("acme.elsewhere.test", AppAddressKind.Subdomain))));
        options.Apps.Domain = null;
        Assert.Contains("has no apps domain", await Verify(Instance("acme", new AppAddress("acme.apps.test", AppAddressKind.Subdomain))));
    }

    [Fact]
    public async Task A_custom_domain_must_resolve_to_this_host_or_a_listed_address_and_a_name_that_does_not_resolve_is_refused()
    {
        Assert.Null(await Verify(Instance("acme", Custom("sales.contoso.test"))));                // a listed address (a balancer's)
        Assert.Contains("is not an address of this host", await Verify(Instance("acme", Custom("elsewhere.contoso.test"))));
        Assert.Contains("does not resolve", await Verify(Instance("acme", Custom("nowhere.contoso.test"))));
        dns["loop.contoso.test"] = [IPAddress.Loopback];
        secrets["secret:cert-loop"] = Pfx("loop.contoso.test", "pw");
        Assert.Null(await Verify(Instance("acme", Custom("loop.contoso.test", "secret:cert-loop", "secret:cert-sales-pw"))));   // loopback reaches only this machine
        dns["here.contoso.test"] = [IPAddress.Parse("10.1.2.3")];
        secrets["secret:cert-here"] = Pfx("here.contoso.test", "pw");
        Assert.Null(await Verify(Instance("acme", Custom("here.contoso.test", "secret:cert-here", "secret:cert-sales-pw"))));    // one of this machine's own
    }

    [Fact]
    public async Task A_name_under_the_apps_domain_is_bound_as_a_subdomain_not_as_a_custom_domain()
    {
        dns["acme.apps.test"] = [IPAddress.Loopback];
        Assert.Contains("bind it as a subdomain", await Verify(Instance("acme", Custom("acme.apps.test"))));
    }

    [Fact]
    public async Task The_certificate_must_be_readable_have_a_key_be_valid_and_be_for_the_name()
    {
        Assert.Contains("is not set", await Verify(Instance("acme", Custom("sales.contoso.test", "secret:missing"))));
        secrets["secret:expired"] = Pfx("sales.contoso.test", "pw", expired: true);
        Assert.Contains("is not valid now", await Verify(Instance("acme", Custom("sales.contoso.test", "secret:expired"))));
        secrets["secret:wrong-name"] = Pfx("other.contoso.test", "pw");
        Assert.Contains("is not for that name", await Verify(Instance("acme", Custom("sales.contoso.test", "secret:wrong-name"))));
        secrets["secret:no-key"] = Pfx("sales.contoso.test", "pw", withKey: false);
        Assert.Contains("cannot be read", await Verify(Instance("acme", Custom("sales.contoso.test", "secret:no-key", null))) ?? "") ;
        Assert.Contains("cannot be read", await Verify(Instance("acme", Custom("sales.contoso.test", "secret:cert-sales", "secret:missing-pw"))));
        secrets["secret:garbage"] = "not base64 at all";
        Assert.Contains("cannot be read", await Verify(Instance("acme", Custom("sales.contoso.test", "secret:garbage", null))));
    }

    [Fact]
    public void A_binding_is_a_redirect_uri_of_the_instances_client_and_of_the_hosts_when_added_and_gone_when_removed()
    {
        var database = GatewayState.Open(folder);
        var clients = new OidcClients(database);
        var apps = new Uri("https://apps.test:8818/");
        var primary = AppInstanceClients.CallbackOf(apps);
        var (host, _) = clients.Register("Apps", [primary, "https://manual.test/callback"], confidential: false);
        var definitions = new DefinitionStore(database);
        var store = new AppInstanceStore(definitions);
        store.PublishApp("schemaVersion: 1\nname: notes\nversion: 1.0.0\nviews: [views/home.yaml]\n", "ada");
        var acmeName = AppInstanceClients.CallbackOfName("acme.apps.test", apps);
        var salesName = AppInstanceClients.CallbackOfName("sales.contoso.test", apps);
        Assert.Equal("https://acme.apps.test:8818/apps/-/callback", acmeName);

        store.Save(Instance("acme", new AppAddress("acme.apps.test", AppAddressKind.Subdomain), Custom("sales.contoso.test")), "ada");
        store.Save(Instance("globex", new AppAddress("globex.apps.test", AppAddressKind.Subdomain)), "ada");
        var saved = store.Get("acme")!;
        Assert.Equal([primary, acmeName, salesName], AppInstanceClients.SyncRedirects(clients, saved, apps, host.ClientId, store.List()));
        Assert.Equal([primary, acmeName, salesName], clients.AppClientOf("acme")!.RedirectUris);
        Assert.Equal([primary, "https://manual.test/callback", acmeName, salesName], clients.Find(host.ClientId)!.RedirectUris);          // the administrator's own are kept

        // removing a binding removes its redirect URI, from both; the other one stays
        store.Save(Instance("acme", new AppAddress("acme.apps.test", AppAddressKind.Subdomain)), "ada");
        AppInstanceClients.SyncRedirects(clients, store.Get("acme")!, apps, host.ClientId, store.List());
        Assert.Equal([primary, acmeName], clients.AppClientOf("acme")!.RedirectUris);
        Assert.Equal([primary, "https://manual.test/callback", acmeName], clients.Find(host.ClientId)!.RedirectUris);

        // no address at all: only the host's callback
        store.Save(new AppInstance("acme", "notes"), "ada");
        AppInstanceClients.SyncRedirects(clients, store.Get("acme")!, apps, host.ClientId, store.List());
        Assert.Equal([primary], clients.AppClientOf("acme")!.RedirectUris);
        Assert.Equal([primary, "https://manual.test/callback"], clients.Find(host.ClientId)!.RedirectUris);

        // a host client held elsewhere is not touched
        AppInstanceClients.SyncRedirects(clients, saved, apps, "a-client-at-another-home", store.List());
        // starting the module gives a bound instance its names too
        store.Save(Instance("globex", new AppAddress("globex.apps.test", AppAddressKind.Subdomain)), "ada");
        AppInstanceClients.Reconcile(database, store, primary, host.ClientId);
        Assert.Contains(AppInstanceClients.CallbackOfName("globex.apps.test", apps), clients.AppClientOf("globex")!.RedirectUris);
    }
}
