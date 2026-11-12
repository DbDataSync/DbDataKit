using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Http;

namespace DbDataKit.Runner.Kit.Testing.Tests;

// A dry run (plan 27a IX16): the job reads, and nothing it keeps changes and nothing is sent.

public sealed record DryPushInput(RecordedHttp Http);

public sealed class DryPushProgress : Progress
{
    [State] public string? Cursor { get; set; }
}

public static class DryPushCounters
{
    public static readonly Counter Claimed = new("claimed");
    public static readonly Counter Taken = new("taken");
}

public sealed record DryMessage(string Text);

/// <summary>Reads an API, sends a batch, moves its cursor, claims the same file twice and looks in the inbox: everything a dry run must not keep.</summary>
[RunnerTask("dry-push")]
public sealed class DryPush : RunnerJob<DryPushInput, DryPushProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        using var http = new OutsideHttp(Input.Http.Client("https://api.example.test/v1/"), new OutsideHttpOptions { DryRun = Run.DryRun, OnWouldSend = Run.WouldDo, Delay = (_, _) => Task.CompletedTask });
        await http.GetAsync("orders", ct: ct);                                                                   // a read goes out, dry or not
        var answer = await http.SendJsonAsync(HttpMethod.Post, "orders/batch", new { ids = new[] { 1, 2, 3 } }, idempotencyKey: "batch-1", ct: ct);
        if (Run.DryRun) Assert.Equal(System.Net.HttpStatusCode.NoContent, answer.Status);

        Progress.Cursor = "after-push";
        for (var i = 0; i < 2; i++)
        {
            var claim = await Files.ClaimAsync("share", "/drop/a.csv", 5, DateTimeOffset.UnixEpoch, "hash-a", ct);
            if (claim is null) continue;
            Count(DryPushCounters.Claimed, 1);
            await claim.CompleteAsync(ct: ct);
        }

        if (await Inbox<DryMessage>().TakeAsync(ct) is not null) Count(DryPushCounters.Taken, 1);
    }
}

public class DryRunTests
{
    private static HttpRecording Orders(bool withPost)
    {
        var recording = new HttpRecording().Add("GET", "https://api.example.test/v1/orders", json: """{"items":[]}""");
        return withPost ? recording.Add("POST", "https://api.example.test/v1/orders/batch", status: 202) : recording;
    }

    [Fact]
    public async Task A_dry_run_reads_sends_nothing_keeps_nothing_and_says_what_it_would_have_done()
    {
        var recorded = new RecordedHttp(Orders(withPost: false));                          // the POST is not recorded: if it were sent the run would fail naming it
        using var sim = new RunSimulator<DryPush>(new DryPushInput(recorded), dryRun: true);

        await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(["GET https://api.example.test/v1/orders"], recorded.Served);
        Assert.Empty(recorded.Misses);
        recorded.Verify();
        Assert.Null(sim.Progress<DryPushProgress>().Cursor);                       // the cursor moved in the run and was not kept
        Assert.Equal(2, sim.Counted(DryPushCounters.Claimed));                             // the ledger was not written, so the second claim of the same content is granted too
        Assert.Equal(0, sim.Counted(DryPushCounters.Taken));
        var log = sim.Log.Select(l => l.Message).ToList();
        Assert.Contains(log, m => m.StartsWith("This is a dry run", StringComparison.Ordinal));
        Assert.Contains(log, m => m == "Dry run, would: POST https://api.example.test/v1/orders/batch (15 byte(s), Idempotency-Key batch-1)");
        Assert.Contains(log, m => m.Contains("mark the file '/drop/a.csv' processed", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("take the next inbox item", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_job_run_for_real_sends_keeps_the_cursor_and_writes_the_ledger()
    {
        var recorded = new RecordedHttp(Orders(withPost: true));
        using var sim = new RunSimulator<DryPush>(new DryPushInput(recorded));

        await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(["GET https://api.example.test/v1/orders", "POST https://api.example.test/v1/orders/batch"], recorded.Served);
        Assert.Equal("after-push", sim.Progress<DryPushProgress>().Cursor);
        Assert.Equal(1, sim.Counted(DryPushCounters.Claimed));                             // the second claim of the same content finds it processed
        Assert.DoesNotContain(sim.Log, l => l.Message.StartsWith("This is a dry run", StringComparison.Ordinal));
    }
}
