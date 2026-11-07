using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Config;

/// <summary>
/// Plan 20 O06: the identity home as a module of the serving layer. It has a listener of its own (never the Flight or management port), its people and keys in the Proxy's state, its first-run invitation in the
/// data directory, the shared administration verbs at the command line, and a console (an OpenID Connect client) signs a person in through it.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class IdentityModuleTests : IAsyncLifetime
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbproxy-identity-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int flightPort, managementPort, identityPort;

    private static readonly HashSet<int> HandedOut = [];

    /// <summary>A free loopback port, never one this process handed out before: a released port can be given out again at once, and the test's three ports must differ (the identity home and the Flight port may not share one).</summary>
    private static int FreePort()
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            lock (HandedOut) if (HandedOut.Add(port)) return port;
        }
    }

    private static HttpClient Trusting(Uri? baseAddress = null, Version? version = null) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = baseAddress,
            DefaultRequestVersion = version ?? HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

    private string IdentityUrl => $"https://localhost:{identityPort}";

    public ValueTask InitializeAsync()
    {
        var config = ProxyConfigFile.DirectoryIn(dataDirectory);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        return ValueTask.CompletedTask;
    }

    private async Task StartAsync(params string[] more)
    {
        // a free port is looked up and released before the server binds it, so another process can take it in between: that is answered by looking up others
        for (var attempt = 1; ; attempt++)
        {
            (flightPort, managementPort, identityPort) = (FreePort(), FreePort(), FreePort());
            app = ProxyHost.Build([
                $"--Proxy:DataDirectory={dataDirectory}", "--Proxy:Listen:Address=127.0.0.1", $"--Proxy:Listen:Port={flightPort}",
                "--Proxy:Management:Enabled=true", "--Proxy:Management:Address=127.0.0.1", $"--Proxy:Management:Port={managementPort}",
                "--Proxy:Identity:Enabled=true", "--Proxy:Identity:Address=127.0.0.1", $"--Proxy:Identity:Port={identityPort}", .. more,
            ]);
            try
            {
                await app.StartAsync();
                return;
            }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                await app.DisposeAsync();
                app = null;
            }
        }
    }

    private async Task StopAsync()
    {
        if (app is null) return;
        await app.StopAsync();
        await app.DisposeAsync();
        app = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(dataDirectory);
    }

    [Fact]
    public void The_options_refuse_an_identity_home_without_tls_on_another_listeners_port_or_with_a_public_address_that_is_not_https()
    {
        static IReadOnlyList<DbDataKit.Config.Yaml.ConfigDiagnostic> Load(string yaml)
        {
            using var file = TempConfig.Create(("server.yaml", yaml));
            return ConfigTestSupport.LoadDirectory(file.Directory).Diagnostics;
        }

        Assert.Contains(Load("tls: { mode: none }\nlisten: { address: 127.0.0.1 }\nidentity: { enabled: true }\n"), d => d.KeyPath == "Identity.Enabled");
        Assert.Contains(Load("listen: { address: 127.0.0.1, port: 8817 }\nidentity: { enabled: true }\n"), d => d.KeyPath == "Identity.Port");
        Assert.Contains(Load("management: { enabled: true, port: 8817 }\nidentity: { enabled: true }\n"), d => d.KeyPath == "Identity.Port");
        Assert.Contains(Load("identity: { enabled: true, publicUrl: 'http://identity.example' }\n"), d => d.KeyPath == "Identity.PublicUrl");
        Assert.Contains(Load("identity: { enabled: true, publicUrl: 'https://identity.example/home' }\n"), d => d.KeyPath == "Identity.PublicUrl");
        Assert.Contains(Load("identity: { enabled: true, address: 'everywhere' }\n"), d => d.KeyPath == "Identity.Address");
        Assert.DoesNotContain(Load("identity: { enabled: true, publicUrl: 'https://identity.example' }\n"), d => d.KeyPath?.StartsWith("Identity", StringComparison.Ordinal) == true);

        // personal access tokens (U20): an install may lower the maximum, never past a year
        Assert.Contains(Load("identity: { enabled: true, accessTokenMaxDays: 400 }\n"), d => d.KeyPath == "Identity.AccessTokenMaxDays");
        Assert.DoesNotContain(Load("identity: { enabled: true, accessTokenMaxDays: 30 }\n"), d => d.KeyPath == "Identity.AccessTokenMaxDays");

        // Entra (O06b): the tenant over https, with the app's id and secret; no half a section
        Assert.Contains(Load("identity: { enabled: true, entra: { authority: 'http://login.example/t/v2.0', clientId: a, clientSecret: 'secret:x:y' } }\n"), d => d.KeyPath == "Identity.Entra.Authority");
        Assert.Contains(Load("identity: { enabled: true, entra: { authority: 'https://login.microsoftonline.com/t/v2.0', clientSecret: 'secret:x:y' } }\n"), d => d.KeyPath == "Identity.Entra.ClientId");
        Assert.Contains(Load("identity: { enabled: true, entra: { authority: 'https://login.microsoftonline.com/t/v2.0', clientId: a } }\n"), d => d.KeyPath == "Identity.Entra.ClientSecret");
        Assert.Contains(Load("identity: { enabled: true, entra: { clientId: a } }\n"), d => d.KeyPath == "Identity.Entra.Authority");
        Assert.DoesNotContain(Load("identity: { enabled: true, windows: { enabled: true }, entra: { authority: 'https://login.microsoftonline.com/t/v2.0', clientId: a, clientSecret: 'secret:x:y' } }\n"),
            d => d.KeyPath?.StartsWith("Identity", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task The_home_is_served_on_its_own_listener_and_on_no_other_and_writes_its_first_run_invitation()
    {
        await StartAsync();
        using var http = Trusting(new Uri(IdentityUrl));
        var discovery = JsonNode.Parse(await http.GetStringAsync("/.well-known/openid-configuration"))!;
        Assert.Equal(IdentityUrl, (string?)discovery["issuer"]);
        Assert.Contains("identity-sign-in", await http.GetStringAsync(IdentityHome.SignInPath));
        Assert.Contains("identity-invite", await http.GetStringAsync(IdentityHome.InvitePath));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/users")).StatusCode);   // the people are not listed to anyone not signed in

        // a browser speaks HTTP/1.1 to it; the Flight listener is HTTP/2 only and the management API is the console's
        using var flight = Trusting(new Uri($"https://127.0.0.1:{flightPort}"), HttpVersion.Version20);
        Assert.Equal(HttpStatusCode.NotFound, (await flight.GetAsync("/.well-known/openid-configuration")).StatusCode);
        using var management = Trusting(new Uri($"https://127.0.0.1:{managementPort}"));
        foreach (var path in new[] { "/.well-known/openid-configuration", IdentityHome.SignInPath, IdentityHome.InvitePath })
            Assert.Equal(HttpStatusCode.NotFound, (await management.GetAsync(path)).StatusCode);

        var firstRun = File.ReadAllText(Path.Combine(ProxyIdentityModule.DirectoryOf(new ProxyOptions { DataDirectory = dataDirectory }), DbDataKit.Web.Auth.BootstrapInvite.FileName));
        Assert.Contains("/invite#", firstRun);
        Assert.Contains("dbdataproxy identity invite", firstRun);
    }

    [Fact]
    public async Task An_instance_that_lists_only_the_identity_module_opens_no_flight_port()
    {
        File.WriteAllText(Path.Combine(ProxyConfigFile.DirectoryIn(dataDirectory), "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\nmodules: [identity]\n");
        await StartAsync();

        using var home = Trusting(new Uri(IdentityUrl));
        Assert.Equal(HttpStatusCode.OK, (await home.GetAsync("/.well-known/openid-configuration")).StatusCode);
        using var flight = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () => await flight.ConnectAsync(IPAddress.Loopback, flightPort));
    }

    [Fact]
    public void A_module_that_does_not_exist_is_refused_naming_the_setting_and_the_known_ones()
    {
        using var file = TempConfig.Create(("server.yaml", "modules: [flightsql, ftp]\n"));
        var diagnostics = ProxyConfig.Load(new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(file.Directory)).Build()).Diagnostics;
        var refused = Assert.Single(diagnostics, d => d.KeyPath == "Modules");
        Assert.Contains("`ftp`", refused.Message);
        Assert.Contains("flightsql, identity", refused.Fix);
    }

    [Fact]
    public async Task A_console_signs_a_person_in_through_the_serving_layer_with_the_homes_id_and_roles()
    {
        await StartAsync();
        var state = app!.Services.GetRequiredService<StateDatabase>();
        var ada = new IdentityUserStore(state).CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer, emailVerified: true);

        // the operator's commands, on the same state, with the server running
        static async Task<(int Exit, IDictionary<string, object?> Data)> Run(params string[] args)
        {
            var context = new DbDataKit.Cli.CliContext(new StringWriter(), new StringWriter(), TextReader.Null);
            return (await DbDataProxy.Server.Hosting.Cli.Router().RunAsync(args, context), context.Data);
        }
        const string Redirect = "https://console.example.invalid/api/identity/callback";
        var registered = await Run("identity", "client-add", "--repo", dataDirectory, "--name", "Operations console", "--redirect", Redirect);
        Assert.Equal(0, registered.Exit);
        Assert.Equal(0, (await Run("identity", "assign", "--repo", dataDirectory, "--user", ada.Id, "--server", "build-1", "--roles", "operator")).Exit);

        // the console begins a sign-in; the person, signed in at the home (the session a passkey sign-in makes), is sent back with a code; the console completes it
        var client = new OpenIdClient(new OpenIdClientOptions(IdentityUrl, (string)registered.Data["client_id"]!, (string)registered.Data["secret"]!, Redirect), Trusting());
        var authorize = await client.BeginAsync("/servers", default);
        Assert.StartsWith(IdentityUrl + "/identity/authorize?", authorize.AbsoluteUri);

        using var browser = Trusting();
        var notSignedIn = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, notSignedIn.StatusCode);
        Assert.StartsWith(IdentityHome.SignInPath, notSignedIn.Headers.Location!.OriginalString);

        var request = new HttpRequestMessage(HttpMethod.Get, authorize);
        request.Headers.Add("Cookie", $"{ProxyIdentityModule.Auth.SessionCookie}={new IdentitySessionStore(state).Create(ada.Id).Id}");
        var back = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.StartsWith(Redirect + "?", back.Headers.Location!.AbsoluteUri);

        var query = System.Web.HttpUtility.ParseQueryString(back.Headers.Location.Query);
        var signedIn = await client.CompleteAsync(query["state"], query["code"], query["error"], default);
        Assert.Equal(ada.Id, signedIn.Identity.UserId);
        Assert.Equal("/servers", signedIn.ReturnUrl);
        Assert.Contains(signedIn.Identity.Roles, r => r.Key.Contains("build-1", StringComparison.Ordinal) && r.Value.Contains("operator"));
    }

    [Fact]
    public async Task The_command_line_reads_the_settings_without_keeping_a_watch_on_the_folder()
    {
        // every `dbdataproxy identity` verb reads the settings; a watcher left behind each time is an inotify instance on Linux, and a user has 128 of them
        if (!OperatingSystem.IsLinux()) return;
        static int Watches() => new DirectoryInfo("/proc/self/fd").GetFileSystemInfos().Count(f => f.LinkTarget?.Contains("inotify", StringComparison.Ordinal) == true);
        var before = Watches();
        for (var i = 0; i < 20; i++)
        {
            var context = new DbDataKit.Cli.CliContext(new StringWriter(), new StringWriter(), TextReader.Null);
            Assert.Equal(0, await DbDataProxy.Server.Hosting.Cli.Router().RunAsync(["identity", "client-list", "--repo", dataDirectory], context));
        }
        Assert.True(Watches() <= before, $"{Watches() - before} inotify instances were left behind by 20 commands.");
    }

    [Fact]
    public async Task The_signing_keys_survive_a_restart_and_an_instance_with_another_key_refuses_to_start_naming_the_secret()
    {
        await StartAsync();
        using (var http = Trusting(new Uri(IdentityUrl)))
        {
            var before = await http.GetStringAsync("/identity/jwks");
            await StopAsync();
            await StartAsync();
            using var again = Trusting(new Uri(IdentityUrl));
            Assert.Equal(before, await again.GetStringAsync("/identity/jwks"));
        }
        await StopAsync();

        // another key in this instance's store: what a second instance of a group given the wrong secret would have
        var store = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dataDirectory });
        using (var other = SecretValue.From(KeySigningKeyProtector.NewKey()))
            await store.SetAsync(SecretName.Parse(ProxyIdentityModule.SigningKeySecret), other);
        var failed = await Assert.ThrowsAnyAsync<SecretConfigurationException>(() => StartAsync());
        Assert.Contains(ProxyIdentityModule.SigningKeySecret, failed.Message);
        Assert.Contains("same key", failed.Message);
        if (app is not null) { await app.DisposeAsync(); app = null; }
    }

    [Fact]
    public async Task A_person_signs_in_at_the_serving_layer_through_entra_with_the_mapped_roles_and_a_refused_sign_in_signs_nobody_in()
    {
        // a stand-in tenant: discovery, keys, and a token endpoint that signs an RS256 id token with a groups claim for the home's app
        using var tenantKey = RSA.Create(2048);
        var tenantPort = FreePort();
        var authority = $"http://127.0.0.1:{tenantPort}/t/v2.0";
        var nonce = "";
        var tenantBuilder = WebApplication.CreateSlimBuilder();
        tenantBuilder.WebHost.UseUrls($"http://127.0.0.1:{tenantPort}");
        await using var tenant = tenantBuilder.Build();
        tenant.MapGet("/t/v2.0/.well-known/openid-configuration", () => Results.Json(new { issuer = authority, authorization_endpoint = authority + "/authorize", token_endpoint = authority + "/token", jwks_uri = authority + "/keys" }));
        tenant.MapGet("/t/v2.0/keys", () =>
        {
            var p = tenantKey.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", kid = "k1", n = Base64Url.EncodeToString(p.Modulus!), e = Base64Url.EncodeToString(p.Exponent!) } } });
        });
        tenant.MapPost("/t/v2.0/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            if (form["client_secret"] != "tenant-secret") return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            static string B(object o) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(o));
            var input = B(new { alg = "RS256", kid = "k1" }) + "." + B(new Dictionary<string, object>
            {
                ["iss"] = authority, ["aud"] = "home-app", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), ["sub"] = "oid-ada", ["name"] = "Ada Lovelace", ["nonce"] = nonce, ["groups"] = new[] { "g-operators" },
            });
            return Results.Json(new { id_token = input + "." + Base64Url.EncodeToString(tenantKey.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
        });
        await tenant.StartAsync();

        await StartAsync($"--Proxy:Identity:Entra:Authority={authority}", "--Proxy:Identity:Entra:ClientId=home-app", "--Proxy:Identity:Entra:ClientSecret=tenant-secret", "--Proxy:Identity:Windows:Enabled=true");
        var state = app!.Services.GetRequiredService<StateDatabase>();
        new HomeDirectory(state).MapExternalGroup(ExternalSources.Entra, "g-operators", "build-1", ServerRole.Operator);
        using var browser = Trusting(new Uri(IdentityUrl));

        // the home's sign-in page offers both upstreams
        var page = await browser.GetStringAsync(IdentityHome.SignInPath);
        Assert.Contains("identity-sign-in-entra", page);
        Assert.Contains("identity-sign-in-windows", page);

        async Task<HttpResponseMessage> ThroughTenant(string back)
        {
            var start = await browser.GetAsync(UpstreamSignIn.EntraPath);
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
            Assert.StartsWith(authority + "/authorize?", start.Headers.Location!.AbsoluteUri);
            var query = System.Web.HttpUtility.ParseQueryString(start.Headers.Location.Query);
            Assert.Equal(IdentityUrl + UpstreamSignIn.EntraCallbackPath, query["redirect_uri"]);
            nonce = query["nonce"]!;
            return await browser.GetAsync($"{UpstreamSignIn.EntraCallbackPath}?state={Uri.EscapeDataString(query["state"]!)}&{back}");
        }
        static bool SetsSession(HttpResponseMessage r) => r.Headers.TryGetValues("Set-Cookie", out var c) && c.Any(v => v.StartsWith(ProxyIdentityModule.Auth.SessionCookie + "=", StringComparison.Ordinal));

        var refused = await ThroughTenant("error=access_denied");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(SetsSession(refused));
        Assert.Empty(new IdentityUserStore(state).List());

        var signedIn = await ThroughTenant("code=from-tenant");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.True(SetsSession(signedIn));
        var ada = Assert.Single(new IdentityUserStore(state).List());
        Assert.Equal("Ada Lovelace", ada.DisplayName);
        Assert.True(new HomeDirectory(state).RolesOn(ada.Id, "build-1").Roles.HasFlag(ServerRole.Operator));
    }

    [Fact]
    public async Task The_management_api_administers_the_home_this_proxy_serves_and_says_so_when_it_serves_none()
    {
        // the same admin API as identity run's (13a U21), under the management API's prefix; the local channel's caller holds every role
        var database = StateDatabase.OpenModules(Path.Combine(dataDirectory, "admin-test.db"), DbDataKit.Identity.IdentityHomeState.Modules);
        Directory.CreateDirectory(dataDirectory);
        var configuration = new ConfigurationBuilder().Build();
        var served = new DbDataProxy.Server.Management.ProxyManagement(configuration, dataDirectory, "proxy-1", "test", identityAdmin: new IdentityAdminApi(database, "https://identity.example.invalid/"));
        var caller = DbDataProxy.Server.Management.ManagementCaller.Local("operator");

        var made = await served.HandleAsync("POST", IdentityAdminApi.Prefix + "/service-accounts", "{\"name\":\"nightly\"}", caller);
        Assert.Equal(200, made.Status);
        var listed = await served.HandleAsync("GET", IdentityAdminApi.Prefix + "/service-accounts", null, caller);
        Assert.Equal("nightly", (string?)listed.Body["service_accounts"]![0]!["name"]);
        Assert.Equal(204, (await served.HandleAsync("DELETE", IdentityAdminApi.Prefix + "/service-accounts/nightly", null, caller)).Status);

        var none = new DbDataProxy.Server.Management.ProxyManagement(configuration, dataDirectory, "proxy-1", "test");
        var refused = await none.HandleAsync("GET", IdentityAdminApi.Prefix + "/people", null, caller);
        Assert.Equal(404, refused.Status);
        Assert.Contains("identity.enabled", (string?)refused.Body["error"]);
    }
}
