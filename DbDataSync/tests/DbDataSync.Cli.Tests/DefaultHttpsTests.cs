using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DbDataSync.Api;
using DbDataSync.Api.Configuration;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Plan 33, SC41 (decision D16): Sync serves https by default. A fresh server with nothing configured makes a self-signed certificate and serves https, <c>--url http://...</c> is the only way to plain http,
/// a configured certificate is used and no second one is made, <c>health check</c> with no flags passes against the generated certificate without switching validation off, and a request over http to the https port is refused.
/// The server is the real host (<see cref="DbDataSyncHost"/>) on a free loopback port, started the way <c>server run</c> starts it.
/// </summary>
public sealed class DefaultHttpsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-default-https-").FullName;
    private readonly List<IAsyncDisposable> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers)
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GitTempDirectory.DeleteRecursively(_root);
    }

    private static string? NoEnvironment(string name) => null;

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>What <c>server run</c> does between reading the address and serving it, without the DuckDB install and the wait for a stop.</summary>
    private async Task StartAsync(string url)
    {
        ServeCommand.Prepare(_root);
        Assert.Null(ServeCommand.EnsureCertificate(_root, url, NoEnvironment));
        var app = DbDataSyncHost.Build(ServeCommand.BuildHostArgs([], _root, Path.Combine(_root, "state.db"), url));
        _servers.Add(app);
        await app.StartAsync();
    }

    private static HttpClient Trusting(X509Certificate2 expected) => new(new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && presented.GetCertHashString() == expected.GetCertHashString(),
        },
    }) { Timeout = TimeSpan.FromSeconds(20) };

    private string CerPath => Path.ChangeExtension(ManagedSelfSignedCertificate.PfxPath(_root), ".cer");

    [Fact]
    public void TheDefaultAddressIsHttpsLocalhost5080_AndTheHelpDoesNotNamePlainHttp()
    {
        Assert.Equal("https://localhost:5080", ApiOptions.DefaultUrl);
        Assert.Equal(ApiOptions.DefaultUrl, ServeCommand.ResolveUrl([], _root, NoEnvironment));
        Assert.Equal("http://127.0.0.1:9", ServeCommand.ResolveUrl(["--url", "http://127.0.0.1:9"], _root, NoEnvironment));

        var help = new StringWriter();
        var original = Console.Out;
        Console.SetOut(help);
        try { Help.Print(); }
        finally { Console.SetOut(original); }
        Assert.Contains("--url https://localhost:5080", help.ToString());
        Assert.DoesNotContain("http://localhost:5080", help.ToString());
    }

    [Fact]
    public void TheSetupScreensStartFromTheDefaultConstant_AndTheStarterFileDoesNotShowPlainHttp()
    {
        var tui = Path.Combine(RepositoryRoot(), "DbDataSync", "src", "DbDataSync.Cli", "Tui");
        var source = File.ReadAllText(Path.Combine(tui, "SetupScreen.cs")) + File.ReadAllText(Path.Combine(tui, "Tabs", "GeneralTab.cs"));
        Assert.DoesNotContain("localhost:5080", source);
        Assert.Contains("ApiOptions.DefaultUrl", source);

        ServeCommand.Prepare(_root);
        Assert.DoesNotContain("http://localhost:5080", File.ReadAllText(DbDataSyncConfigFile.PathIn(_root)));
    }

    [Fact]
    public async Task AFreshServerServesHttps_WritesItsCertificate_AndHealthCheckWithNoFlagsPassesAgainstIt()
    {
        var url = $"https://localhost:{FreePort()}";
        Assert.False(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));

        await StartAsync(url);

        Assert.True(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));
        Assert.True(File.Exists(CerPath));
        using var made = X509CertificateLoader.LoadCertificateFromFile(CerPath);
        var names = string.Join(' ', made.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames())).ToLowerInvariant();
        Assert.Contains("localhost", names);
        Assert.Contains(Environment.MachineName.ToLowerInvariant(), names);

        using (var client = Trusting(made))
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/api/health")).StatusCode);

        // `health check` with no flags: the address comes from the configuration, as the default would
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", url);
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try { Assert.Equal(0, await HealthCommand.RunAsync(["check", "--repo", _root])); }
        finally { Console.SetError(original); }
        Assert.DoesNotContain("did not validate", error.ToString()); // trusted by the pinned file, not waved through
    }

    [Fact]
    public async Task HealthCheckTrustsOnlyTheCertificateOfTheDataFolder_NotAnyCertificate()
    {
        var url = $"https://localhost:{FreePort()}";
        await StartAsync(url);
        using var made = X509CertificateLoader.LoadCertificateFromFile(CerPath);
        using var other = ManagedSelfSignedCertificate.Generate("localhost", 1);

        // the pinned certificate is accepted; with normal validation (strict) another one is refused, and so is none
        using (var pinned = new HealthProbeClient(TimeSpan.FromSeconds(20), made, strict: true))
            Assert.True((await pinned.Client.GetAsync($"{url}/api/health")).IsSuccessStatusCode);
        using (var wrong = new HealthProbeClient(TimeSpan.FromSeconds(20), other, strict: true))
            await Assert.ThrowsAsync<HttpRequestException>(() => wrong.Client.GetAsync($"{url}/api/health"));
        using (var none = new HealthProbeClient(TimeSpan.FromSeconds(20), pinned: null, strict: true))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => none.Client.GetAsync($"{url}/api/health"));
            Assert.Contains("did not validate", none.CertificateProblem);
        }

        // an https address that is not this machine's is checked strictly (the probe is made for the check, the request here goes to the local server
        // whose certificate no trust store knows); a loopback address with no pinned file is reported, not refused
        using (var remote = HealthProbeClient.ForCheck("https://sync.example.invalid:5080", _root, TimeSpan.FromSeconds(20)))
            await Assert.ThrowsAsync<HttpRequestException>(() => remote.Client.GetAsync($"{url}/api/health"));
        var nowhere = Directory.CreateTempSubdirectory("dbdatasync-nopin-").FullName;
        try
        {
            using var loopbackWithoutFile = HealthProbeClient.ForCheck(url, nowhere, TimeSpan.FromSeconds(20));
            Assert.True((await loopbackWithoutFile.Client.GetAsync($"{url}/api/health")).IsSuccessStatusCode);
            Assert.Contains("did not validate", loopbackWithoutFile.CertificateProblem);
        }
        finally
        {
            Directory.Delete(nowhere, recursive: true);
        }
    }

    [Fact]
    public async Task AnExplicitHttpUrlServesPlainHttp_AndMakesNoCertificate()
    {
        var url = $"http://127.0.0.1:{FreePort()}";

        await StartAsync(url);

        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/api/health")).StatusCode);
        Assert.False(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));
        Assert.False(File.Exists(CerPath));
        Assert.False(DbDataSyncConfigFile.Read(_root).ContainsKey("Kestrel:Certificates:Default:Path"));
        Assert.Equal(0, await HealthCommand.RunAsync(["check", "--repo", _root, "--url", url]));
    }

    [Fact]
    public async Task APlainHttpRequestToTheHttpsPort_IsRefused_NeverAnsweredOrRedirected()
    {
        var port = FreePort();
        await StartAsync($"https://localhost:{port}");

        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var refused = await Record.ExceptionAsync(() => client.GetAsync($"http://127.0.0.1:{port}/api/health"));

        // Kestrel reads the request as a failed TLS handshake and drops the connection: the client gets no HTTP answer at all
        Assert.IsType<HttpRequestException>(refused);
    }

    [Fact]
    public async Task AConfiguredCertificateIsUsed_AndNoSecondOneIsMade()
    {
        var path = Path.Combine(_root, "mine.pfx");
        using (var mine = ManagedSelfSignedCertificate.Generate("localhost", 30))
            File.WriteAllBytes(path, mine.Export(X509ContentType.Pfx, "pw"));
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "Path", path);
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "Password", "pw");
        var url = $"https://localhost:{FreePort()}";

        await StartAsync(url);

        Assert.False(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));
        using var expected = X509CertificateLoader.LoadPkcs12FromFile(path, "pw");
        using var client = Trusting(expected);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/api/health")).StatusCode);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DbDataKit.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("DbDataKit.slnx not found above " + AppContext.BaseDirectory);
    }
}
