using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace DbDataKit.Serving.Tests;

/// <summary>Plan 24a UC12: the result cache and its key. Off unless a plan says so; the key holds the access, channel, preview and version; a cold or lost cache changes no result.</summary>
public class ServingCacheTests
{
    private static readonly ServingCachePlan Rules = new(TimeSpan.FromMinutes(5), ServingCacheScope.Rules, "7", ["sales"]);

    private static ServingCaller Caller(string id, params (string Name, string[] Values)[] attributes) =>
        new(id, id) { Attributes = attributes.ToDictionary(a => a.Name, a => (IReadOnlyList<string>)a.Values) };

    private static ServingContext Context(ServingCaller caller, string channel = "prod", string? preview = null) => new(caller, channel, preview);

    private static ServingRequest.Sql Sql(string text = "select 1") => new("main", text);

    [Fact]
    public void Two_callers_with_the_same_effective_access_share_an_entry_and_different_values_do_not()
    {
        var east1 = Context(Caller("u1", ("region", ["east", "west"])));
        var east2 = Context(Caller("u2", ("region", ["west", "east", "east"])));
        var north = Context(Caller("u3", ("region", ["north"])));
        var none = Context(Caller("u4"));

        Assert.Equal(ServingCacheKey.Build(east1, Sql(), Rules), ServingCacheKey.Build(east2, Sql(), Rules));
        Assert.NotEqual(ServingCacheKey.Build(east1, Sql(), Rules), ServingCacheKey.Build(north, Sql(), Rules));
        Assert.NotEqual(ServingCacheKey.Build(east1, Sql(), Rules), ServingCacheKey.Build(none, Sql(), Rules));
        Assert.NotEqual(ServingCacheKey.Build(north, Sql(), Rules), ServingCacheKey.Build(none, Sql(), Rules));
    }

    [Fact]
    public void Scope_caller_keeps_callers_apart_and_scope_shared_does_not()
    {
        var a = Context(Caller("a"));
        var b = Context(Caller("b"));
        var perCaller = Rules with { Scope = ServingCacheScope.Caller };
        var shared = Rules with { Scope = ServingCacheScope.Shared };

        Assert.NotEqual(ServingCacheKey.Build(a, Sql(), perCaller), ServingCacheKey.Build(b, Sql(), perCaller));
        Assert.Equal(ServingCacheKey.Build(a, Sql(), shared), ServingCacheKey.Build(b, Sql(), shared));
        Assert.NotEqual(ServingCacheKey.Build(a, Sql(), perCaller), ServingCacheKey.Build(a, Sql(), shared));
    }

    [Fact]
    public void Channels_previews_versions_and_the_query_make_different_keys()
    {
        var caller = Caller("u1");
        var prod = ServingCacheKey.Build(Context(caller), Sql(), Rules);

        Assert.NotEqual(prod, ServingCacheKey.Build(Context(caller, "staging"), Sql(), Rules));
        Assert.NotEqual(prod, ServingCacheKey.Build(Context(caller, preview: "draft-1"), Sql(), Rules));
        Assert.NotEqual(ServingCacheKey.Build(Context(caller, preview: "draft-1"), Sql(), Rules), ServingCacheKey.Build(Context(caller, preview: "draft-2"), Sql(), Rules));
        Assert.NotEqual(prod, ServingCacheKey.Build(Context(caller), Sql(), Rules with { DefinitionVersion = "8" }));
        Assert.NotEqual(prod, ServingCacheKey.Build(Context(caller), Sql("select 2"), Rules));
        Assert.NotEqual(prod, ServingCacheKey.Build(Context(caller), new ServingRequest.Sql("other", "select 1"), Rules));
        Assert.NotEqual(prod, ServingCacheKey.Build(Context(caller), new ServingRequest.Sql("main", "select 1", [new ServingParameter(null, "int64", "1")]), Rules));
        Assert.NotEqual(prod, ServingCacheKey.Build(Context(caller), new ServingRequest.Sql("main", "select 1", null, new ServingLimits(MaxRows: 1)), Rules));
        Assert.Equal(prod, ServingCacheKey.Build(Context(caller), Sql(), Rules));
    }

    [Fact]
    public void A_field_boundary_cannot_be_forged()
    {
        var ctx = Context(Caller("u1"));
        Assert.NotEqual(
            ServingCacheKey.Build(ctx, new ServingRequest.Sql("a", "bc"), Rules),
            ServingCacheKey.Build(ctx, new ServingRequest.Sql("ab", "c"), Rules));
    }

    [Fact]
    public async Task A_dataset_without_a_plan_is_never_stored()
    {
        var inner = new CountingQueries();
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(inner, cache, NoServingCache.Instance);

        for (var i = 0; i < 3; i++) await Drain(await queries.RunAsync(Context(Caller("u1")), Sql()));

        Assert.Equal(3, inner.Runs);
        Assert.Equal(0, cache.Stats.Entries);
        Assert.Equal(0, cache.Stats.Hits + cache.Stats.Misses);
    }

    [Fact]
    public async Task A_hit_replays_the_batches_of_the_answer_byte_for_byte_and_says_so_in_the_provenance()
    {
        var inner = new CountingQueries();
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules));
        var ctx = Context(Caller("u1"));

        var (first, firstProvenance) = await Drain(await queries.RunAsync(ctx, Sql()));
        var (second, secondProvenance) = await Drain(await queries.RunAsync(ctx, Sql()));
        var uncached = await Drain(await new CachingServingQueries(inner, cache, NoServingCache.Instance).RunAsync(ctx, Sql()));

        Assert.Equal(2, inner.Runs);
        Assert.Equal(first, second);
        Assert.Equal(first, uncached.Bytes);
        Assert.Null(firstProvenance.Cache);
        Assert.Equal("hit", secondProvenance.Cache);
        Assert.Equal(firstProvenance with { Cache = null }, secondProvenance with { Cache = null });
        Assert.Equal(new ServingCacheStats(1, 1, 1, cache.Stats.Bytes, 0, 0), cache.Stats);
    }

    [Fact]
    public async Task Dropping_the_cache_gives_byte_identical_results()
    {
        var inner = new CountingQueries();
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules));
        var ctx = Context(Caller("u1"));
        var before = await Drain(await queries.RunAsync(ctx, Sql()));
        var warm = await Drain(await queries.RunAsync(ctx, Sql()));

        Assert.Equal(1, cache.Clear());
        var cold = await Drain(await queries.RunAsync(ctx, Sql()));

        Assert.Equal(before.Bytes, warm.Bytes);
        Assert.Equal(before.Bytes, cold.Bytes);
        Assert.Equal(2, inner.Runs);
    }

    [Fact]
    public async Task Callers_with_different_attributes_do_not_see_each_others_entries()
    {
        var inner = new CountingQueries();
        var queries = new CachingServingQueries(inner, new MemoryServingCache(), new FixedPolicy(Rules));
        var east = Context(Caller("u1", ("region", ["east"])));
        var west = Context(Caller("u2", ("region", ["west"])));

        await Drain(await queries.RunAsync(east, Sql()));
        var other = await Drain(await queries.RunAsync(west, Sql()));
        var again = await Drain(await queries.RunAsync(east, Sql()));

        Assert.Equal(2, inner.Runs);
        Assert.Null(other.Provenance.Cache);
        Assert.Equal("hit", again.Provenance.Cache);
    }

    [Fact]
    public async Task A_preview_does_not_share_with_its_channel_and_a_channel_does_not_share_with_another()
    {
        var inner = new CountingQueries();
        var queries = new CachingServingQueries(inner, new MemoryServingCache(), new FixedPolicy(Rules));
        var caller = Caller("u1");

        await Drain(await queries.RunAsync(Context(caller), Sql()));
        await Drain(await queries.RunAsync(Context(caller, preview: "draft"), Sql()));
        await Drain(await queries.RunAsync(Context(caller, "staging"), Sql()));

        Assert.Equal(3, inner.Runs);
    }

    [Fact]
    public async Task An_entry_past_its_time_to_live_is_a_miss()
    {
        var time = new ManualTime();
        var inner = new CountingQueries();
        var cache = new MemoryServingCache(time: time);
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules with { Ttl = TimeSpan.FromMinutes(5) }));
        var ctx = Context(Caller("u1"));

        await Drain(await queries.RunAsync(ctx, Sql()));
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("hit", (await Drain(await queries.RunAsync(ctx, Sql()))).Provenance.Cache);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null((await Drain(await queries.RunAsync(ctx, Sql()))).Provenance.Cache);
        Assert.Equal(2, inner.Runs);
    }

    [Fact]
    public async Task An_answer_that_fails_while_it_streams_is_not_stored()
    {
        var inner = new CountingQueries { FailAfterFirstBatch = true };
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules));

        await Assert.ThrowsAsync<ServingLimitException>(async () => await Drain(await queries.RunAsync(Context(Caller("u1")), Sql())));

        Assert.Equal(0, cache.Stats.Entries);
    }

    [Fact]
    public async Task An_answer_read_only_in_part_is_not_stored()
    {
        var inner = new CountingQueries();
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules));

        await using (var answer = await queries.RunAsync(Context(Caller("u1")), Sql()))
        {
            await foreach (var _ in answer.ReadBatchesAsync()) break;
        }

        Assert.Equal(0, cache.Stats.Entries);
    }

    [Fact]
    public async Task A_clear_while_the_query_runs_keeps_the_stale_answer_out()
    {
        var inner = new CountingQueries();
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules));
        var ctx = Context(Caller("u1"));

        await using (var answer = await queries.RunAsync(ctx, Sql()))
        {
            Assert.Equal(0, cache.Invalidate("sales"));
            await foreach (var _ in answer.ReadBatchesAsync()) { }
        }

        Assert.Equal(0, cache.Stats.Entries);
    }

    [Fact]
    public async Task A_dataset_is_cleared_by_its_name_and_the_others_stay()
    {
        var inner = new CountingQueries();
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules, r => r.Source == "main" ? ["sales"] : ["hr"]));
        var ctx = Context(Caller("u1"));
        await Drain(await queries.RunAsync(ctx, Sql()));
        await Drain(await queries.RunAsync(ctx, new ServingRequest.Sql("people", "select 1")));

        Assert.Equal(1, cache.Invalidate("SALES"));

        Assert.Equal(1, cache.Stats.Entries);
        Assert.Equal("hit", (await Drain(await queries.RunAsync(ctx, new ServingRequest.Sql("people", "select 1")))).Provenance.Cache);
        Assert.Null((await Drain(await queries.RunAsync(ctx, Sql()))).Provenance.Cache);
    }

    [Fact]
    public async Task An_entry_that_cannot_say_what_it_reads_goes_with_any_clear()
    {
        var cache = new MemoryServingCache();
        var queries = new CachingServingQueries(new CountingQueries(), cache, new FixedPolicy(Rules with { Datasets = null }));
        await Drain(await queries.RunAsync(Context(Caller("u1")), Sql()));

        Assert.Equal(1, cache.Invalidate("anything"));
    }

    [Fact]
    public async Task The_size_bound_drops_the_least_recently_used_and_an_oversized_answer_passes_through()
    {
        var inner = new CountingQueries();
        var probe = new MemoryServingCache();
        await Drain(await new CachingServingQueries(inner, probe, new FixedPolicy(Rules)).RunAsync(Context(Caller("u1")), Sql()));
        var entryBytes = probe.Stats.Bytes;

        var cache = new MemoryServingCache(new MemoryServingCacheOptions(MaxBytes: entryBytes * 2 + 10, MaxEntryBytes: entryBytes + 10));
        var queries = new CachingServingQueries(inner, cache, new FixedPolicy(Rules));
        foreach (var text in new[] { "select 1", "select 2", "select 3" }) await Drain(await queries.RunAsync(Context(Caller("u1")), Sql(text)));

        Assert.True(cache.Stats.Entries <= 2);
        Assert.True(cache.Stats.Evictions >= 1);
        Assert.Null((await Drain(await queries.RunAsync(Context(Caller("u1")), Sql("select 1")))).Provenance.Cache);

        var tiny = new MemoryServingCache(new MemoryServingCacheOptions(MaxBytes: 1024 * 1024, MaxEntryBytes: 10));
        var passed = await Drain(await new CachingServingQueries(inner, tiny, new FixedPolicy(Rules)).RunAsync(Context(Caller("u1")), Sql()));
        Assert.Equal(0, tiny.Stats.Entries);
        Assert.NotEmpty(passed.Bytes);
    }

    private static async Task<(byte[] Bytes, ServingProvenance Provenance)> DrainBytes(IServingAnswer answer)
    {
        await using (answer)
        {
            using var stream = new MemoryStream();
            using (var writer = new ArrowStreamWriter(stream, answer.Schema, leaveOpen: true))
            {
                await foreach (var batch in answer.ReadBatchesAsync()) await writer.WriteRecordBatchAsync(batch);
                await writer.WriteEndAsync();
            }

            return (stream.ToArray(), answer.Provenance);
        }
    }

    private static async Task<DrainedAnswer> Drain(IServingAnswer answer)
    {
        var (bytes, provenance) = await DrainBytes(answer);
        return new DrainedAnswer(bytes, provenance);
    }

    private sealed record DrainedAnswer(byte[] Bytes, ServingProvenance Provenance)
    {
        public void Deconstruct(out byte[] bytes, out ServingProvenance provenance) => (bytes, provenance) = (Bytes, Provenance);
    }

    private sealed class FixedPolicy(ServingCachePlan plan, Func<ServingRequest, IReadOnlyList<string>>? datasets = null) : IServingCachePolicy
    {
        public ServingCachePlan? PlanFor(ServingContext context, ServingRequest request) => datasets is null ? plan : plan with { Datasets = datasets(request) };
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingQueries : IServingQueries
    {
        public int Runs;
        public bool FailAfterFirstBatch;

        public Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.FromResult<IServingAnswer>(new Answer(FailAfterFirstBatch, new ServingProvenance("select 1", "7", context.Channel, request.Source)));
        }

        public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Answer(bool fail, ServingProvenance provenance) : IServingAnswer
    {
        private static readonly Schema Columns = new Schema.Builder().Field(new Field("n", Int32Type.Default, false)).Build();

        public ServingProvenance Provenance { get; } = provenance;

        public Schema Schema => Columns;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var b = 0; b < 2; b++)
            {
                var column = new Int32Array.Builder().AppendRange([b * 10 + 1, b * 10 + 2, b * 10 + 3]).Build();
                yield return new RecordBatch(Columns, [column], 3);
                await Task.Yield();
                if (fail && b == 0) throw new ServingLimitException("limit", "rows");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
