using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Support;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 22a GP35c: the apps host's semantic route (<c>/apps/&lt;instance&gt;/_data/api/v1/semantic/...</c>) through the real host: the models the manifest lists, a model's catalog and a query, answered by the semantic module as the
/// person the app's token names, so the data sources their home groups reach decide what they see.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsSemanticRouteTests : IAsyncLifetime
{
    private const string Request = """
        {"spec_version": "0.3", "kind": "query",
         "query": {"measures": ["orders.net_revenue", "orders.order_count"], "limit": 100, "attributes": ["customers.region"],
                   "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
                   "order": [["orders.net_revenue", "desc"]]}}
        """;

    private const string Manifest = "schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nsemantic: [sales_catalog]\n";

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-appsem-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int appsPort;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public ValueTask InitializeAsync()
    {
        var config = GatewayConfigFile.DirectoryIn(dataDirectory);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
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

    private async Task<(HttpClient Apps, HttpClient Home, StateDatabase Database, string Ada, string Bob, string Eve)> StartAsync()
    {
        appsPort = FreePort();
        var identityPort = FreePort();
        while (identityPort == appsPort) identityPort = FreePort();
        var warehouse = Path.Combine(dataDirectory, "warehouse.duckdb");
        using (var c = new DuckDBConnection($"Data Source={warehouse}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "seed.sql"));
            cmd.ExecuteNonQuery();
        }

        // the state is made before the host is, as an operator's commands would: three people of the home (two with a role on the app), an app that lists the model, and the client the apps host signs in with
        var database = GatewayState.Open(dataDirectory);
        var users = new IdentityUserStore(database);
        var ada = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer).Id;
        var bob = users.CreateUser("Bob Babbage", "bob@example.invalid", UserRole.Viewer).Id;
        var eve = users.CreateUser("Eve Noether", "eve@example.invalid", UserRole.Viewer).Id;
        HomeSeed.Join(database, ada, "analysts");
        HomeSeed.Join(database, bob, "others");
        var directory = new HomeDirectory(database);
        foreach (var person in new[] { ada, bob }) directory.AssignApp(AssignmentSubject.User(person), "acme", ServerRole.Viewer);
        var store = new AppInstanceStore(new DefinitionStore(database));
        store.PublishApp(new AppPackage(Manifest, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<html><head></head><body>x</body></html>") }), "ada");
        store.Save(new AppInstance("acme", "notes"), "ada");
        store.PublishApp(new AppPackage(Manifest.Replace("name: notes", "name: bare").Replace("semantic: [sales_catalog]\n", ""), new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<html></html>") }), "ada");
        store.Save(new AppInstance("bare", "bare"), "ada");
        directory.AssignApp(AssignmentSubject.User(ada), "bare", ServerRole.Viewer);
        var (client, _) = new OidcClients(database).Register("Apps", [$"https://localhost:{appsPort}{AppsEndpoints.CallbackPath}"], confidential: false);

        var settings = new Dictionary<string, string?>
        {
            ["Gateway:DataDirectory"] = dataDirectory, ["Gateway:Management:Enabled"] = "false", ["Gateway:Modules:0"] = "apps", ["Gateway:Modules:1"] = "identity",
            ["Gateway:Apps:Address"] = "127.0.0.1", ["Gateway:Apps:Port"] = appsPort.ToString(), ["Gateway:Apps:Home:ClientId"] = client.ClientId,
            ["Gateway:Identity:Address"] = "127.0.0.1", ["Gateway:Identity:Port"] = identityPort.ToString(), ["Gateway:Identity:PublicUrl"] = $"https://localhost:{identityPort}",
            ["Gateway:DataSources:warehouse:Provider"] = "duckdb", ["Gateway:DataSources:warehouse:ConnectionString"] = $"Data Source={warehouse}",
            ["Gateway:DataSources:other:Provider"] = "duckdb", ["Gateway:DataSources:other:ConnectionString"] = "Data Source=:memory:",
            ["Gateway:Semantic:Models:0:Catalog"] = Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"),
            ["Gateway:Semantic:Models:0:Sources:sqlserver_sales"] = "warehouse",
        };
        HomeSeed.Open(settings, ["warehouse"], group: "analysts");
        HomeSeed.Open(settings, ["other"], group: "others");
        app = GatewayHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await app.StartAsync();
        return (Trusting($"https://localhost:{appsPort}"), Trusting($"https://localhost:{identityPort}"), database, ada, bob, eve);
    }

    private static async Task<string> SignInAsync(HttpClient apps, HttpClient home, StateDatabase database, string user, string path)
    {
        var start = await apps.GetAsync(path);
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        authorize.Headers.Add("Cookie", $"{GatewayIdentityModule.Auth.SessionCookie}={new IdentitySessionStore(database).Create(user).Id}");
        var back = await home.SendAsync(authorize);
        var callback = await apps.GetAsync(back.Headers.Location);
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AppsEndpoints.SessionCookie + "=", StringComparison.Ordinal));
        return cookie[(AppsEndpoints.SessionCookie.Length + 1)..].Split(';')[0];
    }

    private static async Task<string> TokenAsync(HttpClient apps, string instance, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/apps/{instance}/_token");
        request.Headers.Add("Cookie", $"{AppsEndpoints.SessionCookie}={session}");
        request.Headers.Add("X-DbData-Shell", "1");
        var response = await apps.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["token"]!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient apps, string token, HttpMethod method, string path, string? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Headers = { { "Authorization", "Bearer " + token } } };
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return apps.SendAsync(request);
    }

    [Fact]
    public async Task An_app_lists_its_models_reads_a_catalog_and_runs_a_query_as_the_person_whose_groups_reach_the_data()
    {
        var (apps, home, database, ada, _, _) = await StartAsync();
        var token = await TokenAsync(apps, "acme", await SignInAsync(apps, home, database, ada, "/apps/acme/"));

        var models = JsonNode.Parse(await (await SendAsync(apps, token, HttpMethod.Get, "/apps/acme/_data/api/v1/semantic/models")).Content.ReadAsStringAsync())!;
        Assert.Equal(["sales_catalog"], models["models"]!.AsArray().Select(m => (string)m!["name"]!));

        var catalog = JsonNode.Parse(await (await SendAsync(apps, token, HttpMethod.Get, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/catalog")).Content.ReadAsStringAsync())!;
        Assert.Equal("sales_catalog", (string)catalog["model"]!);
        Assert.Contains("orders.net_revenue", catalog["measures"]!.AsArray().Select(m => (string)m!["name"]!));
        Assert.Contains("customers.region", catalog["attributes"]!.AsArray().Select(m => (string)m!["name"]!));

        var response = await SendAsync(apps, token, HttpMethod.Post, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/query", Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", string.Join(' ', response.Headers.GetValues("Access-Control-Allow-Origin")));
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("ok", (string)result["status"]!);
        Assert.Equal(["customers_region", "orders_net_revenue", "orders_order_count"], result["columns"]!.AsArray().Select(c => (string)c!["name"]!));
        Assert.Equal(["West|500|6", "East|400|4"], result["rows"]!.AsArray().Select(r => string.Join('|', r!.AsArray().Select(v => v!.ToJsonString().Trim('"')))));
        Assert.NotNull((string?)result["request_hash"]);
    }

    [Fact]
    public async Task A_person_whose_groups_do_not_reach_the_models_source_is_refused_and_the_manifest_scopes_the_models()
    {
        var (apps, home, database, ada, bob, _) = await StartAsync();

        // Bob has a role on the app, and his group uses another data source: the app asks as Bob and gets Bob's rights
        var bobToken = await TokenAsync(apps, "acme", await SignInAsync(apps, home, database, bob, "/apps/acme/"));
        var denied = await SendAsync(apps, bobToken, HttpMethod.Post, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/query", Request);
        Assert.True(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound, await denied.Content.ReadAsStringAsync());
        Assert.DoesNotContain("West", await denied.Content.ReadAsStringAsync());

        var adaSession = await SignInAsync(apps, home, database, ada, "/apps/bare/");
        var bareToken = await TokenAsync(apps, "bare", adaSession);
        // an app whose manifest lists no semantic model may use none, whatever Ada may do herself
        var unlisted = await SendAsync(apps, bareToken, HttpMethod.Post, "/apps/bare/_data/api/v1/semantic/models/sales_catalog/query", Request);
        Assert.Equal(HttpStatusCode.Forbidden, unlisted.StatusCode);
        Assert.Contains("does not list it", await unlisted.Content.ReadAsStringAsync());
        Assert.Equal([], JsonNode.Parse(await (await SendAsync(apps, bareToken, HttpMethod.Get, "/apps/bare/_data/api/v1/semantic/models")).Content.ReadAsStringAsync())!["models"]!.AsArray());

        // the verbs, the unlisted paths, a request that is not a query, a limit out of range and no token
        var adaToken = await TokenAsync(apps, "acme", await SignInAsync(apps, home, database, ada, "/apps/acme/"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(apps, adaToken, HttpMethod.Get, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/query")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(apps, adaToken, HttpMethod.Post, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/catalog", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(apps, adaToken, HttpMethod.Get, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/members")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(apps, adaToken, HttpMethod.Post, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/query", Request.Replace("\"kind\": \"query\"", "\"kind\": \"explain\""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(apps, adaToken, HttpMethod.Post, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/query", Request.Replace("\"limit\": 100", "\"limit\": 20000"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await apps.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/apps/acme/_data/api/v1/semantic/models/sales_catalog/query"))).StatusCode);
    }
}
