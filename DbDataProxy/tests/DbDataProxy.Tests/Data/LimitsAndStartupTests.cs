using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Tests.Auth;
using DbDataProxy.Tests.Config;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace DbDataProxy.Tests.Data;

/// <summary>Optional per data source and per user concurrency limits (S-6), the TLS opt-out warning and the validation of the new keys.</summary>
public class LimitsAndStartupTests
{
    private static DataSourceCatalog Catalog(DataSourceOptions source, QueryLimits limits) =>
        new(new StaticMonitor(new ProxyOptions { DataSources = { ["a"] = source } }), TestSources.Registry(), limits: limits);

    private static DataSourceOptions Duck(int max = 0, int perUser = 0, int timeoutSeconds = 1) =>
        new() { Provider = "duckdb", ConnectionString = "Data Source=:memory:", MaxConcurrentQueries = max, MaxConcurrentQueriesPerUser = perUser, AcquireTimeoutSeconds = timeoutSeconds };

    private static readonly QueryRequest Select = new("SELECT 1 AS n");

    private static async Task<IQueryResult> RunAsync(DataSourceCatalog catalog, string user, CancellationToken ct = default) =>
        await catalog.Resolve(new ProxyPrincipal(user, "password", ["a"], "a"), "a").Executor.ExecuteQueryAsync(Select, ct);

    [Fact]
    public async Task No_limit_is_the_default_and_means_no_waiting()
    {
        var catalog = Catalog(Duck(), new QueryLimits());
        var open = new List<IQueryResult>();
        for (int i = 0; i < 20; i++) open.Add(await RunAsync(catalog, "u"));
        foreach (var r in open) await r.DisposeAsync();
    }

    [Fact]
    public async Task The_data_source_cap_holds_while_a_result_is_open_and_refuses_after_the_acquire_timeout()
    {
        var catalog = Catalog(Duck(max: 2), new QueryLimits());
        var first = await RunAsync(catalog, "u1");
        var second = await RunAsync(catalog, "u2");

        var ex = await Assert.ThrowsAsync<RpcException>(() => RunAsync(catalog, "u3"));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Contains("busy", ex.Status.Detail);

        await first.DisposeAsync(); // a streaming result keeps its slot until it is disposed
        await (await RunAsync(catalog, "u3")).DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task The_per_user_cap_stops_one_user_without_blocking_the_others()
    {
        var catalog = Catalog(Duck(max: 10, perUser: 1), new QueryLimits());
        var held = await RunAsync(catalog, "alice");

        await Assert.ThrowsAsync<RpcException>(() => RunAsync(catalog, "alice"));
        await (await RunAsync(catalog, "bob")).DisposeAsync();

        await held.DisposeAsync();
        await (await RunAsync(catalog, "alice")).DisposeAsync();
    }

    [Fact]
    public async Task A_waiting_request_gets_the_slot_when_one_is_returned_and_failures_give_theirs_back()
    {
        var catalog = Catalog(Duck(max: 1, timeoutSeconds: 10), new QueryLimits());
        var held = await RunAsync(catalog, "u");
        var waiter = RunAsync(catalog, "u");
        await Task.Delay(100);
        Assert.False(waiter.IsCompleted);
        await held.DisposeAsync();
        await (await waiter).DisposeAsync();

        // a statement that fails must not leak its slot
        var executor = catalog.Resolve(new ProxyPrincipal("u", "password", ["a"], "a"), "a").Executor;
        for (int i = 0; i < 3; i++)
            await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteQueryAsync(new QueryRequest("SELEKT"), CancellationToken.None));
        await (await RunAsync(catalog, "u")).DisposeAsync();
    }

    [Fact]
    public void Limits_are_rebuilt_when_the_configuration_changes_and_dropped_when_removed()
    {
        var limits = new QueryLimits();
        var gate = limits.For("a", Duck(max: 2));
        Assert.Same(gate, limits.For("a", Duck(max: 2)));
        Assert.NotSame(gate, limits.For("a", Duck(max: 3)));
        Assert.Null(limits.For("a", Duck()));
    }

    // ---- start-up ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Explicit_tls_opt_outs_are_warned_about_by_source_name_and_never_show_the_string()
    {
        var logs = new CaptureLoggerProvider();
        var options = new ProxyOptions
        {
            DataSources =
            {
                ["optout"] = new DataSourceOptions { Provider = "sqlserver", ConnectionString = "Server=s;User Id=u;Password=SENTINEL-pw;TrustServerCertificate=True" },
                ["plain"] = new DataSourceOptions { Provider = "sqlserver", ConnectionString = "Server=s;User Id=u;Password=SENTINEL-pw;Encrypt=False" },
                ["secure"] = new DataSourceOptions { Provider = "sqlserver", ConnectionString = "Server=s;User Id=u;Password=SENTINEL-pw" },
                ["odbc_secure"] = new DataSourceOptions { Provider = "odbc", ConnectionString = "Driver={ODBC Driver 18 for SQL Server};Server=s" },
                ["odbc_optout"] = new DataSourceOptions { Provider = "odbc", ConnectionString = "Driver={ODBC Driver 18 for SQL Server};Server=s;TrustServerCertificate=yes" },
                ["duck"] = new DataSourceOptions { Provider = "duckdb", ConnectionString = "Data Source=:memory:" },
            },
        };

        var named = ConnectionStartupChecks.WarnAboutTlsOptOuts(options, null, logs.CreateLogger("t"));

        Assert.Equal(["odbc_optout", "optout", "plain"], named.Order());
        var lines = logs.Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("Data source optout "));
        Assert.All(lines, l => Assert.DoesNotContain("SENTINEL-pw", l));
    }

    // ---- validation ------------------------------------------------------------------------------------------------------

    private static ProxyConfigLoad Load(string source) =>
        ConfigTestSupport.LoadJson("{ \"Proxy\": { \"SchemaVersion\": 2, \"DataSources\": { \"d\": { " + source + " } } } }");

    [Fact]
    public void The_new_keys_validate_and_default_to_off()
    {
        var load = Load("\"Provider\": \"duckdb\", \"ConnectionString\": \"x\"");
        Assert.True(load.Ok, ConfigTestSupport.Format(load));
        var d = load.Value!.DataSources["d"];
        Assert.Equal(0, d.MaxConcurrentQueries);
        Assert.Equal(0, d.MaxConcurrentQueriesPerUser);
        Assert.Equal(30, d.AcquireTimeoutSeconds);
        Assert.Empty(d.AllowedDirectories);
        Assert.True(load.Value.LogDriverErrors);

        var full = Load("\"Provider\": \"duckdb\", \"ConnectionString\": \"x\", \"MaxConcurrentQueries\": 8, \"MaxConcurrentQueriesPerUser\": 2, \"AcquireTimeoutSeconds\": 5, \"AllowedDirectories\": [ \"" +
            (OperatingSystem.IsWindows() ? "C:/data" : "/srv/data") + "\" ]");
        Assert.True(full.Ok, ConfigTestSupport.Format(full));
    }

    [Theory]
    [InlineData("\"MaxConcurrentQueries\": -1")]
    [InlineData("\"MaxConcurrentQueriesPerUser\": 100001")]
    [InlineData("\"AcquireTimeoutSeconds\": 0")]
    public void Out_of_range_limits_are_refused(string setting)
    {
        var load = Load("\"Provider\": \"duckdb\", \"ConnectionString\": \"x\", " + setting);
        Assert.False(load.Ok);
    }

    [Fact]
    public void Allowed_directories_must_be_absolute_and_only_for_duckdb()
    {
        var relative = Load("\"Provider\": \"duckdb\", \"ConnectionString\": \"x\", \"AllowedDirectories\": [ \"data/lake\" ]");
        Assert.False(relative.Ok);
        Assert.Contains("absolute path", ConfigTestSupport.Format(relative));

        var wrongProvider = Load("\"Provider\": \"sqlserver\", \"ConnectionString\": \"Server=s\", \"AllowedDirectories\": [ \"" + (OperatingSystem.IsWindows() ? "C:/data" : "/srv/data") + "\" ]");
        Assert.False(wrongProvider.Ok);
        Assert.Contains("only with the duckdb provider", ConfigTestSupport.Format(wrongProvider));
    }

    [Fact]
    public void Allowed_directories_on_a_writable_source_and_a_per_user_cap_above_the_source_cap_are_warnings()
    {
        string dir = OperatingSystem.IsWindows() ? "C:/data" : "/srv/data";
        var writable = Load("\"Provider\": \"duckdb\", \"ConnectionString\": \"x\", \"AllowWrites\": true, \"AllowedDirectories\": [ \"" + dir + "\" ]");
        Assert.True(writable.Ok);
        Assert.Contains("no effect", ConfigTestSupport.Format(writable));

        var caps = Load("\"Provider\": \"duckdb\", \"ConnectionString\": \"x\", \"MaxConcurrentQueries\": 2, \"MaxConcurrentQueriesPerUser\": 5");
        Assert.True(caps.Ok);
        Assert.Contains("never be reached", ConfigTestSupport.Format(caps));
    }
}
