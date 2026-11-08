using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.State;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataProxy.Tests.Config;

/// <summary>Plan 26, AP05: the apps host as a module of the serving layer: off by default, a listener of its own, serving the instances published in the Proxy's state with the sandbox and egress policy.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsModuleTests : IAsyncLifetime
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbproxy-apps-" + Guid.NewGuid().ToString("N")[..8]);
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
        var config = ProxyConfigFile.DirectoryIn(dataDirectory);
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

    private const string Manifest = "schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write]}\n";

    /// <summary>The state is made before the host is (as an operator's commands would): a person with a role on one app, an app published, and the client the apps host signs in with, registered at the home.</summary>
    private (string ClientId, string Ada, string Bob, StateDatabase Database) Prepare()
    {
        var database = ProxyState.Open(dataDirectory);
        var users = new IdentityUserStore(database);
        var ada = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer).Id;
        var bob = users.CreateUser("Bob Babbage", "bob@example.invalid", UserRole.Viewer).Id;
        new HomeDirectory(database).AssignApp(AssignmentSubject.User(ada), "acme", ServerRole.Viewer);
        var store = new AppInstanceStore(new DefinitionStore(database));
        store.PublishApp(new AppPackage(Manifest, new Dictionary<string, byte[]>
        {
            ["index.html"] = Encoding.UTF8.GetBytes("<html><head></head><body>hello notes</body></html>"), ["app.js"] = "1"u8.ToArray(),
        }), "ada");
        store.Save(new AppInstance("acme", "notes"), "ada");
        var (client, _) = new OidcClients(database).Register("Apps", [$"https://localhost:{appsPort}{AppsEndpoints.CallbackPath}"], confidential: false);
        return (client.ClientId, ada, bob, database);
    }

    private async Task<(HttpClient Apps, HttpClient Home)> StartAsync(string clientId)
    {
        var identityPort = FreePort();
        while (identityPort == appsPort) identityPort = FreePort();   // a port given back is free to be handed out again, to the second listener too
        app = ProxyHost.Build([
            $"--Proxy:DataDirectory={dataDirectory}", "--Proxy:Modules:0=apps", "--Proxy:Modules:1=identity",
            "--Proxy:Apps:Address=127.0.0.1", "--Proxy:Apps:RolesRefreshSeconds=1", $"--Proxy:Apps:Port={appsPort}", $"--Proxy:Apps:Home:ClientId={clientId}", 
            "--Proxy:Identity:Address=127.0.0.1", $"--Proxy:Identity:Port={identityPort}", $"--Proxy:Identity:PublicUrl=https://localhost:{identityPort}",
        ]);
        await app.StartAsync();
        return (Trusting($"https://localhost:{appsPort}"), Trusting($"https://localhost:{identityPort}"));
    }

    /// <summary>The browser's round trip: to the apps host, to the home with the person's session there, back to the callback. Returns the cookie the host set.</summary>
    private static async Task<string> SignInAsync(HttpClient apps, HttpClient home, StateDatabase database, string user, string path)
    {
        var start = await apps.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        authorize.Headers.Add("Cookie", $"{ProxyIdentityModule.Auth.SessionCookie}={new IdentitySessionStore(database).Create(user).Id}");
        var back = await home.SendAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var callback = await apps.GetAsync(back.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(path + "?launch", callback.Headers.Location!.OriginalString);   // the sign-in returns to the launch address
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AppsEndpoints.SessionCookie + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie[(AppsEndpoints.SessionCookie.Length + 1)..].Split(';')[0];
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string path, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (session is not null) request.Headers.Add("Cookie", $"{AppsEndpoints.SessionCookie}={session}");
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task A_person_with_a_role_signs_in_through_the_home_and_gets_the_shell_with_a_sandboxed_frame()
    {
        appsPort = FreePort();
        var (clientId, ada, _, database) = Prepare();
        var (apps, home) = await StartAsync(clientId);

        var session = await SignInAsync(apps, home, database, ada, "/apps/acme/");
        var shell = await GetAsync(apps, "/apps/acme/", session);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        var body = await shell.Content.ReadAsStringAsync();
        Assert.Contains("sandbox=\"allow-scripts allow-forms allow-popups\"", body);
        Assert.Contains("src=\"/apps/acme/_app/\"", body);
        Assert.DoesNotContain("allow-same-origin", body);
        Assert.Equal("no-store", string.Join(' ', shell.Headers.GetValues("Cache-Control")));
    }

    [Fact]
    public async Task A_role_taken_away_at_the_home_stops_working_within_the_refresh_interval_and_one_given_starts()
    {
        appsPort = FreePort();
        var (clientId, ada, bob, database) = Prepare();
        var (apps, home) = await StartAsync(clientId);
        var session = await SignInAsync(apps, home, database, ada, "/apps/acme/");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(apps, "/apps/acme/", session)).StatusCode);

        new HomeDirectory(database).AssignApp(AssignmentSubject.User(ada), "acme", ServerRole.None);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // the host asks the home again, with the refresh token, and the home no longer gives her the role
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(apps, "/apps/acme/", session)).StatusCode);

        new HomeDirectory(database).AssignApp(AssignmentSubject.User(ada), "acme", ServerRole.Viewer);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(apps, "/apps/acme/", session)).StatusCode);
    }

    [Fact]
    public async Task Nobody_signed_in_is_sent_to_the_home_and_a_person_without_a_role_on_the_app_is_refused()
    {
        appsPort = FreePort();
        var (clientId, _, bob, database) = Prepare();
        var (apps, home) = await StartAsync(clientId);

        var anonymous = await GetAsync(apps, "/apps/acme/");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith(home.BaseAddress!.AbsoluteUri, anonymous.Headers.Location!.AbsoluteUri);

        var session = await SignInAsync(apps, home, database, bob, "/apps/acme/");
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(apps, "/apps/acme/", session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(apps, "/apps/globex/", session)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await GetAsync(apps, "/apps/acme", session)).StatusCode);   // the slash form, so relative links resolve
        Assert.Equal(HttpStatusCode.Redirect, (await GetAsync(apps, "/apps/acme/", "not-a-session")).StatusCode);
    }

    private static async Task<HttpResponseMessage> PostTokenAsync(HttpClient http, string instance, string? session, bool header = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/apps/{instance}/_token");
        if (session is not null) request.Headers.Add("Cookie", $"{AppsEndpoints.SessionCookie}={session}");
        if (header) request.Headers.Add("X-DbData-Shell", "1");
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task The_shell_brokers_a_short_token_for_this_person_and_this_instance_only()
    {
        appsPort = FreePort();
        var (clientId, ada, bob, database) = Prepare();
        var (apps, home) = await StartAsync(clientId);
        var session = await SignInAsync(apps, home, database, ada, "/apps/acme/");

        var shell = await (await GetAsync(apps, "/apps/acme/", session)).Content.ReadAsStringAsync();
        Assert.Contains($"src=\"{AppShell.ScriptPath}\"", shell);
        Assert.Contains("data-instance=\"acme\"", shell);
        var script = await apps.GetAsync(AppShell.ScriptPath);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("f.contentWindow === event.source", await script.Content.ReadAsStringAsync());   // only the app's own frame is answered

        var response = await PostTokenAsync(apps, "acme", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(300, (int)body["expiresIn"]!);
        Assert.Equal("app:acme", (string)body["audience"]!);
        var claims = JsonNode.Parse(Convert.FromBase64String(((string)body["token"]!).Split('.')[1].Replace('-', '+').Replace('_', '/').PadRight(((string)body["token"]!).Split('.')[1].Length + 3 & ~3, '=')))!;
        Assert.Equal(("app:acme", ada), ((string)claims["aud"]!, (string)claims["sub"]!));
        Assert.Contains("viewer", claims["roles"]!.AsArray().Select(r => (string)r!));
        Assert.DoesNotContain("admin", claims["roles"]!.AsArray().Select(r => (string)r!));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostTokenAsync(apps, "acme", session, header: false)).StatusCode);   // not from the shell page
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostTokenAsync(apps, "acme", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostTokenAsync(apps, "globex", session)).StatusCode);

        var bobSession = await SignInAsync(apps, home, database, bob, "/apps/acme/");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostTokenAsync(apps, "acme", bobSession)).StatusCode);   // no role on the app, no token
    }

    private async Task<(int Exit, string Out, string Error)> Command(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await DbDataProxy.Server.Hosting.Cli.Router().RunAsync([.. args, "--repo", dataDirectory], new DbDataKit.Cli.CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task An_app_published_and_an_instance_saved_at_the_command_line_are_listed_pinned_and_refused_when_wrong()
    {
        var app = Path.Combine(dataDirectory, "notes-app");
        Directory.CreateDirectory(Path.Combine(app, "assets"));
        File.WriteAllText(Path.Combine(app, "app.yaml"), Manifest);
        File.WriteAllText(Path.Combine(app, "index.html"), "<html><head></head><body>v1</body></html>");
        File.WriteAllText(Path.Combine(app, "assets", "app.js"), "1");

        var published = await Command("apps", "publish", "--dir", app, "--note", "first");
        Assert.Equal(0, published.Exit);
        Assert.Contains("Published notes version 1 (2 files)", published.Out);   // the manifest is the version's, not one of its files

        Assert.Equal(0, (await Command("apps", "publish", "--dir", app)).Exit);   // a second version
        var saved = await Command("apps", "instance-save", "--name", "acme", "--app", "notes", "--pin", "1", "--title", "Acme notes", "--setting", "theme=blue", "--bind", "territories=acme-data");
        Assert.Equal(0, saved.Exit);
        Assert.Contains("/apps/acme/", saved.Out);
        Assert.Contains("acme  notes  pinned to 1  Acme notes", (await Command("apps", "instance-list")).Out);

        // refused with the reason, and nothing saved
        var unknown = await Command("apps", "instance-save", "--name", "bad", "--app", "nothing-published");
        Assert.NotEqual(0, unknown.Exit);
        Assert.NotEmpty(unknown.Error);
        Assert.Equal(2, (await Command("apps", "instance-save", "--name", "bad", "--app", "notes", "--setting", "oops")).Exit);
        Assert.Equal(1, (await Command("apps", "publish", "--dir", Path.Combine(dataDirectory, "missing"))).Exit);
        Assert.DoesNotContain("bad  ", (await Command("apps", "instance-list")).Out);
    }

    [Fact]
    public async Task An_app_is_listed_rolled_back_and_removed_at_the_command_line()
    {
        var app = Path.Combine(dataDirectory, "notes-app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "app.yaml"), Manifest);
        File.WriteAllText(Path.Combine(app, "index.html"), "<html><head></head><body>v1</body></html>");
        Assert.Equal(0, (await Command("apps", "publish", "--dir", app)).Exit);
        File.WriteAllText(Path.Combine(app, "index.html"), "<html><head></head><body>v2</body></html>");
        Assert.Equal(0, (await Command("apps", "publish", "--dir", app)).Exit);
        Assert.Equal(0, (await Command("apps", "instance-save", "--name", "acme", "--app", "notes")).Exit);
        Assert.Equal(0, (await Command("apps", "nav-save", "--name", "main", "--title", "Main", "--item", "acme")).Exit);

        Assert.Contains("notes  serves version 2  (versions: 1, 2)", (await Command("apps", "list")).Out);
        Assert.Contains("1  ", (await Command("apps", "list", "--app", "notes")).Out);
        var back = await Command("apps", "rollback", "--app", "notes");
        Assert.Equal(0, back.Exit);
        Assert.Contains("now serves version 1", back.Out);
        Assert.Contains("now serves version 2", (await Command("apps", "rollback", "--app", "notes", "--to", "2")).Out);
        Assert.NotEqual(0, (await Command("apps", "rollback", "--app", "notes", "--to", "7")).Exit);
        Assert.Equal(2, (await Command("apps", "rollback", "--app", "notes", "--to", "x")).Exit);

        var refused = await Command("apps", "remove", "--app", "notes");   // an instance still runs it
        Assert.Equal(1, refused.Exit);
        Assert.Contains("acme", refused.Error);
        Assert.Equal(0, (await Command("apps", "instance-remove", "--name", "acme")).Exit);
        Assert.Equal(0, (await Command("apps", "nav-remove", "--name", "main")).Exit);
        Assert.Equal(0, (await Command("apps", "remove", "--app", "notes")).Exit);
        Assert.Contains("No app is published", (await Command("apps", "list")).Out);
        Assert.Contains("No instance is published", (await Command("apps", "instance-list")).Out);
        Assert.Equal(1, (await Command("apps", "instance-remove", "--name", "acme")).Exit);
    }

    /// <summary>The samples a tester publishes (docs/apps-testing.md) stay valid and publishable: the repository's own copy, found from the test's folder upwards.</summary>
    [Fact]
    public async Task The_sample_apps_validate_publish_and_run_as_instances()
    {
        var folder = AppContext.BaseDirectory;
        while (folder is not null && !Directory.Exists(Path.Combine(folder, "DbDataProxy", "templates", "apps"))) folder = Path.GetDirectoryName(folder);
        var samples = Path.Combine(folder ?? throw new DirectoryNotFoundException("DbDataProxy/templates/apps"), "DbDataProxy", "templates", "apps");
        foreach (var sample in new[] { "territory-notes", "front-portal", "territory-views" })
        {
            var checkedIt = await Command("apps", "validate", "--dir", Path.Combine(samples, sample));
            Assert.Equal(0, checkedIt.Exit);
            Assert.Contains("the files reach no other host", checkedIt.Out);
            Assert.Equal(0, (await Command("apps", "publish", "--dir", Path.Combine(samples, sample))).Exit);
        }
        Assert.Equal(0, (await Command("apps", "instance-save", "--name", "notes", "--app", "territory-notes", "--bind", "territories=default")).Exit);
        Assert.Equal(0, (await Command("apps", "instance-save", "--name", "front", "--app", "front-portal")).Exit);
        Assert.Equal(0, (await Command("apps", "instance-save", "--name", "views", "--app", "territory-views", "--bind", "territories=default")).Exit);
        Assert.Equal(0, (await Command("apps", "nav-save", "--name", "main", "--title", "Main", "--item", "notes=Territory notes,views=Territory views")).Exit);
        Assert.Contains("front  front-portal", (await Command("apps", "instance-list")).Out);
        Assert.Contains("views  territory-views", (await Command("apps", "instance-list")).Out);
    }

    [Fact]
    public async Task An_app_of_views_only_is_served_its_generated_page_its_specs_and_the_runtime_the_build_put_next_to_the_program()
    {
        // the build copies the web build of DbDataKit/web/view-app to `apps-views` beside the program; a developer who has not built it gets a stand-in for the test, removed again
        var runtime = Path.Combine(AppContext.BaseDirectory, ProxyAppsModule.ViewsRuntimeFolder);
        var standIn = !Directory.Exists(runtime);
        if (standIn)
        {
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, ViewsRuntime.ScriptName), "/* stand-in runtime */");
        }
        try
        {
            appsPort = FreePort();
            var (clientId, _, _, database) = Prepare();
            var store = new AppInstanceStore(new DefinitionStore(database));
            store.PublishApp(new AppPackage("schemaVersion: 1\nname: territory-views\nversion: 1.0.0\nviews: [views/list.yaml]\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write]}\n",
                new Dictionary<string, byte[]> { ["views/list.yaml"] = "schemaVersion: 1\nkind: grid\ntitle: Territories\nmodel: territories\nentity: territory\ncolumns: [code, name]\n"u8.ToArray() }), "ada");
            store.Save(new AppInstance("tv", "territory-views"), "ada");
            var (apps, _) = await StartAsync(clientId);

            var page = await GetAsync(apps, "/apps/tv/_app/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("<script src=\"views-app.js\">", await page.Content.ReadAsStringAsync());
            Assert.Contains("sandbox", string.Join(' ', page.Headers.GetValues("Content-Security-Policy")));
            var specs = await GetAsync(apps, "/apps/tv/_app/views.json");
            Assert.Equal("application/json", specs.Content.Headers.ContentType?.MediaType);
            var json = JsonNode.Parse(await specs.Content.ReadAsStringAsync())!;
            Assert.Equal(("view-spec/1", "grid"), ((string)json["format"]!, (string)json["views"]!["views/list.yaml"]!["kind"]!));
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(apps, "/apps/tv/_app/views-app.js")).StatusCode);
        }
        finally
        {
            if (standIn) Directory.Delete(runtime, true);
        }
    }

    [Fact]
    public async Task A_declared_portals_home_shows_its_view_and_only_the_tiles_the_person_may_open()
    {
        appsPort = FreePort();
        var (clientId, ada, bob, database) = Prepare();
        var store = new AppInstanceStore(new DefinitionStore(database));
        store.PublishApp(new AppPackage("schemaVersion: 1\nname: front-portal\ntitle: Front\nversion: 1.0.0\nkind: portal\nportal:\n  mounts: [{app: acme, path: /notes/}]\n  home: [views/home.yaml]\n",
            new Dictionary<string, byte[]> { ["views/home.yaml"] = "schemaVersion: 1\nblocks:\n  - text: |\n      # Hello from the front\n      Read the **guide**.\n  - links:\n      - label: Handbook\n        href: https://example.com/handbook\n  - tiles: all\n"u8.ToArray() }), "ada");
        store.Save(new AppInstance("front", "front-portal"), "ada");
        var (apps, home) = await StartAsync(clientId);

        var adaPage = await (await GetAsync(apps, "/apps/front/", await SignInAsync(apps, home, database, ada, "/apps/acme/"))).Content.ReadAsStringAsync();
        Assert.Contains("<h1>Hello from the front</h1>", adaPage);
        Assert.Contains("href=\"https://example.com/handbook\"", adaPage);
        Assert.Contains("/apps/front/notes/", adaPage);                  // Ada holds a role on acme: its tile
        var bobPage = await (await GetAsync(apps, "/apps/front/", await SignInAsync(apps, home, database, bob, "/apps/acme/"))).Content.ReadAsStringAsync();
        Assert.Contains("<h1>Hello from the front</h1>", bobPage);
        Assert.DoesNotContain("/apps/front/notes/", bobPage);            // Bob holds none: no tile, and no refusal advertised
        Assert.Contains("Nothing here is open to you yet.", bobPage);
    }

    [Fact]
    public async Task A_trusted_portal_runs_on_the_hosts_origin_only_with_an_approval_and_otherwise_the_default_portal_says_why()
    {
        appsPort = FreePort();
        var (clientId, ada, _, database) = Prepare();
        var definitions = new DefinitionStore(database);
        var store = new AppInstanceStore(definitions);
        const string portal = "schemaVersion: 1\nname: sales-portal\ntitle: Sales\nversion: 1.0.0\nkind: portal\nentry: index.html\ntrust: trusted\nroles: [author]\nmodels:\n  - model: territories\n    entities: {territory: [read, write]}\nportal:\n  mounts: [{app: acme, path: /sales/}]\n";
        store.PublishApp(new AppPackage(portal, new Dictionary<string, byte[]> { ["index.html"] = "<html><head></head><body>portal code runs here</body></html>"u8.ToArray() }), "ada");
        store.Save(new AppInstance("front", "sales-portal"), "ada");
        var (apps, home) = await StartAsync(clientId);
        var session = await SignInAsync(apps, home, database, ada, "/apps/acme/");

        // without an approval: the default portal, the reason on the page, and none of the portal's code (neither at its address, nor as a file, nor a token for it)
        async Task AssertFallbackAsync()
        {
            var page = await GetAsync(apps, "/apps/front/", session);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var body = await page.Content.ReadAsStringAsync();
            Assert.Contains("Showing the default portal instead.", body);
            Assert.Contains("asks to be trusted", body);
            Assert.DoesNotContain("portal code runs here", body);
            Assert.Contains("/apps/front/sales/", body);   // the default portal still lists what the portal mounts that she may open
            var mounted = await (await GetAsync(apps, "/apps/front/sales/", session)).Content.ReadAsStringAsync();
            Assert.Contains("src=\"/apps/acme/_app/\"", mounted);
            Assert.Contains("Showing the default portal instead.", mounted);
            Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(apps, "/apps/front/_app/", session)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PostTokenAsync(apps, "front", session)).StatusCode);
        }
        await AssertFallbackAsync();

        // approved: the portal's own document, on this origin (no sandbox directive; it may frame this host's pages), and it can ask for its token
        var approval = new AppApprovalStore(definitions).Approve("sales-portal", 1, "grace");
        var approved = await GetAsync(apps, "/apps/front/", session);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Contains("portal code runs here", await approved.Content.ReadAsStringAsync());
        var policy = string.Join(' ', approved.Headers.GetValues("Content-Security-Policy"));
        Assert.DoesNotContain("sandbox", policy);
        Assert.Contains("frame-src 'self'", policy);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(apps, "/apps/front/_app/", session)).StatusCode);
        Assert.Contains("portal code runs here", await (await GetAsync(apps, "/apps/front/sales/", session)).Content.ReadAsStringAsync());   // its own routes are its own page

        // revoked: back to the default portal at once
        new AppApprovalStore(definitions).Revoke(approval.Id, "grace");
        await AssertFallbackAsync();
    }

    [Fact]
    public async Task A_trusted_app_runs_only_while_an_approval_stands_for_its_version()
    {
        appsPort = FreePort();
        var (clientId, ada, _, database) = Prepare();
        var definitions = new DefinitionStore(database);
        new AppInstanceStore(definitions).PublishApp(new AppPackage(Manifest + "trust: trusted\n", new Dictionary<string, byte[]> { ["index.html"] = "<html></html>"u8.ToArray() }), "ada");   // version 2 asks to be trusted
        var (apps, home) = await StartAsync(clientId);
        var session = await SignInAsync(apps, home, database, ada, "/apps/acme/");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(apps, "/apps/acme/", session)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostTokenAsync(apps, "acme", session)).StatusCode);

        var approvals = new AppApprovalStore(definitions);
        var approval = approvals.Approve("notes", 2, "grace");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(apps, "/apps/acme/", session)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostTokenAsync(apps, "acme", session)).StatusCode);

        approvals.Revoke(approval.Id, "grace");
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(apps, "/apps/acme/", session)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostTokenAsync(apps, "acme", session)).StatusCode);
    }

    [Fact]
    public async Task The_origins_an_administrator_approves_become_the_apps_policy_and_nothing_else_does()
    {
        appsPort = FreePort();
        var (clientId, _, _, database) = Prepare();
        var definitions = new DefinitionStore(database);
        new AppInstanceStore(definitions).PublishApp(new AppPackage(Manifest + "trust: trusted\negress:\n  connect: [\"https://api.contoso.com\", \"https://other.contoso.com\"]\n", new Dictionary<string, byte[]> { ["index.html"] = "<html></html>"u8.ToArray() }), "ada");
        var (apps, _) = await StartAsync(clientId);
        string Policy() => apps.GetAsync("/apps/acme/_app/index.html").GetAwaiter().GetResult().Headers.GetValues("Content-Security-Policy").Single();

        Assert.DoesNotContain("contoso", Policy());                                                                         // asked, not approved
        var approvals = new AppApprovalStore(definitions);
        Assert.Throws<AppInstanceException>(() => approvals.Approve("notes", 2, "grace", egress: AppApprovalStore.ParseEgress("connect=https://evil.example")));   // an approval cannot widen
        approvals.Approve("notes", 2, "grace", egress: AppApprovalStore.ParseEgress("connect=https://api.contoso.com"));
        var policy = Policy();

        Assert.Contains("https://api.contoso.com", policy);
        Assert.DoesNotContain("other.contoso.com", policy);                                                                // asked and not approved
        Assert.Throws<AppInstanceException>(() => AppApprovalStore.ParseEgress("fetch=https://x"));
    }

    [Fact]
    public async Task Validate_reports_addresses_of_other_hosts_the_manifest_does_not_ask_for()
    {
        var app = Path.Combine(dataDirectory, "leaky-app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "app.yaml"), Manifest);
        File.WriteAllText(Path.Combine(app, "index.html"), "<html>\n<script src=\"https://cdn.evil.example/x.js\"></script></html>");

        var result = await Command("apps", "validate", "--dir", app);

        Assert.True(result.Exit == 0, result.Error);
        Assert.Contains("index.html:2  https://cdn.evil.example", result.Out);
        File.WriteAllText(Path.Combine(app, "index.html"), "<html></html>");
        Assert.Contains("reach no other host", (await Command("apps", "validate", "--dir", app)).Out);
        File.WriteAllText(Path.Combine(app, "app.yaml"), "name: Bad Name\n");
        Assert.NotEqual(0, (await Command("apps", "validate", "--dir", app)).Exit);
    }

    /// <summary>People of the home made before the commands run: an approver (the Approver role on this server) and one who is not.</summary>
    private void MakeApprovers()
    {
        var database = ProxyState.Open(dataDirectory);
        var users = new IdentityUserStore(database);
        var options = ProxyConfiguration.LoadOptions(dataDirectory);
        options.DataDirectory = dataDirectory;
        var server = ProxyStoreModule.ServerIdOf(options, database);
        new HomeDirectory(database).Assign(AssignmentSubject.User(users.CreateUser("Grace Hopper", "grace@example.invalid", UserRole.Viewer).Id), server, ServerRole.Approver);
        new HomeDirectory(database).Assign(AssignmentSubject.User(users.CreateUser("Bob Babbage", "bob@example.invalid", UserRole.Viewer).Id), server, ServerRole.Viewer | ServerRole.Author);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_trusted_app_is_approved_by_hash_before_it_is_published_and_the_publish_requires_it()
    {
        MakeApprovers();
        var app = Path.Combine(dataDirectory, "trusted-app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "app.yaml"), Manifest.Replace("name: notes", "name: vault") + "trust: trusted\n");
        File.WriteAllText(Path.Combine(app, "index.html"), "<html><body>v1</body></html>");

        // no approval: refused, naming the missing approval and the hash; nothing is published
        var refused = await Command("apps", "publish", "--dir", app);
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("no approval", refused.Error);
        var hash = System.Text.RegularExpressions.Regex.Match(refused.Error, "hash ([0-9a-f]{64})").Groups[1].Value;
        Assert.NotEmpty(hash);

        // the approver must be a home user who holds the Approver role (the refusal names the user and the role), and not the author
        var notApprover = await Command("apps", "approve", "--path", app, "--by", "Bob Babbage");
        Assert.NotEqual(0, notApprover.Exit);
        Assert.Contains("Bob Babbage", notApprover.Error);
        Assert.Contains("Approver", notApprover.Error);
        var stranger = await Command("apps", "approve", "--path", app, "--by", "Mallory");
        Assert.Contains("not a user of the identity home", stranger.Error);

        var approved = await Command("apps", "approve", "--path", app, "--by", "Grace Hopper", "--note", "reviewed");
        Assert.True(approved.Exit == 0, approved.Error);
        Assert.Contains("hash " + hash, approved.Out);

        // a changed byte is another hash and has no approval; the approved bytes publish, and the version is noted on the approval
        File.WriteAllText(Path.Combine(app, "index.html"), "<html><body>v2</body></html>");
        Assert.Contains("no approval", (await Command("apps", "publish", "--dir", app)).Error);
        File.WriteAllText(Path.Combine(app, "index.html"), "<html><body>v1</body></html>");
        var published = await Command("apps", "publish", "--dir", app);
        Assert.True(published.Exit == 0, published.Error);
        var listed = (await Command("apps", "approval-list")).Out;
        Assert.Contains("approved by Grace Hopper", listed);
        Assert.Contains("hash " + hash[..12], listed);
        Assert.Contains("published as version 1", listed);
        Assert.DoesNotContain("revoked", listed);

        // revoked: the same bytes no longer publish, and the record stays
        var id = System.Text.RegularExpressions.Regex.Match(listed, "apr_[0-9a-f]+").Value;
        // revoking is a decision of the same weight: the Approver role is needed here too, and a refusal changes nothing
        var notRevoker = await Command("apps", "approval-revoke", "--id", id, "--by", "Bob Babbage");
        Assert.NotEqual(0, notRevoker.Exit);
        Assert.Contains("Bob Babbage", notRevoker.Error);
        Assert.Contains("Approver", notRevoker.Error);
        Assert.Contains("not a user of the identity home", (await Command("apps", "approval-revoke", "--id", id, "--by", "Mallory")).Error);
        Assert.DoesNotContain("revoked", (await Command("apps", "approval-list")).Out);
        Assert.Equal(0, (await Command("apps", "approval-revoke", "--id", id, "--by", "Grace Hopper")).Exit);
        Assert.Contains("revoked", (await Command("apps", "approval-list")).Out);
        Assert.Contains("no approval", (await Command("apps", "publish", "--dir", app)).Error);
        Assert.NotEqual(0, (await Command("apps", "approval-revoke", "--id", "apr_nothing", "--by", "Grace Hopper")).Exit);
    }

    [Fact]
    public async Task An_already_published_version_and_a_bare_hash_are_still_approved_and_the_modes_are_exclusive()
    {
        MakeApprovers();
        var app = Path.Combine(dataDirectory, "plain-trusted");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "app.yaml"), Manifest.Replace("name: notes", "name: vault") + "trust: sandbox\n");
        File.WriteAllText(Path.Combine(app, "index.html"), "<html></html>");
        Assert.Equal(0, (await Command("apps", "publish", "--dir", app)).Exit);   // a sandboxed app publishes without any approval

        Assert.NotEqual(0, (await Command("apps", "approve", "--app", "vault", "--version", "1", "--by", "Grace Hopper")).Exit);   // it did not ask to be trusted
        File.WriteAllText(Path.Combine(app, "app.yaml"), Manifest.Replace("name: notes", "name: vault") + "trust: trusted\n");
        Assert.Contains("no approval", (await Command("apps", "publish", "--dir", app)).Error);

        var author = await Command("apps", "approve", "--path", app, "--by", Environment.UserName);
        Assert.NotEqual(0, author.Exit);   // not an approver (and the publisher): refused either way
        var bare = await Command("apps", "approve", "--hash", new string('a', 64), "--app", "vault", "--by", "grace@example.invalid");
        Assert.True(bare.Exit == 0, bare.Error);
        Assert.Contains("not a hash", (await Command("apps", "approve", "--hash", "xyz", "--app", "vault", "--by", "Grace Hopper")).Error);
        Assert.NotEqual(0, (await Command("apps", "approve", "--path", app, "--app", "vault", "--version", "1", "--by", "Grace Hopper")).Exit);   // one way at a time
        Assert.NotEqual(0, (await Command("apps", "approve", "--by", "Grace Hopper")).Exit);
    }

    [Fact]
    public async Task The_data_route_wants_an_apps_token_and_answers_from_the_groups_published_models()
    {
        appsPort = FreePort();
        var (clientId, ada, _, database) = Prepare();
        var (apps, home) = await StartAsync(clientId);
        var session = await SignInAsync(apps, home, database, ada, "/apps/acme/");

        // no token (a session cookie is no credential here), and the preflight of a browser call from the sandboxed frame
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(apps, "/apps/acme/_data/api/v1/models/territories/entities/territory/rows", session)).StatusCode);
        var preflight = await apps.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/apps/acme/_data/api/v1/models/territories/changes"));
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("*", string.Join(' ', preflight.Headers.GetValues("Access-Control-Allow-Origin")));

        // a token the broker made, for a model nobody published to the group; and the path of a paired store, which is gone
        var token = (string)JsonNode.Parse(await (await PostTokenAsync(apps, "acme", session)).Content.ReadAsStringAsync())!["token"]!;
        HttpRequestMessage Get(string path) => new(HttpMethod.Get, path) { Headers = { { "Authorization", "Bearer " + token } } };
        var missing = await apps.SendAsync(Get("/apps/acme/_data/api/v1/models/territories/entities/territory/rows"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("published to this group", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Gone, (await apps.SendAsync(Get("/apps/acme/_data/store-1/api/v1/models/territories/entities/territory/rows"))).StatusCode);
    }

    [Fact]
    public async Task The_bundle_is_served_without_a_session_under_the_sandbox_policy_and_a_callback_nobody_started_is_refused()
    {
        appsPort = FreePort();
        var (clientId, _, _, _) = Prepare();
        var (apps, _) = await StartAsync(clientId);

        var page = await GetAsync(apps, "/apps/acme/_app/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadAsStringAsync();
        Assert.Contains("<base href=\"/apps/acme/_app/\">", body);
        Assert.Contains("sandbox allow-scripts", string.Join(' ', page.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(apps, "/apps/acme/_app/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(apps, "/apps/acme/_app/missing.js")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(apps, AppsEndpoints.CallbackPath + "?state=nope&code=x")).StatusCode);
    }

    [Fact]
    public void The_options_refuse_the_apps_host_without_tls_or_on_another_listeners_port()
    {
        static IReadOnlyList<DbDataKit.Config.Yaml.ConfigDiagnostic> Load(string yaml)
        {
            using var file = TempConfig.Create(("server.yaml", yaml));
            return ConfigTestSupport.LoadDirectory(file.Directory).Diagnostics;
        }

        Assert.Contains(Load("tls: { mode: none }\nmodules: [apps]\n"), d => d.KeyPath == "Modules");
        Assert.Contains(Load("listen: { address: 127.0.0.1, port: 8818 }\nmodules: [flightsql, apps]\n"), d => d.KeyPath == "Apps.Port");
        Assert.Contains(Load("management: { enabled: true, port: 8818 }\nmodules: [apps]\n"), d => d.KeyPath == "Apps.Port");
        Assert.Contains(Load("identity: { enabled: true, port: 8818 }\nmodules: [apps]\n"), d => d.KeyPath == "Apps.Port");
        Assert.Contains(Load("modules: [apps]\napps: { address: nowhere }\n"), d => d.KeyPath == "Apps.Address");
        Assert.DoesNotContain(Load("modules: [apps, identity]\napps: { home: { clientId: abc } }\n"), d => d.KeyPath?.StartsWith("Apps", StringComparison.Ordinal) == true);
        Assert.Contains(Load("modules: [apps, identity]\n"), d => d.KeyPath == "Apps.Home.ClientId");
        Assert.Contains(Load("modules: [apps]\napps: { home: { clientId: abc } }\n"), d => d.KeyPath == "Apps.Home.Authority");
        Assert.Contains(Load("modules: [apps]\napps: { home: { clientId: abc, authority: 'http://home.example' } }\n"), d => d.KeyPath == "Apps.Home.Authority");
        Assert.Contains(Load("modules: [apps, identity]\napps: { publicUrl: 'https://apps.example/x', home: { clientId: abc } }\n"), d => d.KeyPath == "Apps.PublicUrl");
    }
}
