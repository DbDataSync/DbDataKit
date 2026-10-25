using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Hosting.Local;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Config;

/// <summary>The Proxy's management API (plan 13, P03): its own listener, pairing, the token and the roles, the effective configuration without a secret in it, validation of a candidate, the local channel.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ManagementTests : IAsyncLifetime
{
    private const string Sentinel = "SENTINEL-6f1c-do-not-leak";
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbproxy-mgmt-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication app = null!;
    private int managementPort;
    private Uri flight = null!;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly string BadBatch = Conn.Yaml("erp", proxy: ["batch_size: 0"]);

    private string ConfigDirectory => ProxyConfigFile.DirectoryIn(dataDirectory);

    private void Write(string name, string text)
    {
        var path = Path.Combine(ConfigDirectory, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private async Task StartAsync(bool management = true, bool localChannel = false, string? localDirectory = null, string[]? extra = null)
    {
        managementPort = FreePort();
        // Secrets in every place one can be written: the sentinel must not come back in any answer.
        Write("server.yaml", $"schemaVersion: 2\ntls: {{ mode: selfSigned, certificatePassword: {Sentinel}-tls }}\n");
        Write("proxy/auth.yaml", $"jwt:\n  signingKey: {Sentinel}-jwt\n  audience: api://x\n  mappings: [ {{ claim: c, value: v, dataSources: ['*'] }} ]\n");
        Write("proxy/users.yaml", $"users:\n  - name: bob\n    passwordHash: {Sentinel}-hash\n    dataSources: [erp]\n");
        Write("connections/erp.yaml", Conn.Yaml("erp", connectionString: "Server=s;Encrypt=True", auth: "auth: { mode: sql, user: u, password_secret: 'env:ERP_DB_PASSWORD' }"));
        var args = new List<string>
        {
            $"--Proxy:DataDirectory={dataDirectory}", "--Proxy:Listen:Address=127.0.0.1", "--Proxy:Listen:Port=0",
            $"--Proxy:Management:Enabled={management}", "--Proxy:Management:Address=127.0.0.1", $"--Proxy:Management:Port={managementPort}",
            $"--Proxy:Management:LocalChannel={localChannel}",
        };
        if (localDirectory is not null) args.Add($"--Proxy:Management:LocalChannelDirectory={localDirectory}");
        args.AddRange(extra ?? []);
        app = ProxyHost.Build([.. args]);
        await app.StartAsync();
        flight = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        try { Directory.Delete(dataDirectory, true); } catch (IOException) { }
    }

    private Uri ManagementUrl => new($"https://127.0.0.1:{managementPort}");

    private PairingFiles Files => new(ProxyConfigFile.PathIn(dataDirectory), Path.Combine(dataDirectory, "pairing-codes.json"));

    private async Task<PairedServer> PairAsync(ServerRole ceiling = ServerRole.Admin)
    {
        var code = Files.Codes().Create(ServerRoles.Close(ceiling)).Code;
        return await PairingClient.PairAsync(ManagementUrl, code, console);
    }

    private async Task<HttpResponseMessage> CallAsync(PairedServer server, HttpMethod method, string path, ServerRole roles, string? body = null)
    {
        using var http = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint));
        using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, path));
        request.Headers.Add(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(server.InstanceId, "ada", "Ada", roles)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task A_console_pairs_on_the_management_port_and_reads_the_server_and_its_configuration_with_the_role_each_call_needs()
    {
        await StartAsync();
        var server = await PairAsync();

        var about = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Viewer)).Content.ReadAsStringAsync())!;
        Assert.Equal("dbdataproxy", (string)about["product"]!);
        Assert.Equal(server.InstanceId, (string)about["instance_id"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/config", ServerRole.Viewer)).StatusCode);
        var config = await CallAsync(server, HttpMethod.Get, "/api/v1/config", ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        var document = JsonNode.Parse(await config.Content.ReadAsStringAsync())!;
        Assert.True((bool)document["valid"]!);
        var values = document["values"]!.AsArray().ToDictionary(v => (string)v!["path"]!);
        Assert.Equal("2", (string)values["SchemaVersion"]!["value"]!);
        Assert.EndsWith("server.yaml", (string)values["Tls.Mode"]!["source"]!);
        Assert.True((int)values["Tls.Mode"]!["line"]! > 0);
        Assert.Equal("********", (string)values["Tls.CertificatePassword"]!["value"]!);
        Assert.True((bool)values["Tls.CertificatePassword"]!["secret"]!);
        Assert.Equal("Server=s;Encrypt=True;User Id=u;Password=env:ERP_DB_PASSWORD", (string)values["DataSources.erp.ConnectionString"]!["value"]!);
    }

    [Fact]
    public async Task No_answer_contains_a_secret_whatever_the_call()
    {
        await StartAsync();
        var server = await PairAsync();
        var answers = new List<string>
        {
            await (await CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Admin)).Content.ReadAsStringAsync(),
            await (await CallAsync(server, HttpMethod.Get, "/api/v1/config", ServerRole.Admin)).Content.ReadAsStringAsync(),
            await (await CallAsync(server, HttpMethod.Post, "/api/v1/config/validate", ServerRole.Admin, "{}")).Content.ReadAsStringAsync(),
            // a candidate that is wrong in a way that quotes its own text: the secret in the file is not in the message about it
            await (await CallAsync(server, HttpMethod.Post, "/api/v1/config/validate", ServerRole.Admin,
                JsonSerializer(new { files = new Dictionary<string, string> { ["connections/erp.yaml"] = Conn.Yaml("erp", connectionString: $"Server=s;Password={Sentinel}-cand", proxy: ["batch_size: 0"]) } }))).Content.ReadAsStringAsync(),
            await (await CallAsync(server, HttpMethod.Get, "/api/v1/nothing", ServerRole.Admin)).Content.ReadAsStringAsync(),
        };
        Assert.All(answers, a => Assert.DoesNotContain(Sentinel, a));
        // and the secrets really are in the files being read (otherwise the test proves nothing)
        Assert.Contains(Sentinel, File.ReadAllText(Path.Combine(ConfigDirectory, "proxy", "auth.yaml")));
    }

    /// <summary>The Flight port speaks HTTP/2 only, so a request to it says so.</summary>
    private static Task<HttpResponseMessage> GetAsync(HttpClient http, Uri uri) =>
        http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher });

    private static string JsonSerializer(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public async Task A_candidate_is_validated_against_the_current_files_without_writing_anything()
    {
        await StartAsync();
        var server = await PairAsync();
        var before = File.ReadAllText(Path.Combine(ConfigDirectory, "server.yaml"));

        var good = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Post, "/api/v1/config/validate", ServerRole.Admin,
            JsonSerializer(new { files = new Dictionary<string, string> { ["connections/new.yaml"] = Conn.Yaml("new", "duckdb", "Data Source=:memory:", proxy: ["allow_writes: true"]) } }))).Content.ReadAsStringAsync())!;
        Assert.True((bool)good["valid"]!);

        var bad = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Post, "/api/v1/config/validate", ServerRole.Admin,
            JsonSerializer(new { files = new Dictionary<string, string> { ["connections/erp.yaml"] = BadBatch } }))).Content.ReadAsStringAsync())!;
        Assert.False((bool)bad["valid"]!);
        var diagnostic = bad["diagnostics"]!.AsArray().Single(d => ((string?)d!["key"])?.EndsWith("BatchSize") == true)!;
        Assert.EndsWith("erp.yaml", (string)diagnostic["file"]!);
        Assert.Equal(Conn.LineOf(BadBatch, "batch_size"), (int)diagnostic["line"]!);

        var unparsable = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Post, "/api/v1/config/validate", ServerRole.Admin,
            JsonSerializer(new { files = new Dictionary<string, string> { ["server.yaml"] = "listen:\n  port: [1\n" } }))).Content.ReadAsStringAsync())!;
        Assert.False((bool)unparsable["valid"]!);
        Assert.Contains("server.yaml", (string)unparsable["diagnostics"]![0]!["message"]!);

        // a name outside the layout is refused before anything is written
        var outside = await CallAsync(server, HttpMethod.Post, "/api/v1/config/validate", ServerRole.Admin, JsonSerializer(new { files = new Dictionary<string, string> { ["../escape.yaml"] = "x: 1" } }));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Equal(before, File.ReadAllText(Path.Combine(ConfigDirectory, "server.yaml")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.yaml")));
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "new.yaml")));
    }

    [Fact]
    public async Task The_management_api_is_never_on_the_flight_port_and_the_flight_service_is_never_on_the_management_port()
    {
        await StartAsync();
        var server = await PairAsync();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

        foreach (var path in new[] { "/api/v1/about", "/management/v1/pairing" })
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(http, new Uri(flight, path))).StatusCode);   // the Flight port
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri(ManagementUrl, "/management/v1/pairing"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(ManagementUrl, "/"))).StatusCode);
        foreach (var port in new[] { flight, ManagementUrl })
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(http, new Uri(port, "/health/live"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(http, new Uri(port, "/version"))).StatusCode);
        }
        _ = server;
    }

    [Fact]
    public async Task Without_a_token_a_call_is_refused_and_an_unpaired_console_stays_out()
    {
        await StartAsync();
        var server = await PairAsync();
        using var http = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(new Uri(ManagementUrl, "/api/v1/about"))).StatusCode);

        Files.Clients().Remove(console.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task Off_by_default_nothing_listens_and_the_paths_are_not_there()
    {
        await StartAsync(management: false);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(http, new Uri(flight, "/management/v1/pairing"))).StatusCode);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(new Uri(ManagementUrl, "/management/v1/pairing")));
    }

    [Fact]
    public async Task The_local_channel_answers_the_command_line_on_this_machine_as_an_administrator()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix socket's folder is chosen here; the Windows pipe has one name per machine");
        var socketDirectory = Path.Combine(dataDirectory, "run");
        await StartAsync(localChannel: true, localDirectory: socketDirectory);

        await using var client = await LocalChannelClient.ConnectAsync(new LocalChannelOptions("dbdataproxy") { Directory = socketDirectory });
        var answer = JsonNode.Parse(await client.SendAsync(Encoding.UTF8.GetBytes("""{"method":"GET","path":"/api/v1/config"}""")))!;
        Assert.Equal(200, (int)answer["status"]!);
        Assert.NotEmpty(answer["body"]!["values"]!.AsArray());
        Assert.DoesNotContain(Sentinel, answer.ToJsonString());

        var nothing = JsonNode.Parse(await client.SendAsync(Encoding.UTF8.GetBytes("""{"method":"GET","path":"/api/v1/nothing"}""")))!;
        Assert.Equal(404, (int)nothing["status"]!);
        var garbage = JsonNode.Parse(await client.SendAsync(Encoding.UTF8.GetBytes("not json")))!;
        Assert.Equal(400, (int)garbage["status"]!);
    }

    [Fact]
    public void The_options_refuse_a_management_api_without_tls_or_on_the_flight_port()
    {
        using var file = TempConfig.Create(("server.yaml", "tls: { mode: none }\nlisten: { address: 127.0.0.1 }\nmanagement: { enabled: true }\n"));
        var load = ProxyConfig.Load(new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(file.Directory)).Build());
        Assert.False(load.Ok);
        Assert.Contains(load.Diagnostics, d => d.KeyPath == "Management.Enabled");

        using var same = TempConfig.Create(("server.yaml", "listen: { address: 127.0.0.1, port: 8816 }\nmanagement: { enabled: true, port: 8816 }\n"));
        var clash = ProxyConfig.Load(new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(same.Directory)).Build());
        Assert.Contains(clash.Diagnostics, d => d.KeyPath == "Management.Port");
    }
}
