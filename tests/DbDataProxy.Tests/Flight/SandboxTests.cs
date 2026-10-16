using Apache.Arrow;
using DbDataProxy.Tests.Support;
using Grpc.Core;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// The DuckDB sandbox of a read-only source (read-only file, no external file access, no extensions, locked settings) proven at the engine: these sources
/// have <c>Classifier = off</c>, so the lexical check does not run and what stops each statement is DuckDB itself (S-1).
/// </summary>
public class SandboxTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<ProxyClient> LoginAsync()
    {
        var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        return client;
    }

    private static string Sql(string path) => path.Replace('\\', '/');

    private string Dir => Sql(Path.GetDirectoryName(proxy.DuckDbPath)!);

    public static TheoryData<string> Hostile =>
    [
        "ATTACH '{dir}/evil.duckdb' AS evil",
        "SELECT * FROM read_csv('{private}')",
        "SELECT * FROM read_csv_auto('{private}')",
        "SELECT * FROM read_text('{private}')",
        "SELECT * FROM read_parquet('{dir}/nothing.parquet')",
        "SELECT * FROM '{private}'",
        "SELECT * FROM glob('{dir}/*')",
        "COPY prices TO '{dir}/out.csv'",
        "COPY (SELECT 1 AS a) TO '{dir}/out2.csv'",
        "INSTALL httpfs",
        "LOAD httpfs",
        "INSTALL 'C:/nope/evil.duckdb_extension'",
        "SET enable_external_access = true",
        "SET lock_configuration = false",
        "SET autoinstall_known_extensions = true",
        "INSERT INTO prices VALUES (9, 9, 'x')",
        "CREATE TABLE evil(a INT)",
        "DROP TABLE prices",
    ];

    private string Expand(string sql) => sql.Replace("{private}", Sql(proxy.PrivateCsv)).Replace("{dir}", Dir);

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task Hostile_statements_fail_at_the_engine_on_a_read_only_source_with_the_classifier_off(string template)
    {
        using var client = await LoginAsync();
        string sql = Expand(template);

        var ex = await Rpc.ExpectAsync(() => client.QueryAsync(sql, "duck_off"));

        Assert.NotEqual(StatusCode.OK, ex.StatusCode);
        // The client gets a kind and an engine error class, never the engine's own text (which would quote the path or the file's content).
        Assert.DoesNotContain("hunter2", ex.Status.Detail);
        Assert.DoesNotContain(Dir, ex.Status.Detail);
        Assert.DoesNotContain("evil", ex.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(proxy.DuckDbPath)!, "out.csv")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(proxy.DuckDbPath)!, "out2.csv")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(proxy.DuckDbPath)!, "evil.duckdb")));
    }

    [Fact]
    public async Task The_same_file_is_readable_on_a_writable_source_so_it_is_the_sandbox_that_blocks_it()
    {
        using var client = await LoginAsync();
        var (_, batches) = await client.QueryAsync($"SELECT secret FROM read_csv('{Sql(proxy.PrivateCsv)}')", "duck");
        Assert.Equal("hunter2", ((StringArray)Assert.Single(batches).Column("secret")).GetString(0));
    }

    [Fact]
    public async Task Ordinary_queries_still_work_in_the_sandbox()
    {
        using var client = await LoginAsync();
        var (_, batches) = await client.QueryAsync("SELECT count(*) AS n FROM prices", "duck_off");
        Assert.Equal(1L, ((Int64Array)Assert.Single(batches).Column("n")).GetValue(0));
        Assert.Contains("prices", Assert.Single(await RowsAsync(client, "SELECT table_name FROM information_schema.tables WHERE table_name = 'prices'", "duck_off")));
    }

    [Fact]
    public async Task Allowed_directories_open_exactly_one_door()
    {
        using var client = await LoginAsync();

        var (_, batches) = await client.QueryAsync($"SELECT CAST(sum(b) AS BIGINT) AS total FROM read_csv('{Sql(Path.Combine(proxy.LakeDirectory, "lake.csv"))}')", "duck_lake");
        Assert.Equal(6L, Convert.ToInt64(((Int64Array)Assert.Single(batches).Column("total")).GetValue(0)));

        foreach (var sql in new[]
        {
            $"SELECT * FROM read_csv('{Sql(proxy.PrivateCsv)}')",
            $"ATTACH '{Dir}/evil.duckdb' AS evil",
            "INSTALL httpfs",
            "SET allowed_directories = []",
            "SET enable_external_access = true",
            "INSERT INTO prices VALUES (9, 9, 'x')",
        })
        {
            var ex = await Rpc.ExpectAsync(() => client.QueryAsync(sql, "duck_lake"));
            Assert.NotEqual(StatusCode.OK, ex.StatusCode);
        }
    }

    private static async Task<List<string>> RowsAsync(ProxyClient client, string sql, string source)
    {
        var (_, batches) = await client.QueryAsync(sql, source);
        return batches.SelectMany(b => Enumerable.Range(0, b.Length).Select(i => DbDataProxy.Data.ArrowText.Get(b.Column(0), i) ?? "")).ToList();
    }
}
