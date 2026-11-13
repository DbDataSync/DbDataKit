using DbDataKit.Dialect;
using DbDataGateway.Data;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Tests.Support;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace DbDataGateway.Tests.Data;

/// <summary>PostgreSQL as a Gateway data source (plan 13a, P12): what is decided without a server, then the limits against the shared test engine.</summary>
public sealed class PostgresSourceTests
{
    private static DataSourceCatalog Catalog(DataSourceOptions source, QueryLimits limits) =>
        new(new StaticMonitor(new GatewayOptions { DataSources = { ["a"] = source } }), TestSources.Registry(), limits: limits);

    private static DataSourceOptions Postgres(int max = 0, int timeoutSeconds = 1) =>
        new() { Provider = "postgres", ConnectionString = PostgresEnv.ConnectionString(), MaxConcurrentQueries = max, AcquireTimeoutSeconds = timeoutSeconds };

    private static async Task<IQueryResult> RunAsync(DataSourceCatalog catalog, string user) =>
        await catalog.Resolve(new GatewayPrincipal(user, "password", ["a"], "a"), "a").Executor.ExecuteQueryAsync(new QueryRequest("SELECT 1 AS n"), default);

    [Fact]
    public void The_gateway_classifies_postgresql_statements_with_the_postgresql_dialect_and_binds_named_markers()
    {
        var source = TestSources.Postgres("Host=h;Database=d;Username=u;Password=p", readOnly: true);
        Assert.Equal("postgres", source.Engine);
        Assert.Same(ProviderDialects.Postgres, source.Dialect);
        Assert.Equal(DbDataGateway.Data.ParameterStyle.NamedAtSign, source.ParameterStyle);
        Assert.Equal(EngineId.Postgres, source.Dialect.Id);

        // dollar quoting is one string, and a ? inside it is not a marker
        Assert.Equal(0, ParameterMarkers.Count("SELECT $$ ? $$", source.Dialect));
        Assert.Equal("SELECT $$ ? $$, @p1", ParameterMarkers.RewriteToNamed("SELECT $$ ? $$, ?", source.Dialect));

        Assert.False(ReadOnlyPolicy.Check("SELECT pg_read_file('/etc/passwd')", source.Dialect).Allowed);
        Assert.False(ReadOnlyPolicy.Check("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", source.Dialect).Allowed);
        Assert.True(ReadOnlyPolicy.Check("SELECT $$; DROP TABLE t$$", source.Dialect).Allowed);
    }

    [Fact]
    public async Task Numbered_markers_are_counted_and_bound_by_position_not_by_name()
    {
        // the semantic module writes $1, $2 for a PostgreSQL source (plan 22a GP11): a parameter with a name is matched by name by Npgsql, and the statement then found none (08P01)
        var dialect = ProviderDialects.Postgres;
        Assert.Equal(2, ParameterMarkers.CountNumbered("SELECT 1 WHERE a = $1 AND b IN ($2)", dialect));
        Assert.Equal(0, ParameterMarkers.CountNumbered("SELECT $$ $1 $$, '$2'", dialect));
        Assert.Equal(0, ParameterMarkers.Count("SELECT 1 WHERE a = $1", dialect));

        await PostgresEnv.EnsureReachableAsync();
        var executor = Catalog(Postgres(), new QueryLimits()).Resolve(new GatewayPrincipal("u1", "password", ["a"], "a"), "a").Executor;
        await using var result = await executor.ExecuteQueryAsync(new QueryRequest("SELECT $1::text AS a, $2::int AS b", Parameters: [new QueryParameter("p1", "string", "x"), new QueryParameter("p2", "int64", "7")]), default);
        var rows = 0;
        await foreach (var batch in result.ReadBatchesAsync(default)) rows += batch.Length;
        Assert.Equal(1, rows);
        await Assert.ThrowsAsync<ParameterMismatchException>(async () => await (await executor.ExecuteQueryAsync(new QueryRequest("SELECT $1, $2", Parameters: [new QueryParameter(null, "string", "x")]), default)).DisposeAsync());
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
        var options = new GatewayOptions
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

    private sealed class StaticMonitor(GatewayOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}
