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
        var users = new UserStore(database);
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
        authorize.Headers.Add("Cookie", $"{ProxyIdentityModule.Auth.SessionCookie}={new SessionStore(database).Create(user).Id}");
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
    public async Task The_data_route_wants_an_apps_token_and_a_paired_store()
    {
        appsPort = FreePort();
        var (clientId, ada, _, database) = Prepare();
        var (apps, home) = await StartAsync(clientId);
        var session = await SignInAsync(apps, home, database, ada, "/apps/acme/");

        // no token (a session cookie is no credential here), and the preflight of a browser call from the sandboxed frame
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(apps, "/apps/acme/_data/store-1/api/v1/models/territories/entities/territory/rows", session)).StatusCode);
        var preflight = await apps.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/apps/acme/_data/store-1/api/v1/models/territories/changes"));
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("*", string.Join(' ', preflight.Headers.GetValues("Access-Control-Allow-Origin")));

        // a token the broker made, for a store nobody paired
        var token = (string)JsonNode.Parse(await (await PostTokenAsync(apps, "acme", session)).Content.ReadAsStringAsync())!["token"]!;
        var request = new HttpRequestMessage(HttpMethod.Get, "/apps/acme/_data/store-1/api/v1/models/territories/entities/territory/rows");
        request.Headers.Add("Authorization", "Bearer " + token);
        Assert.Equal(HttpStatusCode.NotFound, (await apps.SendAsync(request)).StatusCode);
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
