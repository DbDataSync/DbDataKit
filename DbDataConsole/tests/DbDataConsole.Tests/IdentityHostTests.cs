using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Cli;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>
/// Plan 13a U15: the identity home run on its own (<c>dbdatakit identity run</c>), and moved out of wherever it ran. A console signs in through it; the home is stopped and started again from a copy of its directory at the same
/// address (a different process, a different folder, as a move to another machine is); the console's session, its refresh token, its client, the roles and the tokens all keep working.
/// </summary>
public sealed class IdentityHostTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-idhost-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private readonly Clock clock = new();
    private WebApplication? home;
    private HttpClient browser = null!, console = null!;
    private PairableServer server = null!;
    private WebApplication consoleApp = null!;
    private string homeDirectory = null!, homeUrl = null!, ada = null!;
    private int homePort;

    private const string ServerId = "server-a";

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }

    private static int FreePort() => TestPorts.Next();

    private static async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await ConsoleCli.RunAsync(args, context);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    private static HttpClient Trusting(Uri? baseAddress = null) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = baseAddress };

    private async Task<WebApplication> StartHome(string directory)
    {
        var settings = new IdentityHostSettings(directory, new Uri($"https://localhost:{homePort}"));
        var app = IdentityHost.Build(settings, ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        return app;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(root);
        homePort = FreePort();
        homeUrl = $"https://localhost:{homePort}";
        homeDirectory = Path.Combine(root, "home-1");
        Directory.CreateDirectory(homeDirectory);
        browser = Trusting();

        // the operator's commands, offline, before the home has ever run
        var db = IdentityHostState.Open(homeDirectory);
        ada = new UserStore(db).CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer).Id;
        var consolePort = FreePort();
        var registered = await Cli("identity", "client-add", "--repo", homeDirectory, "--name", "Console", "--redirect", $"https://localhost:{consolePort}{ConsoleFederation.CallbackPath}", "--logout-redirect", $"https://localhost:{consolePort}/");
        Assert.Equal(0, registered.Exit);
        var (clientId, secret) = ((string)registered.Data["client_id"]!, (string)registered.Data["secret"]!);
        Assert.Equal(0, (await Cli("identity", "assign", "--repo", homeDirectory, "--user", ada, "--app", "console", "--roles", "admin")).Exit);
        Assert.Equal(0, (await Cli("identity", "assign", "--repo", homeDirectory, "--user", ada, "--server", ServerId, "--roles", "admin,operator")).Exit);

        home = await StartHome(homeDirectory);

        server = await PairableServer.StartAsync(instanceId: ServerId);
        var consoleDirectory = Path.Combine(root, "console");
        Directory.CreateDirectory(consoleDirectory);
        var identity = new IdentityHostSettings(homeDirectory, new Uri(homeUrl));
        consoleApp = ConsoleHost.Build(
            new ConsoleSettings(consoleDirectory, new Uri($"https://localhost:{consolePort}"), Home: new IdentityHomeSettings(new Uri(homeUrl), clientId, secret, "console")),
            ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"),
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                // the home's certificate is its own: the console in this test trusts it as a console is told to
                services.AddSingleton(new OpenIdClient(
                    new OpenIdClientOptions(homeUrl, clientId, secret, $"https://localhost:{consolePort}{ConsoleFederation.CallbackPath}", $"https://localhost:{consolePort}/"),
                    new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(10) }));
            });
        await consoleApp.StartAsync();
        started.Add(consoleApp);
        console = Trusting(new Uri($"https://localhost:{consolePort}"));
        await consoleApp.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCodeWith(ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver), "Server A", default);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Enumerable.Reverse(started)) await s.DisposeAsync();
        if (home is not null) await home.DisposeAsync();
        await server.DisposeAsync();
        browser.Dispose(); console.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    /// <summary>The person has a session at the home (a passkey sign-in makes one; the test makes the row the sign-in would).</summary>
    private string HomeSession(string userId) => home!.Services.GetRequiredService<SessionStore>().Create(userId).Id;

    private async Task<string> SignIn()
    {
        var start = await console.GetAsync(ConsoleFederation.SignInPath + "?returnUrl=/users");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        authorize.Headers.Add("Cookie", $"{IdentityHost.Auth.SessionCookie}={HomeSession(ada)}");
        var back = await browser.SendAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var callback = await console.GetAsync(back.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(ConsoleHost.Auth.SessionCookie + "=", StringComparison.Ordinal));
        return cookie[(ConsoleHost.Auth.SessionCookie.Length + 1)..].Split(';')[0];
    }

    private async Task<HttpResponseMessage> EchoResponse(string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/relay/{ServerId}/api/echo");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return await console.SendAsync(request);
    }

    private async Task<JsonNode> Echo(string session)
    {
        var response = await EchoResponse(session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static string[] Roles(JsonNode echo) => [.. echo["roles"]!.AsArray().Select(r => (string)r!)];

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from)) Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static readonly TimeSpan PastTheInterval = FederationPolicy.DefaultRefreshInterval + TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_console_signs_in_through_the_home_that_runs_on_its_own_and_the_token_carries_the_homes_roles()
    {
        var session = await SignIn();
        var seen = await Echo(session);
        Assert.Equal(ada, (string)seen["subject"]!);                                         // the home's user id, in what the server audits
        Assert.Contains("admin", Roles(seen));
        Assert.Contains("operator", Roles(seen));
    }

    [Fact]
    public async Task The_homes_state_directory_moves_and_the_console_its_session_its_refresh_token_and_the_tokens_keep_working()
    {
        var session = await SignIn();
        Assert.Contains("operator", Roles(await Echo(session)));
        var jwksBefore = await browser.GetStringAsync($"{homeUrl}/identity/jwks");
        Assert.Contains("\"kid\"", jwksBefore);

        // the home stops, and runs from a copy of its directory in another place, at the same address
        await home!.DisposeAsync();
        home = null;
        var moved = Path.Combine(root, "elsewhere", "home-2");
        Copy(homeDirectory, moved);
        Directory.Delete(homeDirectory, true);
        home = await StartHome(moved);

        // the keys are the same: a token the old home signed still verifies against the new home's key set
        Assert.Equal(jwksBefore, await browser.GetStringAsync($"{homeUrl}/identity/jwks"));
        var discovery = JsonNode.Parse(await browser.GetStringAsync($"{homeUrl}/.well-known/openid-configuration"))!;
        Assert.Equal(homeUrl, (string)discovery["issuer"]!);

        // the console's session lives on: its refresh token is the old home's and the new home honours it; the roles are read again from the moved state
        Assert.Equal(0, (await Cli("identity", "assign", "--repo", moved, "--user", ada, "--server", ServerId, "--roles", "viewer")).Exit);
        clock.Offset = PastTheInterval;
        Assert.Equal(["viewer"], Roles(await Echo(session)));

        // and a new sign-in through the moved home works, with the same client and the same person
        var second = await SignIn();
        Assert.Equal(ada, (string)(await Echo(second))["subject"]!);
    }

    [Fact]
    public async Task A_home_moved_without_its_key_ring_cannot_read_its_signing_keys_and_says_so_instead_of_signing_with_nothing()
    {
        await SignIn();
        await home!.DisposeAsync();
        home = null;
        var moved = Path.Combine(root, "elsewhere", "home-2");
        Copy(homeDirectory, moved);
        Directory.Delete(Path.Combine(moved, "dataprotection"), true);                       // the keys are in the state; what protects them stayed behind
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartHome(moved));
        Assert.IsAssignableFrom<CryptographicException>(Unwrap(ex));

        static Exception Unwrap(Exception e) => e.InnerException is { } inner && e is not CryptographicException ? Unwrap(inner) : e;
    }

    [Fact]
    public async Task The_signing_key_is_not_in_the_state_database_in_the_clear()
    {
        var db = home!.Services.GetRequiredService<StateDatabase>();
        var stored = db.Retry(() =>
        {
            using var connection = db.OpenConnection();
            using var cmd = db.Command(connection, "SELECT PrivateKey FROM SigningKeys;");
            return (string)cmd.ExecuteScalar()!;
        });
        using var ecdsa = ECDsa.Create();
        Assert.ThrowsAny<CryptographicException>(() => ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_person_who_is_not_signed_in_is_sent_to_the_homes_sign_in_page_and_only_to_a_path_of_the_home()
    {
        var start = await console.GetAsync(ConsoleFederation.SignInPath);
        var authorize = await browser.GetAsync(start.Headers.Location);                        // no session at the home
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.StartsWith("/identity/sign-in?returnUrl=", authorize.Headers.Location!.OriginalString);

        var page = await browser.GetAsync(homeUrl + authorize.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("identity-sign-in-passkey", html);
        Assert.Contains("requested.startsWith('/identity/')", html);                           // the page returns only under /identity/
        Assert.Contains("/api/auth/passkey/begin", html);
    }

    [Fact]
    public async Task What_is_not_the_providers_public_endpoints_stays_closed()
    {
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"{homeUrl}/.well-known/openid-configuration")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"{homeUrl}/identity/jwks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync($"{homeUrl}/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync($"{homeUrl}/api/invites", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task A_home_with_no_user_writes_the_first_run_invitation_and_signing_out_of_the_console_ends_the_persons_sessions_at_the_home()
    {
        // (the person made in the test exists, so the invitation is removed at start; a fresh directory writes one)
        var fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(fresh);
        var port = FreePort();
        await using var other = IdentityHost.Build(new IdentityHostSettings(fresh, new Uri($"https://localhost:{port}")), ConsoleHost.Certificate(fresh), _ => { }, webRoot: Path.Combine(fresh, "none"));
        await other.StartAsync();
        Assert.Contains("/invite#", await File.ReadAllTextAsync(Path.Combine(fresh, "FIRST-RUN.txt")));
        Assert.False(File.Exists(Path.Combine(homeDirectory, "FIRST-RUN.txt")));

        // sign-out: the console sends the browser to the home's end-session endpoint from another site, so no cookie of the home comes with it; the id token hint names the person
        var session = await SignIn();
        var homeSession = HomeSession(ada);
        var users = home!.Services.GetRequiredService<UserStore>();
        var sessions = home.Services.GetRequiredService<SessionStore>();
        Assert.NotNull(sessions.Resolve(homeSession, users));
        var signOut = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-out");
        signOut.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var redirectTo = new Uri((string)JsonNode.Parse(await (await console.SendAsync(signOut)).Content.ReadAsStringAsync())!["redirectTo"]!);
        var atHome = await browser.GetAsync(redirectTo);
        Assert.Equal(HttpStatusCode.Redirect, atHome.StatusCode);
        Assert.Null(sessions.Resolve(homeSession, users));                                       // the home's own session of hers is gone, though no cookie came with the request
    }

    [Fact]
    public async Task The_identity_noun_registers_clients_and_gives_roles_and_a_wrong_use_is_refused()
    {
        var directory = Path.Combine(root, "cli");
        var list = await Cli("identity", "client-list", "--repo", directory);
        Assert.Equal(0, list.Exit);
        Assert.Contains("No client yet", list.Out);

        var added = await Cli("identity", "client-add", "--repo", directory, "--name", "App", "--redirect", "https://app.example/cb, https://app.example/cb2", "--public");
        Assert.Equal(0, added.Exit);
        Assert.False(added.Data.ContainsKey("secret"));
        var clients = (List<Dictionary<string, object?>>)(await Cli("identity", "client-list", "--repo", directory)).Data["clients"]!;
        Assert.Equal("App", clients.Single()["name"]);
        Assert.Equal(["https://app.example/cb", "https://app.example/cb2"], (List<string>)clients.Single()["redirect_uris"]!);
        Assert.Equal(false, clients.Single()["confidential"]);

        Assert.Equal(2, (await Cli("identity", "client-add", "--repo", directory, "--name", "Bad", "--redirect", "not a uri")).Exit);
        Assert.Equal(2, (await Cli("identity", "client-add", "--repo", directory, "--name", "NoRedirect")).Exit);
        Assert.Equal(2, (await Cli("identity", "assign", "--repo", directory, "--user", "x", "--roles", "admin")).Exit);                         // no server or app
        Assert.Equal(2, (await Cli("identity", "assign", "--repo", directory, "--user", "x", "--server", "s", "--app", "a", "--roles", "admin")).Exit);
        Assert.Equal(2, (await Cli("identity", "assign", "--repo", directory, "--user", "x", "--server", "s", "--roles", "emperor")).Exit);
        Assert.Equal(1, (await Cli("identity", "assign", "--repo", directory, "--user", "nobody", "--server", "s", "--roles", "admin")).Exit);
        Assert.Equal(2, (await Cli("identity", "run", "--repo", directory, "--listen", "http://localhost:1")).Exit);
        Assert.Equal(2, (await Cli("identity", "run", "--repo", directory, "--bogus")).Exit);
    }

    [Fact]
    public async Task An_external_group_is_mapped_and_unmapped_from_the_command_line()
    {
        var directory = Path.Combine(root, "cli-map");
        var db = IdentityHostState.Open(directory);
        var entra = new UserStore(db).CreateUser("Grace", "grace@example.invalid", UserRole.Viewer);
        var homeDirectory = new HomeDirectory(db);
        homeDirectory.SetExternalGroups(entra.Id, ExternalSources.Entra, ["Group-1"]);

        Assert.Equal(0, (await Cli("identity", "map-group", "--repo", directory, "--source", "entra", "--group", "group-1", "--server", "s1", "--project", "sales", "--roles", "author")).Exit);
        Assert.Equal(ServerRole.Author, homeDirectory.RolesOn(entra.Id, "s1").Projects["sales"] & ServerRole.Author);
        Assert.Equal(0, (await Cli("identity", "map-group", "--repo", directory, "--source", "entra", "--group", "group-1", "--app", "console", "--roles", "viewer")).Exit);
        Assert.Equal(ServerRole.Viewer, homeDirectory.RolesIn(entra.Id, "console"));
        Assert.Equal(0, (await Cli("identity", "map-group", "--repo", directory, "--source", "entra", "--group", "group-1", "--app", "console", "--roles", "none")).Exit);
        Assert.Equal(ServerRole.None, homeDirectory.RolesIn(entra.Id, "console"));
        Assert.Equal(2, (await Cli("identity", "map-group", "--repo", directory, "--source", "ldap", "--group", "g", "--app", "c", "--roles", "viewer")).Exit);
    }
}
