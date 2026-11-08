using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DbDataGateway.Tests.Auth;

public class PasswordHasherTests
{
    [Fact]
    public void Verifies_correct_password_and_rejects_wrong_one()
    {
        string hash = PasswordHasher.Hash("s3cret", iterations: 1000);
        Assert.True(PasswordHasher.Verify("s3cret", hash));
        Assert.False(PasswordHasher.Verify("S3cret", hash));
    }

    [Fact]
    public void Hashes_are_salted()
    {
        Assert.NotEqual(PasswordHasher.Hash("same", 1000), PasswordHasher.Hash("same", 1000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("PBKDF2-SHA256$abc$AAAA$AAAA")]
    [InlineData("PBKDF2-SHA256$1000$not-base64!$AAAA")]
    public void Malformed_hashes_never_verify(string stored)
    {
        Assert.False(PasswordHasher.Verify("x", stored));
    }
}

public class ValidatorTests
{
    private static IOptionsMonitor<GatewayOptions> Options(GatewayOptions o) => new StaticMonitor(o);

    private sealed class StaticMonitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private static GatewayOptions Config() => new()
    {
        DataSources = { ["a"] = new DataSourceOptions(), ["pt"] = new DataSourceOptions { PassThrough = true } },
    };

    [Fact]
    public async Task Passthrough_carries_credentials_and_is_limited_to_passthrough_sources()
    {
        var v = new PassThroughValidator(Options(Config()));
        var p = (await v.ValidateAsync("dbuser", "dbpass", default)).Principal;
        Assert.NotNull(p);
        Assert.Equal(new DbCredentials("dbuser", "dbpass"), p.Credentials);
        Assert.True(p.CanUse("pt"));
        Assert.False(p.CanUse("a"));
        Assert.Equal("pt", p.DefaultDataSource);
    }

    [Fact]
    public async Task Passthrough_can_be_disabled()
    {
        var cfg = Config();
        cfg.Auth.AllowPassThrough = false;
        Assert.Equal(CredentialResult.NotMine, await new PassThroughValidator(Options(cfg)).ValidateAsync("u", "p", default));
    }
}

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbgateway-sessions-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DbDataGateway.Tests.Config.TestFolder.Delete(_directory);
    }

    /// <summary>A store over a state database of its own, as an instance has (plan 20 O02: the sessions are in the state).</summary>
    private SessionStore Store(GatewayOptions options, TimeProvider clock)
    {
        options.DataDirectory = _directory;
        var monitor = new StaticMonitor(options);
        var state = DbDataGateway.Server.State.GatewayState.Open(_directory);
        return new SessionStore(monitor, clock, state, new DbDataGateway.Server.State.GroupSessionKey(state, monitor));
    }

    private sealed class StaticMonitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private static GatewayPrincipal Alice() => new("alice", "password", ["a"]);

    [Fact]
    public void Issued_token_resolves_to_principal_and_unknown_token_does_not()
    {
        var store = Store(new GatewayOptions(), TimeProvider.System);
        string token = store.Issue(Alice());
        Assert.Equal("alice", store.TryGet(token)!.Name);
        Assert.Null(store.TryGet("bogus"));
    }

    [Fact]
    public void Idle_sessions_expire_but_activity_extends_them()
    {
        var clock = new FakeTimeProvider();
        var store = Store(new GatewayOptions { Auth = { SessionIdleMinutes = 10 } }, clock);
        string token = store.Issue(Alice());

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(store.TryGet(token));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(store.TryGet(token));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(store.TryGet(token));
    }

    [Fact]
    public void Sessions_have_an_absolute_lifetime()
    {
        var clock = new FakeTimeProvider();
        var store = Store(new GatewayOptions { Auth = { SessionIdleMinutes = 600, SessionMaxHours = 1 } }, clock);
        string token = store.Issue(Alice());
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotNull(store.TryGet(token));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.TryGet(token));
    }

    [Fact]
    public void Revoked_tokens_stop_working()
    {
        var store = Store(new GatewayOptions(), TimeProvider.System);
        string token = store.Issue(Alice());
        store.Revoke(token);
        Assert.Null(store.TryGet(token));
    }
}
