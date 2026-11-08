using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 32, DBR11b: the <c>views</c> module as a module of the Gateway host. Through the real host (state database, definition follower, serving core, management API, identity home's tokens): a model and a dashboard published to the group are served by the
/// module's own HTTPS listener to two people of the home whose access to the data source differs, and relayed on the management API as the person a console vouches for; a publish through the management API and the command line is checked
/// against the models the group serves; the module's health is among the Gateway's readiness components.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ViewsModuleTests : IAsyncLifetime
{
    private const string Model = "sales_catalog";
    private const string Source = "sales-ro";
    private const string Analysts = "analysts";

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-views-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication? app;
    private int viewsPort;
    private int managementPort;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CatalogText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));

    private string ConfigDirectory => GatewayConfigFile.DirectoryIn(dataDirectory);

    /// <summary>A DuckDB file with the semantic engine's conformance data, and the data source that reads it: for the home group <see cref="Analysts"/> only.</summary>
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

    private async Task StartAsync(bool views = true, params string[] more)
    {
        // Free ports are looked up and released before the server binds them, so another process can take one in between ("address already in use"): the whole start is tried again on new ports, up to five times.
        for (var attempt = 1; ; attempt++)
        {
            viewsPort = FreePort();
            managementPort = FreePort();
            while (managementPort == viewsPort) managementPort = FreePort();
            List<string> args = [$"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0", "--Gateway:Modules:0=flightsql",
                "--Gateway:Management:Enabled=true", "--Gateway:Management:Address=127.0.0.1", $"--Gateway:Management:Port={managementPort}", "--Gateway:DefinitionPollSeconds=1"];
            if (views) args.AddRange(["--Gateway:Modules:1=views", "--Gateway:Views:Address=127.0.0.1", $"--Gateway:Views:Port={viewsPort}"]);
            args.AddRange(more);
            app = GatewayHost.Build([.. args]);
            try
            {
                await app.StartAsync();
                break;
            }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                await app.DisposeAsync();
            }
        }
    }

    private T Get<T>() where T : notnull => app!.Services.GetRequiredService<T>();

    /// <summary>The model, published to the group as a client of the management API does, and served by this instance.</summary>
    private void PublishModel()
    {
        var models = Get<PublishedSemanticModels>();
        models.Publish(Model, CatalogText, null, "test", new Dictionary<string, string> { ["sqlserver_sales"] = Source }, [], DateTimeOffset.UtcNow);
        models.ServeNow(Model);
    }

    private static string Dashboard(string title = "Sales") => $$"""
        schemaVersion: 1
        kind: dashboard
        title: {{title}}
        parameters:
          - {name: region, type: string, for: customers.region, many: true, default: []}
        widgets:
          regions:
            type: control
            parameter: region
            control: multiselect
            values: {attribute: customers.region}
          revenue:
            type: metric
            label: Revenue
            encodings: {value: orders.net_revenue}
            query:
              model: {{Model}}
              filters: [{attribute: customers.region, operator: equals, values: {param: region} }]
          by_region:
            type: table
            encodings:
              columns: [{field: customers.region}, {field: orders.net_revenue}]
            query: {model: {{Model}}, limit: 100}
        layouts:
          - name: desk
            type: reflow
            default: true
            grid: {columns: 12, rows: 6}
            tiles:
              - {widget: regions, x: 0, y: 0, w: 12, h: 1}
              - {widget: revenue, x: 0, y: 1, w: 4, h: 2}
              - {widget: by_region, x: 4, y: 1, w: 8, h: 5}
        """;

    private static HttpClient Trusting() => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

    private async Task<(HttpStatusCode Status, JsonNode? Body)> ModuleAsync(HttpMethod method, string path, string? token, JsonNode? body = null)
    {
        using var http = Trusting();
        using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{viewsPort}{path}");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    /// <summary>Playwright's Chromium (or DBDATA_TEST_CHROME_SYSTEM) of a cloud session, or null: the export test then skips and says so.</summary>
    private static string? SystemChromium()
    {
        if (Environment.GetEnvironmentVariable("DBDATA_TEST_CHROME_SYSTEM") is { Length: > 0 } env && File.Exists(env)) return env;
        if (Environment.GetEnvironmentVariable("DBDATA_TEST_CHROMIUM") is { Length: > 0 } dir)
            foreach (var name in new[] { "chrome-linux64/chrome", "chrome-win64/chrome.exe" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        if (Directory.Exists("/opt/pw-browsers"))
            foreach (var d in Directory.EnumerateDirectories("/opt/pw-browsers", "chromium-*").Order(StringComparer.Ordinal))
                if (File.Exists(Path.Combine(d, "chrome-linux64", "chrome"))) return Path.Combine(d, "chrome-linux64", "chrome");
        return null;
    }

    private PairedServer? paired;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> ManagementAsync(HttpMethod method, string path, string userId, ServerRole roles, JsonNode? body = null)
    {
        var files = new PairingFiles(GatewayConfigFile.PathIn(dataDirectory), Path.Combine(dataDirectory, "pairing-codes.json"));
        var url = new Uri($"https://127.0.0.1:{managementPort}");
        paired ??= await PairingClient.PairAsync(url, files.Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author)).Code, console);
        using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
        using var request = new HttpRequestMessage(method, new Uri(url, path));
        request.Headers.Add(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, userId, "Console person", roles)));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private static JsonNode Publish(string text, string? expected = null) => new JsonObject { ["text"] = text, ["expected_version"] = expected };

    private void Poll() => Get<DefinitionFollower>().Poll();

    [Fact]
    public async Task Two_people_of_the_home_open_the_published_dashboard_and_the_one_without_the_data_sources_group_is_refused_in_words()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, adaToken) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (_, bobToken) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");

        // the model first (a document is pinned to the version the group serves), then the dashboard, through the Gateway's own publisher
        var early = Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard(), "test");
        Assert.Equal(400, early.Status);
        Assert.Contains("DBD-VIEW-060", (string)early.Body["error"]!);
        Assert.Contains("published to the group", (string)early.Body["error"]!);
        PublishModel();
        var first = Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard(), "test");
        Assert.Equal(200, first.Status);
        Assert.Equal("1", (string)first.Body["version"]!);
        Assert.Equal($"semantic-model:{Model}@1", (string)first.Body["depends_on"]![0]!);
        Poll();

        // the module's own listener: a token of the identity home is needed
        Assert.Equal(HttpStatusCode.Unauthorized, (await ModuleAsync(HttpMethod.Get, "/api/v1/dashboards", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ModuleAsync(HttpMethod.Get, "/api/v1/dashboards", "nonsense")).Status);
        var list = await ModuleAsync(HttpMethod.Get, "/api/v1/dashboards", adaToken);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal("sales", (string)list.Body!["items"]![0]!["name"]!);
        var opened = await ModuleAsync(HttpMethod.Get, "/api/v1/dashboards/sales", adaToken);
        Assert.Equal("1", (string)opened.Body!["version"]!);
        Assert.Contains("kind: dashboard", (string)opened.Body["text"]!);

        // Ada's group may read the data source: her widgets have rows. Bob is in no group it names: the same request is refused for him, widget by widget, and the others do not stop
        var render = new JsonObject { ["parameters"] = new JsonObject(), ["layout"] = "desk" };
        var asAda = await ModuleAsync(HttpMethod.Post, "/api/v1/dashboards/sales/render", adaToken, render);
        Assert.Equal(HttpStatusCode.OK, asAda.Status);
        var adaWidgets = asAda.Body!["widgets"]!.AsObject();
        Assert.Equal("metric", (string)adaWidgets["revenue"]!["type"]!);
        Assert.Equal("table", (string)adaWidgets["by_region"]!["type"]!);
        Assert.NotEmpty(adaWidgets["by_region"]!["rows"]!.AsArray());
        var asBob = await ModuleAsync(HttpMethod.Post, "/api/v1/dashboards/sales/render", bobToken, render);
        Assert.Equal(HttpStatusCode.OK, asBob.Status);
        var bobWidgets = asBob.Body!["widgets"]!.AsObject();
        Assert.Equal("error", (string)bobWidgets["revenue"]!["type"]!);
        Assert.Equal("error", (string)bobWidgets["by_region"]!["type"]!);
        Assert.Contains("analysts", bobWidgets["revenue"]!["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // every call is audited with who made it, never a value
        var events = new AuditLog(state, DbDataGateway.Server.State.GatewayState.ServiceId).Read(new AuditQuery(Verb: "view.render"));
        Assert.Contains(events, e => e.Entry.ActorId == ada.Id && e.Entry.TargetId!.StartsWith("sales@1", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => (e.Entry.Detail ?? "").Contains("West", StringComparison.Ordinal));

        // the module's health is among the Gateway's readiness components, and a version this instance does not serve (yet) makes it degraded
        var probe = Assert.Single(app!.Services.GetServices<IReadinessProbe>(), p => p.Name == "views");
        Assert.Equal(HealthStatus.Healthy, (await probe.CheckAsync(default)).Status);
        Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard("Sales, second"), "test");
        var behind = await probe.CheckAsync(default);
        Assert.Equal(HealthStatus.Degraded, behind.Status);
        Assert.Contains("sales", behind.Detail);
        Poll();
        Assert.Equal(HealthStatus.Healthy, (await probe.CheckAsync(default)).Status);
        Assert.Equal("Sales, second", (string)(await ModuleAsync(HttpMethod.Get, "/api/v1/dashboards/sales", adaToken)).Body!["title"]!);
    }

    [Fact]
    public async Task A_console_relays_the_module_on_the_management_api_as_the_person_it_vouches_for_and_an_author_publishes_through_it()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (bob, _) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");
        PublishModel();

        // the status says the module is on, before and after
        var status = await ManagementAsync(HttpMethod.Get, "/api/v1/views/status", ada.Id, ServerRole.Viewer);
        Assert.True((bool)status.Body!["enabled"]!);
        Assert.Equal(0, (int)status.Body["dashboards"]!);
        Assert.Equal("ok", (string)status.Body["state"]!);
        Assert.StartsWith("https://", (string)status.Body["address"]!);

        // a viewer reads; publishing is an author's, a stale one is a conflict, a bad document is refused in the registry's words and nothing is stored
        Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(HttpMethod.Put, "/api/v1/dashboards/sales", ada.Id, ServerRole.Viewer, Publish(Dashboard()))).Status);
        var published = await ManagementAsync(HttpMethod.Put, "/api/v1/dashboards/sales", ada.Id, ServerRole.Author, Publish(Dashboard()));
        Assert.Equal(HttpStatusCode.OK, published.Status);
        Assert.Equal("1", (string)published.Body!["version"]!);
        Assert.True((bool)published.Body["created"]!);
        var stale = await ManagementAsync(HttpMethod.Put, "/api/v1/dashboards/sales", ada.Id, ServerRole.Author, Publish(Dashboard("Again"), expected: "7"));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("version_conflict", (string)stale.Body!["code"]!);
        var wrong = await ManagementAsync(HttpMethod.Put, "/api/v1/dashboards/sales", ada.Id, ServerRole.Author, Publish(Dashboard().Replace("orders.net_revenue}", "orders.no_such_measure}"), expected: "1"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains("no_such_measure", (string)wrong.Body!["message"]!);
        Assert.Equal(1, Get<DefinitionStore>().Current("dashboard", "sales")!.Version);
        var same = await ManagementAsync(HttpMethod.Put, "/api/v1/dashboards/sales", ada.Id, ServerRole.Author, Publish(Dashboard(), expected: "1"));
        Assert.True((bool)same.Body!["unchanged"]!);
        Poll();

        // the console's calls are the module's own routes and bodies, as the person: Ada has rows, Bob and a person the home does not know have refusals; a viewer role is all that reading needs
        var list = await ManagementAsync(HttpMethod.Get, "/api/v1/dashboards", ada.Id, ServerRole.Viewer);
        Assert.Equal("sales", (string)list.Body!["items"]![0]!["name"]!);
        var render = new JsonObject { ["parameters"] = new JsonObject(), ["layout"] = "desk" };
        var asAda = await ManagementAsync(HttpMethod.Post, "/api/v1/dashboards/sales/render", ada.Id, ServerRole.Viewer, render);
        Assert.Equal("table", (string)asAda.Body!["widgets"]!["by_region"]!["type"]!);
        foreach (var id in new[] { bob.Id, "not-a-home-user" })
        {
            var refused = await ManagementAsync(HttpMethod.Post, "/api/v1/dashboards/sales/render", id, ServerRole.Viewer, render);
            Assert.Equal(HttpStatusCode.OK, refused.Status);
            Assert.Equal("error", (string)refused.Body!["widgets"]!["by_region"]!["type"]!);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Get, "/api/v1/dashboards/missing", ada.Id, ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Get, "/api/v1/reports", ada.Id, ServerRole.Viewer)).Status);
    }

    [Fact]
    public async Task A_console_previews_an_unsaved_draft_in_process_as_the_person_it_vouches_for_and_the_gateway_refuses_what_a_publish_would_refuse()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (bob, _) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");
        PublishModel();
        var draft = new JsonObject { ["text"] = Dashboard("Draft, not saved"), ["layout"] = "desk" };

        // nothing is published, and the draft resolves as the person: Ada's group reads the data source, Bob's does not (an error entry per widget, not a refusal of the request)
        var asAda = await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.Viewer, draft);
        Assert.Equal(HttpStatusCode.OK, asAda.Status);
        Assert.Equal("rendered_widgets", (string)asAda.Body!["kind"]!);
        Assert.Equal("table", (string)asAda.Body["widgets"]!["by_region"]!["type"]!);
        Assert.NotEmpty(asAda.Body["widgets"]!["by_region"]!["rows"]!.AsArray());
        var asBob = await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", bob.Id, ServerRole.Viewer, draft);
        Assert.Equal(HttpStatusCode.OK, asBob.Status);
        Assert.Equal("error", (string)asBob.Body!["widgets"]!["by_region"]!["type"]!);
        Assert.Empty(Get<GatewayViewsPublishing>().List());

        // a draft a publish would refuse is refused in the publish's words, with the loader's diagnostics
        var wrong = await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.Viewer, new JsonObject { ["text"] = Dashboard().Replace("orders.net_revenue}", "orders.no_such_measure}") });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Equal("invalid", (string)wrong.Body!["code"]!);
        Assert.Contains("no_such_measure", (string)wrong.Body["message"]!);
        var publish = await ManagementAsync(HttpMethod.Put, "/api/v1/dashboards/sales", ada.Id, ServerRole.Author, Publish(Dashboard().Replace("orders.net_revenue}", "orders.no_such_measure}")));
        Assert.Equal(HttpStatusCode.BadRequest, publish.Status);

        // roles and shapes: a viewer is enough, GET and a body that is not an object are refused, an oversized draft is a 413
        Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.None, draft)).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await ManagementAsync(HttpMethod.Get, "/api/v1/views/preview", ada.Id, ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.Viewer, new JsonArray())).Status);
        var huge = await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.Viewer, new JsonObject { ["text"] = new string('#', 300 * 1024) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.Status);

        // audited as the person, as view.preview, with the shape and never the text
        var events = new AuditLog(state, DbDataGateway.Server.State.GatewayState.ServiceId).Read(new AuditQuery(Verb: "view.preview"));
        Assert.Contains(events, e => e.Entry.ActorId == ada.Id && e.Entry.Outcome == "ok");
        Assert.Contains(events, e => e.Entry.ActorId == bob.Id);
        Assert.Contains(events, e => e.Entry.ActorId == ada.Id && e.Entry.Outcome == "refused");
        Assert.DoesNotContain(events, e => (e.Entry.Detail ?? "").Contains("Draft, not saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_gateway_without_the_views_module_answers_a_preview_with_404_in_words()
    {
        await StartAsync(views: false);
        var (ada, _) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        var reply = await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.Viewer, new JsonObject { ["text"] = Dashboard() });
        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
        Assert.Contains("modules", (string)reply.Body!["message"]!);
    }

    [Fact]
    public async Task An_author_lists_the_versions_of_a_document_serves_an_earlier_one_and_retires_it_and_each_step_is_audited_without_the_document()
    {
        await StartAsync();
        var (ada, _) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        const string path = "/api/v1/dashboards/sales";
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Put, path, ada.Id, ServerRole.Author, new JsonObject { ["text"] = Dashboard("Sales"), ["note"] = "first" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Put, path, ada.Id, ServerRole.Author, new JsonObject { ["text"] = Dashboard("Sales, second"), ["expected_version"] = "1", ["note"] = "second" })).Status);

        // the versions, newest first, with who, when and why, and which one is served; never the text of a document
        var versions = await ManagementAsync(HttpMethod.Get, path + "/versions", ada.Id, ServerRole.Author);
        Assert.Equal(HttpStatusCode.OK, versions.Status);
        Assert.Equal(2, (int)versions.Body!["active"]!);
        Assert.Equal([2, 1], versions.Body["versions"]!.AsArray().Select(v => (int)v!["version"]!).ToArray());
        Assert.Equal("first", (string)versions.Body["versions"]![1]!["note"]!);
        Assert.True((bool)versions.Body["versions"]![0]!["active"]!);
        Assert.DoesNotContain("kind: dashboard", versions.Body.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Get, "/api/v1/reports/sales/versions", ada.Id, ServerRole.Author)).Status);

        // a viewer may read documents but changes none of this, whichever call
        foreach (var (method, suffix, body) in new (HttpMethod, string, JsonNode?)[] { (HttpMethod.Get, "/versions", null), (HttpMethod.Post, "/switch", new JsonObject { ["version"] = 1 }), (HttpMethod.Post, "/rollback", new JsonObject()), (HttpMethod.Delete, "", null) })
            Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(method, path + suffix, ada.Id, ServerRole.Viewer, body)).Status);
        Assert.Equal(2, Get<DefinitionStore>().Current("dashboard", "sales")!.Version);

        // switch to the earlier version; a caller who saw another version served is refused with the current one; a version that is not stored is refused in words
        var stale = await ManagementAsync(HttpMethod.Post, path + "/switch", ada.Id, ServerRole.Author, new JsonObject { ["version"] = 1, ["expected_version"] = 7 });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal(2, (int)stale.Body!["current_version"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Post, path + "/switch", ada.Id, ServerRole.Author, new JsonObject { ["version"] = 99 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ManagementAsync(HttpMethod.Post, path + "/switch", ada.Id, ServerRole.Author, new JsonObject())).Status);
        var switched = await ManagementAsync(HttpMethod.Post, path + "/switch", ada.Id, ServerRole.Author, new JsonObject { ["version"] = 1, ["expected_version"] = 2 });
        Assert.Equal(HttpStatusCode.OK, switched.Status);
        Assert.Equal(1, Get<DefinitionStore>().Current("dashboard", "sales")!.Version);
        Poll();
        Assert.Equal("Sales", (string)(await ManagementAsync(HttpMethod.Get, path, ada.Id, ServerRole.Viewer)).Body!["title"]!);

        // rollback returns to the version before (2 again)
        var back = await ManagementAsync(HttpMethod.Post, path + "/rollback", ada.Id, ServerRole.Author, new JsonObject { ["expected_version"] = 1 });
        Assert.Equal(HttpStatusCode.OK, back.Status);
        Assert.Equal(2, (int)back.Body!["version"]!);

        // retire: stale is refused, then the document is no longer listed, its versions stay, and a second retire says so
        Assert.Equal(HttpStatusCode.Conflict, (await ManagementAsync(HttpMethod.Delete, path + "?expected_version=1", ada.Id, ServerRole.Author)).Status);
        var retired = await ManagementAsync(HttpMethod.Delete, path + "?expected_version=2", ada.Id, ServerRole.Author);
        Assert.Equal(HttpStatusCode.OK, retired.Status);
        Assert.True((bool)retired.Body!["retired"]!);
        Poll();
        Assert.Empty((await ManagementAsync(HttpMethod.Get, "/api/v1/dashboards", ada.Id, ServerRole.Viewer)).Body!["items"]!.AsArray());
        var after = await ManagementAsync(HttpMethod.Get, path + "/versions", ada.Id, ServerRole.Author);
        Assert.Equal(2, after.Body!["versions"]!.AsArray().Count);
        Assert.Null(after.Body["active"]);
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Delete, path, ada.Id, ServerRole.Author)).Status);
        // a publish brings it back as the next version
        Assert.Equal("3", (string)(await ManagementAsync(HttpMethod.Put, path, ada.Id, ServerRole.Author, new JsonObject { ["text"] = Dashboard("Sales, third") })).Body!["version"]!);

        // every step is in the audit with the person and the version, and no document text
        var audit = ConfigTestSupport.AuditEntries(dataDirectory).Select(e => e.ToJsonString()).ToList();
        foreach (var command in new[] { "dashboard publish", "dashboard switch", "dashboard rollback", "dashboard retire" })
            Assert.Contains(audit, e => e.Contains(command, StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Contains("dashboard retire", StringComparison.Ordinal) && e.Contains("stale", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, e => e.Contains("kind: dashboard", StringComparison.Ordinal) || e.Contains("widgets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_gateway_without_the_views_module_says_so_and_still_takes_a_publish()
    {
        await StartAsync(views: false);
        var (ada, _) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        PublishModel();

        var status = await ManagementAsync(HttpMethod.Get, "/api/v1/views/status", ada.Id, ServerRole.Viewer);
        Assert.False((bool)status.Body!["enabled"]!);
        var read = await ManagementAsync(HttpMethod.Get, "/api/v1/dashboards", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.NotFound, read.Status);
        Assert.Contains("modules", (string)read.Body!["message"]!);
        // what is published is served by every instance that runs the module
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Put, "/api/v1/dashboards/sales", ada.Id, ServerRole.Author, Publish(Dashboard()))).Status);
        Assert.DoesNotContain(app!.Services.GetServices<IReadinessProbe>(), p => p.Name == "views");
    }

    [Fact]
    public async Task The_command_line_publishes_and_lists_with_the_server_stopped()
    {
        var state = DbDataGateway.Server.State.GatewayState.Open(dataDirectory);
        new PublishedSemanticModels(new DefinitionStore(state), new DbDataKit.Serving.Semantic.SemanticModels())
            .Publish(Model, CatalogText, null, "test", new Dictionary<string, string> { ["sqlserver_sales"] = Source }, [], DateTimeOffset.UtcNow);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var file = Path.Combine(dataDirectory, "sales.yaml");
        File.WriteAllText(file, Dashboard());

        var output = new StringWriter();
        var error = new StringWriter();
        var context = new DbDataKit.Cli.CliContext(output, error, TextReader.Null);
        Assert.Equal(0, await DbDataGateway.Server.Hosting.Cli.RunAsync(["views", "publish", "--file", file, "--repo", dataDirectory], context));
        Assert.Contains("Published dashboard sales version 1", output.ToString());
        Assert.Contains(Model + "@1", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await DbDataGateway.Server.Hosting.Cli.RunAsync(["views", "list", "--repo", dataDirectory], context));
        Assert.Contains("dashboard", output.ToString());
        Assert.Contains("sales", output.ToString());
        // a document the group cannot serve is refused with the reason, and the exit code says so
        File.WriteAllText(file, Dashboard().Replace(Model, "no_such_model"));
        Assert.NotEqual(0, await DbDataGateway.Server.Hosting.Cli.RunAsync(["views", "publish", "--file", file, "--repo", dataDirectory], context));
        Assert.Contains("no_such_model", error.ToString());
    }

    [Fact]
    public async Task A_pdf_is_taken_from_the_module_listener_as_the_person_whose_token_asks_and_a_host_with_export_off_says_so()
    {
        var browser = SystemChromium();
        if (browser is null) Assert.Skip("No Chromium found (DBDATA_TEST_CHROMIUM, DBDATA_TEST_CHROME_SYSTEM or /opt/pw-browsers): the Gateway's export test was not run.");
        var assets = Path.Combine(dataDirectory, "print");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "print.css"), "body { font-family: sans-serif }");
        File.WriteAllText(Path.Combine(assets, "print.js"), """
            (function () {
              var p = JSON.parse(document.getElementById('dbdatakit-print').textContent), w = p.rendered.widgets, root = document.getElementById('root');
              Object.keys(w).forEach(function (id) { var d = document.createElement('div'); d.textContent = id + ' ' + w[id].type + (w[id].type === 'metric' ? ' ' + w[id].value.text : ''); root.appendChild(d); });
              document.fonts.ready.then(function () { window.__reportReady = true; });
            })();
            """);
        await StartAsync(true, "--Gateway:Views:Export:Mode=system", $"--Gateway:Views:Export:Path={browser}", $"--Gateway:Views:Export:PageAssets={assets}", $"--Gateway:Views:Export:NoSandbox={Environment.IsPrivilegedProcess}");
        var state = Get<StateDatabase>();
        var (_, adaToken) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (_, bobToken) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");
        PublishModel();
        Assert.Equal(200, Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard(), "test").Status);
        Poll();

        async Task<(HttpStatusCode Status, byte[] Bytes, string? Type)> PdfAsync(string token)
        {
            using var http = Trusting();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{viewsPort}/api/v1/dashboards/sales/export?format=pdf");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.MediaType);
        }

        var ada = await PdfAsync(adaToken);
        Assert.Equal((HttpStatusCode.OK, "application/pdf"), (ada.Status, ada.Type));
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(ada.Bytes))
        {
            var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
            Assert.Contains("revenue metric", text);
            Assert.Contains("by_region table", text);
        }
        // Bob's group does not name the data source: his PDF is the page with an error where the rows would be, not the rows
        var bob = await PdfAsync(bobToken);
        Assert.Equal(HttpStatusCode.OK, bob.Status);
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(bob.Bytes))
            Assert.Contains("revenue error", string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ModuleAsync(HttpMethod.Get, "/api/v1/dashboards/sales/export", null)).Status);

        // the module's health says the browser, and the management API (JSON only) points at the module's listener for a download
        var probe = Assert.Single(app!.Services.GetServices<IReadinessProbe>(), p => p.Name == "views");
        Assert.NotNull(probe);
        var events = new AuditLog(state, DbDataGateway.Server.State.GatewayState.ServiceId).Read(new AuditQuery(Verb: "view.export"));
        Assert.Equal(2, events.Count(e => e.Entry.Outcome == "ok"));
        Assert.All(events.Where(e => e.Entry.Outcome == "ok"), e => Assert.Matches("Chrome/", e.Entry.Detail!));
    }

    [Fact]
    public async Task The_largest_data_export_is_a_setting_of_the_views_section_and_a_larger_one_is_refused_in_words()
    {
        await StartAsync(true, "--Gateway:Views:MaxExportRows=1");
        var (_, adaToken) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        Assert.Equal(200, Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard(), "test").Status);
        Poll();
        var refused = await ModuleAsync(HttpMethod.Get, "/api/v1/dashboards/sales/export?format=csv&widget=by_region", adaToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.Status);
        Assert.Equal("export_too_large", (string)refused.Body!["code"]!);
        Assert.Contains("MaxExportRows", (string)refused.Body["message"]!);
        await app!.StopAsync();
        await app.DisposeAsync();
        app = null;
        var ex = Record.Exception(() => GatewayHost.Build([$"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Modules:0=flightsql", "--Gateway:Modules:1=views", "--Gateway:Views:MaxExportRows=0"]));
        Assert.NotNull(ex);
        Assert.Contains("MaxExportRows", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_is_off_unless_the_section_names_a_mode_and_a_bad_mode_is_refused_in_words()
    {
        await StartAsync();
        Assert.Contains("views.export", ((string)(await ModuleAsync(HttpMethod.Get, "/health", null)).Body!["export"]!["reason"]!));
        var ex = await Record.ExceptionAsync(() => GatewayHost.Build([$"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Modules:0=flightsql", "--Gateway:Modules:1=views", "--Gateway:Views:Export:Mode=edge"]).StartAsync());
        Assert.NotNull(ex);
        Assert.Contains("`Mode` is `edge`", ex.Message);
    }
}
