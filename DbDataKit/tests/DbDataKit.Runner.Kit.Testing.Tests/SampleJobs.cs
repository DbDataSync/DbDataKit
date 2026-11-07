using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Kit.Testing.Tests;

// Jobs under test: the daily drop of plan 28b section 2.2 with its outside world in memory, and a fan-out with a checkpoint saved twice.

/// <summary>The outside world of the daily drop: an SFTP server holding files, an archive, and a warehouse that counts what it was given.</summary>
public sealed class Drop
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();

    public ConcurrentDictionary<DateOnly, ArchiveReceipt> Archived { get; } = new();

    public int Inserted;

    public int Archives;

    public int Deletes;

    /// <summary>The slots of the jobs: the SFTP server and the warehouse, what a runner would bind to connections.</summary>
    public SlotOpener Opener => (slot, _, _) => Task.FromResult<object>(slot == "sftp" ? this : throw new InvalidOperationException($"no connection for slot '{slot}'"));

    public static string Path(DateOnly day) => $"/drop/{day:yyyy-MM-dd}.csv";

    /// <summary>A file of <paramref name="rows"/> lines.</summary>
    public static byte[] Csv(int rows) => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, rows).Select(i => $"id{i},{i}\n")));
}

public sealed record ArchiveReceipt(string Url, long Bytes);

public sealed record DailyDropInput(DateOnly Day, bool ArchiveThrows = false);

public sealed class DailyDropSlots : Slots
{
    public Slot<Drop> Sftp { get; } = new("sftp");
}

public static class DailyDropSteps
{
    public static readonly StreamStep<DateOnly> Download = Step.Stream<DateOnly>("download", day => $"{day:yyyy-MM-dd}", "text/csv");

    public static readonly ValueStep<DateOnly, ArchiveReceipt> Archive = Step.Value<DateOnly, ArchiveReceipt>("archive", day => $"{day:yyyy-MM-dd}");
}

public static class DailyDropCounters
{
    public static readonly Counter Rows = new("rows");
}

[RunnerTask("daily-drop")]
public sealed class DailyDrop : RunnerJob<DailyDropInput, NoProgress, DailyDropSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var drop = await Slots.Sftp.OpenAsync(ct);
        var path = Drop.Path(Input.Day);
        PayloadRef file = await Step(DailyDropSteps.Download, Input.Day)
            .Repeatable()                                                      // a crash mid-copy leaves the file on the server; after the copy the kit adopts the payload
            .StreamAsync(async (sink, ct) =>
            {
                await sink.WriteAsync(drop.Files[path], ct);
                drop.Files.TryRemove(path, out _);                             // only after the copy finished
                Interlocked.Increment(ref drop.Deletes);
            }, ct);

        Status("loading");
        await using var reader = file.OpenRead();
        using var text = new StreamReader(reader);
        var batch = 0;
        while (await text.ReadLineAsync(ct) is not null)
        {
            if (++batch == 100)
            {
                Interlocked.Add(ref drop.Inserted, batch);
                Count(DailyDropCounters.Rows, batch);
                batch = 0;
            }
        }

        if (batch > 0)
        {
            Interlocked.Add(ref drop.Inserted, batch);
            Count(DailyDropCounters.Rows, batch);
        }

        await Step(DailyDropSteps.Archive, Input.Day)
            .Check(ct => Task.FromResult(drop.Archived.TryGetValue(Input.Day, out var found) ? found : null))
            .RunAsync(ct =>
            {
                if (Input.ArchiveThrows) throw new InvalidOperationException("the archive is full");
                Interlocked.Increment(ref drop.Archives);
                var made = new ArchiveReceipt($"archive://{Input.Day:yyyy-MM-dd}", file.Size);
                drop.Archived[Input.Day] = made;                               // the effect, before the step's result is stored
                return Task.FromResult(made);
            }, ct);
        Run.Log.LogInformation("archived {Day}", Input.Day);
    }
}

// ---- a fan-out whose progress is saved at two commit points

public sealed class FanoutProgress : Progress
{
    [Checkpoint] public long Position { get; set; }

    [State] public string? Note { get; set; }
}

public static class FanoutUnits
{
    public static readonly Unit<string> Customer = Unit.Of<string>("customer", c => c);
}

public static class FanoutCounters
{
    public static readonly Counter Rows = new("rows");
}

[RunnerTask("fanout")]
public sealed class Fanout : RunnerJob<NoInput, FanoutProgress, NoSlots>
{
    public static readonly string[] Customers = ["a", "b", "c"];

    protected override async Task RunAsync(CancellationToken ct)
    {
        await ForEachAsync(FanoutUnits.Customer, Customers, parallelism: 1, (c, ct) =>
        {
            Count(FanoutCounters.Rows, 10);
            return Task.CompletedTask;
        }, ct);

        Progress.Position = 1;
        await SaveProgressAsync(ct);                                           // the first commit point
        Progress.Position = 2;
        Progress.Note = "second";
        await SaveProgressAsync(ct);                                           // the second
    }
}
