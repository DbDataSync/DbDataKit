using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DbDataProxy.Tests.Auth;

internal sealed class StaticMonitor(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
{
    public ProxyOptions CurrentValue => value;
    public ProxyOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
}

public class LoginThrottleTests
{
    private static (ChainedCredentialValidator Chain, FakeTimeProvider Clock) Create(int maxFailures = 3, int lockoutMinutes = 1)
    {
        var options = new ProxyOptions
        {
            Auth = { MaxFailedLogins = maxFailures, LockoutMinutes = lockoutMinutes },
            Users = [new UserOptions { Name = "alice", PasswordHash = PasswordHasher.Hash("pw", 1000), DataSources = ["a"] }],
        };
        var monitor = new StaticMonitor(options);
        var clock = new FakeTimeProvider();
        var chain = new ChainedCredentialValidator([new LocalUserValidator(monitor)], new LoginThrottle(monitor, clock));
        return (chain, clock);
    }

    [Fact]
    public async Task Repeated_failures_lock_out_even_the_correct_password_until_the_lockout_ends()
    {
        var (chain, clock) = Create();
        for (int i = 0; i < 3; i++) Assert.Null(await chain.ValidateAsync("alice", "wrong", default, "10.0.0.1"));

        Assert.Null(await chain.ValidateAsync("alice", "pw", default, "10.0.0.1"));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.NotNull(await chain.ValidateAsync("alice", "pw", default, "10.0.0.1"));
    }

    [Fact]
    public async Task Lockout_is_per_client_so_an_attacker_cannot_lock_out_the_real_user_elsewhere()
    {
        var (chain, _) = Create();
        for (int i = 0; i < 3; i++) await chain.ValidateAsync("alice", "wrong", default, "203.0.113.9");

        Assert.NotNull(await chain.ValidateAsync("alice", "pw", default, "10.0.0.1"));
    }

    [Fact]
    public async Task Success_clears_the_failure_count()
    {
        var (chain, _) = Create();
        await chain.ValidateAsync("alice", "wrong", default, "c");
        await chain.ValidateAsync("alice", "wrong", default, "c");
        Assert.NotNull(await chain.ValidateAsync("alice", "pw", default, "c"));
        await chain.ValidateAsync("alice", "wrong", default, "c");
        await chain.ValidateAsync("alice", "wrong", default, "c");
        Assert.NotNull(await chain.ValidateAsync("alice", "pw", default, "c"));
    }

    [Fact]
    public async Task Unknown_users_are_throttled_too()
    {
        var (chain, _) = Create();
        for (int i = 0; i < 3; i++) await chain.ValidateAsync("nobody", "x", default, "c");
        // Locked out: the check short-circuits before any work is done.
        Assert.Null(await chain.ValidateAsync("nobody", "x", default, "c"));
    }

    [Fact]
    public async Task Throttling_can_be_disabled()
    {
        var (chain, _) = Create(maxFailures: 0);
        for (int i = 0; i < 20; i++) await chain.ValidateAsync("alice", "wrong", default, "c");
        Assert.NotNull(await chain.ValidateAsync("alice", "pw", default, "c"));
    }
}

public class DataSourceCatalogTests
{
    private sealed class FakeWorker : IWorkerExecutorFactory
    {
        public string? LastConnectionString;
        public IQueryExecutor Create(string connectionString)
        {
            LastConnectionString = connectionString;
            return new DbQueryExecutor(new DelegateConnectionFactory(_ => throw new NotSupportedException()), connectionString);
        }
    }

    private static ProxyOptions Config() => new()
    {
        DataSources =
        {
            ["a"] = new DataSourceOptions { Provider = "duckdb", ConnectionString = "Data Source=:memory:" },
            ["b"] = new DataSourceOptions { Provider = "duckdb", ConnectionString = "Data Source=:memory:" },
            ["legacy"] = new DataSourceOptions { Provider = "odbc", Bitness = "x86", ConnectionString = "Driver={Legacy};Dsn=x" },
            ["pt"] = new DataSourceOptions { Provider = "sqlserver", PassThrough = true, ConnectionString = "Server=s;Database=d" },
            ["badx86"] = new DataSourceOptions { Provider = "sqlserver", Bitness = "x86", ConnectionString = "Server=s" },
        },
    };

    private static DataSourceCatalog Catalog(ProxyOptions options, IWorkerExecutorFactory? worker = null) =>
        new(new StaticMonitor(options), [new OdbcProvider(), new SqlServerProvider(), new DuckDbProvider()], worker);

    private static StatusCode CodeOf(Action action) => Assert.Throws<RpcException>(action).StatusCode;

    [Fact]
    public void Explicit_choice_wins_and_must_be_permitted()
    {
        var catalog = Catalog(Config());
        var user = new ProxyPrincipal("u", "password", ["a"], "a");
        Assert.Equal("a", catalog.Resolve(user, "a").Name);
        Assert.Equal(StatusCode.PermissionDenied, CodeOf(() => catalog.Resolve(user, "b")));
        Assert.Equal(StatusCode.NotFound, CodeOf(() => catalog.Resolve(user, "zzz")));
    }

    [Fact]
    public void Falls_back_to_the_default_then_to_the_only_permitted_source()
    {
        var catalog = Catalog(Config());
        Assert.Equal("b", catalog.Resolve(new ProxyPrincipal("u", "password", ["a", "b"], "b"), null).Name);
        Assert.Equal("a", catalog.Resolve(new ProxyPrincipal("u", "password", ["a"]), null).Name);
        Assert.Equal(StatusCode.InvalidArgument, CodeOf(() => catalog.Resolve(new ProxyPrincipal("u", "password", ["a", "b"]), null)));
    }

    [Fact]
    public void Wildcard_grants_every_source()
    {
        var catalog = Catalog(Config());
        Assert.Equal("b", catalog.Resolve(new ProxyPrincipal("u", "password", ["*"]), "b").Name);
    }

    [Fact]
    public void Passthrough_sources_require_caller_credentials_and_inject_them()
    {
        var catalog = Catalog(Config());
        var withoutCreds = new ProxyPrincipal("u", "password", ["pt"], "pt");
        Assert.Equal(StatusCode.PermissionDenied, CodeOf(() => catalog.Resolve(withoutCreds, "pt")));

        var withCreds = new ProxyPrincipal("dbuser", "passthrough", ["pt"], "pt", new DbCredentials("dbuser", "s3cret"));
        Assert.Equal("pt", catalog.Resolve(withCreds, null).Name); // builds an executor; the connection is not opened yet
    }

    [Fact]
    public void X86_odbc_sources_go_through_the_worker_with_the_connection_string()
    {
        var worker = new FakeWorker();
        var catalog = Catalog(Config(), worker);
        catalog.Resolve(new ProxyPrincipal("u", "password", ["legacy"]), "legacy");
        Assert.Equal("Driver={Legacy};Dsn=x", worker.LastConnectionString);
    }

    [Fact]
    public void X86_sources_report_clear_errors_when_unavailable_or_unsupported()
    {
        var user = new ProxyPrincipal("u", "password", ["*"]);
        Assert.Equal(StatusCode.Unavailable, CodeOf(() => Catalog(Config()).Resolve(user, "legacy")));
        Assert.Equal(StatusCode.Internal, CodeOf(() => Catalog(Config(), new FakeWorker()).Resolve(user, "badx86")));
    }
}
