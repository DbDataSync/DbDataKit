using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DbDataProxy.Tests.Auth;

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
    private static IOptionsMonitor<ProxyOptions> Options(ProxyOptions o) => new StaticMonitor(o);

    private sealed class StaticMonitor(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;
        public ProxyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }

    private static ProxyOptions Config() => new()
    {
        DataSources = { ["a"] = new DataSourceOptions(), ["pt"] = new DataSourceOptions { PassThrough = true } },
        Users = [new UserOptions { Name = "alice", PasswordHash = PasswordHasher.Hash("pw", 1000), DataSources = ["a"], DefaultDataSource = "a" }],
    };

    [Fact]
    public async Task Local_user_with_correct_password_is_accepted()
    {
        var v = new LocalUserValidator(Options(Config()));
        var p = (await v.ValidateAsync("ALICE", "pw", default)).Principal;
        Assert.NotNull(p);
        Assert.Equal("a", p.DefaultDataSource);
        Assert.True(p.CanUse("a"));
        Assert.False(p.CanUse("pt"));
    }

    [Fact]
    public async Task Local_user_with_wrong_password_or_unknown_user_is_rejected()
    {
        var v = new LocalUserValidator(Options(Config()));
        Assert.Equal(CredentialResult.Rejected, await v.ValidateAsync("alice", "nope", default));
        Assert.Equal(CredentialResult.NotMine, await v.ValidateAsync("mallory", "pw", default));
    }

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

    [Fact]
    public async Task Chain_prefers_local_users_over_passthrough()
    {
        var o = Options(Config());
        var chain = new ChainedCredentialValidator([new LocalUserValidator(o), new PassThroughValidator(o)]);
        Assert.Equal("password", (await chain.ValidateAsync("alice", "pw", default))!.Method);
        // A known local user with a wrong password must not fall through to pass-through.
        Assert.Null(await chain.ValidateAsync("alice", "wrong", default));
        Assert.Equal("passthrough", (await chain.ValidateAsync("bob", "x", default))!.Method);
    }
}

public class SessionStoreTests
{
    private sealed class StaticMonitor(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;
        public ProxyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }

    private static ProxyPrincipal Alice() => new("alice", "password", ["a"]);

    [Fact]
    public void Issued_token_resolves_to_principal_and_unknown_token_does_not()
    {
        var store = new SessionStore(new StaticMonitor(new ProxyOptions()), TimeProvider.System);
        string token = store.Issue(Alice());
        Assert.Equal("alice", store.TryGet(token)!.Name);
        Assert.Null(store.TryGet("bogus"));
    }

    [Fact]
    public void Idle_sessions_expire_but_activity_extends_them()
    {
        var clock = new FakeTimeProvider();
        var store = new SessionStore(new StaticMonitor(new ProxyOptions { Auth = { SessionIdleMinutes = 10 } }), clock);
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
        var store = new SessionStore(new StaticMonitor(new ProxyOptions { Auth = { SessionIdleMinutes = 600, SessionMaxHours = 1 } }), clock);
        string token = store.Issue(Alice());
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotNull(store.TryGet(token));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.TryGet(token));
    }

    [Fact]
    public void Revoked_tokens_stop_working()
    {
        var store = new SessionStore(new StaticMonitor(new ProxyOptions()), TimeProvider.System);
        string token = store.Issue(Alice());
        store.Revoke(token);
        Assert.Null(store.TryGet(token));
    }
}
