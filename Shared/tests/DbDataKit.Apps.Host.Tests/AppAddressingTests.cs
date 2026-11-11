using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.Apps.Tests;

/// <summary>
/// Plan 26 AP08: one listener serves two names with two certificates (SNI), routes a request by Host and path to the instance bound there, refuses a name's other pages, serves an `origin: own` app only at its names, trusts
/// X-Forwarded-Host and X-Forwarded-Prefix from a listed proxy only, and sees a binding change without a restart. Over real TLS on loopback with self-signed certificates; names are only names (a connect callback sends every one to the loopback listener).
/// </summary>
public sealed class AppAddressingTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-addressing-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider clock = new();
    private AppInstanceStore store = null!;
    private AppBindings bindings = null!;
    private WebApplication app = null!;
    private int port;
    private readonly X509Certificate2 hostCertificate = Certificate("apps.test");
    private readonly X509Certificate2 contosoCertificate = Certificate("reports.contoso.test");
    private readonly Dictionary<string, string> secrets = [];
    private readonly List<string> problems = [];

    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, "pw"), "pw");
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        var definitions = new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module]));
        store = new AppInstanceStore(definitions);
        store.PublishApp("schemaVersion: 1\nname: notes\nversion: 1.0.0\nviews: [views/home.yaml]\n", "ada");
        store.PublishApp("schemaVersion: 1\nname: sovereign\nversion: 1.0.0\nentry: src/main.ts\norigin: own\n", "ada");
        secrets["secret:contoso-cert"] = Convert.ToBase64String(contosoCertificate.Export(X509ContentType.Pfx, "pw"));
        secrets["secret:contoso-pw"] = "pw";
        store.Save(new AppInstance("acme", "notes") { Addresses = [new AppAddress("acme.apps.test", AppAddressKind.Subdomain)] }, "ada");
        store.Save(new AppInstance("contoso", "notes") { Addresses = [new AppAddress("reports.contoso.test", AppAddressKind.Custom, "/", "secret:contoso-cert", "secret:contoso-pw"), new AppAddress("www.contoso.test", AppAddressKind.Custom, "/planning", "secret:contoso-cert", "secret:contoso-pw")] }, "ada");
        store.Save(new AppInstance("plain", "notes"), "ada");
        store.Save(new AppInstance("own", "sovereign") { Addresses = [new AppAddress("own.apps.test", AppAddressKind.Subdomain)] }, "ada");

        bindings = new AppBindings(store, clock);
        var certificates = new AppCertificates(bindings, () => hostCertificate, reference => secrets.GetValueOrDefault(reference), problems.Add);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseHttps(https => https.ServerCertificateSelector = (_, name) => certificates.Select(name));
        }));
        app = builder.Build();
        app.UseAppAddresses(bindings, store, new TrustedProxies(["127.0.0.1"]));
        app.UseRouting();
        app.MapGet("/apps/-/shell.js", () => "shell");
        app.MapGet("/apps/{instance}/{**rest}", (HttpContext c, string instance, string? rest) => $"instance={instance} rest={rest} path={c.Request.Path} public={AppAddressing.PublicPath(c)} bound={AppAddressing.Of(c)?.Binding?.Instance}");
        app.MapFallback((HttpContext c) => $"fallback path={c.Request.Path}");
        await app.StartAsync();
        port = new Uri(app.Urls.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private HttpClient Client(List<string>? seen = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, app.Urls.Select(u => new Uri(u).Port).First()), token);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, certificate, _, _) => { seen?.Add(new X509Certificate2(certificate!).GetNameInfo(X509NameType.DnsName, false)); return true; } },
        };
        return new HttpClient(handler);
    }

    private Task<string> Get(HttpClient client, string host, string path, params (string, string)[] headers) => GetAsync(client, $"https://{host}:{port}{path}", headers);

    private static async Task<string> GetAsync(HttpClient client, string url, (string, string)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(request);
        return (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task One_listener_serves_two_names_with_two_certificates_and_every_other_name_with_the_hosts()
    {
        var seen = new List<string>();
        using var client = Client(seen);
        await Get(client, "reports.contoso.test", "/");
        await Get(client, "acme.apps.test", "/");
        await Get(client, "apps.test", "/");
        Assert.Equal(["reports.contoso.test", "apps.test", "apps.test"], seen);
        Assert.Empty(problems);
    }

    [Fact]
    public async Task A_custom_domain_whose_certificate_cannot_be_read_gets_the_hosts_and_the_reason_is_reported_once()
    {
        secrets.Remove("secret:contoso-cert");
        var seen = new List<string>();
        using var client = Client(seen);
        await Get(client, "reports.contoso.test", "/");
        Assert.Equal(["apps.test"], seen);
        Assert.Contains(problems, p => p.Contains("reports.contoso.test") && p.Contains("is not set"));
    }

    [Fact]
    public async Task A_name_is_routed_to_its_instance_by_host_and_path()
    {
        using var client = Client();
        Assert.Equal("200 instance=acme rest= path=/apps/acme/ public=/ bound=acme", await Get(client, "acme.apps.test", "/"));
        Assert.Equal("200 instance=acme rest=_app/main.js path=/apps/acme/_app/main.js public=/_app/main.js bound=acme", await Get(client, "acme.apps.test", "/_app/main.js"));
        Assert.Equal("200 instance=contoso rest= path=/apps/contoso/ public=/ bound=contoso", await Get(client, "reports.contoso.test", "/"));
        // a path of a name is the longest binding's: /planning belongs to the second address of contoso
        Assert.Equal("200 instance=contoso rest=a path=/apps/contoso/a public=/planning/a bound=contoso", await Get(client, "www.contoso.test", "/planning/a"));
        // the host itself, a name nothing is bound at, is served as before
        Assert.StartsWith("200 instance=plain rest= path=/apps/plain/ public=/apps/plain/ bound=", await Get(client, "apps.test", "/apps/plain/"));
        Assert.Equal("200 fallback path=/anything", await Get(client, "apps.test", "/anything"));
    }

    [Fact]
    public async Task A_names_other_pages_are_not_served_but_the_shared_shell_script_is()
    {
        using var client = Client();
        Assert.StartsWith("404", await Get(client, "acme.apps.test", "/apps/contoso/"));
        Assert.StartsWith("404", await Get(client, "acme.apps.test", "/apps/catalog"));
        Assert.StartsWith("200 instance=acme", await Get(client, "acme.apps.test", "/apps/acme/_app/"));
        Assert.Equal("200 shell", await Get(client, "acme.apps.test", "/apps/-/shell.js"));
    }

    [Fact]
    public async Task An_app_of_origin_own_is_served_at_its_names_and_never_at_the_hosts_own_origin()
    {
        using var client = Client();
        Assert.StartsWith("200 instance=own", await Get(client, "own.apps.test", "/"));
        var refused = await Get(client, "apps.test", "/apps/own/_app/");
        Assert.StartsWith("404", refused);
        Assert.Contains("own.apps.test", refused);
    }

    [Fact]
    public async Task A_forwarded_host_and_prefix_are_trusted_from_a_listed_proxy_only()
    {
        using var client = Client();
        // the loopback client is the listed proxy: the name and prefix it forwards are the browser's
        Assert.Equal("200 instance=acme rest=x path=/apps/acme/x public=/x bound=acme", await Get(client, "apps.test", "/x", ("X-Forwarded-Host", "acme.apps.test")));
        Assert.Equal("200 instance=contoso rest=a path=/apps/contoso/a public=/planning/a bound=contoso", await Get(client, "apps.test", "/a", ("X-Forwarded-Host", "www.contoso.test"), ("X-Forwarded-Prefix", "/planning")));

        var untrusted = new AppBindings(store, clock);
        Assert.False(new TrustedProxies(["10.0.0.0/8"]).Trusts(IPAddress.Loopback));
        Assert.True(new TrustedProxies(["10.0.0.0/8", "::1"]).Trusts(IPAddress.Parse("10.4.5.6")));
        Assert.True(new TrustedProxies(["::1"]).Trusts(IPAddress.IPv6Loopback));
        Assert.False(new TrustedProxies(["192.168.1.0/24"]).Trusts(IPAddress.Parse("192.168.2.1")));
        Assert.False(TrustedProxies.None.Trusts(IPAddress.Loopback));
        Assert.NotNull(untrusted);
    }

    [Fact]
    public async Task A_binding_added_or_removed_is_routed_within_the_tables_lifetime_without_a_restart()
    {
        using var client = Client();
        Assert.Equal("200 fallback path=/", await Get(client, "late.apps.test", "/"));
        store.Save(new AppInstance("late", "notes") { Addresses = [new AppAddress("late.apps.test", AppAddressKind.Subdomain)] }, "ada");
        // inside the lifetime the table is the one read; after it, the new binding is routed (at another instance of the group too: the table is the shared state)
        Assert.Equal("200 fallback path=/", await Get(client, "late.apps.test", "/"));
        clock.Advance(AppBindings.Lifetime + TimeSpan.FromSeconds(1));
        Assert.StartsWith("200 instance=late", await Get(client, "late.apps.test", "/"));
        store.Save(new AppInstance("late", "notes"), "ada");
        bindings.Invalidate();
        Assert.Equal("200 fallback path=/", await Get(client, "late.apps.test", "/"));
    }
}
