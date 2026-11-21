using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// The Gateway's public listener: the modules that have no port of their own answer on one HTTPS port, routed by path. One process serves sign-in, an app, a store read, a view, a feed and an MCP call on that port; a module
/// with a port of its own answers there and not under its prefix; a module that is off answers 404 under its prefix.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class PublicListenerTests : IAsyncLifetime
{
    private const string Model = "sales_catalog";
    private const string Source = "sales-ro";
    private const string Analysts = "analysts";

    private const string Queries = """

        queries:
          revenue_by_region:
            description: Net revenue by region
            sql: SELECT customers.region, MEASURE(orders.net_revenue) AS net FROM sales_catalog GROUP BY ALL ORDER BY customers.region LIMIT 1000
        """;

    private const string Manifest = "schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write]}\n";

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-public-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int publicPort;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string ConfigDirectory => GatewayConfigFile.DirectoryIn(dataDirectory);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(ConfigDirectory, "connections"));
        File.WriteAllText(Path.Combine(ConfigDirectory, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        var file = Path.Combine(dataDirectory, "sales.duckdb");
        using (var connection = new DuckDB.NET.Data.DuckDBConnection("Data Source=" + file))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "seed.sql"));
            command.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(ConfigDirectory, "connections", Source + ".yaml"), Conn.Yaml(Source, "duckdb", "Data Source=" + file, gateway: [$"groups: [{Analysts}]"]));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(dataDirectory);
    }

    private static HttpClient Trusting(string url) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(url) };

    private async Task StartAsync(IEnumerable<string> modules, params string[] more)
    {
        var args = new List<string> { $"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Management:Enabled=false", "--Gateway:DefinitionPollSeconds=1", "--Gateway:Public:Address=127.0.0.1", $"--Gateway:Public:Port={publicPort}" };
        args.AddRange(modules.Select((m, i) => $"--Gateway:Modules:{i}={m}"));
        args.AddRange(more);
        app = GatewayHost.Build([.. args]);
        await app.StartAsync();
    }

    private T Get<T>() where T : notnull => app!.Services.GetRequiredService<T>();

    private void PublishModel()
    {
        var models = Get<PublishedSemanticModels>();
        models.Publish(Model, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml")) + Queries, null, "test", new Dictionary<string, string> { ["sqlserver_sales"] = Source }, [], DateTimeOffset.UtcNow);
        models.ServeNow(Model);
        Get<DefinitionFollower>().Poll();
    }

    /// <summary>A person of the home with a role on this server, and their personal access token.</summary>
    private string Person()
    {
        var state = Get<StateDatabase>();
        var (user, token) = HomeSeed.Person(state, "Ann", "ann@example.invalid", Analysts);
        var serverId = GatewayStoreModule.ServerIdOf(Get<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, state);
        new HomeDirectory(state).Assign(AssignmentSubject.User(user.Id), serverId, ServerRole.Viewer | ServerRole.Author);
        return token;
    }

    /// <summary>Whether the feeds module itself answered: its not-found names its prefix, the listener's does not.</summary>
    private static async Task<bool> FeedsAnsweredAsync(HttpClient http, string token) =>
        (await (await CallAsync(http, HttpMethod.Get, "/feeds/api/v1/nothing", token)).Content.ReadAsStringAsync()).Contains("/feeds/api/v1/queries/", StringComparison.Ordinal);

    private static async Task<HttpResponseMessage> CallAsync(HttpClient http, HttpMethod method, string path, string? token, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task One_process_serves_sign_in_an_app_a_store_read_a_view_a_feed_and_an_mcp_call_on_one_port()
    {
        publicPort = FreePort();
        // the state a person with a role on an app and the home's client for the apps host would be in
        string ada;
        string clientId;
        {
            var database = GatewayState.Open(dataDirectory);
            ada = new IdentityUserStore(database).CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer).Id;
            new HomeDirectory(database).AssignApp(AssignmentSubject.User(ada), "acme", ServerRole.Viewer);
            var instances = new AppInstanceStore(new DefinitionStore(database));
            instances.PublishApp(new AppPackage(Manifest, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<html><head></head><body>hello notes</body></html>"), ["app.js"] = "1"u8.ToArray() }), "ada");
            instances.Save(new AppInstance("acme", "notes"), "ada");
            clientId = new OidcClients(database).Register("Apps", [$"https://localhost:{publicPort}{AppsEndpoints.CallbackPath}"], confidential: false).Item1.ClientId;
        }
        await StartAsync(["identity", "apps", "store", "views", "feeds", "mcp"], $"--Gateway:Apps:RolesRefreshSeconds=1", $"--Gateway:Apps:Home:ClientId={clientId}");
        var state = Get<StateDatabase>();
        var token = Person();
        PublishModel();
        using var http = Trusting($"https://localhost:{publicPort}");

        // sign-in: the home's discovery document names the public port as its issuer, and its sign-in page is served
        var discovery = JsonNode.Parse(await http.GetStringAsync("/.well-known/openid-configuration"))!;
        Assert.Equal($"https://localhost:{publicPort}", ((string)discovery["issuer"]!).TrimEnd('/'));
        var signIn = await http.GetAsync("/identity/sign-in");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.StartsWith("text/html", signIn.Content.Headers.ContentType!.MediaType);

        // an app: the host sends the browser to the home on the same port, the home returns it, and the shell is served
        var start = await http.GetAsync("/apps/acme/");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        Assert.Equal(publicPort, authorize.RequestUri!.Port);
        authorize.Headers.Add("Cookie", $"{GatewayIdentityModule.Auth.SessionCookie}={new IdentitySessionStore(state).Create(ada).Id}");
        var back = await http.SendAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var callback = await http.GetAsync(back.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AppsEndpoints.SessionCookie + "=", StringComparison.Ordinal)).Split(';')[0];
        var shell = new HttpRequestMessage(HttpMethod.Get, "/apps/acme/");
        shell.Headers.Add("Cookie", cookie);
        var page = await http.SendAsync(shell);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("sandbox=", await page.Content.ReadAsStringAsync());

        // a store read: a caller with no token is refused by the store module, one with a token reads the models
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(http, HttpMethod.Get, "/store/api/v1/models", null)).StatusCode);
        var models = await CallAsync(http, HttpMethod.Get, "/store/api/v1/models", token);
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.NotNull(JsonNode.Parse(await models.Content.ReadAsStringAsync()));

        // a view: the person's own views
        var views = await CallAsync(http, HttpMethod.Get, "/views/api/v1/my/views", token);
        Assert.Equal(HttpStatusCode.OK, views.StatusCode);

        // a feed: a saved query's rows, and the module's own not-found names the prefix
        var feed = await CallAsync(http, HttpMethod.Get, $"/feeds/api/v1/queries/{Model}/revenue_by_region", token);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        var rows = JsonNode.Parse(await feed.Content.ReadAsStringAsync())!;
        Assert.Equal("ok", (string?)rows["status"]);
        Assert.NotEmpty(rows["rows"]!.AsArray());
        var missing = await CallAsync(http, HttpMethod.Get, "/feeds/api/v1/nothing", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("/feeds/api/v1/queries/", await missing.Content.ReadAsStringAsync());

        // an MCP call
        var mcp = await CallAsync(http, HttpMethod.Post, "/mcp", token, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
        Assert.NotEmpty(JsonNode.Parse(await mcp.Content.ReadAsStringAsync())!["result"]!["tools"]!.AsArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(http, HttpMethod.Post, "/mcp", null, """{"jsonrpc":"2.0","id":1,"method":"ping"}""")).StatusCode);

        // a path nobody serves is a 404 from the listener itself
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/nothing/here")).StatusCode);
    }

    [Fact]
    public async Task A_module_with_a_port_of_its_own_answers_there_and_not_under_its_prefix()
    {
        publicPort = FreePort();
        var storePort = FreePort();
        while (storePort == publicPort) storePort = FreePort();
        await StartAsync(["store", "feeds"], "--Gateway:Store:Address=127.0.0.1", $"--Gateway:Store:Port={storePort}");
        var token = Person();
        using var pub = Trusting($"https://localhost:{publicPort}");
        using var own = Trusting($"https://localhost:{storePort}");

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(own, HttpMethod.Get, "/api/v1/models", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(pub, HttpMethod.Get, "/store/api/v1/models", token)).StatusCode);
        // the feeds module has no port set and stays on the public listener
        Assert.True(await FeedsAnsweredAsync(pub, token));
    }

    [Fact]
    public async Task A_module_that_is_off_answers_404_under_its_prefix()
    {
        publicPort = FreePort();
        await StartAsync(["feeds"]);
        var token = Person();
        using var http = Trusting($"https://localhost:{publicPort}");

        foreach (var path in new[] { "/store/api/v1/models", "/views/api/v1/my/views", "/mcp", "/apps/acme/", "/identity/sign-in" })
        {
            var response = await CallAsync(http, HttpMethod.Get, path, token);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path}: {(int)response.StatusCode}");
        }
        Assert.True(await FeedsAnsweredAsync(http, token));
    }

    [Fact]
    public void Port_clashes_are_named_on_the_later_setting_and_a_module_without_a_port_never_clashes()
    {
        static IReadOnlyList<DbDataKit.Config.Yaml.ConfigDiagnostic> Load(string yaml)
        {
            using var file = TempConfig.Create(("server.yaml", yaml));
            return ConfigTestSupport.LoadDirectory(file.Directory).Diagnostics;
        }

        // the public listener against the Flight port, and a module's own port against the public listener's
        Assert.Contains(Load("listen: { address: 127.0.0.1, port: 9000 }\npublic: { port: 9000 }\nmodules: [flightsql, store]\n"), d => d.KeyPath == "Public.Port");
        Assert.Contains(Load("public: { port: 9000 }\nstore: { port: 9000 }\nfeeds: { }\nmodules: [flightsql, store, feeds]\n"), d => d.KeyPath == "Store.Port" || d.KeyPath == "Public.Port");
        // with every module on a port of its own the public listener is not used, so its port clashes with nothing
        Assert.DoesNotContain(Load("listen: { address: 127.0.0.1, port: 9000 }\npublic: { port: 9000 }\nstore: { port: 9001 }\nmodules: [flightsql, store]\n"), d => d.KeyPath == "Public.Port");
        Assert.DoesNotContain(Load("modules: [flightsql, store, feeds]\n"), d => d.KeyPath?.EndsWith(".Port", StringComparison.Ordinal) == true);
    }
}
