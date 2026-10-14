using Apache.Arrow;
using DbDataProxy.Data;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Flight;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace DbDataProxy.Tests.Flight;

public class PendingQueryStoreTests
{
    private sealed class FakeResult : IQueryResult
    {
        public bool Disposed;
        public Schema Schema { get; } = new Schema([], null);
        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static ResolvedDataSource Source() =>
        new("s", new DataSourceOptions(), new DbQueryExecutor(new DelegateConnectionFactory(_ => throw new NotSupportedException()), ""));

    [Fact]
    public void A_handle_can_be_taken_once_and_only_by_its_owner()
    {
        var store = new PendingQueryStore(new FakeTimeProvider());
        byte[] handle = store.Add("alice", Source(), new FakeResult());

        Assert.Null(store.Take(handle, "mallory"));
        Assert.NotNull(store.Take(handle, "ALICE"));
        Assert.Null(store.Take(handle, "alice"));
    }

    [Fact]
    public void Unknown_handles_are_not_found()
    {
        var store = new PendingQueryStore(new FakeTimeProvider());
        Assert.Null(store.Take(new byte[16], "alice"));
    }

    [Fact]
    public async Task Expired_results_are_disposed_by_the_sweep_and_cannot_be_taken()
    {
        var clock = new FakeTimeProvider();
        var store = new PendingQueryStore(clock);
        var result = new FakeResult();
        byte[] handle = store.Add("alice", Source(), result);

        clock.Advance(PendingQueryStore.Lifetime + TimeSpan.FromSeconds(1));
        await store.SweepAsync();

        Assert.True(result.Disposed);
        Assert.Null(store.Take(handle, "alice"));
    }

    [Fact]
    public void Callers_are_limited_in_how_many_unfetched_results_they_can_hold()
    {
        var store = new PendingQueryStore(new FakeTimeProvider());
        for (int i = 0; i < PendingQueryStore.MaxPerOwner; i++) store.Add("alice", Source(), new FakeResult());

        var ex = Assert.Throws<RpcException>(() => store.EnsureCapacity("alice"));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        store.EnsureCapacity("bob"); // other callers are unaffected
    }

    [Fact]
    public async Task Disposing_the_store_releases_everything_still_waiting()
    {
        var store = new PendingQueryStore(new FakeTimeProvider());
        var result = new FakeResult();
        store.Add("alice", Source(), result);
        await store.DisposeAsync();
        Assert.True(result.Disposed);
    }
}
