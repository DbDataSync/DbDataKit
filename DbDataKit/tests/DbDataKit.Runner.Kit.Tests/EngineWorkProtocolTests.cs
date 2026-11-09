using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.Runner.Protocol.V1;
using DbDataKit.Scheduling;
using Grpc.Core;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>
/// Plan 28c E11: the runner's queue of an engine's own items, through the protocol (<c>EnqueueWork</c>, <c>ClaimWork</c>, <c>StartWork</c>, <c>FinishWork</c>, <c>ReleaseWork</c>, <c>IsCancelRequested</c>, <c>WorkState</c>),
/// bound to the run's own owner: the run's package, channel and task, which its token names.
/// </summary>
public sealed class EngineWorkProtocolTests
{
    private const string WorkService = "runner.work";

    private static ModuleWorkQueueStore Queue(Harness h) => new(h.Database, WorkService);

    private static string OwnerOf(string task) => $"{Harness.Package}/{Harness.Channel}/{task}";

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_job_queues_claims_starts_and_finishes_items_of_its_own_owner(string engine)
    {
        using var h = new Harness(engine, remote: true);
        while (QueueWorker.Done.TryDequeue(out _)) { }

        var result = await h.Run<QueueWorker>();

        Assert.Equal(TaskEndState.Succeeded, result.End.State);
        Assert.Equal(["pass/a/{\"n\":1}", "pass/b/{\"n\":1}", "pass/c/{\"n\":1}"], QueueWorker.Done.Order().ToArray());
        var left = Queue(h).ListInFlight(OwnerOf("queue-worker"));
        Assert.Empty(left);
        Assert.False(Queue(h).HasOutstandingWork(OwnerOf("queue-worker")));
        // the runner's own queue never saw them
        Assert.Empty(new ModuleWorkQueueStore(h.Database, "runner").ListInFlight(OwnerOf("queue-worker")));
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_token_for_one_task_cannot_claim_start_finish_release_or_ask_about_an_item_of_another(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var a = new RawClient(h.Host.Endpoint, h.Grant("replication-a"));
        using var b = new RawClient(h.Host.Endpoint, h.Grant("replication-b"));
        a.Start();
        b.Start();

        var forB = b.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "orders", Lane = "changes" }, b.Headers);
        var forA = a.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "orders", Lane = "changes" }, a.Headers);
        Assert.NotEqual(forA.RunId, forB.RunId);                        // the same kind and subject under two owners are two items

        // A claims its own item, and only that one
        var claimed = a.Client.ClaimWork(new ClaimWorkRequest(), a.Headers);
        Assert.True(claimed.HasItem);
        Assert.Equal(forA.RunId, claimed.RunId);
        Assert.False(a.Client.ClaimWork(new ClaimWorkRequest(), a.Headers).HasItem);   // B's item is not A's to take

        // B's item id, named by A's token: refused, every way
        var bItem = b.Client.ClaimWork(new ClaimWorkRequest(), b.Headers);
        Assert.True(bItem.HasItem);
        foreach (var call in new Func<RpcException?>[]
        {
            () => Catch(() => a.Client.StartWork(new WorkItemRef { Id = bItem.Id }, a.Headers)),
            () => Catch(() => a.Client.FinishWork(new FinishWorkRequest { Id = bItem.Id, Outcome = WorkOutcome.Done }, a.Headers)),
            () => Catch(() => a.Client.ReleaseWork(new WorkItemRef { Id = bItem.Id }, a.Headers)),
            () => Catch(() => a.Client.IsCancelRequested(new WorkRunRef { RunId = bItem.RunId }, a.Headers)),
        })
        {
            var refused = call();
            Assert.NotNull(refused);
            Assert.Equal(StatusCode.PermissionDenied, refused!.StatusCode);
            Assert.Contains(OwnerOf("replication-a"), refused.Status.Detail, StringComparison.Ordinal);
        }

        // nothing of B's changed: it is still claimed by B, and B can run it
        Assert.True(b.Client.StartWork(new WorkItemRef { Id = bItem.Id }, b.Headers).Applied);
        Assert.True(b.Client.FinishWork(new FinishWorkRequest { Id = bItem.Id, Outcome = WorkOutcome.Done }, b.Headers).Applied);
        Assert.Empty(Queue(h).ListInFlight(OwnerOf("replication-b")));
        var rows = Queue(h).ListInFlight(OwnerOf("replication-a"));
        Assert.Equal(WorkItemStatus.Claimed, Assert.Single(rows).Item.Status);
    }

    private static RpcException? Catch(Action call)
    {
        try
        {
            call();
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public void An_item_one_run_holds_cannot_be_finished_by_another_run_of_the_same_task(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var first = new RawClient(h.Host.Endpoint, h.Grant("replication-a"));
        using var second = new RawClient(h.Host.Endpoint, h.Grant("replication-a"));
        first.Start();
        second.Start();
        first.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "orders" }, first.Headers);
        var item = first.Client.ClaimWork(new ClaimWorkRequest(), first.Headers);

        Assert.False(second.Client.StartWork(new WorkItemRef { Id = item.Id }, second.Headers).Applied);
        Assert.False(second.Client.FinishWork(new FinishWorkRequest { Id = item.Id, Outcome = WorkOutcome.Done }, second.Headers).Applied);
        Assert.False(second.Client.ReleaseWork(new WorkItemRef { Id = item.Id }, second.Headers).Applied);
        Assert.True(first.Client.StartWork(new WorkItemRef { Id = item.Id }, first.Headers).Applied);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public void What_a_run_still_holds_goes_back_to_the_queue_when_it_ends_or_its_token_is_withdrawn(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var owner = OwnerOf("replication-a");

        // the run ends with a claim in hand
        var token = h.Grant("replication-a");
        using (var run = new RawClient(h.Host.Endpoint, token))
        {
            run.Start();
            run.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "one" }, run.Headers);
            run.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "two" }, run.Headers);
            Assert.True(run.Client.ClaimWork(new ClaimWorkRequest(), run.Headers).HasItem);
            Assert.True(run.Client.ClaimWork(new ClaimWorkRequest(), run.Headers).HasItem);
            Assert.All(Queue(h).ListInFlight(owner), w => Assert.Equal(WorkItemStatus.Claimed, w.Item.Status));
            run.Client.End(new EndRequest { End = new TaskEndMessage { State = EndState.Stopped } }, run.Headers);
        }

        Assert.All(Queue(h).ListInFlight(owner), w => Assert.Equal(WorkItemStatus.Pending, w.Item.Status));
        h.Host.Unregister(token);

        // the process dies: no End, the runner withdraws the token
        var next = h.Grant("replication-a");
        using (var run = new RawClient(h.Host.Endpoint, next))
        {
            run.Start();
            Assert.True(run.Client.ClaimWork(new ClaimWorkRequest(), run.Headers).HasItem);
            Assert.Contains(Queue(h).ListInFlight(owner), w => w.Item.Status == WorkItemStatus.Claimed);
            h.Host.Unregister(next);
        }

        Assert.All(Queue(h).ListInFlight(owner), w => Assert.Equal(WorkItemStatus.Pending, w.Item.Status));
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public void A_run_the_runner_asked_to_stop_claims_nothing_more_and_a_cancel_of_an_item_is_seen(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var token = h.Grant("replication-a");
        using var run = new RawClient(h.Host.Endpoint, token);
        run.Start();
        var queued = run.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "one" }, run.Headers);
        run.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "two" }, run.Headers);
        var item = run.Client.ClaimWork(new ClaimWorkRequest(), run.Headers);
        Assert.Equal(queued.RunId, item.RunId);

        Assert.False(run.Client.IsCancelRequested(new WorkRunRef { RunId = item.RunId }, run.Headers).Requested);
        Assert.True(Queue(h).RequestCancel(Guid.Parse(item.RunId)));                       // an operator asks for this item to stop
        Assert.True(run.Client.IsCancelRequested(new WorkRunRef { RunId = item.RunId }, run.Headers).Requested);

        Assert.True(h.Host.Cancel(token));                                                  // the runner asks the run to stop
        Assert.False(run.Client.ClaimWork(new ClaimWorkRequest(), run.Headers).HasItem);   // although "two" is waiting
        Assert.True(run.Client.ReleaseWork(new WorkItemRef { Id = item.Id }, run.Headers).Applied);   // a stopped run gives back what it held
        Assert.Equal(2, Queue(h).ListInFlight(OwnerOf("replication-a")).Count(w => w.Item.Status == WorkItemStatus.Pending));
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public void The_work_state_says_when_each_subject_was_last_queued_and_what_is_in_flight_for_the_owner_only(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var a = new RawClient(h.Host.Endpoint, h.Grant("replication-a"));
        using var b = new RawClient(h.Host.Endpoint, h.Grant("replication-b"));
        a.Start();
        b.Start();
        Assert.False(a.Client.WorkState(new WorkStateRequest { Kinds = { "pass" } }, a.Headers).Outstanding);
        a.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "orders" }, a.Headers);
        a.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "sweep", Subject = "orders" }, a.Headers);
        b.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "customers" }, b.Headers);
        var item = a.Client.ClaimWork(new ClaimWorkRequest(), a.Headers);
        a.Client.StartWork(new WorkItemRef { Id = item.Id }, a.Headers);
        a.Client.FinishWork(new FinishWorkRequest { Id = item.Id, Outcome = WorkOutcome.Done }, a.Headers);

        var state = a.Client.WorkState(new WorkStateRequest { Kinds = { "pass", "sweep", "never" } }, a.Headers);

        Assert.True(state.Outstanding);                                                     // the sweep is still pending
        var pass = Assert.Single(state.Kinds, k => k.Kind == "pass").Subjects;
        var orders = Assert.Single(pass);                                                   // B's customers are not A's
        Assert.Equal("orders", orders.Subject);
        Assert.False(orders.InFlight);                                                      // it was finished
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - orders.LastEnqueuedUnixMs) < 60_000);
        Assert.True(Assert.Single(Assert.Single(state.Kinds, k => k.Kind == "sweep").Subjects).InFlight);
        Assert.Empty(Assert.Single(state.Kinds, k => k.Kind == "never").Subjects);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public void An_unknown_outcome_and_a_missing_name_are_refused(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var run = new RawClient(h.Host.Endpoint, h.Grant("replication-a"));
        run.Start();

        Assert.Equal(StatusCode.InvalidArgument, Catch(() => run.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "", Subject = "x" }, run.Headers))!.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, Catch(() => run.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = " " }, run.Headers))!.StatusCode);
        run.Client.EnqueueWork(new EnqueueWorkRequest { Kind = "pass", Subject = "x" }, run.Headers);
        var item = run.Client.ClaimWork(new ClaimWorkRequest(), run.Headers);
        Assert.Equal(StatusCode.InvalidArgument, Catch(() => run.Client.FinishWork(new FinishWorkRequest { Id = item.Id }, run.Headers))!.StatusCode);
    }

    [Fact]
    public async Task A_run_in_this_process_has_no_queue_and_says_so()
    {
        using var h = new Harness(DbDataKit.State.StateEngineIds.Sqlite, remote: false);

        var result = await h.Run<QueueWorker>();

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Contains("work queue is the runner's", result.End.Failure!.Message, StringComparison.Ordinal);
    }
}
