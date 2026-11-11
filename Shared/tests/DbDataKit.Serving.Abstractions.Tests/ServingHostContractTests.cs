using Apache.Arrow;

namespace DbDataKit.Serving.Tests;

/// <summary>What anything that implements <see cref="IServingHost"/> promises a protocol, shown on the fake host (plan 19, N01); N03 turns these into a suite that runs against every real host.</summary>
public class ServingHostContractTests
{
    private readonly FakeServingHost _host = new();

    private async Task<ServingCaller> Admin() => (await _host.AuthenticateAsync(new ServingCredentials.Password("admin", "admin-pw")))!;
    private async Task<ServingCaller> Reader() => (await _host.AuthenticateAsync(new ServingCredentials.Password("reader", "reader-pw")))!;

    private static async Task<(List<int> Ids, ServingException? Stopped)> Read(IServingResult result)
    {
        var ids = new List<int>();
        try
        {
            await foreach (var batch in result.ReadBatchesAsync()) ids.AddRange(((Int32Array)batch.Column(0)).Values.ToArray());
            return (ids, null);
        }
        catch (ServingException ex) { return (ids, ex); }
    }

    [Fact]
    public async Task Each_kind_of_credentials_identifies_a_caller_or_nobody()
    {
        Assert.Equal("admin", (await _host.AuthenticateAsync(new ServingCredentials.Password("admin", "admin-pw")))!.Name);
        Assert.Equal("reader", (await _host.AuthenticateAsync(new ServingCredentials.Bearer("token-reader")))!.Name);
        Assert.Equal("admin", (await _host.AuthenticateAsync(new ServingCredentials.Windows("CORP\\admin")))!.Name);

        Assert.Null(await _host.AuthenticateAsync(new ServingCredentials.Password("admin", "wrong")));
        Assert.Null(await _host.AuthenticateAsync(new ServingCredentials.Password("nobody", "x")));
        Assert.Null(await _host.AuthenticateAsync(new ServingCredentials.Bearer("junk")));
        Assert.Null(await _host.AuthenticateAsync(new ServingCredentials.Windows("CORP\\someone")));
    }

    [Fact]
    public async Task A_caller_is_told_about_the_tables_they_may_query_and_no_others()
    {
        var all = await _host.DescribeAsync(await Admin(), new ServingCatalogRequest()).Select(t => $"{t.Schema}.{t.Name}").ToListAsync();
        var mine = await _host.DescribeAsync(await Reader(), new ServingCatalogRequest()).Select(t => $"{t.Schema}.{t.Name}").ToListAsync();

        Assert.Equal(["public.items", "secret.salaries"], all);
        Assert.Equal(["public.items"], mine);
        Assert.Equal(["secret.salaries"], await _host.DescribeAsync(await Admin(), new ServingCatalogRequest(Schema: "secret")).Select(t => $"{t.Schema}.{t.Name}").ToListAsync());
        Assert.Equal(2, (await _host.DescribeAsync(await Admin(), new ServingCatalogRequest(Table: "items")).SingleAsync()).Columns.FieldsList.Count);
    }

    [Fact]
    public async Task A_query_runs_as_the_caller_in_batches_and_an_unlimited_caller_gets_every_row()
    {
        await using var result = await _host.QueryAsync(await Admin(), new ServingQuery("main", "public.items"));

        Assert.Equal(["id", "name"], result.Schema.FieldsList.Select(f => f.Name).ToList());
        var (ids, stopped) = await Read(result);
        Assert.Equal([1, 2, 3, 4, 5], ids);
        Assert.Null(stopped);
    }

    [Fact]
    public async Task A_limit_ends_the_result_with_an_error_that_says_so_never_silently()
    {
        var reader = await Reader();
        Assert.Equal(FakeServingHost.ReaderRowLimit, _host.LimitsFor(reader, "main").MaxRows);

        await using var result = await _host.QueryAsync(reader, new ServingQuery("main", "public.items"));
        var (ids, stopped) = await Read(result);

        Assert.Equal([1, 2, 3], ids);                                  // what the caller may have, then the stop
        var limit = Assert.IsType<ServingLimitException>(stopped);
        Assert.Equal("rows", limit.Limit);
    }

    [Fact]
    public async Task A_query_can_ask_for_less_than_the_limit_but_never_more()
    {
        await using var fewer = await _host.QueryAsync(await Reader(), new ServingQuery("main", "public.items", Asked: new ServingLimits(MaxRows: 1)));
        var (ids, stopped) = await Read(fewer);
        Assert.Equal([1], ids);
        Assert.IsType<ServingLimitException>(stopped);

        await using var more = await _host.QueryAsync(await Reader(), new ServingQuery("main", "public.items", Asked: new ServingLimits(MaxRows: 1000)));
        Assert.Equal([1, 2, 3], (await Read(more)).Ids);
    }

    [Fact]
    public async Task What_the_caller_may_not_have_what_is_not_there_and_what_is_not_a_query_are_different_errors()
    {
        await Assert.ThrowsAsync<ServingDeniedException>(async () => await _host.QueryAsync(await Reader(), new ServingQuery("main", "secret.salaries")));
        await Assert.ThrowsAsync<ServingNotFoundException>(async () => await _host.QueryAsync(await Admin(), new ServingQuery("main", "public.nothing")));
        await Assert.ThrowsAsync<ServingNotFoundException>(async () => await _host.QueryAsync(await Admin(), new ServingQuery("other", "public.items")));
        await Assert.ThrowsAsync<ServingInvalidException>(async () => await _host.QueryAsync(await Admin(), new ServingQuery("main", " ")));
    }

    [Fact]
    public async Task A_host_without_a_write_path_says_so_and_one_with_it_applies_its_own_rules()
    {
        Assert.Null(_host.Writes);

        var writing = new FakeServingHost(withWrites: true);
        var admin = (await writing.AuthenticateAsync(new ServingCredentials.Password("admin", "admin-pw")))!;
        var reader = (await writing.AuthenticateAsync(new ServingCredentials.Password("reader", "reader-pw")))!;
        Assert.Equal(1, (await writing.Writes!.ExecuteAsync(admin, new ServingQuery("main", "update items"))).Rows);
        await Assert.ThrowsAsync<ServingDeniedException>(() => writing.Writes.ExecuteAsync(reader, new ServingQuery("main", "update items")));

        static async IAsyncEnumerable<RecordBatch> One()
        {
            yield return new RecordBatch(new Schema.Builder().Field(new Apache.Arrow.Field("id", Apache.Arrow.Types.Int32Type.Default, false)).Build(), [new Int32Array.Builder().Append(7).Build()], 1);
            await Task.CompletedTask;
        }
        Assert.Equal(1, (await writing.Writes.IngestAsync(admin, "main", "items", One())).Rows);
        Assert.Equal(["update items", "ingest items"], writing.Written);
    }
}
