using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Hosting;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Tests.Auth;

/// <summary>
/// Plan 26 AP29: the apps host's sessions in the group's shared state. Two instances are two <see cref="AppSessions"/> over their own connections to one database: a person who signed in at A is signed in at B, the home's
/// refresh token is in the database only encrypted, a refresh made at one instance is the other's, and ending a session ends it for the group. On SQLite, and on PostgreSQL and SQL Server when the shared engines are up.
/// </summary>
public sealed class AppSessionStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dbgateway-appsessions-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly StateVerbDatabases databases = new();

    public void Dispose()
    {
        databases.Dispose();
        Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DbDataGateway.Tests.Config.TestFolder.Delete(directory);
    }

    private sealed class StaticMonitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly HomeIdentity Ada = new("user-7", "Ada", "ada@example.invalid", ["analysts"], new Dictionary<string, IReadOnlyList<string>> { ["app:acme"] = ["author", "viewer"] })
    {
        Attributes = new Dictionary<string, IReadOnlyList<string>> { ["region"] = ["north"] },
    };

    /// <summary>One instance of the group: its own state connection and group key (the same secret store and state, as a group has).</summary>
    private (AppSessionStore Store, StateDatabase State) Instance(string engine, string? connection)
    {
        var options = new GatewayOptions { DataDirectory = directory };
        var monitor = new StaticMonitor(options);
        var state = GatewayState.Open(directory, engine == "sqlite" ? null : new StateSettings(engine, connection!), text => text.Replace($"env:{StateVerbDatabases.PasswordVariable}", databases.Password, StringComparison.Ordinal));
        return (new AppSessionStore(state, new GroupSessionKey(state, monitor)), state);
    }

    [Theory]
    [MemberData(nameof(StateVerbDatabases.Engines), MemberType = typeof(StateVerbDatabases))]
    public async Task A_session_made_at_one_instance_is_accepted_at_another_and_ends_for_the_group(string engine)
    {
        string? connection = null;
        if (engine != "sqlite")
        {
            connection = databases.Provision(engine);
            Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, databases.Password);
        }
        var (storeA, _) = Instance(engine, connection);
        var (storeB, stateB) = Instance(engine, connection);
        var clock = new Clock();
        var refreshed = 0;
        var home = new HomeIdentity("user-7", "Ada", null, [], new Dictionary<string, IReadOnlyList<string>> { ["app:acme"] = ["viewer"] });
        Task<(HomeIdentity, HomeGrant)> Refresh(string token, CancellationToken ct)
        {
            refreshed++;
            return Task.FromResult((home, new HomeGrant("refresh-" + refreshed, null)));
        }
        var a = new AppSessions(clock, Refresh, TimeSpan.FromMinutes(5), storeA);
        var b = new AppSessions(clock, Refresh, TimeSpan.FromMinutes(5), storeB);

        var made = a.Create(Ada, "refresh-0");

        // B has never seen the cookie's id and finds the person with their roles, groups and attributes as the home gave them
        Assert.Null(b.Find(made.Id));   // the memory of B is empty: only a lookup that may ask the state finds it
        var atB = await b.FindAsync(made.Id, default);
        Assert.NotNull(atB);
        Assert.Equal(("user-7", "Ada", "ada@example.invalid"), (atB.Identity.UserId, atB.Identity.Name, atB.Identity.Email));
        Assert.Equal(["author", "viewer"], atB.RolesOn("acme"));
        Assert.Equal(["analysts"], atB.Identity.Groups);
        Assert.Equal(["north"], atB.Identity.Attributes["region"]);
        Assert.Equal(made.ExpiresAtUtc, atB.ExpiresAtUtc);
        Assert.Null(await b.FindAsync("not-a-session", default));

        // the database holds the hash of the id and the refresh token only encrypted
        using (var held = stateB.OpenConnection())
        {
            using var read = stateB.Command("serving", held, "SELECT session_hash, refresh_token FROM {table:app_session};");
            using var reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.DoesNotContain(made.Id, reader.GetString(0), StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-0", reader.GetString(1), StringComparison.Ordinal);
        }

        // the home is asked once, by whichever instance finds the answer stale, and the other takes that answer
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(["viewer"], (await b.FindAsync(made.Id, default))!.RolesOn("acme"));
        Assert.Equal(1, refreshed);
        Assert.Equal(["author", "viewer"], a.Find(made.Id)!.RolesOn("acme"));   // A still holds its own copy until it looks again
        Assert.Equal(["viewer"], (await a.FindAsync(made.Id, default))!.RolesOn("acme"));
        Assert.Equal(1, refreshed);   // A did not use the refresh token B had already used

        // ending it at one instance ends it at the other, and a session that ran out is nobody's
        a.End(made.Id);
        Assert.NotNull(b.Find(made.Id));   // B's memory has it until it looks at the state again, which it does within StoreRecheck
        clock.Now += AppSessions.StoreRecheck + TimeSpan.FromSeconds(1);
        Assert.Null(await b.FindAsync(made.Id, default));
        Assert.Null(b.Find(made.Id));
        var other = a.Create(Ada);
        clock.Now += AppSessions.Lifetime + TimeSpan.FromMinutes(1);
        Assert.Null(await b.FindAsync(other.Id, default));
        a.Create(Ada);   // a new session removes the ones that ended
        using var left = stateB.OpenConnection();
        using var count = stateB.Command("serving", left, "SELECT COUNT(*) FROM {table:app_session};");
        Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public async Task A_row_made_under_another_groups_key_is_no_session()
    {
        var (storeA, stateA) = Instance("sqlite", null);
        var sessions = new AppSessions(store: storeA);
        var made = sessions.Create(Ada, "refresh-0");

        // a second group's state with its own key cannot read the first's refresh token
        var otherDirectory = Path.Combine(directory, "other");
        var options = new GatewayOptions { DataDirectory = otherDirectory };
        var otherState = GatewayState.Open(otherDirectory);
        var otherStore = new AppSessionStore(otherState, new GroupSessionKey(otherState, new StaticMonitor(options)));
        using (var connection = stateA.OpenConnection())
        {
            using var read = stateA.Command("serving", connection, "SELECT session_hash, person, refresh_token, expires_at, asked_at FROM {table:app_session};");
            using var reader = read.ExecuteReader();
            Assert.True(reader.Read());
            using var copy = otherState.OpenConnection();
            using var insert = otherState.Command("serving", copy, "INSERT INTO {table:app_session} (session_hash, person, refresh_token, expires_at, asked_at) VALUES ($h, $i, $t, $e, $a);");
            insert.Bind(otherState, "h", reader.GetString(0)).Bind(otherState, "i", reader.GetString(1)).Bind(otherState, "t", reader.GetString(2)).Bind(otherState, "e", reader.Time(3)).Bind(otherState, "a", reader.Time(4));
            insert.ExecuteNonQuery();
        }

        Assert.Null(otherStore.Find(made.Id));
        await Task.CompletedTask;
    }
}
