using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Hosting.Local;
using DbDataKit.Web.Trust;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Config;

/// <summary>The Proxy's management API (plan 13, P03): its own listener, pairing, the token and the roles, the effective configuration without a secret in it, validation of a candidate, the local channel.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed partial class ManagementTests : IAsyncLifetime
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
        TestFolder.Delete(dataDirectory);
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
            await (await CallAsync(server, HttpMethod.Get, "/api/v1/identities", ServerRole.Admin)).Content.ReadAsStringAsync(),
            await (await CallAsync(server, HttpMethod.Get, "/api/v1/data-sources", ServerRole.Admin)).Content.ReadAsStringAsync(),
        };
        Assert.All(answers, a => Assert.DoesNotContain(Sentinel, a));
        Assert.Contains("\"has_signing_key\":true", answers[^2].Replace(" ", ""));
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

    // ---- data sources (P04) ----

    private static string Scratch(string name) => Conn.Yaml(name, "duckdb", "Data Source=:memory:", proxy: ["allow_writes: true"]);

    private static string Put(string text, string? expected) => JsonSerializer(new { text, expected_version = expected });

    private IOptionsMonitor<ProxyOptions> Options => app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>();

    private static readonly ProxyPrincipal Anyone = new("test", "test", ["*"]);

    [Fact]
    public async Task A_data_source_is_created_read_listed_and_in_use_at_once_and_a_change_and_a_removal_are_applied_without_a_restart()
    {
        await StartAsync();
        var server = await PairAsync();
        var catalog = app.Services.GetRequiredService<DataSourceCatalog>();
        Assert.Throws<Grpc.Core.RpcException>(() => catalog.Resolve(Anyone, "scratch"));   // before

        var created = await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, Put(Scratch("scratch"), null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var version = (string)JsonNode.Parse(await created.Content.ReadAsStringAsync())!["version"]!;
        Assert.True(Options.CurrentValue.DataSources.ContainsKey("scratch"));              // applied: no restart, no wait for a file watcher
        await using (var result = await catalog.Resolve(Anyone, "scratch").Executor.ExecuteQueryAsync(new QueryRequest("SELECT 42 AS answer"), CancellationToken.None))
            Assert.Single(result.Schema.FieldsList);                                         // after: it answers a query

        var listed = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/data-sources", ServerRole.Admin)).Content.ReadAsStringAsync())!["data_sources"]!.AsArray();
        Assert.Contains(listed, d => (string)d!["name"]! == "scratch" && (string)d["engine"]! == "duckdb" && (bool)d["valid"]!);
        var read = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/data-sources/scratch", ServerRole.Admin)).Content.ReadAsStringAsync())!;
        Assert.Equal(version, (string)read["version"]!);
        Assert.Equal(Scratch("scratch"), (string)read["text"]!);

        // a change, based on the version just read
        var changed = Conn.Yaml("scratch", "duckdb", "Data Source=:memory:", proxy: ["allow_writes: true", "batch_size: 500"]);
        var second = await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, Put(changed, version));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(500, Options.CurrentValue.DataSources["scratch"].BatchSize);
        var newVersion = (string)JsonNode.Parse(await second.Content.ReadAsStringAsync())!["version"]!;

        // removed, and gone from use
        var removed = await CallAsync(server, HttpMethod.Delete, $"/api/v1/data-sources/scratch?expected_version={newVersion}", ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "scratch.yaml")));
        Assert.Throws<Grpc.Core.RpcException>(() => catalog.Resolve(Anyone, "scratch"));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Delete, "/api/v1/data-sources/scratch", ServerRole.Admin)).StatusCode);
    }

    [Fact]
    public async Task A_write_based_on_an_old_version_is_refused_with_the_current_one_and_nothing_is_overwritten()
    {
        await StartAsync();
        var server = await PairAsync();
        var first = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, Put(Scratch("scratch"), null))).Content.ReadAsStringAsync())!;
        var version = (string)first["version"]!;

        // creating what exists, and changing from a version that is not the current one
        var again = await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, Put(Scratch("scratch"), null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(version, (string)JsonNode.Parse(await again.Content.ReadAsStringAsync())!["current_version"]!);
        var old = await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, Put(Conn.Yaml("scratch", "duckdb", "Data Source=:memory:", proxy: ["batch_size: 7"]), "0000"));
        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Equal(Scratch("scratch"), File.ReadAllText(Path.Combine(ConfigDirectory, "connections", "scratch.yaml")));
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(server, HttpMethod.Delete, "/api/v1/data-sources/scratch?expected_version=0000", ServerRole.Admin)).StatusCode);
        Assert.True(File.Exists(Path.Combine(ConfigDirectory, "connections", "scratch.yaml")));
    }

    [Fact]
    public async Task A_data_source_that_would_make_the_configuration_invalid_is_refused_with_the_reasons_and_nothing_is_written()
    {
        await StartAsync();
        var server = await PairAsync();
        var bad = Conn.Yaml("bad", proxy: ["batch_size: 0"]);
        var refused = await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/bad", ServerRole.Admin, Put(bad, null));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        Assert.False((bool)body["valid"]!);
        Assert.Contains(body["diagnostics"]!.AsArray(), d => ((string?)d!["key"])?.EndsWith("BatchSize") == true && (int)d["line"]! == Conn.LineOf(bad, "batch_size"));
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "bad.yaml")));

        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/bad", ServerRole.Admin, Put(Conn.Yaml("bad", connectionString: "Server=s;Password=hunter2"), null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/..%2Fescape", ServerRole.Admin, Put(Scratch("x"), null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, "not json")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Viewer, Put(Scratch("scratch"), null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/data-sources", ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task Every_change_is_a_commit_by_the_person_through_their_console_and_a_line_in_the_audit_with_no_content()
    {
        await StartAsync();
        var server = await PairAsync();
        await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, Put(Scratch("scratch"), null));
        var version = (string)JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/data-sources/scratch", ServerRole.Admin)).Content.ReadAsStringAsync())!["version"]!;
        await CallAsync(server, HttpMethod.Delete, $"/api/v1/data-sources/scratch?expected_version={version}", ServerRole.Admin);

        var history = new GitCommitService(ConfigDirectory).GetHistory("connections", 10);
        Assert.Equal(["Remove data source scratch", "Add data source scratch", "Configuration as found"], history.Select(c => c.Message).ToList());
        // the person, and an address that names them and the console and cannot be mistaken for a mailbox
        Assert.All(history.Take(2), c => Assert.Equal("Ada", c.AuthorName));
        Assert.All(history.Take(2), c => Assert.Equal($"ada@{console.Id}.console.invalid", c.AuthorEmail));

        var audit = File.ReadAllLines(Path.Combine(dataDirectory, "management-audit.log")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(["created", "removed"], audit.Select(a => (string)a["outcome"]!).ToList());
        Assert.All(audit, a => { Assert.Equal("scratch", (string)a["target"]!); Assert.Equal("ada", (string)a["userId"]!); });
        Assert.DoesNotContain("duckdb", string.Join("\n", File.ReadAllLines(Path.Combine(dataDirectory, "management-audit.log"))));
    }

    [Fact]
    public async Task A_data_source_is_tested_by_connecting_and_the_failure_is_a_kind_and_a_code_never_the_drivers_text()
    {
        await StartAsync();
        var server = await PairAsync();
        await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/scratch", ServerRole.Admin, Put(Scratch("scratch"), null));
        var ok = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Post, "/api/v1/data-sources/scratch/test", ServerRole.Admin)).Content.ReadAsStringAsync())!;
        Assert.True((bool)ok["ok"]!);

        await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/nowhere", ServerRole.Admin,
            Put(Conn.Yaml("nowhere", connectionString: "Server=127.0.0.1,1;Database=d;Connect Timeout=3", auth: "auth: { mode: sql, user: u, password_secret: 'env:NOWHERE_PW' }"), null));
        using (new EnvScope(("NOWHERE_PW", "pw")))
        {
            var failed = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Post, "/api/v1/data-sources/nowhere/test", ServerRole.Admin)).Content.ReadAsStringAsync())!;
            Assert.False((bool)failed["ok"]!);
            Assert.False(string.IsNullOrWhiteSpace((string)failed["message"]!));
            Assert.DoesNotContain("127.0.0.1", (string)failed["message"]!);
        }

        await CallAsync(server, HttpMethod.Put, "/api/v1/data-sources/their-own", ServerRole.Admin, Put(Conn.Yaml("their-own", "odbc", "Driver={X};Server=s", proxy: ["pass_through: true"]), null));
        var passThrough = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Post, "/api/v1/data-sources/their-own/test", ServerRole.Admin)).Content.ReadAsStringAsync())!;
        Assert.False((bool)passThrough["ok"]!);
        Assert.Contains("database credentials", (string)passThrough["message"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Post, "/api/v1/data-sources/none/test", ServerRole.Admin)).StatusCode);
    }

    [Fact]
    public async Task The_local_channel_changes_data_sources_as_the_operating_system_user()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix socket's folder is chosen here");
        var socketDirectory = Path.Combine(dataDirectory, "run");
        await StartAsync(localChannel: true, localDirectory: socketDirectory);
        await using var client = await LocalChannelClient.ConnectAsync(new LocalChannelOptions("dbdataproxy") { Directory = socketDirectory });

        var message = System.Text.Json.JsonSerializer.Serialize(new { method = "PUT", path = "/api/v1/data-sources/scratch", body = Put(Scratch("scratch"), null) });
        var answer = JsonNode.Parse(await client.SendAsync(Encoding.UTF8.GetBytes(message)))!;
        Assert.Equal(200, (int)answer["status"]!);
        Assert.True(Options.CurrentValue.DataSources.ContainsKey("scratch"));
        Assert.EndsWith("@local.invalid", new GitCommitService(ConfigDirectory).GetHistory("connections", 1)[0].AuthorEmail);
    }

    // ---- client identities (P05) ----

    private static string UserBody(string? password, string[] sources, string? expected = null, string authType = "password", string? defaultSource = null) =>
        JsonSerializer(new Dictionary<string, object?> { ["auth_type"] = authType, ["password"] = password, ["data_sources"] = sources, ["default_data_source"] = defaultSource, ["expected_version"] = expected });

    private async Task<JsonNode> IdentitiesAsync(PairedServer server) =>
        JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/identities", ServerRole.Admin)).Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_user_is_created_with_a_password_that_is_hashed_here_and_never_returned_and_can_sign_in_at_once()
    {
        await StartAsync();
        var server = await PairAsync();
        const string password = "correct horse battery staple";
        Write("connections/scratch.yaml", Scratch("scratch"));

        var created = await CallAsync(server, HttpMethod.Put, "/api/v1/users/carol", ServerRole.Admin, UserBody(password, ["scratch"], defaultSource: "scratch"));
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.DoesNotContain(password, createdText);
        Assert.DoesNotContain("PBKDF2", createdText);

        // only the hash is in the file, and the user is in effect without a restart
        var file = File.ReadAllText(Path.Combine(ConfigDirectory, "proxy", "users.yaml"));
        Assert.DoesNotContain(password, file);
        Assert.Contains("PBKDF2-SHA256$", file);
        var carol = Options.CurrentValue.Users.Single(u => u.Name == "carol");
        Assert.True(PasswordHasher.Verify(password, carol.PasswordHash!));
        Assert.Equal(["scratch"], carol.DataSources);

        // the view says there is a password and shows no hash; the entry that was there before is untouched
        var view = await IdentitiesAsync(server);
        var shown = view["users"]!.AsArray().Single(u => (string)u!["name"]! == "carol")!;
        Assert.True((bool)shown["has_password"]!);
        Assert.Equal("scratch", (string)shown["default_data_source"]!);
        Assert.DoesNotContain("PBKDF2", view.ToJsonString());
        Assert.DoesNotContain(Sentinel, view.ToJsonString());
        Assert.Contains(view["users"]!.AsArray(), u => (string)u!["name"]! == "bob");
    }

    [Fact]
    public async Task A_change_without_a_password_keeps_the_hash_a_new_password_replaces_it_a_stale_change_is_refused_and_a_user_is_removed()
    {
        await StartAsync();
        var server = await PairAsync();
        Write("connections/scratch.yaml", Scratch("scratch"));
        var first = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Put, "/api/v1/users/carol", ServerRole.Admin, UserBody("one", ["scratch"]))).Content.ReadAsStringAsync())!;
        var hashOne = Options.CurrentValue.Users.Single(u => u.Name == "carol").PasswordHash;
        var v1 = (string)first["version"]!;

        var narrowed = await CallAsync(server, HttpMethod.Put, "/api/v1/users/carol", ServerRole.Admin, UserBody(null, ["scratch", "erp"], v1));
        Assert.Equal(HttpStatusCode.OK, narrowed.StatusCode);
        Assert.Equal(hashOne, Options.CurrentValue.Users.Single(u => u.Name == "carol").PasswordHash);
        Assert.Equal(["scratch", "erp"], Options.CurrentValue.Users.Single(u => u.Name == "carol").DataSources);
        var v2 = (string)JsonNode.Parse(await narrowed.Content.ReadAsStringAsync())!["version"]!;

        // based on the version before the last change: refused, with the current one
        var stale = await CallAsync(server, HttpMethod.Put, "/api/v1/users/carol", ServerRole.Admin, UserBody("two", ["scratch"], v1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(v2, (string)JsonNode.Parse(await stale.Content.ReadAsStringAsync())!["current_version"]!);
        Assert.Equal(hashOne, Options.CurrentValue.Users.Single(u => u.Name == "carol").PasswordHash);

        var replaced = await CallAsync(server, HttpMethod.Put, "/api/v1/users/carol", ServerRole.Admin, UserBody("two", ["scratch"], v2));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var hashTwo = Options.CurrentValue.Users.Single(u => u.Name == "carol").PasswordHash!;
        Assert.NotEqual(hashOne, hashTwo);
        Assert.True(PasswordHasher.Verify("two", hashTwo));

        var v3 = (string)JsonNode.Parse(await replaced.Content.ReadAsStringAsync())!["version"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(server, HttpMethod.Delete, "/api/v1/users/carol?expected_version=0000", ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Delete, $"/api/v1/users/carol?expected_version={v3}", ServerRole.Admin)).StatusCode);
        Assert.DoesNotContain(Options.CurrentValue.Users, u => u.Name == "carol");
        Assert.Contains(Options.CurrentValue.Users, u => u.Name == "bob");   // the entry written by hand is still there
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Delete, "/api/v1/users/carol", ServerRole.Admin)).StatusCode);
    }

    [Fact]
    public async Task A_windows_account_has_no_password_and_a_user_that_would_make_the_configuration_invalid_is_refused_with_the_reasons()
    {
        await StartAsync();
        var server = await PairAsync();
        Write("connections/scratch.yaml", Scratch("scratch"));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Put, "/api/v1/users/ACME%5Cdana", ServerRole.Admin, UserBody(null, ["scratch"], authType: "windows"))).StatusCode);
        var view = await IdentitiesAsync(server);
        var windows = Assert.Single(view["windows_accounts"]!.AsArray())!;
        Assert.Equal("ACME\\dana", (string)windows["name"]!);
        Assert.False((bool)windows["has_password"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/users/ACME%5Ceve", ServerRole.Admin, UserBody("pw", ["scratch"], authType: "windows"))).StatusCode);

        // a data source that does not exist, a password user without a password, a bad name, a bad type
        var unknown = await CallAsync(server, HttpMethod.Put, "/api/v1/users/frank", ServerRole.Admin, UserBody("pw", ["nowhere"]));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("nowhere", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/users/frank", ServerRole.Admin, UserBody(null, ["scratch"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/users/bad%20name", ServerRole.Admin, UserBody("pw", ["scratch"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/users/frank", ServerRole.Admin, UserBody("pw", ["scratch"], authType: "kerberos"))).StatusCode);
        Assert.DoesNotContain(Options.CurrentValue.Users, u => u.Name == "frank");
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Put, "/api/v1/users/frank", ServerRole.Viewer, UserBody("pw", ["scratch"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/identities", ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task Pass_through_is_switched_in_auth_yaml_without_disturbing_the_rest_of_the_file()
    {
        await StartAsync();
        var server = await PairAsync();
        var before = File.ReadAllText(Path.Combine(ConfigDirectory, "proxy", "auth.yaml"));
        Write("proxy/auth.yaml", "# who may sign in\n" + before);
        var view = await IdentitiesAsync(server);
        Assert.True((bool)view["pass_through"]!);

        // a change names the version of the file it read, as every write does
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new { allow_pass_through = false }))).StatusCode);
        var off = await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new { allow_pass_through = false, expected_version = (string)view["auth_version"]! }));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(Options.CurrentValue.Auth.AllowPassThrough);
        var after = File.ReadAllText(Path.Combine(ConfigDirectory, "proxy", "auth.yaml"));
        Assert.StartsWith("# who may sign in", after);                                  // comments survive
        Assert.Contains($"{Sentinel}-jwt", after);                                       // the rest of the file is as it was
        Assert.False((bool)(await IdentitiesAsync(server))["pass_through"]!);

        var version = (string)JsonNode.Parse(await off.Content.ReadAsStringAsync())!["auth_version"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new { allow_pass_through = true, expected_version = "0000" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new { allow_pass_through = true, expected_version = version }))).StatusCode);
        Assert.True(Options.CurrentValue.Auth.AllowPassThrough);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, "{}")).StatusCode);
    }

    [Fact]
    public async Task The_jwt_mappings_are_replaced_as_a_list_and_validated_and_the_rest_of_the_file_stays()
    {
        await StartAsync();
        var server = await PairAsync();
        var view = await IdentitiesAsync(server);
        var version = (string)view["auth_version"]!;

        var changed = await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new
        {
            jwt_mappings = new[] { new { claim = "group", value = "analysts", data_sources = new[] { "erp" }, default_data_source = (string?)"erp" }, new { claim = "role", value = "admin", data_sources = new[] { "*" }, default_data_source = (string?)null } },
            expected_version = version,
        }));
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        var jwt = (await IdentitiesAsync(server))["jwt"]!;
        Assert.Equal(2, jwt["mappings"]!.AsArray().Count);
        Assert.Equal("analysts", (string)jwt["mappings"]![0]!["value"]!);
        Assert.Contains($"{Sentinel}-jwt", File.ReadAllText(Path.Combine(ConfigDirectory, "proxy", "auth.yaml")));   // the signing key is still there, and was never shown
        Assert.Equal("Set 2 JWT mapping(s)", new GitCommitService(ConfigDirectory).GetHistory("proxy", 1)[0].Message);

        // a stale version is refused, a mapping without a claim is refused before anything is written, and an empty list removes them all
        var next = (string)JsonNode.Parse(await changed.Content.ReadAsStringAsync())!["auth_version"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new { jwt_mappings = Array.Empty<object>(), expected_version = version }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new { jwt_mappings = new[] { new { claim = "", value = "x", data_sources = new[] { "erp" } } }, expected_version = next }))).StatusCode);
        Assert.Equal(2, (await IdentitiesAsync(server))["jwt"]!["mappings"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Put, "/api/v1/auth", ServerRole.Admin, JsonSerializer(new { jwt_mappings = Array.Empty<object>(), expected_version = next }))).StatusCode);
        Assert.Empty((await IdentitiesAsync(server))["jwt"]!["mappings"]!.AsArray());
    }

    [Fact]
    public async Task The_view_shows_the_jwt_settings_and_mappings_as_in_effect_and_only_whether_there_is_a_signing_key()
    {
        await StartAsync();
        var server = await PairAsync();
        var jwt = (await IdentitiesAsync(server))["jwt"]!;
        Assert.True((bool)jwt["enabled"]!);
        Assert.True((bool)jwt["has_signing_key"]!);
        Assert.Equal("api://x", (string)jwt["audience"]!);
        var mapping = Assert.Single(jwt["mappings"]!.AsArray())!;
        Assert.Equal("c", (string)mapping["claim"]!);
        Assert.Equal(["*"], mapping["data_sources"]!.AsArray().Select(d => (string)d!).ToList());
        Assert.DoesNotContain(Sentinel, jwt.ToJsonString());
    }
}
