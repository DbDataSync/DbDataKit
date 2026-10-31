using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using DbDataKit.Web.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>
/// Plan 13a U14: one person signs in to two consoles through the identity home and is the same user in both, in what each console tells its server; a console keeps no users of its own; and the second console's ceiling for its
/// server still applies whatever the home says.
/// </summary>
public sealed class ConsoleFederationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-fed-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private HttpClient home = null!, consoleA = null!, consoleB = null!;
    private WebApplication appA = null!;
    private PairableServer serverA = null!, serverB = null!;
    private UserRecord ada = null!, bob = null!;
    private string homeUrl = null!;
    private WebApplication homeApp = null!, appB = null!;
    private HomeDirectory directory = null!;
    private readonly List<string> endedAtHome = [];
    private readonly Clock clockA = new(), clockB = new();

    /// <summary>The console's clock for the home's word: a test moves it, so the refresh interval and the grace pass without waiting.</summary>
    private sealed class Clock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }

    private const string ServerAId = "server-a", ServerBId = "server-b";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Mask : ISigningKeyProtector
    {
        public byte[] Protect(byte[] k) => k.Select(b => (byte)(b ^ 0x33)).ToArray();
        public byte[] Unprotect(byte[] k) => Protect(k);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(root);
        // the identity home
        var homePort = FreePort();
        homeUrl = $"http://127.0.0.1:{homePort}";
        var db = new StateDatabase(Path.Combine(root, "home.db"), HomeSchema.Schema);
        var users = new UserStore(db);
        directory = new HomeDirectory(db);
        var clients = new OidcClients(db);
        var keys = new SigningKeys(db, new Mask());
        var provider = new OpenIdProvider(db, users, directory, clients, keys, homeUrl);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(homeUrl);
        homeApp = builder.Build();
        // the home's own session is the header here; ending it is noting that the home was asked to
        homeApp.MapOpenIdProvider(provider, keys, new OpenIdHostOptions(c => c.Request.Headers["X-Signed-In-User"].FirstOrDefault(), EndSession: c =>
        {
            lock (endedAtHome) endedAtHome.Add(c.Request.Headers["X-Signed-In-User"].FirstOrDefault() ?? "");
            return Task.CompletedTask;
        }));
        await homeApp.StartAsync();
        started.Add(homeApp);
        home = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        // two consoles, each with a server of its own and a client registered at the home
        var portA = FreePort();
        var portB = FreePort();
        var (idA, secretA) = Register("Console A", $"https://localhost:{portA}");
        var (idB, secretB) = Register("Console B", $"https://localhost:{portB}");
        (OidcClient Client, string? Secret) Register(string name, string origin)
        {
            var made = clients.Register(name, [origin + ConsoleFederation.CallbackPath], confidential: true, postLogoutRedirectUris: [origin + "/"]);
            return (made.Client, made.Secret);
        }
        serverA = await PairableServer.StartAsync(instanceId: ServerAId);
        serverB = await PairableServer.StartAsync(instanceId: ServerBId);
        appA = await StartConsole("a", portA, idA.ClientId, secretA!, "console-a", clockA);
        appB = await StartConsole("b", portB, idB.ClientId, secretB!, "console-b", clockB);
        consoleA = ConsoleClient(portA);
        consoleB = ConsoleClient(portB);
        await appA.Services.GetRequiredService<ConsoleInstances>().PairAsync(serverA.Url, serverA.NewCodeWith(ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver), "Server A", default);
        // the second console may grant reading only on its server, whatever the home says
        await appB.Services.GetRequiredService<ConsoleInstances>().PairAsync(serverB.Url, serverB.NewCodeWith(ServerRole.Viewer), "Server B", default);

        ada = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer);
        bob = users.CreateUser("Bob", null, UserRole.Viewer);
        var adaSubject = AssignmentSubject.User(ada.Id);
        directory.AssignApp(adaSubject, "console-a", ServerRole.Admin);
        directory.AssignApp(adaSubject, "console-b", ServerRole.Viewer);
        directory.Assign(adaSubject, ServerAId, ServerRole.Admin | ServerRole.Operator);
        directory.Assign(adaSubject, ServerBId, ServerRole.Admin);       // the home says admin; console B's ceiling says viewer
        // Bob has an account at the home and no role on either console
    }

    private async Task<WebApplication> StartConsole(string name, int port, string clientId, string secret, string appId, TimeProvider clock)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var listen = new Uri($"https://localhost:{port}");
        var app = ConsoleHost.Build(new ConsoleSettings(directory, listen, Home: new IdentityHomeSettings(new Uri(homeUrl), clientId, secret, appId)), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services => services.AddSingleton(clock));
        await app.StartAsync();
        started.Add(app);
        return app;
    }

    private static HttpClient ConsoleClient(int port) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri($"https://localhost:{port}") };

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Enumerable.Reverse(started)) await s.DisposeAsync();
        await serverA.DisposeAsync();
        await serverB.DisposeAsync();
        consoleA.Dispose(); consoleB.Dispose(); home.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    /// <summary>The browser's journey: console to home to console, the person being signed in at the home. Returns the console's session cookie, or the refusal.</summary>
    private async Task<(string? Session, HttpStatusCode Status)> SignIn(HttpClient console, string homeUser)
    {
        var start = await console.GetAsync(ConsoleFederation.SignInPath + "?returnUrl=/users");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        authorize.Headers.Add("X-Signed-In-User", homeUser);
        var back = await home.SendAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var callback = await console.GetAsync(back.Headers.Location);
        if (callback.StatusCode != HttpStatusCode.Redirect) return (null, callback.StatusCode);
        Assert.Equal("/users", callback.Headers.Location!.ToString());
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(ConsoleHost.Auth.SessionCookie + "=", StringComparison.Ordinal));
        return (cookie[(ConsoleHost.Auth.SessionCookie.Length + 1)..].Split(';')[0], callback.StatusCode);
    }

    private static async Task<HttpStatusCode> EchoStatus(HttpClient console, string session, string serverId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/relay/{serverId}/api/echo");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return (await console.SendAsync(request)).StatusCode;
    }

    private static async Task<HttpResponseMessage> SignOut(HttpClient console, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-out");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return await console.SendAsync(request);
    }

    private static readonly TimeSpan PastTheInterval = FederationPolicy.DefaultRefreshInterval + TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PastTheGrace = FederationPolicy.DefaultRefreshInterval + FederationPolicy.DefaultGrace + TimeSpan.FromSeconds(5);

    private static async Task<JsonNode> Echo(HttpClient console, string session, string serverId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/relay/{serverId}/api/echo");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await console.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static string[] Roles(JsonNode echo) => [.. echo["roles"]!.AsArray().Select(r => (string)r!)];

    [Fact]
    public async Task One_person_signs_in_to_two_consoles_and_is_the_same_user_in_both_servers_audit()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        var (sessionB, _) = await SignIn(consoleB, ada.Id);
        Assert.NotNull(sessionA);
        Assert.NotNull(sessionB);

        var seenByA = await Echo(consoleA, sessionA!, ServerAId);
        var seenByB = await Echo(consoleB, sessionB!, ServerBId);
        Assert.Equal(ada.Id, (string)seenByA["subject"]!);          // the home's id, in both
        Assert.Equal(ada.Id, (string)seenByB["subject"]!);
        Assert.Equal("Ada Lovelace", (string)seenByA["name"]!);

        // each console holds her under the home's id, with no credential of its own
        var mirrored = appA.Services.GetRequiredService<UserStore>().Get(ada.Id);
        Assert.Equal("Ada Lovelace", mirrored!.DisplayName);
        Assert.Empty(appA.Services.GetRequiredService<UserStore>().CredentialsOf(ada.Id));
    }

    [Fact]
    public async Task The_roles_are_the_homes_per_server_and_the_second_consoles_ceiling_still_applies()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        var (sessionB, _) = await SignIn(consoleB, ada.Id);

        var onA = Roles(await Echo(consoleA, sessionA!, ServerAId));
        Assert.Contains("admin", onA);
        Assert.Contains("operator", onA);
        Assert.DoesNotContain("approver", onA);          // the home did not give it, whatever an administrator here would have had

        // the home says admin on server B; the second console may grant only viewer there
        Assert.Equal(["viewer"], Roles(await Echo(consoleB, sessionB!, ServerBId)));
    }

    [Fact]
    public async Task Someone_the_home_gives_no_role_on_the_console_does_not_get_in()
    {
        var (session, status) = await SignIn(consoleA, bob.Id);
        Assert.Null(session);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Null(appA.Services.GetRequiredService<UserStore>().Get(bob.Id));
    }

    [Fact]
    public async Task A_console_that_signs_in_through_a_home_keeps_no_first_run_invitation_and_no_users_of_its_own()
    {
        Assert.False(File.Exists(Path.Combine(root, "a", "FIRST-RUN.txt")));
        Assert.False(appA.Services.GetRequiredService<UserStore>().Any());
        Assert.Equal(HttpStatusCode.Unauthorized, (await consoleA.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task A_callback_that_was_not_started_here_signs_nobody_in_and_a_sign_in_state_works_once()
    {
        var forged = await consoleA.GetAsync(ConsoleFederation.CallbackPath + "?state=made-up&code=x");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var start = await consoleA.GetAsync(ConsoleFederation.SignInPath);
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        authorize.Headers.Add("X-Signed-In-User", ada.Id);
        var back = (await home.SendAsync(authorize)).Headers.Location!;
        Assert.Equal(HttpStatusCode.Redirect, (await consoleA.GetAsync(back)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await consoleA.GetAsync(back)).StatusCode);       // replaying the redirect
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example")]
    public async Task Sign_in_returns_only_to_a_path_of_this_console(string returnUrl)
    {
        var start = await consoleA.GetAsync(ConsoleFederation.SignInPath + "?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        authorize.Headers.Add("X-Signed-In-User", ada.Id);
        var back = (await home.SendAsync(authorize)).Headers.Location!;
        Assert.Equal("/", (await consoleA.GetAsync(back)).Headers.Location!.ToString());
    }

    [Fact]
    public async Task Signing_out_of_the_console_ends_the_homes_session()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        var (sessionB, _) = await SignIn(consoleB, ada.Id);
        Assert.Equal(HttpStatusCode.OK, await EchoStatus(consoleA, sessionA!, ServerAId));

        var signOut = await SignOut(consoleA, sessionA!);
        Assert.Equal(HttpStatusCode.OK, signOut.StatusCode);
        var redirectTo = new Uri((string)JsonNode.Parse(await signOut.Content.ReadAsStringAsync())!["redirectTo"]!);

        // the console's own session is over at once
        Assert.Equal(HttpStatusCode.Unauthorized, await EchoStatus(consoleA, sessionA!, ServerAId));
        Assert.Null(appA.Services.GetRequiredService<SessionStore>().Resolve(sessionA!, appA.Services.GetRequiredService<UserStore>()));

        // and the browser is sent to the home's end-session endpoint, with the id token that says whose session and which client
        Assert.Equal(homeUrl, redirectTo.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/identity/end-session", redirectTo.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(redirectTo.Query);
        Assert.False(string.IsNullOrEmpty(query["id_token_hint"]));
        Assert.StartsWith("https://localhost:", query["post_logout_redirect_uri"]);

        var visit = new HttpRequestMessage(HttpMethod.Get, redirectTo);
        visit.Headers.Add("X-Signed-In-User", ada.Id);
        var atHome = await home.SendAsync(visit);
        Assert.Equal(new[] { ada.Id }, endedAtHome);                                         // the home's session was ended
        Assert.Equal(HttpStatusCode.Redirect, atHome.StatusCode);
        Assert.Equal(query["post_logout_redirect_uri"], atHome.Headers.Location!.ToString());  // and the person is brought back to the console

        // what the home ended was the person's sign-in, so the other console's session of hers ends at its next refresh, and her refresh there is refused
        Assert.Equal(HttpStatusCode.OK, await EchoStatus(consoleB, sessionB!, ServerBId));
        clockB.Offset = PastTheInterval;
        Assert.Equal(HttpStatusCode.Unauthorized, await EchoStatus(consoleB, sessionB!, ServerBId));
    }

    [Fact]
    public async Task A_role_removed_at_the_home_applies_within_the_refresh_interval_without_a_new_sign_in()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        Assert.Contains("operator", Roles(await Echo(consoleA, sessionA!, ServerAId)));

        directory.Assign(AssignmentSubject.User(ada.Id), ServerAId, ServerRole.Viewer);          // the operator and admin roles are taken away at the home
        // inside the interval the console still goes by what the home said at the last answer
        Assert.Contains("operator", Roles(await Echo(consoleA, sessionA!, ServerAId)));

        clockA.Offset = PastTheInterval;
        Assert.Equal(["viewer"], Roles(await Echo(consoleA, sessionA!, ServerAId)));              // the same session, no new sign-in
        Assert.Equal(HttpStatusCode.OK, await EchoStatus(consoleA, sessionA!, ServerAId));
    }

    [Fact]
    public async Task A_refresh_lowers_the_homes_roles_by_the_consoles_ceiling_as_a_sign_in_does()
    {
        var (sessionB, _) = await SignIn(consoleB, ada.Id);
        Assert.Equal(["viewer"], Roles(await Echo(consoleB, sessionB!, ServerBId)));

        directory.Assign(AssignmentSubject.User(ada.Id), ServerBId, ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver);
        clockB.Offset = PastTheInterval;
        Assert.Equal(["viewer"], Roles(await Echo(consoleB, sessionB!, ServerBId)));              // refreshed, and still the ceiling's
        var held = appB.Services.GetRequiredService<FederatedUsers>().RolesOn(ada.Id, ServerBId)!.Roles;     // the copy is the home's word; the ceiling is applied when the token is made
        Assert.True(ServerRoles.Allows(held, ServerRole.Approver));
    }

    [Fact]
    public async Task Requests_that_arrive_together_when_a_refresh_is_due_share_one_refresh_and_none_ends_the_session()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        clockA.Offset = PastTheInterval;
        // a refresh token works once: two requests each using it would end the person's sign-in at the home
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => EchoStatus(consoleA, sessionA!, ServerAId)));
        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        clockA.Offset = PastTheInterval + PastTheInterval;
        Assert.Equal(HttpStatusCode.OK, await EchoStatus(consoleA, sessionA!, ServerAId));       // and the next token in the chain is the one that was kept
    }

    [Fact]
    public async Task A_person_the_home_no_longer_gives_a_role_on_the_console_is_signed_out_at_the_next_refresh()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        directory.AssignApp(AssignmentSubject.User(ada.Id), "console-a", ServerRole.None);
        Assert.Equal(HttpStatusCode.OK, await EchoStatus(consoleA, sessionA!, ServerAId));       // not before the interval
        clockA.Offset = PastTheInterval;
        Assert.Equal(HttpStatusCode.Unauthorized, await EchoStatus(consoleA, sessionA!, ServerAId));
        Assert.Null(appA.Services.GetRequiredService<FederatedUsers>().RolesOn(ada.Id, ServerAId));   // nothing of the home's word is kept
    }

    [Fact]
    public async Task A_refresh_that_would_take_an_address_another_account_holds_ends_that_session_and_keeps_none_of_the_homes_word()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        // another account on this console holds an address the home now gives Ada, verified (plan 13a U16)
        appA.Services.GetRequiredService<UserStore>().CreateUser("Local Lovelace", "ada.work@example.invalid", UserRole.Viewer, emailVerified: true);
        new UserStore(new StateDatabase(Path.Combine(root, "home.db"), HomeSchema.Schema)).SetVerifiedEmail(ada.Id, "ada.work@example.invalid");
        Assert.Equal(HttpStatusCode.OK, await EchoStatus(consoleA, sessionA!, ServerAId));       // not before the interval
        clockA.Offset = PastTheInterval;
        Assert.Equal(HttpStatusCode.Unauthorized, await EchoStatus(consoleA, sessionA!, ServerAId));
        Assert.Null(appA.Services.GetRequiredService<FederatedUsers>().RolesOn(ada.Id, ServerAId));
    }

    [Fact]
    public async Task A_home_that_cannot_be_reached_keeps_the_last_roles_for_the_grace_and_then_none()
    {
        var (sessionA, _) = await SignIn(consoleA, ada.Id);
        await homeApp.StopAsync();

        // past the interval the console asks, cannot, and goes on with the last answer for the grace
        clockA.Offset = PastTheInterval;
        Assert.Contains("operator", Roles(await Echo(consoleA, sessionA!, ServerAId)));

        // past the interval and the grace the token carries no role (a server's own authentication refuses a token that holds none: "the token holds no role here"), and the person's sign-in to the console itself is not ended
        clockA.Offset = PastTheGrace;
        Assert.Empty(Roles(await Echo(consoleA, sessionA!, ServerAId)));
        var status = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        status.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={sessionA}");
        Assert.True((bool)JsonNode.Parse(await (await consoleA.SendAsync(status)).Content.ReadAsStringAsync())!["authenticated"]!);
        Assert.Equal(ServerRole.None, appA.Services.GetRequiredService<FederatedUsers>().RolesOn(ada.Id, ServerAId)!.Roles);
    }

    [Fact]
    public async Task A_local_user_is_kept_and_neither_the_refresh_nor_the_sign_out_involves_the_home()
    {
        var local = appA.Services.GetRequiredService<UserStore>().CreateUser("Local Admin", "local@example.invalid", UserRole.Admin);
        var session = appA.Services.GetRequiredService<SessionStore>().Create(local.Id).Id;
        clockA.Offset = PastTheGrace;
        await homeApp.StopAsync();
        var users = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        users.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        Assert.Equal(HttpStatusCode.OK, (await consoleA.SendAsync(users)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SignOut(consoleA, session)).StatusCode);
    }
}
