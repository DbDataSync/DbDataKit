using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Publishing a connection of a project to a paired Proxy as a read-only data source (plan 13, T10, Build's side): pairing as a server client, only the read login leaving, the person and the Publisher role on the call, and the proxy's refusals
/// passed on in its own words. The Proxy here is a stand-in that pairs and records; the Proxy's own rules are tested in its suite.
/// </summary>
public sealed class BuildServerProxyTests : IAsyncLifetime
{
    private const string ReadLogin = "Server=db;User Id=reader;Password=READ-pw-1c9";
    private const string WriteLogin = "Server=db;User Id=writer;Password=WRITE-pw-77f";

    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-proxy-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly List<(string Method, string Path, string Body, ValidatedToken Token)> seen = [];
    private int dataSourceStatus = 404;       // what the proxy says to a read of the data source; 200 means one exists
    private HttpStatusCode writeAnswer = HttpStatusCode.OK;
    private HttpStatusCode modelAnswer = HttpStatusCode.OK;     // what the proxy says to publishing a model
    private WebApplication proxy = null!;
    private WebApplication build = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;
    private string project = null!;
    private string retail = null!;

    private string BuildDir => Path.Combine(root, "build-server");
    private string ProxyDir => Path.Combine(root, "proxy");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(BuildDir);
        Directory.CreateDirectory(ProxyDir);
        project = Path.Combine(root, "sales");
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
        var projects = new ProjectsFile(BuildDir);
        projects.Add(new HostedProject("sales", project, null, null));
        retail = Path.Combine(root, "retail");
        Assert.Equal(0, CliApp.Run(["project", "create", "retail", retail, "--format", "json"], new StringWriter(), new StringWriter()));
        projects.Add(new HostedProject("retail", retail, null, null));

        var secrets = ServerSecrets.Open(BuildDir);
        using (var read = SecretValue.From(ReadLogin)) await secrets.SetAsync(ServerSecrets.NameOf("sqlserver", DbDataKit.Connections.LoginRole.Read), read);
        using (var write = SecretValue.From(WriteLogin)) await secrets.SetAsync(ServerSecrets.NameOf("sqlserver", DbDataKit.Connections.LoginRole.Write), write);

        proxy = StartFakeProxy();
        await proxy.StartAsync();

        var settings = new BuildServerSettings(BuildDir, new Uri("https://127.0.0.1:0"));
        var publisher = new ProxyPublisher(new ProxiesFile(BuildDir), new ConsoleKeyStore(secrets, secrets), secrets);
        build = BuildServer.Build(settings, BuildServer.Certificate(BuildDir), new Idle(), projects, new TuiCommand.CliHost(_ => null).Commands, _ => { }, proxyPublisher: publisher);
        await build.StartAsync();
        var url = new Uri(build.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Approver | ServerRole.Author));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await build.StopAsync(); await build.DisposeAsync();
        await proxy.StopAsync(); await proxy.DisposeAsync();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private sealed class Idle : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, "{}", ""));
    }

    private WebApplication StartFakeProxy()
    {
        var settings = new BuildServerSettings(ProxyDir, new Uri("https://127.0.0.1:0"));
        var files = new PairingFiles(settings.ServerYaml);
        var instanceId = ServerIdentity.InstanceId(settings.ServerYaml, "proxy", "fake proxy");
        var certificate = DirectoryCertificate.Load(ProxyDir);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.UseManagementApiVersion();
        app.MapPairing(new PairingService(instanceId, PairingProof.CertificateFingerprint(certificate), files.Clients(), files.Codes()));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, files.Clients(), new MemoryReplayCache()), _ => { });
        app.MapMethods("/api/v1/{**rest}", ["GET", "PUT", "DELETE"], async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            seen.Add((context.Request.Method, context.Request.Path.Value!, body, context.ManagementToken()));
            var path = context.Request.Path.Value!;
            if (path.StartsWith("/api/v1/secrets/", StringComparison.Ordinal)) return Results.Json(new { reference = "secret:dbdata:proxy:managed-x:" + path[(path.LastIndexOf('/') + 1)..] });
            if (path.StartsWith("/api/v1/semantic-models/", StringComparison.Ordinal))
                return context.Request.Method == "GET" ? Results.Json(new { error = "none" }, statusCode: 404)
                    : modelAnswer == HttpStatusCode.OK ? Results.Json(new { version = 1, created = true }) : Results.Json(new { error = "The catalog does not load, so nothing was published." }, statusCode: (int)modelAnswer);
            if (context.Request.Method == "GET") return dataSourceStatus == 200 ? Results.Json(new { version = "v1" }) : Results.Json(new { error = "none" }, statusCode: 404);
            return writeAnswer == HttpStatusCode.OK
                ? Results.Json(new { version = "v2", created = dataSourceStatus != 200 })
                : Results.Json(new { error = "The object was not made by a publisher, so it is not this client's to change" }, statusCode: (int)writeAnswer);
        });
        return app;
    }

    private async Task Pair(string name = "edge")
    {
        var code = new PairingFiles(new BuildServerSettings(ProxyDir, new Uri("https://127.0.0.1:0")).ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code;
        var output = new StringWriter();
        var exit = await ServerCli.RunAsync(["server", "proxy", "add", name, proxy.Urls.First(), "--code", code, "--repo", BuildDir], output, output, new StringReader(""));
        Assert.True(exit == 0, output.ToString());
    }

    private async Task<HttpResponseMessage> Publish(string who, ServerRole role, string proxyName, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/sales/proxies/{proxyName}/publish") { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, who, who, role)));
        return await http.SendAsync(request);
    }

    private async Task<HttpResponseMessage> Call(HttpMethod method, string path, ServerRole role, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ana", "Ana", role)));
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task A_console_has_the_server_make_a_publisher_code_and_pairs_it_with_a_proxy_through_the_api_without_anyone_typing_a_code()
    {
        // T11: the code this server makes lets another server pair as a publisher, once, and nothing wider
        using (var made = await Call(HttpMethod.Post, "/api/v1/pairing-codes", ServerRole.Admin, new { name = "ana", minutes = 5 }))
        {
            Assert.Equal(HttpStatusCode.OK, made.StatusCode);
            var answer = JsonNode.Parse(await made.Content.ReadAsStringAsync())!;
            Assert.Equal("publisher", (string?)answer["max_role"]![0]);
            var grant = new PairingFiles(new BuildServerSettings(BuildDir, new Uri("https://127.0.0.1:0")).ServerYaml).Codes().Redeem((string?)answer["code"]);
            Assert.Equal(ServerRole.Publisher, grant!.Ceiling);
            Assert.Equal("ana", grant.Label);
        }
        using (var wide = await Call(HttpMethod.Post, "/api/v1/pairing-codes", ServerRole.Admin, new { minutes = 9999 })) Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        using (var notAdmin = await Call(HttpMethod.Post, "/api/v1/pairing-codes", ServerRole.Approver | ServerRole.Author)) Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);

        // the pairing itself: this server pairs with the proxy, using a code the proxy made
        var proxyCode = new PairingFiles(new BuildServerSettings(ProxyDir, new Uri("https://127.0.0.1:0")).ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code;
        using (var denied = await Call(HttpMethod.Post, "/api/v1/proxies", ServerRole.Approver | ServerRole.Author, new { name = "edge", url = proxy.Urls.First(), code = proxyCode })) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var wrong = await Call(HttpMethod.Post, "/api/v1/proxies", ServerRole.Admin, new { name = "edge", url = proxy.Urls.First(), code = "AAAAA-BBBBB-CCCCC-DDDDD" }))
        {
            Assert.Equal(HttpStatusCode.BadGateway, wrong.StatusCode);
            Assert.Empty(new ProxiesFile(BuildDir).All());
        }
        using (var bad = await Call(HttpMethod.Post, "/api/v1/proxies", ServerRole.Admin, new { name = "Not Valid", url = proxy.Urls.First(), code = proxyCode })) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var ok = await Call(HttpMethod.Post, "/api/v1/proxies", ServerRole.Admin, new { name = "edge", url = proxy.Urls.First(), code = proxyCode }))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("edge", new ProxiesFile(BuildDir).All().Single().Name);
        }
        Assert.Equal(ClientKind.Server, new PairingFiles(new BuildServerSettings(ProxyDir, new Uri("https://127.0.0.1:0")).ServerYaml).Clients().All().Single().Kind);
    }

    [Fact]
    public async Task A_proxy_is_paired_as_a_server_client_listed_and_forgotten()
    {
        await Pair();
        var listed = new StringWriter();
        Assert.Equal(0, await ServerCli.RunAsync(["server", "proxy", "list", "--repo", BuildDir], listed, listed, new StringReader("")));
        Assert.Contains("edge", listed.ToString());
        Assert.Equal(ClientKind.Server, new PairingFiles(new BuildServerSettings(ProxyDir, new Uri("https://127.0.0.1:0")).ServerYaml).Clients().All().Single().Kind);

        var bad = new StringWriter();
        Assert.NotEqual(0, await ServerCli.RunAsync(["server", "proxy", "add", "again", proxy.Urls.First(), "--code", "AAAAA-BBBBB-CCCCC-DDDDD", "--repo", BuildDir], bad, bad, new StringReader("")));
        Assert.Equal(0, await ServerCli.RunAsync(["server", "proxy", "remove", "edge", "--repo", BuildDir], new StringWriter(), new StringWriter(), new StringReader("")));
        Assert.Empty(new ProxiesFile(BuildDir).All());
    }

    [Fact]
    public async Task Publishing_sends_the_read_login_once_and_a_definition_that_refers_to_it_never_the_write_login_and_names_the_person()
    {
        await Pair();

        var reply = await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver", data_source = "warehouse" });

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var body = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
        Assert.Equal(("edge", "warehouse", true), (((string)body["proxy"]!), (string)body["data_source"]!, (bool)body["created"]!));

        var secret = seen.Single(r => r.Path == "/api/v1/secrets/warehouse-read");
        Assert.Equal(ReadLogin, (string)JsonNode.Parse(secret.Body)!["value"]!);
        var definition = (string)JsonNode.Parse(seen.Single(r => r.Method == "PUT" && r.Path == "/api/v1/data-sources/warehouse").Body)!["text"]!;
        Assert.Contains("engine: sqlserver", definition);
        Assert.Contains("access: readonly", definition);
        Assert.Contains("connection_string_secret: 'secret:dbdata:proxy:managed-x:warehouse-read'", definition);
        Assert.DoesNotContain("READ-pw", definition);                                       // the file refers to the secret and holds none
        Assert.DoesNotContain(seen, r => r.Body.Contains("WRITE-pw", StringComparison.Ordinal));      // the write login never leaves

        Assert.All(seen, r =>
        {
            Assert.Equal("ada", r.Token.SubjectId);                                         // the person who asked
            Assert.Equal(ServerRole.Publisher, r.Token.Roles & ServerRole.Publisher);
            Assert.Equal(ClientKind.Server, r.Token.Client.Kind);
        });
    }

    [Fact]
    public async Task Publishing_again_changes_the_data_source_on_the_version_read_and_a_refusal_is_passed_on_in_the_proxys_words()
    {
        await Pair();
        await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver" });
        dataSourceStatus = 200;
        seen.Clear();

        var again = await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver" });
        Assert.False((bool)JsonNode.Parse(await again.Content.ReadAsStringAsync())!["created"]!);
        Assert.Equal("v1", (string)JsonNode.Parse(seen.Single(r => r.Method == "PUT" && r.Path == "/api/v1/data-sources/sqlserver").Body)!["expected_version"]!);

        writeAnswer = HttpStatusCode.Forbidden;
        var refused = await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("not this client's to change", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_engine_the_proxy_serves_can_be_published_and_a_connection_of_another_engine_is_refused_in_a_sentence_before_anything_is_sent()
    {
        await Pair();
        var secrets = ServerSecrets.Open(BuildDir);
        // a connection file per driver (readonly where the driver can only be read), each with a read login of its own
        var files = new Dictionary<string, string>
        {
            ["lake"] = "engine: odbc\naccess: readonly", ["local"] = "engine: duckdb\naccess: readonly", ["pg"] = "engine: postgres", ["flight"] = "engine: flightsql\naccess: readonly",
        };
        foreach (var (name, engine) in files)
        {
            File.WriteAllText(Path.Combine(project, "connections", name + ".yaml"), $"schema: connection/1\nname: {name}\n{engine}\n");
            using var login = SecretValue.From($"login-of-{name}");
            await secrets.SetAsync(ServerSecrets.NameOf(name, DbDataKit.Connections.LoginRole.Read), login);
        }

        foreach (var (name, driver) in new[] { ("lake", "odbc"), ("local", "duckdb") })
        {
            seen.Clear();
            var reply = await Publish("ada", ServerRole.Approver, "edge", new { connection = name });
            Assert.True(reply.StatusCode == HttpStatusCode.OK, await reply.Content.ReadAsStringAsync());
            Assert.Equal($"login-of-{name}", (string)JsonNode.Parse(seen.Single(r => r.Path == $"/api/v1/secrets/{name}-read").Body)!["value"]!);
            var definition = (string)JsonNode.Parse(seen.Single(r => r.Method == "PUT" && r.Path == $"/api/v1/data-sources/{name}").Body)!["text"]!;
            Assert.Contains($"engine: {driver}", definition);
            Assert.Contains("access: readonly", definition);
            Assert.Contains($"connection_string_secret: 'secret:dbdata:proxy:managed-x:{name}-read'", definition);
        }

        foreach (var (name, driver) in new[] { ("pg", "postgres"), ("flight", "flightsql") })
        {
            seen.Clear();
            var reply = await Publish("ada", ServerRole.Approver, "edge", new { connection = name });
            Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
            var message = (string)JsonNode.Parse(await reply.Content.ReadAsStringAsync())!["error"]!;
            Assert.Contains("sqlserver, odbc and duckdb", message);
            Assert.Contains($"not {driver}", message);
            Assert.Empty(seen);                                                             // not even the login was sent
        }
    }

    [Fact]
    public async Task Publishing_needs_the_approver_role_a_paired_proxy_a_connection_of_the_project_and_a_read_login()
    {
        await Pair();

        Assert.Equal(HttpStatusCode.Forbidden, (await Publish("bob", ServerRole.Author, "edge", new { connection = "sqlserver" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Publish("ada", ServerRole.Approver, "nowhere", new { connection = "sqlserver" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Publish("ada", ServerRole.Approver, "edge", new { connection = "missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Publish("ada", ServerRole.Approver, "edge", new { })).StatusCode);

        await ServerSecrets.Open(BuildDir).DeleteAsync(ServerSecrets.NameOf("sqlserver", DbDataKit.Connections.LoginRole.Read));
        var none = await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver" });
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Contains("No read login", await none.Content.ReadAsStringAsync());
        Assert.Empty(seen);                                                                 // nothing reached the proxy in any of these
    }

    private async Task<HttpResponseMessage> PublishModel(string who, ServerRole role, string proxyName, object body, string projectId = "retail")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/proxies/{proxyName}/publish-model") { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, who, who, role)));
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Publishing_a_semantic_model_sends_the_connections_it_reads_first_then_the_catalog_with_its_physical_layer_and_never_the_write_login()
    {
        await Pair();

        var reply = await PublishModel("ada", ServerRole.Approver, "edge", new { model = "retail_sales" });

        Assert.True(reply.StatusCode == HttpStatusCode.OK, await reply.Content.ReadAsStringAsync());
        var answer = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
        Assert.Equal(("retail_sales", 1), ((string)answer["model"]!, (int)answer["version"]!));
        Assert.Equal(["sqlserver"], answer["data_sources"]!.AsArray().Select(d => (string)d!));

        // the data source before the model that reads it
        var order = seen.Select(r => r.Path).ToList();
        Assert.True(order.IndexOf("/api/v1/data-sources/sqlserver") < order.IndexOf("/api/v1/semantic-models/retail_sales") && order.IndexOf("/api/v1/data-sources/sqlserver") >= 0);

        var sent = JsonNode.Parse(seen.Single(r => r.Method == "PUT" && r.Path == "/api/v1/semantic-models/retail_sales").Body)!;
        var catalog = DbDataKit.Semantic.CatalogLoader.LoadText((string)sent["catalog"]!);            // what the proxy loads
        Assert.Equal("retail_sales", catalog.Name);
        Assert.Contains(catalog.Doc.Sources.Keys, k => k.Length > 0);
        Assert.All(sent["sources"]!.AsObject(), s => Assert.Equal("sqlserver", (string)s.Value!));     // each source reads the data source of its connection
        Assert.DoesNotContain(seen, r => r.Body.Contains("WRITE-pw", StringComparison.Ordinal));
        Assert.All(seen, r => Assert.Equal("ada", r.Token.SubjectId));
    }

    [Fact]
    public async Task Publishing_a_semantic_model_needs_the_approver_role_a_model_of_the_project_and_passes_on_the_proxys_refusal()
    {
        await Pair();

        Assert.Equal(HttpStatusCode.Forbidden, (await PublishModel("bob", ServerRole.Author, "edge", new { model = "retail_sales" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublishModel("ada", ServerRole.Approver, "edge", new { model = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishModel("ada", ServerRole.Approver, "edge", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublishModel("ada", ServerRole.Approver, "nowhere", new { model = "retail_sales" })).StatusCode);
        Assert.Empty(seen);

        modelAnswer = HttpStatusCode.BadRequest;
        var refused = await PublishModel("ada", ServerRole.Approver, "edge", new { model = "retail_sales" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("nothing was published", await refused.Content.ReadAsStringAsync());
    }
}
