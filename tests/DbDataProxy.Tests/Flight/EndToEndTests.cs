using Apache.Arrow;
using DbDataProxy.Tests.Support;
using Grpc.Core;

namespace DbDataProxy.Tests.Flight;

public class EndToEndTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<ProxyClient> LoginAsync(string user = "alice", string password = ProxyFixture.AlicePassword)
    {
        var client = proxy.Connect();
        await client.AuthenticateAsync(user, password);
        return client;
    }

    [Fact]
    public async Task Handshake_issues_a_bearer_token()
    {
        using var client = await LoginAsync();
        Assert.StartsWith("Bearer ", client.Bearer);
    }

    [Fact]
    public async Task Handshake_rejects_wrong_password()
    {
        using var client = proxy.Connect();
        var ex = await Rpc.ExpectAsync(() => client.AuthenticateAsync("alice", "wrong"));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Queries_without_credentials_are_rejected()
    {
        using var client = proxy.Connect();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT 1"));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Queries_with_a_forged_token_are_rejected()
    {
        using var client = proxy.Connect();
        client.UseHeader("Bearer not-a-real-token");
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT 1"));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Streams_query_results_in_multiple_batches()
    {
        using var client = await LoginAsync();
        var (schema, batches) = await client.QueryAsync("SELECT id, name, score, day FROM numbers ORDER BY id");

        Assert.Equal(["id", "name", "score", "day"], schema.FieldsList.Select(f => f.Name));
        Assert.Equal(10000, batches.Sum(b => b.Length));
        Assert.Equal(0, ((Int32Array)batches[0].Column("id")).GetValue(0));
        Assert.Equal("name-9999", ((StringArray)batches[^1].Column("name")).GetString(batches[^1].Length - 1));
    }

    [Fact]
    public async Task User_cannot_query_a_data_source_they_were_not_granted()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT 1", dataSource: "other"));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task Unknown_data_source_is_reported()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT 1", dataSource: "nope"));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Sql_errors_surface_as_invalid_argument()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELEKT nonsense"));
        Assert.True(ex.StatusCode == StatusCode.InvalidArgument, $"{ex.StatusCode}: {ex.Status.Detail}");
    }

    [Fact]
    public async Task Updates_run_on_writable_sources_and_report_row_counts()
    {
        using var client = await LoginAsync();
        await client.UpdateAsync("CREATE TABLE IF NOT EXISTS scratch(i INTEGER)");
        long affected = await client.UpdateAsync("INSERT INTO scratch SELECT * FROM range(0, 5)");
        var (_, batches) = await client.QueryAsync("SELECT count(*) AS n FROM scratch");
        Assert.Equal(5L, ((Int64Array)batches[0].Column("n")).GetValue(0));
        _ = affected; // the .NET client does not surface the count of prepared updates; see the raw test below
    }

    [Fact]
    public async Task Statement_updates_report_row_counts_to_standard_clients()
    {
        using var client = await LoginAsync();
        await client.RawUpdateAsync("CREATE TABLE IF NOT EXISTS scratch2(i INTEGER)");
        Assert.Equal(7, await client.RawUpdateAsync("INSERT INTO scratch2 SELECT * FROM range(0, 7)"));
    }

    [Fact]
    public async Task Updates_are_refused_on_read_only_sources()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.RawUpdateAsync("CREATE TABLE nope(i INTEGER)", dataSource: "duck_ro"));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }
}
