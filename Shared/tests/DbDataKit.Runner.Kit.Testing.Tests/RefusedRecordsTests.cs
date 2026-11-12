using DbDataKit.Monitoring;
using DbDataKit.Connections;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit.Testing.Tests;

public sealed record Order(string Id, decimal Amount);

public sealed record Outside(List<string> Received)
{
    public HashSet<string> Refusing { get; } = [];
}

public sealed record PushInput(Outside Outside);

/// <summary>A job that pushes orders to an outside system and rejects the ones it refuses.</summary>
[RunnerTask("push-orders")]
public sealed class PushOrders : RunnerJob<PushInput, NoProgress, NoSlots>
{
    public static readonly Order[] Orders = [new("a", 10), new("b", -1), new("c", 30), new("d", -4)];

    protected override async Task RunAsync(CancellationToken ct)
    {
        foreach (var order in Orders)
        {
            if (Input.Outside.Refusing.Contains(order.Id)) await RejectAsync(order, "The amount is negative.", ct);
            else Input.Outside.Received.Add(order.Id);
        }
    }
}

public class RefusedRecordsTests
{
    private static async Task<(RunSimulator<PushOrders> Sim, Outside Outside)> RunAsync()
    {
        var outside = new Outside([]);
        outside.Refusing.UnionWith(["b", "d"]);
        var sim = new RunSimulator<PushOrders>(new PushInput(outside));
        await sim.RunAsync();
        return (sim, outside);
    }

    [Fact]
    public async Task A_refused_record_is_listed_with_its_reason_and_body()
    {
        var (sim, outside) = await RunAsync();
        using var _ = sim;

        Assert.Equal(RunOutcome.CompletedWithFailures, sim.Outcome);
        Assert.Equal(["a", "c"], outside.Received);
        var refused = sim.Refused().List();
        Assert.Equal(2, refused.Count);
        Assert.All(refused, r => Assert.Equal("The amount is negative.", r.Reason));
        Assert.All(refused, r => Assert.Equal("record:Order", r.Source));
        Assert.Equal(new Order("b", -1), sim.Refused().Read<Order>(refused[0].Id));
        Assert.Equal(new Order("d", -4), sim.Refused().Read<Order>(refused[1].Id));
    }

    [Fact]
    public async Task Replay_resends_exactly_the_open_records_once()
    {
        var (sim, _) = await RunAsync();
        using var _s = sim;
        var refused = sim.Refused();
        var sent = new List<(string Id, Guid Key)>();

        var report = await refused.ReplayAsync<Order>((order, key, _) => { sent.Add((order.Id, key)); return Task.CompletedTask; });

        Assert.True(report.Complete);
        Assert.Equal(2, report.Sent);
        Assert.Equal(["b", "d"], sent.Select(s => s.Id));
        Assert.Equal(sent.Select(s => s.Key), refused.List(DeadLetter.Replayed).Select(r => r.Id));      // the id is the idempotency key
        Assert.Empty(refused.List());

        var again = await refused.ReplayAsync<Order>((order, _, _) => { sent.Add((order.Id, Guid.Empty)); return Task.CompletedTask; });
        Assert.Equal(0, again.Sent);
        Assert.Equal(2, sent.Count);                                                                      // nothing was sent twice
    }

    [Fact]
    public async Task A_replay_stops_at_the_first_failure_and_the_next_one_starts_there()
    {
        var (sim, _) = await RunAsync();
        using var _s = sim;
        var refused = sim.Refused();
        var tries = new List<string>();

        var report = await refused.ReplayAsync<Order>((order, _, _) =>
        {
            tries.Add(order.Id);
            throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Network, null, null, raw: "token=secret-canary-91"));
        });

        Assert.Equal(["b"], tries);                                                                       // stopped at the first
        Assert.Equal(0, report.Sent);
        Assert.Equal(1, report.NotTried);
        var open = refused.List();
        Assert.Equal(2, open.Count);
        Assert.Equal(2, open[0].Attempts);                                                                // the first refusal and the failed replay
        Assert.Equal(1, open[1].Attempts);
        Assert.DoesNotContain("secret-canary-91", open[0].Reason);
        Assert.StartsWith("replay failed", open[0].Reason);

        var sent = new List<string>();
        var next = await refused.ReplayAsync<Order>((order, _, _) => { sent.Add(order.Id); return Task.CompletedTask; });
        Assert.Equal(["b", "d"], sent);
        Assert.True(next.Complete);
    }

    [Fact]
    public async Task Continue_leaves_the_failed_one_open_and_sends_the_rest_and_a_discarded_one_is_not_replayed()
    {
        var (sim, _) = await RunAsync();
        using var _s = sim;
        var refused = sim.Refused();
        var sent = new List<string>();

        var report = await refused.ReplayAsync<Order>((order, _, _) =>
        {
            if (order.Id == "b") throw new IOException("down");
            sent.Add(order.Id);
            return Task.CompletedTask;
        }, new ReplayOptions { OnFailure = ReplayFailure.Continue });

        Assert.Equal(["d"], sent);
        Assert.Equal(1, report.Sent);
        Assert.Single(report.Failed);
        var left = Assert.Single(refused.List());
        Assert.True(refused.Discard(left.Id));
        Assert.False(refused.Discard(left.Id));
        Assert.Empty(refused.List());
        Assert.Single(refused.List(DeadLetter.Discarded));
    }
}
