using DbDataKit.Dialect;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace DbDataProxy.Tests.Data;

/// <summary>PostgreSQL as a Proxy data source (plan 13a, P12): what is decided without a server, then the limits against the shared test engine.</summary>
public sealed class PostgresSourceTests
{
    private static DataSourceCatalog Catalog(DataSourceOptions source, QueryLimits limits) =>
        new(new StaticMonitor(new ProxyOptions { DataSources = { ["a"] = source } }), TestSources.Registry(), limits: limits);

    private static DataSourceOptions Postgres(int max = 0, int timeoutSeconds = 1) =>
        new() { Provider = "postgres", ConnectionString = PostgresEnv.ConnectionString(), MaxConcurrentQueries = max, AcquireTimeoutSeconds = timeoutSeconds };

    private static async Task<IQueryResult> RunAsync(DataSourceCatalog catalog, string user) =>
        await catalog.Resolve(new ProxyPrincipal(user, "password", ["a"], "a"), "a").Executor.ExecuteQueryAsync(new QueryRequest("SELECT 1 AS n"), default);

    [Fact]
    public void The_proxy_classifies_postgresql_statements_with_the_postgresql_dialect_and_binds_named_markers()
    {
        var source = TestSources.Postgres("Host=h;Database=d;Username=u;Password=p", readOnly: true);
        Assert.Equal("postgres", source.Engine);
        Assert.Same(ProviderDialects.Postgres, source.Dialect);
        Assert.Equal(DbDataProxy.Data.ParameterStyle.NamedAtSign, source.ParameterStyle);
        Assert.Equal(EngineId.Postgres, source.Dialect.Id);

        // dollar quoting is one string, and a ? inside it is not a marker
        Assert.Equal(0, ParameterMarkers.Count("SELECT $$ ? $$", source.Dialect));
        Assert.Equal("SELECT $$ ? $$, @p1", ParameterMarkers.RewriteToNamed("SELECT $$ ? $$, ?", source.Dialect));

        Assert.False(ReadOnlyPolicy.Check("SELECT pg_read_file('/etc/passwd')", source.Dialect).Allowed);
        Assert.False(ReadOnlyPolicy.Check("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", source.Dialect).Allowed);
        Assert.True(ReadOnlyPolicy.Check("SELECT $$; DROP TABLE t$$", source.Dialect).Allowed);
    }

    [Fact]
    public void The_read_only_session_is_applied_to_a_source_that_cannot_write()
    {
        var composed = TestSources.Postgres("Host=h;Database=d;Username=u;Password=p", readOnly: true).Compose();
        Assert.Contains("default_transaction_read_only=on", composed);
        Assert.DoesNotContain("default_transaction_read_only", TestSources.Postgres("Host=h;Database=d;Username=u;Password=p").Compose());
    }

    [Fact]
    public void An_explicit_tls_opt_out_of_a_postgresql_string_is_warned_about_by_name_and_the_default_is_not()
    {
        var logs = new CaptureLoggerProvider();
        var options = new ProxyOptions
        {
            DataSources =
            {
                ["pg_require"] = new DataSourceOptions { Provider = "postgres", ConnectionString = "Host=h;Username=u;Password=SENTINEL-pw;SSL Mode=Require" },
                ["pg_disable"] = new DataSourceOptions { Provider = "postgres", ConnectionString = "Host=h;Username=u;Password=SENTINEL-pw;SSL Mode=Disable" },
                ["pg_default"] = new DataSourceOptions { Provider = "postgres", ConnectionString = "Host=h;Username=u;Password=SENTINEL-pw" },
                ["pg_verify"] = new DataSourceOptions { Provider = "postgres", ConnectionString = "Host=h;Username=u;Password=SENTINEL-pw;SSL Mode=VerifyFull" },
            },
        };
        var named = ConnectionStartupChecks.WarnAboutTlsOptOuts(options, null, logs.CreateLogger("t"));

        Assert.Equal(["pg_disable", "pg_require"], named.Order());
        Assert.All(logs.Lines.Where(l => l.Level == LogLevel.Warning), l => Assert.DoesNotContain("SENTINEL-pw", l.Text));
    }

    [Fact]
    public async Task The_data_source_cap_holds_while_a_result_is_open_and_refuses_after_the_acquire_timeout()
    {
        await PostgresEnv.EnsureReachableAsync();
        var catalog = Catalog(Postgres(max: 1), new QueryLimits());
        var first = await RunAsync(catalog, "u1");

        var ex = await Assert.ThrowsAsync<RpcException>(() => RunAsync(catalog, "u2"));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);

        await first.DisposeAsync();   // a streaming result keeps its slot until it is disposed
        await (await RunAsync(catalog, "u2")).DisposeAsync();
    }

    private sealed class StaticMonitor(ProxyOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;
        public ProxyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }
}
