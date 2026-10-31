using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>
/// Plan 13a U16, the done-when: one person who signs in with a local passkey user, a Windows account and through the identity home is one user id in the audit (what the managed server receives as the token's subject), the
/// same roles, whatever the case of the address. The Windows sign-in is a fake: the identity the Windows handler would produce, given to the code that maps it to a user (there is no domain on the machine these run on).
/// </summary>
public sealed class ConsoleOneAccountTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-one-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private HttpClient home = null!, console = null!;
    private WebApplication app = null!;
    private PairableServer server = null!;
    private UserStore homeUsers = null!;
    private HomeDirectory directory = null!;
    private UserStore users = null!;
    private SessionStore sessions = null!;
    private string homeUrl = null!;

    private const string ServerId = "server-a";

    private static int FreePort() => TestPorts.Next();

    private sealed class Mask : ISigningKeyProtector
    {
        public byte[] Protect(byte[] k) => k.Select(b => (byte)(b ^ 0x33)).ToArray();
        public byte[] Unprotect(byte[] k) => Protect(k);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(root);
        var homePort = FreePort();
        homeUrl = $"http://127.0.0.1:{homePort}";
        var db = new StateDatabase(Path.Combine(root, "home.db"), HomeSchema.Schema);
        homeUsers = new UserStore(db);
        directory = new HomeDirectory(db);
        var clients = new OidcClients(db);
        var keys = new SigningKeys(db, new Mask());
        var provider = new OpenIdProvider(db, homeUsers, directory, clients, keys, homeUrl);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(homeUrl);
        var homeApp = builder.Build();
        homeApp.MapOpenIdProvider(provider, keys, new OpenIdHostOptions(c => c.Request.Headers["X-Signed-In-User"].FirstOrDefault()));
        await homeApp.StartAsync();
        started.Add(homeApp);
        home = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        var port = FreePort();
        var made = clients.Register("Console", [$"https://localhost:{port}" + ConsoleFederation.CallbackPath], confidential: true);
        server = await PairableServer.StartAsync(instanceId: ServerId);
        var consoleDirectory = Path.Combine(root, "console");
        Directory.CreateDirectory(consoleDirectory);
        app = ConsoleHost.Build(
            new ConsoleSettings(consoleDirectory, new Uri($"https://localhost:{port}"), Home: new IdentityHomeSettings(new Uri(homeUrl), made.Client.ClientId, made.Secret, "console-a")),
            ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await app.StartAsync();
        started.Add(app);
        console = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri($"https://localhost:{port}") };
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCodeWith(ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver), "Server A", default);
        users = app.Services.GetRequiredService<UserStore>();
        sessions = app.Services.GetRequiredService<SessionStore>();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Enumerable.Reverse(started)) await s.DisposeAsync();
        await server.DisposeAsync();
        console.Dispose(); home.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    /// <summary>What the managed server receives for a session: the token's subject and name, which is what its audit records.</summary>
    private async Task<(string Subject, string[] Roles)> AuditOf(string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/relay/{ServerId}/api/echo");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={sessionId}");
        var response = await console.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return ((string)echo["subject"]!, [.. echo["roles"]!.AsArray().Select(r => (string)r!)]);
    }

    private async Task<(string? Session, HttpStatusCode Status, string Body)> SignInThroughHome(string homeUser)
    {
        var start = await console.GetAsync(ConsoleFederation.SignInPath);
        var authorize = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location);
        authorize.Headers.Add("X-Signed-In-User", homeUser);
        var back = await home.SendAsync(authorize);
        var callback = await console.GetAsync(back.Headers.Location);
        if (callback.StatusCode != HttpStatusCode.Redirect) return (null, callback.StatusCode, await callback.Content.ReadAsStringAsync());
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(ConsoleHost.Auth.SessionCookie + "=", StringComparison.Ordinal));
        return (cookie[(ConsoleHost.Auth.SessionCookie.Length + 1)..].Split(';')[0], callback.StatusCode, "");
    }

    private UserRecord HomePerson(string name, string email, bool verified)
    {
        var person = homeUsers.CreateUser(name, email, UserRole.Viewer, emailVerified: verified);
        directory.AssignApp(AssignmentSubject.User(person.Id), "console-a", ServerRole.Admin);
        directory.Assign(AssignmentSubject.User(person.Id), ServerId, ServerRole.Admin | ServerRole.Operator);
        return person;
    }

    private static WindowsIdentityResult Windows(string sid, string sam, string? upn, UserRole? role = UserRole.Admin) => new(sid, sam, role, upn);

    [Fact]
    public async Task One_person_by_a_local_passkey_user_a_windows_account_and_the_home_is_one_user_id_in_the_audit()
    {
        // the administrator invited Ada at Ada@Example.com and she made a passkey: a local user (the invitation's code path creates exactly this)
        var local = users.CreateUser("Ada Lovelace", "Ada@Example.com", UserRole.Admin, emailVerified: true);
        users.AddCredential(local.Id, CredentialMethods.Passkey, "ada-passkey", "public-key", label: "Passkey");
        var passkeySession = sessions.Create(users.FindByCredential(CredentialMethods.Passkey, "ada-passkey")!.Id);
        var byPasskey = await AuditOf(passkeySession.Id);

        // the domain says her user principal name is ADA@EXAMPLE.COM: the same person, in any case
        var viaWindows = WindowsSignIn.Upsert(users, Windows("S-1-5-21-100-1", "CORP\\ada", "ADA@EXAMPLE.COM"), UserRole.Admin);
        var byWindows = await AuditOf(sessions.Create(viaWindows.Id).Id);

        Assert.Equal(local.Id, viaWindows.Id);
        Assert.Equal(byPasskey.Subject, byWindows.Subject);         // the same id and the same roles at the server
        Assert.Equal(byPasskey.Roles, byWindows.Roles);
        Assert.Equal(local.Id, byPasskey.Subject);

        // the identity home knows her as home-ada, with the address ada@example.com, which it verified
        var person = HomePerson("Ada Lovelace", "ada@EXAMPLE.com", verified: true);
        var (homeSession, status, _) = await SignInThroughHome(person.Id);
        Assert.Equal(HttpStatusCode.Redirect, status);

        // one user: the account took the home's id, and the passkey and the Windows account lead to it
        var byHome = await AuditOf(homeSession!);
        Assert.Equal(person.Id, byHome.Subject);
        Assert.Single(users.List());
        Assert.Equal(person.Id, users.FindByCredential(CredentialMethods.Passkey, "ada-passkey")!.Id);
        Assert.Equal(person.Id, WindowsSignIn.Upsert(users, Windows("S-1-5-21-100-1", "CORP\\ada", "ada@example.com"), UserRole.Admin).Id);
        Assert.Equal(person.Id, (await AuditOf(passkeySession.Id)).Subject);             // the session she already had is the same user
        var afterwards = await AuditOf(sessions.Create(users.FindByCredential(CredentialMethods.Passkey, "ada-passkey")!.Id).Id);
        Assert.Equal(byHome.Subject, afterwards.Subject);
        Assert.Equal(byHome.Roles, afterwards.Roles);                // and the same roles: the home's word now applies to every way in
        Assert.DoesNotContain("approver", afterwards.Roles);         // what the console granted a local administrator is not what the home gives; now it is the home's

        // and the home's sign-in again is that same account, not a second one
        Assert.Equal(person.Id, (await AuditOf((await SignInThroughHome(person.Id)).Session!)).Subject);
        Assert.Single(users.List());
    }

    [Fact]
    public async Task An_address_the_home_did_not_verify_links_nothing_even_where_a_windows_account_holds_it()
    {
        var viaWindows = WindowsSignIn.Upsert(users, Windows("S-1-5-21-100-2", "CORP\\grace", "grace@example.com"), UserRole.Viewer);

        // an address the home does not vouch for links nothing: Grace at the home is a separate account here
        var unverified = HomePerson("Grace", "grace@example.com", verified: false);
        var (session, _, _) = await SignInThroughHome(unverified.Id);
        Assert.Equal(unverified.Id, (await AuditOf(session!)).Subject);
        Assert.Equal(2, users.List().Count);
        Assert.NotNull(users.Get(viaWindows.Id));
    }

    [Fact]
    public async Task A_sign_in_that_would_take_an_address_held_by_a_mirror_of_another_home_person_is_refused_with_a_message_and_signs_nobody_in()
    {
        var first = HomePerson("Grace One", "grace@example.com", verified: true);
        Assert.NotNull((await SignInThroughHome(first.Id)).Session);
        Assert.Single(users.List());

        // the home gives the same address to a second person (its own unique key is bypassed here by writing the row as a stale one would be)
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "home.db")};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Users SET EmailKey = NULL WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", first.Id);
            cmd.ExecuteNonQuery();
        }
        var second = HomePerson("Grace Two", "Grace@Example.com", verified: true);
        var (session, status, body) = await SignInThroughHome(second.Id);

        Assert.Null(session);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("already used by another account", body);
        Assert.Null(users.Get(second.Id));
        Assert.Single(users.List());
    }

    [Fact]
    public void A_windows_account_with_no_upn_is_refused_with_a_message_and_a_sam_name_is_never_a_key()
    {
        var refused = Assert.Throws<AccountConflictException>(() => WindowsSignIn.Upsert(users, Windows("S-1-5-21-100-3", "CORP\\ada", upn: null), UserRole.Admin));
        Assert.Contains("no user principal name", refused.Message);
        Assert.Throws<AccountConflictException>(() => WindowsSignIn.Upsert(users, Windows("S-1-5-21-100-3", "CORP\\ada", upn: "CORP\\ada"), UserRole.Admin));
        Assert.Empty(users.List());

        // the same person by UPN is an account, and the SAM name alone afterwards (the claim missing once) is still that account, by its SID
        var ada = WindowsSignIn.Upsert(users, Windows("S-1-5-21-100-4", "CORP\\ada", "ada@corp.example"), UserRole.Admin);
        Assert.Equal(ada.Id, WindowsSignIn.Upsert(users, Windows("S-1-5-21-100-4", "CORP\\ada", upn: null), UserRole.Admin).Id);
        Assert.Single(users.List());
    }
}
