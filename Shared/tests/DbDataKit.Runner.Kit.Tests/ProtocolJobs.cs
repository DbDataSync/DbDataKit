using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Kit.Tests.Samples;

// Jobs for what only the protocol shows: what a run reports one way, and what happens when the runner asks it to stop.

/// <summary>Counts, a status line, lineage and a log line: the one-way messages.</summary>
[RunnerTask("reporter")]
public sealed class Reporter : RunnerJob<NoInput, NoProgress, NoSlots>
{
    private static readonly Counter Seen = new("seen");

    protected override Task RunAsync(CancellationToken ct)
    {
        Count(Seen, 3);
        Count(Seen, 4);
        Status("reporting");
        Status("reported");
        Run.Lineage(["crm.orders"], ["warehouse.orders"]);
        Run.Log.LogInformation("hello from the reporter");
        return Task.CompletedTask;
    }
}

/// <summary>Waits until it is told to stop (the token), and lets the cancellation propagate as a job should.</summary>
[RunnerTask("waits")]
public sealed class Waits : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        World.Current.JobStarted.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
    }
}

/// <summary>Ignores the stop request: the task ends the run itself when the grace period is over.</summary>
[RunnerTask("stubborn")]
public sealed class Stubborn : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        World.Current.JobStarted.TrySetResult();
        await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
    }
}

public sealed class GrantSlots : Slots
{
    public Slot<ConnectionGrant> Source { get; } = new("source", "sqlserver", env: "build-login");
}

/// <summary>Opens its slot through the runner (plan 28c E02) and keeps what it was given for the test to look at.</summary>
[RunnerTask("grant")]
public sealed class GrantReader : RunnerJob<NoInput, NoProgress, GrantSlots>
{
    public static ConnectionGrant? Last { get; set; }

    protected override async Task RunAsync(CancellationToken ct)
    {
        Last = await Slots.Source.OpenAsync(ct);
    }
}

public enum Flavor
{
    Plain,
    Spicy,
}

/// <summary>Every kind of input the binder reads from text.</summary>
public sealed record AllInputs(string Name, int Count, long Big, decimal Price, bool Flag, DateOnly Day, DateTimeOffset At, Guid Id, Flavor Flavor, int[] Numbers, string? Note = null, int Retries = 3, TimeSpan? Wait = null);

/// <summary>Plan 28c E11: a job that uses the runner's queue of its own items: queues three, claims each, starts it, does it and finishes it.</summary>
[RunnerTask("queue-worker")]
public sealed class QueueWorker : RunnerJob<NoInput, NoProgress, NoSlots>
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Done = new();

    protected override async Task RunAsync(CancellationToken ct)
    {
        foreach (var subject in new[] { "a", "b", "c" })
            await Work.EnqueueAsync("pass", subject, lane: "changes", payload: "{\"n\":1}", ct: ct);
        while (await Work.ClaimAsync("changes", ct) is { } item)
        {
            if (!await Work.StartAsync(item.Id, ct)) throw new InvalidOperationException("lost the claim");
            Done.Enqueue($"{item.Kind}/{item.Subject}/{item.Payload}");
            await Work.FinishAsync(item.Id, WorkFinish.Done, ct);
        }
    }
}
