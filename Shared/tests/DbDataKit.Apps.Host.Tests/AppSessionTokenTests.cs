using System.Text;
using DbDataKit.Identity;
using DbDataKit.Identity.Tests;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Apps.Tests;

/// <summary>
/// Plan 26 AP19: the shell's token is the identity home's. The host's <see cref="OpenIdClient"/> signs a person in and trades the session's refresh token for the instance's access token; <see cref="AppDataGateway"/> checks it against the
/// keys the home publishes. The home is real (the provider, keys and clients of the identity tests, behind <see cref="HomeHandler"/>); the host holds no signing key.
/// </summary>
public sealed class AppSessionTokenTests : IDisposable
{
    private const string Redirect = "https://apps.example.invalid/apps/-/callback";
    private const string Issuer = "https://home.example.invalid";

    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-session-token-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore instances;
    private readonly TestHome home;
    private readonly HomeHandler handler;
    private readonly OpenIdClient client;
    private readonly UserRecord ada;
    private readonly Recorder data = new();

    public AppSessionTokenTests()
    {
        Directory.CreateDirectory(folder);
        var definitions = new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module]));
        instances = new AppInstanceStore(definitions);
        instances.PublishApp("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write]}\n", "ada");
        instances.Save(new AppInstance("acme", "notes"), "ada");
        home = new TestHome(new AppInstanceGrants(instances, new AppApprovalStore(definitions)), issuer: Issuer);
        home.Instance("acme");
        ada = home.Person("Ada", ("acme", ServerRole.Author));
        handler = new HomeHandler(home.Provider, home.Keys);
        client = new OpenIdClient(new OpenIdClientOptions(Issuer, home.Host.ClientId, home.HostSecret, Redirect), new HttpClient(handler));
    }

    public void Dispose()
    {
        home.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private sealed class Recorder : IAppData
    {
        public List<AppDataCall> Seen { get; } = [];

        public Task<AppResponse> SendAsync(AppDataCall call, CancellationToken cancellationToken)
        {
            Seen.Add(call);
            return Task.FromResult(new AppResponse(200, "application/json", new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{\"ok\":true}")));
        }
    }

    private AppSessions Sessions(IAppSessionStore? store = null) => new(refresh: client.RefreshAsync, store: store, exchange: client.ExchangeForAppAsync);

    private static HomeIdentity IdentityOf(UserRecord user) => new(user.Id, user.DisplayName, null, [], new Dictionary<string, IReadOnlyList<string>> { ["app:acme"] = ["author"] });

    private AppSession SignedIn(AppSessions sessions) => sessions.Create(IdentityOf(ada), home.SignIn(ada));

    [Fact]
    public async Task The_shells_token_is_the_homes_for_this_instance_and_the_data_gateway_accepts_it_by_the_homes_published_keys()
    {
        var sessions = Sessions();
        var outcome = await sessions.TokenAsync(SignedIn(sessions), "acme", default);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(("app:acme", 600), (outcome.Token!.Audience, outcome.Token.ExpiresInSeconds));
        Assert.Equal("app:acme", home.Provider.Verify(outcome.Token.Token, "at+jwt")!["aud"]);   // signed by the home, with its key
        Assert.Contains(OpenIdProvider.TokenExchangeGrant, handler.Grants);

        // the host's data route checks it against the keys the home publishes (it asks for them once) and has no key of its own
        var jwks = new JwksTokenVerifier(Issuer, new HttpClient(handler));
        var gateway = new AppDataGateway(instances, data, new AppTokenValidator(jwks), jwks);
        var answer = await gateway.ForwardAsync("acme", "api/v1/models/territories/entities/territory/rows", "GET", "", "Bearer " + outcome.Token.Token, [], default);
        Assert.Equal(200, answer.Status);
        var call = Assert.Single(data.Seen);
        Assert.Equal((ada.Id, "Ada", ServerRole.Author), (call.SubjectId, call.SubjectName, call.Roles & ServerRole.Author));
        Assert.Equal(["territories/territory:write"], call.Scope);
        Assert.Equal(401, (await gateway.ForwardAsync("acme", "api/v1/models/territories/entities/territory/rows", "GET", "", "Bearer " + home.Token(ada, "acme").Replace('a', 'b'), [], default)).Status);
    }

    [Fact]
    public async Task A_roles_taken_away_at_the_home_stop_the_next_token_and_a_person_the_home_disabled_is_signed_out()
    {
        var sessions = Sessions();
        var session = SignedIn(sessions);
        Assert.Equal(200, (await sessions.TokenAsync(session, "acme", default)).Status);

        var cy = home.Person("Cy");
        var noRole = await sessions.TokenAsync(sessions.Create(IdentityOf(cy), home.SignIn(cy)), "acme", default);
        Assert.Equal((403, null), (noRole.Status, noRole.Token));
        Assert.Contains("no role", noRole.Message);

        home.Disable(ada);
        var ended = await sessions.TokenAsync(session, "acme", default);
        Assert.Equal(401, ended.Status);
        Assert.Null(sessions.Find(session.Id));                 // the home said the sign-in is over: so is the session
    }

    [Fact]
    public async Task A_home_that_does_not_let_the_host_exchange_or_cannot_be_reached_says_so_and_a_session_with_no_refresh_token_signs_in_again()
    {
        var sessions = Sessions();
        var session = SignedIn(sessions);

        handler.Unreachable = true;
        var down = await sessions.TokenAsync(session, "acme", default);
        Assert.Equal(503, down.Status);
        Assert.NotNull(sessions.Find(session.Id));              // a home that is down does not end the session
        handler.Unreachable = false;

        using var closed = new TestHome(new AppInstanceGrants(instances), hostMayExchange: false, issuer: Issuer);
        closed.Instance("acme");
        var person = closed.Person("Ada", ("acme", ServerRole.Author));
        var other = new OpenIdClient(new OpenIdClientOptions(Issuer, closed.Host.ClientId, closed.HostSecret, Redirect), new HttpClient(new HomeHandler(closed.Provider, closed.Keys)));
        var refusing = new AppSessions(exchange: other.ExchangeForAppAsync);
        var refused = await refusing.TokenAsync(refusing.Create(IdentityOf(person), closed.SignIn(person)), "acme", default);
        Assert.Equal(503, refused.Status);
        Assert.Contains("unauthorized_client", refused.Message);
        Assert.Contains("exchange tokens", refused.Message);

        Assert.Equal(401, (await sessions.TokenAsync(sessions.Create(IdentityOf(ada)), "acme", default)).Status);   // no refresh token to trade
        Assert.Equal(503, (await new AppSessions().TokenAsync(session, "acme", default)).Status);                 // a host with no exchange configured
    }

    [Fact]
    public async Task A_refresh_token_another_instance_of_the_group_has_replaced_is_found_in_the_store_and_the_exchange_goes_on_with_it()
    {
        var store = new MemoryStore();
        var here = Sessions(store);
        var session = SignedIn(here);
        // another instance of the group asked the home with the token and holds the next one (the store is the truth)
        var theirs = Sessions(store);
        var refreshed = await client.RefreshAsync(session.RefreshTokenForTest(), default);
        var stored = store.Find(session.Id)!;
        store.Save(stored with { RefreshToken = refreshed.Grant.RefreshToken, AskedAt = stored.AskedAt.AddSeconds(5) });

        var outcome = await here.TokenAsync(session, "acme", default);
        Assert.Equal(200, outcome.Status);                      // the old token was refused once, the stored one worked
        Assert.NotNull(theirs.Find(session.Id) ?? here.Find(session.Id));
    }

    private sealed class MemoryStore : IAppSessionStore
    {
        private readonly Dictionary<string, StoredAppSession> rows = [];

        public void Save(StoredAppSession session) => rows[session.Id] = session;

        public StoredAppSession? Find(string id) => rows.GetValueOrDefault(id);

        public void Delete(string id) => rows.Remove(id);

        public void DeleteExpired(DateTimeOffset now) { }
    }
}

internal static class AppSessionTestAccess
{
    /// <summary>The refresh token the session holds (internal to the host; the tests see it through <c>InternalsVisibleTo</c>).</summary>
    public static string RefreshTokenForTest(this AppSession session) => (string)typeof(AppSession).GetProperty("RefreshToken", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
}
