using Apache.Arrow;
using DbDataKit.Connections;
using DbDataKit.Connections.FlightSql;
using DbDataKit.Secrets;
using DbDataProxy.Client;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// An upstream Flight SQL server as a Proxy data source (plan 13a, P12), through the shared Flight SQL client. The upstream is the other Flight SQL server this suite has, a Proxy in front of DuckDB (<see cref="ProxyFixture"/>),
/// so the whole path runs in-process: client, this Proxy with its classifier and limits, the shared client, the upstream Proxy, DuckDB.
/// </summary>
public sealed class FlightSqlSourceTests(ProxyFixture upstream) : IClassFixture<ProxyFixture>, IAsyncLifetime
{
    private WebApplication? _app;
    private string _address = "";
    private string _dir = "";

    private string UpstreamLogin(string password = ProxyFixture.AlicePassword, string source = "duck_off") =>
        $"grpc://{new Uri(upstream.Address).Authority};user=alice;password={password};header.x-datasource={source}";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:Users:0:Name"] = "bob",
            ["Proxy:Users:0:PasswordHash"] = DbDataProxy.Server.Auth.PasswordHasher.Hash("pw", 1000),
        };
        string[] sources = ["fs_strict", "fs_off", "fs_badpw"];
        for (int i = 0; i < sources.Length; i++) settings[$"Proxy:Users:0:DataSources:{i}"] = sources[i];

        settings["Proxy:DataSources:fs_strict:Provider"] = "flightsql";
        settings["Proxy:DataSources:fs_strict:ConnectionString"] = UpstreamLogin();
        settings["Proxy:DataSources:fs_strict:Classifier"] = "strict";

        // the classifier off: the upstream server (here read only DuckDB behind a Proxy that does not check text either) is the only control
        settings["Proxy:DataSources:fs_off:Provider"] = "flightsql";
        settings["Proxy:DataSources:fs_off:ConnectionString"] = UpstreamLogin();
        settings["Proxy:DataSources:fs_off:Classifier"] = "off";

        settings["Proxy:DataSources:fs_badpw:Provider"] = "flightsql";
        settings["Proxy:DataSources:fs_badpw:ConnectionString"] = UpstreamLogin("not-the-password");
        settings["Proxy:DataSources:fs_badpw:Classifier"] = "strict";

        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { if (_dir.Length > 0) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<ProxyClient> LoginAsync()
    {
        var client = new ProxyClient(_address);
        await client.AuthenticateAsync("bob", "pw");
        return client;
    }

    private static async Task<List<string?[]>> RowsAsync(QueryResultStream stream)
    {
        var rows = new List<string?[]>();
        await foreach (var batch in stream)
            for (int r = 0; r < batch.Length; r++)
                rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => ArrowText.Get(batch.Column(c), r)).ToArray());
        return rows;
    }

    private static QueryOptions On(string dataSource) => new() { DataSource = dataSource };

    [Fact]
    public async Task A_query_is_answered_by_the_upstream_server_and_typed_columns_arrive()
    {
        using var client = await LoginAsync();
        var rows = await RowsAsync(await client.Raw.QueryAsync("SELECT id, name, score, day FROM numbers WHERE id < 3 ORDER BY id", On("fs_strict")));

        Assert.Equal(3, rows.Count);
        Assert.Equal(["0", "name-0", "0", "2024-01-01"], rows[0]);
        Assert.Equal(["2", "name-2", "3", "2024-01-03"], rows[2]);

        var batch = (await QueryBatchesAsync(client, "SELECT id, amount, note FROM prices", "fs_strict")).First(b => b.Length > 0);
        Assert.IsType<Apache.Arrow.Types.Decimal128Type>(batch.Schema.GetFieldByName("amount").DataType);
        Assert.Equal("1.500", ArrowText.Get(batch.Column("amount"), 0));
    }

    private static async Task<List<RecordBatch>> QueryBatchesAsync(ProxyClient client, string sql, string source)
    {
        var batches = new List<RecordBatch>();
        await foreach (var b in await client.Raw.QueryAsync(sql, On(source))) batches.Add(b);
        return batches;
    }

    [Fact]
    public async Task Parameters_are_bound_in_a_prepared_statement_and_never_become_text()
    {
        using var client = await LoginAsync();
        var rows = await RowsAsync(await client.Raw.QueryAsync("SELECT name FROM numbers WHERE id >= ? AND name <> ? AND id < 4 ORDER BY id", [1, "name-2"], On("fs_strict")));
        Assert.Equal(["name-1", "name-3"], rows.Select(r => r[0]));

        var hostile = await RowsAsync(await client.Raw.QueryAsync("SELECT count(*) FROM prices WHERE note = ?", ["x'; DROP TABLE prices; --"], On("fs_strict")));
        Assert.Equal("0", hostile[0][0]);
        Assert.Equal("1", (await RowsAsync(await client.Raw.QueryAsync("SELECT count(*) FROM prices", On("fs_strict"))))[0][0]);
    }

    [Fact]
    public async Task Metadata_is_read_from_the_upstream_catalog()
    {
        using var client = await LoginAsync();
        var tables = await RowsAsync(await client.Raw.GetTablesAsync(tablePattern: "prices", options: On("fs_strict")));
        Assert.Contains(tables, r => r[2] == "prices");
    }

    public static TheoryData<string> Hostile =>
    [
        "INSERT INTO prices VALUES (9, 1, 'x')",
        "UPDATE prices SET amount = 0",
        "DELETE FROM prices",
        "DROP TABLE prices",
        "CREATE TABLE evil(a INT)",
        "SELECT 1; DROP TABLE prices",
        "WITH d AS (SELECT 1) DELETE FROM prices",
        "COPY prices TO '/tmp/out.csv'",
        "SET threads = 1",
        "",
        "   -- only a comment",
        "SELECT 'unterminated",
    ];

    [Theory, MemberData(nameof(Hostile))]
    public async Task The_classifier_refuses_hostile_text_before_it_reaches_the_upstream_server(string sql)
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync(sql, On("fs_strict"))));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal("1", (await RowsAsync(await client.Raw.QueryAsync("SELECT count(*) FROM prices", On("fs_strict"))))[0][0]);
    }

    [Theory]
    [InlineData("SELECT 1 AS n")]
    [InlineData("WITH t AS (SELECT 1 AS a) SELECT * FROM t")]
    [InlineData("SELECT '; DROP TABLE prices' AS s, \"id\" FROM prices LIMIT 1")]
    public async Task The_classifier_lets_plain_selects_through(string sql)
    {
        using var client = await LoginAsync();
        Assert.NotEmpty(await RowsAsync(await client.Raw.QueryAsync(sql, On("fs_strict"))));
    }

    [Fact]
    public async Task With_the_classifier_off_the_upstream_servers_own_refusal_is_what_the_client_sees_as_a_failure()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync("DELETE FROM prices", On("fs_off"))));
        Assert.NotEqual(StatusCode.OK, ex.StatusCode);
        Assert.Contains("[flightsql ", ex.Status.Detail);
        Assert.DoesNotContain("prices", ex.Status.Detail);
        Assert.Equal("1", (await RowsAsync(await client.Raw.QueryAsync("SELECT count(*) FROM prices", On("fs_off"))))[0][0]);
    }

    [Fact]
    public async Task Failures_report_the_kind_and_the_status_but_not_the_upstream_servers_words()
    {
        using var client = await LoginAsync();

        var syntax = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync("SELEKT 1 FROM prices", On("fs_off"))));
        Assert.Equal(StatusCode.InvalidArgument, syntax.StatusCode);
        Assert.Contains("[flightsql InvalidArgument]", syntax.Status.Detail);
        Assert.DoesNotContain("SELEKT", syntax.Status.Detail);

        var login = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync("SELECT 1", On("fs_badpw"))));
        Assert.Contains("[flightsql Unauthenticated]", login.Status.Detail);
        Assert.DoesNotContain("not-the-password", login.Status.Detail);
    }

    [Fact]
    public async Task The_data_source_cap_holds_while_a_result_is_open_and_refuses_after_the_acquire_timeout()
    {
        var options = new ProxyOptions
        {
            DataSources = { ["a"] = new DataSourceOptions { Provider = "flightsql", ConnectionString = UpstreamLogin(), MaxConcurrentQueries = 1, AcquireTimeoutSeconds = 1 } },
        };
        var catalog = new DataSourceCatalog(new StaticMonitor(options), TestSources.Registry(), limits: new QueryLimits());
        async Task<IQueryResult> Run(string user) =>
            await catalog.Resolve(new ProxyPrincipal(user, "password", ["a"], "a"), "a").Executor.ExecuteQueryAsync(new QueryRequest("SELECT 1 AS n"), default);

        var first = await Run("u1");
        var ex = await Assert.ThrowsAsync<RpcException>(() => Run("u2"));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        await first.DisposeAsync();
        await (await Run("u2")).DisposeAsync();
    }

    // ---- decided without a server --------------------------------------------------------------------------------------------

    private static ConnectionContext Context(string connectionString) => new()
    {
        Definition = SourceConnection.BuildDefinition("x", "flightsql", connectionString, readOnly: true),
        Login = new ResolvedLogin(LoginRole.Default, AuthMode.Sql, "test") { ConnectionString = SecretValue.From(connectionString) },
    };

    [Theory]
    [InlineData("grpc+tls://h:1;token_command={az account get-access-token}", "token_command")]
    [InlineData("grpc+tls://h:1;token_env=PROXY_TOKEN", "token_env")]
    public void A_token_from_a_program_or_the_servers_own_environment_is_refused_for_a_data_source(string text, string option)
    {
        var provider = new FlightSqlProvider();
        var ex = Assert.Throws<ConnectionPolicyException>(() => provider.Composer.Compose(Context(text)));
        Assert.Contains(option, ex.Message);
        Assert.Throws<ConnectionPolicyException>(() => provider.Create(Context(text)));
    }

    [Fact]
    public void A_connection_string_that_is_not_the_grammar_is_refused_without_quoting_it_and_the_description_masks_the_secrets()
    {
        var provider = new FlightSqlProvider();
        var ex = Assert.Throws<ConnectionPolicyException>(() => provider.Composer.Compose(Context("grpc://h:1;password=SENTINEL-pw;bogus=1")));
        Assert.DoesNotContain("SENTINEL-pw", ex.Message);

        var described = provider.Composer.Describe(Context("grpc+tls://h:1;user=u;password=SENTINEL-pw;header.x-datasource=duck"));
        Assert.DoesNotContain("SENTINEL-pw", described);
        Assert.Contains("user=u", described);
        Assert.Throws<ConnectionPolicyException>(() => provider.Composer.ApplyPassThroughCredentials("grpc://h:1", "u", SecretValue.From("p")));
    }

    [Fact]
    public void A_flight_sql_string_without_tls_or_with_tls_skip_verify_is_an_opt_out_named_at_start_up()
    {
        var logs = new CaptureLoggerProvider();
        var options = new ProxyOptions
        {
            DataSources =
            {
                ["plain"] = new DataSourceOptions { Provider = "flightsql", ConnectionString = "grpc://h:1;user=u;password=SENTINEL-pw" },
                ["skip"] = new DataSourceOptions { Provider = "flightsql", ConnectionString = "grpc+tls://h:1;user=u;password=SENTINEL-pw;tls_skip_verify=true" },
                ["tls"] = new DataSourceOptions { Provider = "flightsql", ConnectionString = "grpc+tls://h:1;user=u;password=SENTINEL-pw" },
            },
        };
        var named = ConnectionStartupChecks.WarnAboutTlsOptOuts(options, null, logs.CreateLogger("t"));
        Assert.Equal(["plain", "skip"], named.Order());
        Assert.All(logs.Lines.Where(l => l.Level == LogLevel.Warning), l => Assert.DoesNotContain("SENTINEL-pw", l.Text));
    }

    private sealed class StaticMonitor(ProxyOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;
        public ProxyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }
}
