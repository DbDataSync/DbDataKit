using System.Net;
using System.Net.Sockets;
using DbDataKit.Cli;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataGateway.Tests.Config;

/// <summary>The management API is on by default (plan 33 D15, SC40): https on the loopback address with the generated certificate, so a console can pair with a fresh install; <c>management.enabled: false</c> turns it off.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ManagementDefaultTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-mgmt-default-" + Guid.NewGuid().ToString("N")[..8]);

    public ManagementDefaultTests() => Directory.CreateDirectory(dataDirectory);

    public void Dispose() => TestFolder.Delete(dataDirectory);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient http, Uri uri) =>
        http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher });

    [Fact]
    public void The_default_options_have_the_management_api_on_for_this_machine_on_8816()
    {
        var options = new GatewayOptions();
        Assert.True(options.Management.Enabled);
        Assert.Equal("127.0.0.1", options.Management.Address);
        Assert.Equal(8816, options.Management.Port);
        Assert.Equal(TlsMode.SelfSigned, options.Tls.Mode);
        Assert.False(options.Management.LocalChannel);
    }

    [Fact]
    public async Task A_host_started_with_no_management_settings_answers_health_and_version_on_the_management_https_port_and_never_serves_its_paths_on_the_flight_port()
    {
        // only the ports are chosen (the Flight port and a free management port, so a parallel test cannot hold 8816); nothing says the management API is on
        var managementPort = FreePort();
        await using var app = GatewayHost.Build([$"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0", $"--Gateway:Management:Port={managementPort}"]);
        await app.StartAsync();
        var flight = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(a => !a.Contains($":{managementPort}")));
        var management = new Uri($"https://127.0.0.1:{managementPort}");
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(http, new Uri(management, "/health/live"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(http, new Uri(management, "/version"))).StatusCode);
        foreach (var path in new[] { "/api/v1/about", "/management/v1/pairing" })
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(http, new Uri(flight, path))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(http, new Uri(management, "/management/v1/pairing"))).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task With_management_disabled_nothing_listens_on_the_management_port()
    {
        var managementPort = FreePort();
        await using var app = GatewayHost.Build([$"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0", "--Gateway:Management:Enabled=false", $"--Gateway:Management:Port={managementPort}"]);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.DoesNotContain(addresses, a => a.Contains($":{managementPort}"));
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(new Uri($"https://127.0.0.1:{managementPort}/health/live")));
        await app.StopAsync();
    }

    [Fact]
    public void Tls_none_with_the_management_api_on_by_default_is_refused_naming_both_fixes()
    {
        using var file = TempConfig.Create(("server.yaml", "tls: { mode: none }\nlisten: { address: 127.0.0.1 }\n"));   // says nothing about management
        var load = ConfigTestSupport.LoadDirectory(file.Directory);
        Assert.False(load.Ok);
        var text = ConfigTestSupport.Format(load);
        Assert.Contains("Gateway:Management:Enabled", text);
        Assert.Contains("SelfSigned, File or Store", text);
        Assert.Contains("management.enabled: false", text);

        using var off = TempConfig.Create(("server.yaml", "tls: { mode: none }\nlisten: { address: 127.0.0.1 }\nmanagement: { enabled: false }\n"));
        Assert.True(ConfigTestSupport.LoadDirectory(off.Directory).Ok);
    }

    private async Task<(int Exit, string Out, string Error)> PairAsync(string serverYaml)
    {
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(dataDirectory));
        File.WriteAllText(GatewayConfigFile.PathIn(dataDirectory), serverYaml);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.RunAsync(["server", "pair", "--repo", dataDirectory], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Server_pair_with_management_off_says_no_console_can_reach_the_gateway_and_names_the_setting()
    {
        var r = await PairAsync("schemaVersion: 2\nmanagement: { enabled: false }\n");
        Assert.Equal(0, r.Exit);
        Assert.Contains("management.enabled", r.Error);
        Assert.Contains("no console can reach", r.Error);
    }

    [Fact]
    public async Task Server_pair_with_the_default_management_says_nothing_of_the_kind()
    {
        var r = await PairAsync("schemaVersion: 2\n");
        Assert.Equal(0, r.Exit);
        Assert.Contains("Pairing code:", r.Out);
        Assert.DoesNotContain("management", r.Error);
    }
}
