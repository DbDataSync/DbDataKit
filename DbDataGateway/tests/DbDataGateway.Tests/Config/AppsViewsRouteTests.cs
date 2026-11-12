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
/// Plan 32 DBR13: an app's dashboard, drawn through the apps host's views route (<c>/apps/&lt;instance&gt;/_data/api/v1/dashboards/&lt;file&gt;[/render]</c>) on the real host: the document is read from the package, its widgets are resolved by the
/// views library through the serving core <b>as the person the app's token names</b> (the data sources their home groups reach decide what they see), and the manifest is the second lock on what a document may read.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsViewsRouteTests : IAsyncLifetime
{
    private const string Board = """
        schemaVersion: 1
        kind: dashboard
        title: Sales
        widgets:
          net: {type: metric, label: Net revenue, encodings: {value: orders.net_revenue}, query: {model: sales_catalog}}
          by_region: {type: chart, mark: bar, encodings: {x: customers.region, y: orders.net_revenue}, query: {model: sales_catalog, limit: 20}}
        layouts:
          - {name: desk, type: reflow, default: true, grid: {columns: 12}, tiles: [{widget: net, x: 0, y: 0, w: 4, h: 2}, {widget: by_region, x: 4, y: 0, w: 8, h: 4}]}
        """;

    private const string Manifest = "schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nsemantic: [sales_catalog]\nviews: [views/sales.yaml]\n";

    private const string Sales = "/apps/acme/_data/api/v1/dashboards/views%2Fsales.yaml";

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-appviews-" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<(HttpClient Apps, HttpClient Home, StateDatabase Database, string Ada, string Bob)> StartAsync()
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

        // the state is made before the host is, as an operator's commands would: two people of the home with a role on the app, one whose group reaches the warehouse and one whose does not, and two apps with the same dashboard,
        // one that lists the semantic model and one that does not
        var database = GatewayState.Open(dataDirectory);
        var users = new IdentityUserStore(database);
        var ada = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer).Id;
        var bob = users.CreateUser("Bob Babbage", "bob@example.invalid", UserRole.Viewer).Id;
        HomeSeed.Join(database, ada, "analysts");
        HomeSeed.Join(database, bob, "others");
        var directory = new HomeDirectory(database);
        foreach (var person in new[] { ada, bob }) directory.AssignApp(AssignmentSubject.User(person), "acme", ServerRole.Viewer);
        var store = new AppInstanceStore(new DefinitionStore(database));
        Dictionary<string, byte[]> Files() => new() { ["index.html"] = Encoding.UTF8.GetBytes("<html><head></head><body>x</body></html>"), ["views/sales.yaml"] = Encoding.UTF8.GetBytes(Board) };
        store.PublishApp(new AppPackage(Manifest, Files()), "ada");
        store.Save(new AppInstance("acme", "notes"), "ada");
        store.PublishApp(new AppPackage(Manifest.Replace("name: notes", "name: bare").Replace("semantic: [sales_catalog]\n", ""), Files()), "ada");
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
        return (Trusting($"https://localhost:{appsPort}"), Trusting($"https://localhost:{identityPort}"), database, ada, bob);
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
    public async Task An_app_reads_its_dashboard_and_has_it_resolved_as_the_person_whose_groups_reach_the_data()
    {
        var (apps, home, database, ada, _) = await StartAsync();
        var token = await TokenAsync(apps, "acme", await SignInAsync(apps, home, database, ada, "/apps/acme/"));

        var opened = await SendAsync(apps, token, HttpMethod.Get, Sales);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var document = JsonNode.Parse(await opened.Content.ReadAsStringAsync())!;
        Assert.Equal(("dashboard", Board), ((string)document["kind"]!, (string)document["text"]!));

        var rendered = await SendAsync(apps, token, HttpMethod.Post, Sales + "/render", "{\"layout\":\"desk\",\"parameters\":{}}");
        var body = await rendered.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, rendered.StatusCode);
        Assert.Equal("*", string.Join(' ', rendered.Headers.GetValues("Access-Control-Allow-Origin")));
        var widgets = JsonNode.Parse(body)!["widgets"]!;
        Assert.Equal(("metric", "chart"), ((string)widgets["net"]!["type"]!, (string)widgets["by_region"]!["type"]!));
        Assert.Contains("1,600", widgets["net"]!.ToJsonString());   // the seed's net revenue over every region

        // a second call may narrow the request to some widgets, as the shared client does when a parameter changes
        var only = JsonNode.Parse(await (await SendAsync(apps, token, HttpMethod.Post, Sales + "/render", "{\"layout\":\"desk\",\"only\":[\"net\"]}")).Content.ReadAsStringAsync())!["widgets"]!.AsObject();
        Assert.Equal(["net"], only.Select(w => w.Key));
    }

    [Fact]
    public async Task A_person_whose_groups_do_not_reach_the_models_source_gets_error_tiles_and_not_the_rows()
    {
        var (apps, home, database, _, bob) = await StartAsync();
        var bobToken = await TokenAsync(apps, "acme", await SignInAsync(apps, home, database, bob, "/apps/acme/"));

        var rendered = await SendAsync(apps, bobToken, HttpMethod.Post, Sales + "/render", "{\"layout\":\"desk\"}");
        var body = await rendered.Content.ReadAsStringAsync();
        Assert.DoesNotContain("West", body);
        Assert.DoesNotContain("1,600", body);
        Assert.Equal("error", (string?)JsonNode.Parse(body)!["widgets"]?["net"]?["type"]);   // Bob asks as Bob: his rights, not the app's and not Ada's
    }

    [Fact]
    public async Task The_manifest_scopes_what_a_document_may_read_and_the_route_refuses_the_rest_in_words()
    {
        var (apps, home, database, ada, _) = await StartAsync();
        var bareToken = await TokenAsync(apps, "bare", await SignInAsync(apps, home, database, ada, "/apps/bare/"));
        // the app lists no semantic model: its dashboard may read none, whatever Ada may do herself (the publish check says the same; this is the second lock)
        var refused = await SendAsync(apps, bareToken, HttpMethod.Post, "/apps/bare/_data/api/v1/dashboards/views%2Fsales.yaml/render", "{}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("does not list under `semantic`", (string)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["message"]!);

        var token = await TokenAsync(apps, "acme", await SignInAsync(apps, home, database, ada, "/apps/acme/"));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(apps, token, HttpMethod.Get, "/apps/acme/_data/api/v1/dashboards/views%2Fother.yaml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(apps, token, HttpMethod.Post, Sales + "/render", "{\"nope\":1}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(apps, token, HttpMethod.Post, Sales + "/render", "{\"layout\":\"missing\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(apps, token, HttpMethod.Get, Sales + "/render")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await apps.SendAsync(new HttpRequestMessage(HttpMethod.Post, Sales + "/render"))).StatusCode);
    }
}
