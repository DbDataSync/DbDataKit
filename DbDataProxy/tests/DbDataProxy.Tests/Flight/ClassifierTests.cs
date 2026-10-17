using Apache.Arrow;
using DbDataProxy.Client;
using DbDataProxy.Data;
using DbDataProxy.Tests.Support;
using Grpc.Core;

namespace DbDataProxy.Tests.Flight;

/// <summary>The statement-text check on read-only sources (Classifier = strict | warn | off), on the query and prepare paths.</summary>
public class ClassifierTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<ProxyClient> LoginAsync()
    {
        var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        return client;
    }

    public static TheoryData<string> Hostile =>
    [
        "INSERT INTO prices VALUES (9, 1, 'x')",
        "UPDATE prices SET amount = 0",
        "DELETE FROM prices",
        "DROP TABLE prices",
        "CREATE TABLE evil(a INT)",
        "SELECT 1; DROP TABLE prices",
        "SELECT 1 /* ; */; DELETE FROM prices",
        "SELECT 'it''s'; DELETE FROM prices",
        "WITH d AS (SELECT 1) DELETE FROM prices",
        "ATTACH '/tmp/evil.db' AS evil",
        "INSTALL httpfs",
        "LOAD httpfs",
        "COPY prices TO '/tmp/out.csv'",
        "COPY (SELECT 1) TO '/tmp/out.csv'",
        "PRAGMA version",
        "SELECT * FROM read_csv('/etc/passwd')",
        "SELECT * FROM READ_PARQUET('/tmp/x.parquet')",
        "SELECT * FROM read_csv_auto('/tmp/x.csv')",
        "SELECT getenv('HOME')",
        "SELECT nextval('s')",
        "",
        "   -- only a comment",
        "SELECT 'unterminated",
    ];

    public static TheoryData<string> Benign =>
    [
        "SELECT 1 AS n",
        "SELECT count(*) AS n FROM numbers WHERE id > 10",
        "WITH t AS (SELECT 1 AS a) SELECT * FROM t",
        "SELECT $$; DROP TABLE prices; --$$ AS s",
        "SELECT '; DROP TABLE prices' AS s, \"id\" FROM numbers LIMIT 1",
        "SELECT 1 AS n; ",
    ];

    [Theory, MemberData(nameof(Hostile))]
    public async Task Strict_refuses_hostile_text_on_the_query_path(string sql)
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync(sql, "duck_strict"));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        await AssertPricesIntactAsync(client);
    }

    [Theory, MemberData(nameof(Hostile))]
    public async Task Strict_refuses_hostile_text_on_the_prepare_path(string sql)
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.Raw.DescribeParametersAsync(sql, new QueryOptions { DataSource = "duck_strict" }));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        await AssertPricesIntactAsync(client);
    }

    [Theory, MemberData(nameof(Benign))]
    public async Task Strict_allows_a_single_select(string sql)
    {
        using var client = await LoginAsync();
        var (_, batches) = await client.QueryAsync(sql, "duck_strict");
        Assert.NotEmpty(batches);
        await client.Raw.DescribeParametersAsync(sql, new QueryOptions { DataSource = "duck_strict" });
    }

    [Fact]
    public async Task Strict_refusal_names_the_source_and_the_reason()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT * FROM read_csv('/etc/passwd')", "duck_strict"));
        Assert.Contains("duck_strict", ex.Status.Detail);
        Assert.Contains("read_csv", ex.Status.Detail);
    }

    [Fact]
    public async Task Parameterized_queries_are_checked_too()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await client.Raw.QueryAsync("DELETE FROM prices WHERE id = ?", [1], new QueryOptions { DataSource = "duck_strict" }));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        var ok = await client.Raw.QueryAsync("SELECT amount FROM prices WHERE id = ?", [1], new QueryOptions { DataSource = "duck_strict" });
        await foreach (var _ in ok) { }
    }

    [Fact]
    public async Task Warn_is_the_default_and_runs_statements_it_would_refuse()
    {
        using var client = await LoginAsync();
        // duck_ro sets no Classifier, so it is in warn mode: the text is logged and still runs.
        var (_, batches) = await client.QueryAsync("PRAGMA version", "duck_ro");
        Assert.NotEmpty(batches);
    }

    [Fact]
    public async Task Off_skips_the_check()
    {
        using var client = await LoginAsync();
        var (_, batches) = await client.QueryAsync("PRAGMA version", "duck_off");
        Assert.NotEmpty(batches);
    }

    [Fact]
    public async Task Writable_sources_are_not_classified()
    {
        using var client = await LoginAsync();
        // 'duck' has AllowWrites, so the classifier does not apply (it is the update path's own gate that matters there).
        var (_, batches) = await client.QueryAsync("PRAGMA version", "duck");
        Assert.NotEmpty(batches);
    }

    [Fact]
    public async Task Updates_stay_refused_on_a_read_only_source_whatever_the_classifier_says()
    {
        using var client = await LoginAsync();
        foreach (var source in new[] { "duck_strict", "duck_off", "duck_ro" })
        {
            var ex = await Rpc.ExpectAsync(() => client.RawUpdateAsync("DELETE FROM prices", source));
            Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        }
        await AssertPricesIntactAsync(client);
    }

    private static async Task AssertPricesIntactAsync(ProxyClient client)
    {
        var (_, batches) = await client.QueryAsync("SELECT count(*) AS n FROM prices", "duck");
        Assert.Equal(1L, ((Int64Array)Assert.Single(batches).Column("n")).GetValue(0));
    }
}
