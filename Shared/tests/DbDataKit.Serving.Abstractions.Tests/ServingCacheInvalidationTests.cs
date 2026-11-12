using Apache.Arrow;
using Apache.Arrow.Types;
using Xunit;

namespace DbDataKit.Serving.Tests;

/// <summary>Plan 24a UC13 and UC14: the <c>data.changed/1</c> message, clearing the cache by dataset, the shared epochs that keep the instances of a group in step, and the per-dataset counts of the console.</summary>
public class ServingCacheInvalidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_message_is_read_and_written_and_carries_a_dataset_and_nothing_else()
    {
        var notice = new DataChangedNotice("sqlserver://sql1/sales.dbo.orders", "sync", Now, "run-7");
        Assert.True(DataChangedMessage.TryRead(DataChangedMessage.Write(notice), Now, out var read, out var problem), problem);
        Assert.Equal(notice, read);

        Assert.True(DataChangedMessage.TryRead("""{"message":"data.changed/1","dataset":"a/b","product":"build"}""", Now, out var minimal, out _));
        Assert.Equal(new DataChangedNotice("a/b", "build", Now), minimal);
    }

    [Theory]
    [InlineData("", "JSON object")]
    [InlineData("[]", "JSON object")]
    [InlineData("""{"message":"data.changed/2","dataset":"a","product":"sync"}""", "must be `data.changed/1`")]
    [InlineData("""{"message":"data.changed/1","product":"sync"}""", "`dataset`")]
    [InlineData("""{"message":"data.changed/1","dataset":"a"}""", "`product`")]
    [InlineData("""{"message":"data.changed/1","dataset":"a","product":"sync","rows":[1,2]}""", "`rows` is not a key")]
    [InlineData("""{"message":"data.changed/1","dataset":"a","product":"sync","sql":"select 1"}""", "carries no row data")]
    [InlineData("""{"message":"data.changed/1","dataset":"a","product":"sync","at":"yesterday"}""", "`at`")]
    public void A_message_that_is_not_valid_is_refused_naming_what_is_wrong(string text, string expected)
    {
        Assert.False(DataChangedMessage.TryRead(text, Now, out _, out var problem));
        Assert.Contains(expected, problem);
    }

    private static async Task<MemoryServingCache> Filled(params (string Source, string[] Datasets)[] entries)
    {
        var cache = new MemoryServingCache();
        var ctx = new ServingContext(new ServingCaller("u", "u"));
        foreach (var (source, datasets) in entries)
        {
            var plan = new ServingCachePlan(TimeSpan.FromMinutes(5), ServingCacheScope.Shared, "1", datasets);
            var queries = new CachingServingQueries(new Queries(), cache, new Fixed(plan));
            await using var answer = await queries.RunAsync(ctx, new ServingRequest.Sql(source, "select 1"));
            await foreach (var _ in answer.ReadBatchesAsync()) { }
        }

        return cache;
    }

    [Fact]
    public async Task A_change_clears_the_entries_that_read_the_dataset_and_no_others()
    {
        var cache = await Filled(("orders", ["ds/orders", "ds/customers"]), ("hr", ["ds/people"]));
        var invalidator = new ServingCacheInvalidator(cache, dataset => dataset == "sqlserver://sql1/sales.dbo.orders" ? ["ds/orders"] : null, time: new Fixed(Now));

        var outcome = invalidator.Apply(new DataChangedNotice("sqlserver://sql1/sales.dbo.orders", "sync", Now));

        Assert.Equal((1, false), (outcome.Removed, outcome.Everything));
        Assert.Equal(1, cache.Stats.Entries);
        Assert.Equal(new ServingCacheActivity(1, 1, Now, "sync", "sqlserver://sql1/sales.dbo.orders", 0, null), invalidator.Activity);
    }

    [Fact]
    public async Task A_dataset_the_host_cannot_map_clears_everything_because_stale_rows_are_worse_than_a_cold_cache()
    {
        var cache = await Filled(("orders", ["ds/orders"]), ("hr", ["ds/people"]));
        var invalidator = new ServingCacheInvalidator(cache, _ => null);

        var outcome = invalidator.Apply(new DataChangedNotice("mysql://x/unknown", "build", Now));

        Assert.True(outcome.Everything);
        Assert.Equal(2, outcome.Removed);
        Assert.Equal(0, cache.Stats.Entries);
    }

    [Fact]
    public async Task A_message_that_says_only_if_served_clears_nothing_for_a_dataset_the_host_does_not_serve_and_counts_it_apart()
    {
        var cache = await Filled(("orders", ["ds/orders"]), ("hr", ["ds/people"]));
        var invalidator = new ServingCacheInvalidator(cache, dataset => dataset == "sqlserver://sql1/sales.dbo.orders" ? ["ds/orders"] : null, time: new Fixed(Now));

        var ignored = invalidator.Apply(new DataChangedNotice("mysql://elsewhere/other", "sync", Now, OnlyIfServed: true));

        Assert.Equal((0, false, true), (ignored.Removed, ignored.Everything, ignored.Ignored));
        Assert.Equal(2, cache.Stats.Entries);
        Assert.Equal((0L, 1L), (invalidator.Activity.Received, invalidator.Activity.Ignored));

        var served = invalidator.Apply(new DataChangedNotice("sqlserver://sql1/sales.dbo.orders", "sync", Now, OnlyIfServed: true));
        Assert.Equal((1, false), (served.Removed, served.Ignored));
        Assert.Equal((1L, 1L), (invalidator.Activity.Received, invalidator.Activity.Ignored));
    }

    [Fact]
    public async Task A_change_while_a_query_runs_keeps_that_answer_out_of_the_cache()
    {
        var cache = new MemoryServingCache();
        var plan = new ServingCachePlan(TimeSpan.FromMinutes(5), ServingCacheScope.Shared, "1", ["ds/orders"]);
        var queries = new CachingServingQueries(new Queries(), cache, new Fixed(plan));
        var invalidator = new ServingCacheInvalidator(cache, _ => ["ds/orders"]);

        await using (var answer = await queries.RunAsync(new ServingContext(new ServingCaller("u", "u")), new ServingRequest.Sql("orders", "select 1")))
        {
            invalidator.Apply(new DataChangedNotice("x/y", "sync", Now));
            await foreach (var _ in answer.ReadBatchesAsync()) { }
        }

        Assert.Equal(0, cache.Stats.Entries);
    }

    [Fact]
    public async Task An_administrator_clears_one_dataset_or_everything_and_it_is_counted_apart_from_the_changes()
    {
        var cache = await Filled(("orders", ["ds/orders"]), ("hr", ["ds/people"]));
        var invalidator = new ServingCacheInvalidator(cache, _ => null, time: new Fixed(Now));

        Assert.Equal(1, invalidator.ClearByHand("ds/orders", "ada"));
        Assert.Equal(1, cache.Stats.Entries);
        Assert.Equal(1, invalidator.ClearByHand(null, "ada"));

        var activity = invalidator.Activity;
        Assert.Equal((0L, 2L, 2L, Now), (activity.Received, activity.Cleared, activity.Manual, activity.LastManualAt));
    }

    [Fact]
    public async Task A_change_that_reached_one_instance_clears_the_others_when_they_poll()
    {
        var shared = new Epochs();
        var one = await Filled(("orders", ["ds/orders"]), ("hr", ["ds/people"]));
        var two = await Filled(("orders", ["ds/orders"]), ("hr", ["ds/people"]));
        var first = new ServingCacheInvalidator(one, _ => ["ds/orders"], shared);
        var second = new ServingCacheInvalidator(two, _ => ["ds/orders"], shared);
        Assert.Equal(0, second.Poll());   // the first look only learns the epochs

        first.Apply(new DataChangedNotice("x/y", "build", Now));

        Assert.Equal(1, one.Stats.Entries);
        Assert.Equal(2, two.Stats.Entries);
        Assert.Equal(1, second.Poll());
        Assert.Equal(1, two.Stats.Entries);
        Assert.Equal(0, second.Poll());   // the same epoch twice changes nothing
        Assert.Equal(0, first.Poll());    // and the instance that made the change does not clear again
        Assert.Equal(1, one.Stats.Entries);
    }

    [Fact]
    public async Task A_clear_of_everything_and_a_hand_clear_reach_the_group_too()
    {
        var shared = new Epochs();
        var one = await Filled(("orders", ["ds/orders"]));
        var two = await Filled(("orders", ["ds/orders"]), ("hr", ["ds/people"]));
        var first = new ServingCacheInvalidator(one, _ => null, shared);
        var second = new ServingCacheInvalidator(two, _ => null, shared);
        second.Poll();

        first.Apply(new DataChangedNotice("unknown/x", "sync", Now));
        Assert.Equal(2, second.Poll());
        Assert.Equal(0, two.Stats.Entries);
        Assert.Contains(ServingCacheEpochs.All, shared.Read().Keys);
    }

    [Fact]
    public async Task A_shared_record_that_is_down_is_a_cold_start_and_never_an_error()
    {
        var shared = new Epochs { Down = true };
        var cache = await Filled(("orders", ["ds/orders"]));
        var invalidator = new ServingCacheInvalidator(cache, _ => ["ds/orders"], shared);

        Assert.Equal(1, invalidator.Apply(new DataChangedNotice("x/y", "sync", Now)).Removed);
        Assert.Equal(0, invalidator.Poll());
    }

    [Fact]
    public void An_epoch_is_later_than_the_one_before_and_than_the_clock()
    {
        Assert.Equal(Now.ToUnixTimeMilliseconds() * 1000, ServingCacheEpochs.Next(5, Now));
        Assert.Equal(long.MaxValue / 2 + 1, ServingCacheEpochs.Next(long.MaxValue / 2, Now));
    }

    [Fact]
    public async Task The_cache_counts_hits_misses_and_entries_by_dataset()
    {
        var cache = new MemoryServingCache();
        var plan = new ServingCachePlan(TimeSpan.FromMinutes(5), ServingCacheScope.Shared, "1", ["ds/orders"]);
        var queries = new CachingServingQueries(new Queries(), cache, new Fixed(plan));
        var ctx = new ServingContext(new ServingCaller("u", "u"));
        for (var i = 0; i < 3; i++)
        {
            await using var answer = await queries.RunAsync(ctx, new ServingRequest.Sql("orders", "select 1"));
            await foreach (var _ in answer.ReadBatchesAsync()) { }
        }

        var stats = Assert.Single(cache.DatasetStats);
        Assert.Equal(("ds/orders", 1, 2L, 1L), (stats.Dataset, stats.Entries, stats.Hits, stats.Misses));
    }

    [Theory]
    [InlineData("""{"kind":"query","query":{}}""", true)]
    [InlineData("""{"kind":"describe"}""", false)]
    [InlineData("""{"kind":"members"}""", false)]
    [InlineData("not json", false)]
    public void Only_a_semantic_query_document_can_be_cached(string json, bool expected)
    {
        Assert.Equal(expected, ServingCacheable.IsRead(new ServingRequest.Semantic("semantic:m", json)));
        if (json.StartsWith('{')) Assert.Equal(expected, ServingCacheable.IsRead(new ServingRequest.Sql("semantic:m", json)));
    }

    [Theory]
    [InlineData("select 1", true)]
    [InlineData("  WITH x AS (SELECT 1) SELECT * FROM x", true)]
    [InlineData("EXPLAIN SELECT 1", false)]
    [InlineData("CALL saved_query('a')", false)]
    public void Semantic_sql_is_cached_only_when_it_is_a_select(string text, bool expected) =>
        Assert.Equal(expected, ServingCacheable.IsRead(new ServingRequest.SemanticSql("semantic:m", text)));

    [Fact]
    public void Mdx_is_never_cached() => Assert.False(ServingCacheable.IsRead(new ServingRequest.Mdx("semantic:m", "SELECT")));

    [Fact]
    public async Task The_services_with_a_cache_answer_a_second_time_from_it_and_leave_the_rest_alone()
    {
        var queries = new Queries();
        var plan = new ServingCachePlan(TimeSpan.FromMinutes(5), ServingCacheScope.Shared, "1", ["ds/orders"]);
        var inner = new Services(queries);
        var services = inner.WithCache(new MemoryServingCache(), new Fixed(plan));
        var ctx = new ServingContext(new ServingCaller("u", "u"));

        for (var i = 0; i < 2; i++)
        {
            await using var answer = await services.Queries.RunAsync(ctx, new ServingRequest.Sql("orders", "select 1"));
            await foreach (var _ in answer.ReadBatchesAsync()) { }
        }

        Assert.Equal(1, queries.Runs);
        Assert.Same(inner.Identity, services.Identity);
        Assert.Same(inner.Catalog, services.Catalog);
    }

    private sealed class Epochs : IServingCacheEpochs
    {
        private readonly Dictionary<string, long> _epochs = new(StringComparer.Ordinal);
        public bool Down;

        public long Bump(string dataset, string product, string reason)
        {
            if (Down) throw new IOException("down");
            return _epochs[dataset] = ServingCacheEpochs.Next(_epochs.GetValueOrDefault(dataset), DateTimeOffset.UtcNow);
        }

        public IReadOnlyDictionary<string, long> Read() => Down ? throw new IOException("down") : new Dictionary<string, long>(_epochs);
    }

    private sealed class Fixed(object value) : TimeProvider, IServingCachePolicy
    {
        public override DateTimeOffset GetUtcNow() => value is DateTimeOffset now ? now : DateTimeOffset.UtcNow;

        public ServingCachePlan? PlanFor(ServingContext context, ServingRequest request) =>
            value as ServingCachePlan;
    }

    private sealed class Queries : IServingQueries
    {
        public int Runs;

        public Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.FromResult<IServingAnswer>(new Answer(new ServingProvenance("select 1", "1", context.Channel, request.Source)));
        }

        public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Answer(ServingProvenance provenance) : IServingAnswer
    {
        private static readonly Schema Columns = new Schema.Builder().Field(new Field("n", Int32Type.Default, false)).Build();

        public ServingProvenance Provenance { get; } = provenance;

        public Schema Schema => Columns;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new RecordBatch(Columns, [new Int32Array.Builder().Append(1).Build()], 1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Services(IServingQueries queries) : IServingServices
    {
        public IServingIdentity Identity { get; } = null!;
        public IServingSessions Sessions { get; } = null!;
        public IServingQueries Queries => queries;
        public IServingCatalog Catalog { get; } = null!;
        public IServingEntities? Entities => null;
        public IServingUpdates? Updates => null;
    }
}
