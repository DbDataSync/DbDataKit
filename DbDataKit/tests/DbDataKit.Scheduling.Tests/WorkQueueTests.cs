using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Scheduling.Tests;

/// <summary>A clock the tests move.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The shared work queue on every engine (plan 13, M02): what may run together, leases, cancellation, and that one state database can hold two services' queues.</summary>
public sealed class WorkQueueTests : IDisposable
{
    private const string Sync = "dbdatasync";
    private readonly StateEngines _engines = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Open(engine, new StateSchema([WorkQueueSchema.Queue]));

    private WorkQueueStore Queue(StateDatabase database, string service = Sync) => new(database, service, _clock);

    public static TheoryData<string> Engines => StateEngines.Engines;

    [Theory, MemberData(nameof(Engines))]
    public void Enqueueing_the_same_work_twice_is_one_item_and_the_same_run(string engine)
    {
        var queue = Queue(Open(engine));

        var first = queue.Enqueue("orders-sync", "Primary", "dbo.Orders");
        var second = queue.Enqueue("orders-sync", "Primary", "dbo.Orders");

        Assert.Equal(first, second);
        Assert.Throws<WorkQueueCollisionException>(() => queue.EnqueueOrThrow("orders-sync", "Primary", "dbo.Orders"));
        Assert.NotEqual(first, queue.Enqueue("orders-sync", "Primary", "dbo.Customers"));
        Assert.NotEqual(first, queue.Enqueue("orders-sync", "BulkLoad", "dbo.Orders"));
        Assert.NotEqual(first, queue.Enqueue("orders-sync", "BulkLoad", "dbo.Orders", part: "seg-1"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Finished_work_can_be_queued_again(string engine)
    {
        var queue = Queue(Open(engine));
        var first = queue.Enqueue("o", "Primary", "s");
        var item = queue.TryClaimNext("o", "w1")!;
        queue.MarkDone(item.Id);

        var again = queue.Enqueue("o", "Primary", "s");

        Assert.NotEqual(first, again);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_claim_takes_the_highest_priority_then_the_oldest_and_carries_the_payload(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("o", "Primary", "old");
        _clock.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue("o", "Primary", "urgent", priority: 5, payloadJson: """{"reader":"reload"}""");
        _clock.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue("o", "Primary", "new");

        var first = queue.TryClaimNext("o", "w1")!;
        var second = queue.TryClaimNext("o", "w1")!;
        var third = queue.TryClaimNext("o", "w1")!;

        Assert.Equal(["urgent", "old", "new"], new[] { first.Subject, second.Subject, third.Subject });
        Assert.Equal("""{"reader":"reload"}""", first.PayloadJson);
        Assert.Equal(WorkItemStatus.Claimed, first.Status);
        Assert.Null(queue.TryClaimNext("o", "w1"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Work_is_not_claimable_before_it_is_available_and_only_by_its_owner(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("a", "Primary", "s", availableAt: _clock.GetUtcNow().AddMinutes(10));

        Assert.Null(queue.TryClaimNext("a", "w1"));
        Assert.Null(queue.TryClaimNext("b", "w1"));
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(queue.TryClaimNext("b", "w1"));
        Assert.NotNull(queue.TryClaimNext("a", "w1"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_whole_subject_item_and_a_part_of_it_exclude_each_other_but_two_parts_do_not(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("o", "BulkLoad", "dbo.Orders", part: "seg-1");
        queue.Enqueue("o", "BulkLoad", "dbo.Orders", part: "seg-2");
        queue.Enqueue("o", "BulkLoad", "dbo.Orders"); // the whole subject
        queue.Enqueue("o", "BulkLoad", "dbo.Customers");

        var seg1 = queue.TryClaimNext("o", "w1")!;
        var seg2 = queue.TryClaimNext("o", "w2")!;
        var other = queue.TryClaimNext("o", "w3")!;
        var whole = queue.TryClaimNext("o", "w4");

        Assert.Equal(["seg-1", "seg-2", ""], new[] { seg1.Part, seg2.Part, other.Part });
        Assert.Equal("dbo.Customers", other.Subject);
        Assert.Null(whole); // parts of dbo.Orders are running

        queue.MarkDone(seg1.Id);
        Assert.Null(queue.TryClaimNext("o", "w4")); // seg-2 still is
        queue.MarkDone(seg2.Id);
        Assert.Equal("dbo.Orders", queue.TryClaimNext("o", "w4")!.Subject);
    }

    [Theory, MemberData(nameof(Engines))]
    public void While_the_whole_subject_runs_no_part_of_it_may_start(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("o", "Refresh", "daily");
        var whole = queue.TryClaimNext("o", "w1")!;
        queue.Enqueue("o", "Refresh", "daily", part: "2026-10-24");

        Assert.Null(queue.TryClaimNext("o", "w2"));
        queue.MarkDone(whole.Id);
        Assert.Equal("2026-10-24", queue.TryClaimNext("o", "w2")!.Part);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Different_kinds_of_work_on_one_subject_may_run_together(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("o", "Primary", "dbo.Orders");
        queue.Enqueue("o", "BulkLoad", "dbo.Orders");

        Assert.NotNull(queue.TryClaimNext("o", "w1"));
        Assert.NotNull(queue.TryClaimNext("o", "w2"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Two_subjects_that_write_one_resource_take_turns_across_owners(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("crm-sync", "Primary", "crm.Customers", resource: "dw.dim_customer");
        queue.Enqueue("erp-sync", "Primary", "erp.Clients", resource: "dw.dim_customer");
        queue.Enqueue("erp-sync", "Primary", "erp.Orders", resource: "dw.fact_orders");

        var first = queue.TryClaimNext("crm-sync", "w1")!;
        var other = queue.TryClaimNext("erp-sync", "w2");

        Assert.Equal("erp.Orders", other!.Subject); // a different resource is free
        Assert.Null(queue.TryClaimNext("erp-sync", "w3"));
        queue.MarkDone(first.Id);
        Assert.Equal("erp.Clients", queue.TryClaimNext("erp-sync", "w3")!.Subject);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_lane_runs_at_most_its_cap_per_owner(string engine)
    {
        var queue = Queue(Open(engine));
        for (var i = 1; i <= 4; i++) queue.Enqueue("sales", "Refresh", $"model{i}", lane: "Refresh");
        queue.Enqueue("sales", "Check", "model1", lane: "Check");
        queue.Enqueue("other", "Refresh", "m", lane: "Refresh");

        var a = queue.TryClaimNext("sales", "w1", lane: "Refresh", maxRunningInLane: 2)!;
        var b = queue.TryClaimNext("sales", "w2", lane: "Refresh", maxRunningInLane: 2)!;

        Assert.Null(queue.TryClaimNext("sales", "w3", lane: "Refresh", maxRunningInLane: 2));
        Assert.NotNull(queue.TryClaimNext("sales", "w3", lane: "Check", maxRunningInLane: 2)); // another lane
        Assert.NotNull(queue.TryClaimNext("other", "w4", lane: "Refresh", maxRunningInLane: 2)); // another owner
        queue.MarkDone(a.Id);
        Assert.NotNull(queue.TryClaimNext("sales", "w3", lane: "Refresh", maxRunningInLane: 2));
        queue.MarkRunning(b.Id);
        Assert.Null(queue.TryClaimNext("sales", "w5", lane: "Refresh", maxRunningInLane: 2));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_worker_that_stops_renewing_loses_the_item(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("o", "Primary", "s");
        var item = queue.TryClaimNext("o", "w1")!;

        _clock.Advance(queue.LeaseDuration - TimeSpan.FromSeconds(1));
        Assert.Equal(0, queue.ReleaseExpired());
        Assert.True(queue.Heartbeat(item.Id, "w1"));
        Assert.False(queue.Heartbeat(item.Id, "someone-else"));

        _clock.Advance(queue.LeaseDuration - TimeSpan.FromSeconds(1));
        Assert.Equal(0, queue.ReleaseExpired()); // renewed
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, queue.ReleaseExpired());
        Assert.False(queue.Heartbeat(item.Id, "w1")); // no longer theirs
        var again = queue.TryClaimNext("o", "w2")!;
        Assert.Equal(item.Id, again.Id);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Claims_of_a_dead_worker_go_back_to_the_queue_by_owner_or_by_run(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("o", "Primary", "a");
        var run = queue.Enqueue("o", "Primary", "b");
        queue.Enqueue("p", "Primary", "c");
        queue.TryClaimNext("o", "w1");
        queue.TryClaimNext("o", "w1");
        queue.TryClaimNext("p", "w1");

        Assert.Equal(["o", "p"], queue.OwnersWithHeldWork().Order());
        Assert.Equal(1, queue.ReleaseClaimsForRun(run));
        Assert.Equal(1, queue.ReleaseClaimsForOwner("o"));
        Assert.Equal(["p"], queue.OwnersWithHeldWork());
        Assert.NotNull(queue.TryClaimNext("o", "w2"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Pending_work_is_cancelled_directly_and_held_work_by_request(string engine)
    {
        var queue = Queue(Open(engine));
        var waiting = queue.Enqueue("o", "Primary", "a");
        var held = queue.Enqueue("o", "Primary", "b");
        queue.TryClaimNext("o", "w1", runId: held);

        Assert.True(queue.TryCancelPending(waiting));
        Assert.False(queue.TryCancelPending(held));
        Assert.False(queue.IsCancelRequested(held));
        Assert.True(queue.RequestCancel(held));
        Assert.True(queue.IsCancelRequested(held));
        Assert.False(queue.RequestCancel(waiting));
        Assert.Null(queue.TryClaimNext("o", "w2")); // the cancelled one is not claimable
    }

    [Theory, MemberData(nameof(Engines))]
    public void Questions_about_what_is_outstanding_answer_per_owner_kind_subject_and_lane(string engine)
    {
        var queue = Queue(Open(engine));
        queue.Enqueue("o", "Reconcile", "dbo.Orders", part: "seg-1", lane: "Bulk");

        Assert.True(queue.HasOutstandingWork("o"));
        Assert.True(queue.HasOutstandingWork("o", "Bulk"));
        Assert.False(queue.HasOutstandingWork("o", "Incremental"));
        Assert.False(queue.HasOutstandingWork("p"));
        Assert.True(queue.HasInFlight("o", "Reconcile", "dbo.Orders"));
        Assert.False(queue.HasInFlight("o", "Reconcile", "dbo.Customers"));
        Assert.False(queue.HasInFlight("o", "Primary", "dbo.Orders"));

        queue.MarkDone(queue.TryClaimNext("o", "w1")!.Id);
        Assert.False(queue.HasOutstandingWork("o"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Two_services_share_a_database_without_seeing_each_others_work(string engine)
    {
        var database = Open(engine);
        var sync = Queue(database, "dbdatasync");
        var build = Queue(database, "dbdatabuild");

        var a = sync.Enqueue("o", "Primary", "s", resource: "target");
        var b = build.Enqueue("o", "Primary", "s", resource: "target");

        Assert.NotEqual(a, b); // the same names, two items
        Assert.Null(build.TryClaimNext("o", "w1", runId: a));
        Assert.NotNull(sync.TryClaimNext("o", "w1"));
        Assert.NotNull(build.TryClaimNext("o", "w1")); // sync's running item does not hold the resource for build
        Assert.Equal(["o"], sync.OwnersWithHeldWork());
        Assert.False(sync.TryCancelPending(b));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_run_lock_is_held_by_one_run_at_a_time_per_subject(string engine)
    {
        var database = Open(engine);
        var locks = new RunLockStore(database, Sync, _clock);
        var other = new RunLockStore(database, "dbdatabuild", _clock);

        Assert.True(locks.TryAcquire("o", "Primary", "dbo.Orders", Guid.NewGuid()));
        Assert.False(locks.TryAcquire("o", "Primary", "dbo.Orders", Guid.NewGuid()));
        Assert.True(locks.IsLocked("o", "Primary", "dbo.Orders"));
        Assert.True(locks.TryAcquire("o", "BulkLoad", "dbo.Orders", Guid.NewGuid()));
        Assert.True(locks.TryAcquire("o", "Primary", "dbo.Customers", Guid.NewGuid()));
        Assert.True(other.TryAcquire("o", "Primary", "dbo.Orders", Guid.NewGuid()));

        locks.Release("o", "Primary", "dbo.Orders");

        Assert.False(locks.IsLocked("o", "Primary", "dbo.Orders"));
        Assert.True(locks.TryAcquire("o", "Primary", "dbo.Orders", Guid.NewGuid()));
    }
}
