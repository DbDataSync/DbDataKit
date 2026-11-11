using Apache.Arrow;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.Serving.FlightSql.Tests;

public class PendingAnswerStoreTests
{
    private sealed class FakeAnswer : IServingAnswer
    {
        public bool Disposed;
        public Schema Schema { get; } = new Schema([], null);
        public ServingProvenance Provenance { get; } = new("select 1", null, "prod", "s");
        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void A_handle_can_be_taken_once_and_only_by_its_owner()
    {
        var store = new PendingAnswerStore(new FakeTimeProvider());
        byte[] handle = store.Add("alice", "s", new FakeAnswer());

        Assert.Null(store.Take(handle, "mallory"));
        Assert.NotNull(store.Take(handle, "ALICE"));
        Assert.Null(store.Take(handle, "alice"));
    }

    [Fact]
    public void Unknown_handles_are_not_found()
    {
        var store = new PendingAnswerStore(new FakeTimeProvider());
        Assert.Null(store.Take(new byte[16], "alice"));
    }

    [Fact]
    public async Task Expired_results_are_disposed_by_the_sweep_and_cannot_be_taken()
    {
        var clock = new FakeTimeProvider();
        var store = new PendingAnswerStore(clock);
        var answer = new FakeAnswer();
        byte[] handle = store.Add("alice", "s", answer);

        clock.Advance(PendingAnswerStore.Lifetime + TimeSpan.FromSeconds(1));
        await store.SweepAsync();

        Assert.True(answer.Disposed);
        Assert.Null(store.Take(handle, "alice"));
    }

    [Fact]
    public void Callers_are_limited_in_how_many_unfetched_results_they_can_hold()
    {
        var store = new PendingAnswerStore(new FakeTimeProvider());
        for (int i = 0; i < PendingAnswerStore.MaxPerOwner; i++) store.Add("alice", "s", new FakeAnswer());

        var ex = Assert.Throws<RpcException>(() => store.EnsureCapacity("alice"));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        store.EnsureCapacity("bob"); // other callers are unaffected
    }

    [Fact]
    public async Task Disposing_the_store_releases_everything_still_waiting()
    {
        var store = new PendingAnswerStore(new FakeTimeProvider());
        var answer = new FakeAnswer();
        store.Add("alice", "s", answer);
        await store.DisposeAsync();
        Assert.True(answer.Disposed);
    }
}
