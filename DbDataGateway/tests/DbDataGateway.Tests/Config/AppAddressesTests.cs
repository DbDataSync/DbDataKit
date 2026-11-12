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
    private readonly GatewayOptions options = new() { InstanceId = "gw-test-1", Apps = { Domain = "apps.test", Addresses = ["203.0.113.10"] } };
    private readonly Dictionary<string, string[]> txt = [];
    private readonly Dictionary<string, IPAddress[]> dns = new() { ["sales.contoso.test"] = [IPAddress.Parse("203.0.113.10")], ["elsewhere.contoso.test"] = [IPAddress.Parse("198.51.100.7")], ["here.contoso.test"] = [IPAddress.Parse("10.1.2.3")] };
    private readonly AppAddressVerifier verifier;

    public AppAddressesTests()
    {
        Directory.CreateDirectory(folder);
        verifier = new AppAddressVerifier(() => options, reference => secrets.GetValueOrDefault(reference),
            (host, _) => dns.TryGetValue(host, out var found) ? Task.FromResult<IReadOnlyList<IPAddress>>(found) : throw new System.Net.Sockets.SocketException(11001),
            () => [IPAddress.Parse("10.1.2.3")], resolveTxt: (name, _) => Task.FromResult<IReadOnlyList<string>>(txt.GetValueOrDefault(name) ?? []));
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
    public async Task A_custom_domain_that_does_not_resolve_here_is_accepted_when_its_challenge_record_holds_the_value_and_refused_with_the_record_to_create_when_not()
    {
        var record = verifier.Challenge("acme", "Nowhere.Contoso.Test.");
        Assert.Equal("_dbdatakit-challenge.nowhere.contoso.test", record.Name);
        Assert.Equal("TXT", record.Type);
        Assert.StartsWith(AppDomainChallenge.ValuePrefix, record.Value);
        Assert.Equal(record, verifier.Challenge("acme", "nowhere.contoso.test"));                               // the same every time: the editor shows it before the record exists
        Assert.NotEqual(record.Value, verifier.Challenge("other", "nowhere.contoso.test").Value);               // per instance
        Assert.NotEqual(record.Value, verifier.Challenge("acme", "elsewhere.contoso.test").Value);              // per name

        var refused = await Verify(Instance("acme", Custom("nowhere.contoso.test")));
        Assert.Contains("does not resolve", refused);
        Assert.Contains($"create the TXT record {record.Name} with the value {record.Value}", refused);          // the refusal says what to create

        txt[record.Name] = ["v=spf1 -all", "somebody else's text"];
        Assert.Contains("does not resolve", await Verify(Instance("acme", Custom("nowhere.contoso.test"))));     // other records prove nothing
        txt[record.Name] = ["v=spf1 -all", record.Value.Replace("0", "1").Replace("a", "b")];
        Assert.Contains("does not resolve", await Verify(Instance("acme", Custom("nowhere.contoso.test"))));     // a wrong value proves nothing
        txt[record.Name] = ["note", record.Value];
        secrets["secret:cert-nowhere"] = Pfx("nowhere.contoso.test", "pw");
        Assert.Null(await Verify(Instance("acme", Custom("nowhere.contoso.test", "secret:cert-nowhere", "secret:cert-sales-pw"))));   // proved, so accepted
        secrets["secret:cert-nowhere"] = Pfx("wrong.contoso.test", "pw");
        Assert.Contains("is not for that name", await Verify(Instance("acme", Custom("nowhere.contoso.test", "secret:cert-nowhere", "secret:cert-sales-pw"))));   // the certificate is still checked
        Assert.Contains("does not resolve", await Verify(Instance("other", Custom("nowhere.contoso.test"))));    // another instance cannot use acme's record
    }

    [Fact]
    public async Task Verify_says_which_proof_holds_and_what_to_create_otherwise()
    {
        var host = "nowhere.contoso.test";
        var before = await verifier.CheckAsync("acme", host, CancellationToken.None);
        Assert.False(before.Verified);
        Assert.Null(before.Via);
        Assert.Contains("does not hold " + before.Record.Value, before.Reason);
        Assert.Contains("does not resolve", before.Reason);

        txt[before.Record.Name] = [before.Record.Value];
        var proved = await verifier.CheckAsync("acme", host, CancellationToken.None);
        Assert.True(proved.Verified);
        Assert.Equal("challenge", proved.Via);
        Assert.Null(proved.Reason);

        var resolving = await verifier.CheckAsync("acme", "sales.contoso.test", CancellationToken.None);
        Assert.True(resolving.Verified);
        Assert.Equal("address", resolving.Via);                                                                  // a name that already points here needs no record

        var under = await verifier.CheckAsync("acme", "acme.apps.test", CancellationToken.None);
        Assert.False(under.Verified);
        Assert.Contains("bind it as a subdomain", under.Reason);
    }

    [Fact]
    public void A_dns_answer_with_txt_records_is_read_and_a_failure_or_an_unknown_name_is_not_a_record()
    {
        byte[] Answer(ushort id, int code, params string[][] records)
        {
            var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x81, (byte)(0x80 | code), 0, 1, 0, (byte)records.Length, 0, 0, 0, 0 };
            foreach (var label in "_dbdatakit-challenge.x.test".Split('.')) { bytes.Add((byte)label.Length); bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
            bytes.AddRange([0, 0, 16, 0, 1]);
            foreach (var strings in records)
            {
                var data = strings.SelectMany(t => new[] { (byte)System.Text.Encoding.UTF8.GetByteCount(t) }.Concat(System.Text.Encoding.UTF8.GetBytes(t))).ToArray();
                bytes.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 60, (byte)(data.Length >> 8), (byte)data.Length]);
                bytes.AddRange(data);
            }
            return [.. bytes];
        }
        Assert.Equal(["hello", "dbdatakit-domain-verification=abc"], DnsTxtResolver.ParseTxt(Answer(7, 0, ["hello"], ["dbdatakit-domain-verification=", "abc"]), 7));   // a record's strings are joined
        Assert.Empty(DnsTxtResolver.ParseTxt(Answer(7, 3), 7));                                                   // NXDOMAIN
        Assert.Throws<InvalidOperationException>(() => DnsTxtResolver.ParseTxt(Answer(7, 2), 7));                // SERVFAIL
        Assert.Throws<InvalidOperationException>(() => DnsTxtResolver.ParseTxt(Answer(8, 0, ["x"]), 7));         // not for this question
        Assert.Throws<InvalidOperationException>(() => DnsTxtResolver.ParseTxt(Answer(7, 0, ["hello"])[..^3], 7)); // cut short
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
