using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbDataKit.Hosting;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataProxy.Tests.Flight;

/// <summary>/health, /version and the container health probe, served next to Flight by the shared host.</summary>
public sealed class HostingEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbproxy-host-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Tls:Mode"] = "SelfSigned",
        };
        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HttpClient Client() => new(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } })
    {
        BaseAddress = _address,
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };

    [Fact]
    public async Task Health_and_version_are_served_over_http2_with_the_certificate_reported()
    {
        using var client = Client();
        var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var health = await client.GetFromJsonAsync<JsonElement>("/health", TestContext.Current.CancellationToken);
        Assert.True(health.GetProperty("ready").GetBoolean());
        var certificate = health.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "certificate");
        Assert.Equal("Healthy", certificate.GetProperty("status").GetString(), ignoreCase: true);

        var version = await client.GetFromJsonAsync<JsonElement>("/version", TestContext.Current.CancellationToken);
        Assert.Equal("dbdataproxy", version.GetProperty("product").GetString());
        Assert.False(string.IsNullOrEmpty(version.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Healthcheck_flag_succeeds_against_the_running_proxy_and_fails_when_nothing_listens()
    {
        var running = new ProxyOptions { Listen = { Address = _address.Host, Port = _address.Port } };
        var options = HealthCheck.Options(running);
        Assert.Equal(0, await HealthProbeClient.TryHandleAsync(["--healthcheck"], options));

        var closed = HealthCheck.Options(new ProxyOptions { Listen = { Address = "127.0.0.1", Port = 1 } });
        Assert.Equal(1, await HealthProbeClient.TryHandleAsync(["--healthcheck"], closed));
    }

    [Fact]
    public void Healthcheck_default_url_follows_the_listen_address_and_tls_mode()
    {
        Assert.Equal("https://127.0.0.1:8815/health/live", HealthCheck.Options(new ProxyOptions()).DefaultUrl); // 0.0.0.0 is probed through loopback
        Assert.Equal("http://10.1.2.3:9000/health/live",
            HealthCheck.Options(new ProxyOptions { Listen = { Address = "10.1.2.3", Port = 9000 }, Tls = { Mode = TlsMode.None } }).DefaultUrl);
        Assert.Equal("https://[::1]:8815/health/live", HealthCheck.Options(new ProxyOptions { Listen = { Address = "::1" } }).DefaultUrl);
    }

    [Fact]
    public async Task Stopping_the_host_with_nothing_in_flight_is_prompt()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _app.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"stop took {watch.Elapsed}");
    }
}
