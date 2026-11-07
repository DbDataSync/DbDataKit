using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace DbDataKit.Runner.Kit.Tests.Samples;

// The four sample jobs of plan 28b section 2, as real jobs over in-memory services. Each is the text of the plan with what the plan leaves out filled in
// (the services, the unit and counter holders, the warehouse the fan-out writes to).

public sealed record Row(string Id, int Value);

public sealed record Page(IReadOnlyList<Row> Rows, DateTimeOffset Newest);

public sealed record Changes(IReadOnlyList<Row> Rows, DateTimeOffset Newest)
{
    public int Count => Rows.Count;
}

public sealed record Customer(string Id, string Name);

public sealed record Invoice(string Id, decimal Amount)
{
    public bool IsValid(out string why)
    {
        why = Amount > 0 ? "" : $"The invoice {Id} has an amount of {Amount}.";
        return Amount > 0;
    }
}

public interface IWarehouse : IAsyncDisposable
{
    Task UpsertAsync(string table, IReadOnlyList<Row> rows, CancellationToken ct);

    Task InsertAsync(string table, IReadOnlyList<Row> rows, CancellationToken ct);
}

/// <summary>The outside world of the sample jobs: a CRM, an SFTP server, an archive, a mail service and a warehouse, all in memory. One per test, found through <see cref="Current"/> (it flows with the async calls).</summary>
public sealed class World
{
    private static readonly AsyncLocal<World?> Held = new();

    public static World Current => Held.Value ?? throw new InvalidOperationException("No World is set for this test.");

    public static World Begin() => Held.Value = new World();

    /// <summary>Set when the first body of the `cancel` mode is running, so a test cancels at that moment, not after a guessed time (a slow start, as on Windows, must not matter).</summary>
    public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // the warehouse
    public ConcurrentQueue<(string Table, Row Row)> Warehouse { get; } = new();

    // the CRM: orders pages by modified time, customers, changes per customer
    public List<Page> OrderPages { get; } = [];

    public int OrderPagesFailAfter { get; set; } = int.MaxValue;

    public ConcurrentBag<DateTimeOffset> OrdersAskedSince { get; } = [];

    public List<Customer> Customers { get; } = [];

    public ConcurrentDictionary<string, int> CustomerRuns { get; } = new();

    public ConcurrentDictionary<string, int> FailingCustomers { get; } = new();

    public DateTimeOffset CustomerNewest { get; set; } = DateTimeOffset.Parse("2026-11-07T12:00:00Z");

    // sftp and the archive
    public ConcurrentDictionary<string, byte[]> Sftp { get; } = new();

    public int Downloads;

    public ConcurrentDictionary<DateOnly, ArchiveReceipt> Archived { get; } = new();

    public int ArchiveBodies;

    // mail
    public ConcurrentDictionary<string, LetterReceipt> Letters { get; } = new();

    public int LettersSent;

    // counters and switches of the trouble, backfill and stoppable jobs
    public int Bodies;
    public int Checks;

    public Exception? Throw { get; set; }

    public ConcurrentBag<int> Ran { get; } = [];

    public Func<int, Task>? AfterDay { get; set; }

    public int FailOnDay { get; set; } = -1;

    public PayloadRef? LastPayload { get; set; }

    /// <summary>Set by the protocol test jobs when they begin, so a test can act while a run is in flight.</summary>
    public TaskCompletionSource JobStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What the job saw when it read its streamed result back (the ref cannot be read once the run is over, when the run is remote).</summary>
    public ReadBack? ReadBack { get; set; }

    public object? Seen { get; set; }

    public ConcurrentDictionary<string, DateTimeOffset> CustomerSince { get; } = new();

    // a hook a test uses to do something to the world while a job runs
    public Func<Task>? OnOrderPage { get; set; }

    public async IAsyncEnumerable<Page> OrdersModifiedSince(DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
    {
        OrdersAskedSince.Add(since);
        var served = 0;
        foreach (var page in OrderPages.Where(p => p.Newest > since))
        {
            ct.ThrowIfCancellationRequested();
            if (served++ >= OrderPagesFailAfter) throw new IOException("The CRM dropped the connection.");
            if (OnOrderPage is { } hook) await hook().ConfigureAwait(false);
            yield return page;
        }
    }

    public Task<IReadOnlyList<Customer>> CustomersAsync() => Task.FromResult<IReadOnlyList<Customer>>(Customers);

    public Task<Changes> ChangesAsync(string customer, DateTimeOffset since)
    {
        CustomerRuns.AddOrUpdate(customer, 1, (_, n) => n + 1);
        CustomerSince[customer] = since;
        if (FailingCustomers.TryGetValue(customer, out var left) && left > 0)
        {
            FailingCustomers[customer] = left - 1;
            throw new InvalidOperationException($"The CRM refused the customer {customer}.");
        }

        return Task.FromResult(new Changes([new Row(customer + "-1", 1), new Row(customer + "-2", 2)], CustomerNewest));
    }
}

public sealed class MemoryWarehouse(World world) : IWarehouse
{
    public Task UpsertAsync(string table, IReadOnlyList<Row> rows, CancellationToken ct) => Add(table, rows);

    public Task InsertAsync(string table, IReadOnlyList<Row> rows, CancellationToken ct) => Add(table, rows);

    private Task Add(string table, IReadOnlyList<Row> rows)
    {
        foreach (var row in rows) world.Warehouse.Enqueue((table, row));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ---- 2.1 incremental API pull (a typed checkpoint)

public sealed record OrdersInput;

public sealed class OrdersProgress : Progress
{
    [Checkpoint]
    public DateTimeOffset OrdersModified { get; set; }
}

public sealed class OrdersSlots : Slots
{
    public Slot<IWarehouse> Warehouse { get; } = new("warehouse");
}

public static class OrdersCounters
{
    public static readonly Counter Rows = new("rows");
}

[RunnerTask("orders")]
public sealed class Orders : RunnerJob<OrdersInput, OrdersProgress, OrdersSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        await using var warehouse = await Slots.Warehouse.OpenAsync(ct);
        var newest = Progress.OrdersModified;
        await foreach (var page in World.Current.OrdersModifiedSince(newest, ct))
        {
            await warehouse.UpsertAsync("orders", page.Rows, ct);
            newest = page.Newest;
            Count(OrdersCounters.Rows, page.Rows.Count);
            Status($"orders up to {newest:u}");
        }

        Progress.OrdersModified = newest;
    }
}

// ---- 2.2 a file that disappears when it is downloaded (typed steps, streaming)

public static class DailyDropSteps
{
    public static readonly StreamStep<DateOnly> Download = Step.Stream<DateOnly>("download", day => $"{day:yyyy-MM-dd}");

    public static readonly ValueStep<DateOnly, ArchiveReceipt> Archive = Step.Value<DateOnly, ArchiveReceipt>("archive", day => $"{day:yyyy-MM-dd}");
}

public static class DailyDropCounters
{
    public static readonly Counter Rows = new("rows");
}

public sealed record ArchiveReceipt(string Url, long Bytes);

public sealed record DailyDropInput(DateOnly Day);

public sealed class DailyDropSlots : Slots
{
    public Slot<IWarehouse> Warehouse { get; } = new("warehouse");
}

[RunnerTask("daily-drop")]
public sealed class DailyDrop : RunnerJob<DailyDropInput, NoProgress, DailyDropSlots>
{
    private static string Remote(DateOnly day) => $"/drop/{day:yyyy-MM-dd}.csv";

    protected override async Task RunAsync(CancellationToken ct)
    {
        var world = World.Current;
        PayloadRef file = await Step(DailyDropSteps.Download, Input.Day)
            .Repeatable()                                                      // a crash mid-copy leaves the file on the server; after the copy the kit adopts the complete payload (K14)
            .StreamAsync(async (sink, ct) =>
            {
                Interlocked.Increment(ref world.Downloads);
                var bytes = world.Sftp[Remote(Input.Day)];
                await sink.WriteAsync(bytes, ct);
                world.Sftp.TryRemove(Remote(Input.Day), out _);                // only after the copy finished
            }, ct);

        await using var warehouse = await Slots.Warehouse.OpenAsync(ct);
        await using var reader = file.OpenRead();
        using var text = new StreamReader(reader);
        var batch = new List<Row>();
        while (await text.ReadLineAsync(ct) is { } line)
        {
            var parts = line.Split(',');
            batch.Add(new Row(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
            if (batch.Count == 2)
            {
                await warehouse.InsertAsync("orders_raw", batch, ct);
                Count(DailyDropCounters.Rows, batch.Count);
                batch = [];
            }
        }

        if (batch.Count > 0)
        {
            await warehouse.InsertAsync("orders_raw", batch, ct);
            Count(DailyDropCounters.Rows, batch.Count);
        }

        ArchiveReceipt receipt = await Step(DailyDropSteps.Archive, Input.Day)
            .Check(ct => Task.FromResult(world.Archived.TryGetValue(Input.Day, out var found) ? found : null))
            .RunAsync(ct =>
            {
                Interlocked.Increment(ref world.ArchiveBodies);
                var made = new ArchiveReceipt($"archive://{Input.Day:yyyy-MM-dd}", file.Size);
                world.Archived[Input.Day] = made;                              // the external effect, before the step's result is stored
                return Task.FromResult(made);
            }, ct);
        Status($"archived {receipt.Url}");
    }
}

// ---- 2.3 fan-out over customers (typed units, typed per-key progress)

public sealed class CustomersProgress : Progress
{
    public Keyed<CustomerProgress> Customer { get; } = new();
}

public sealed class CustomerProgress
{
    [Checkpoint]
    public DateTimeOffset Modified { get; set; }

    [State]
    public string? Cursor { get; set; }
}

public static class CustomerUnits
{
    public static readonly Unit<Customer> Sync = Unit.Of<Customer>("customer", c => c.Id);
}

public static class CustomerCounters
{
    public static readonly Counter Rows = new("rows");
}

[RunnerTask("customers")]
[RunnerDeclarations(typeof(CustomerUnits), typeof(CustomerCounters))]
public sealed class Customers : RunnerJob<NoInput, CustomersProgress, OrdersSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var world = World.Current;
        var customers = await world.CustomersAsync();
        await using var warehouse = await Slots.Warehouse.OpenAsync(ct);
        await ForEachAsync(CustomerUnits.Sync, customers, parallelism: 4, async (c, ct) =>
        {
            var mine = Progress.Customer[c.Id];
            var rows = await world.ChangesAsync(c.Id, mine.Modified);
            await warehouse.UpsertAsync("customer_changes", rows.Rows, ct);
            mine.Modified = rows.Newest;
            mine.Cursor = $"after-{rows.Rows[^1].Id}";
            Count(CustomerCounters.Rows, rows.Count);
        }, ct);
    }
}

// ---- 2.4 a webhook consumer (typed inbox, dead letters) and work that is not bytes or rows

public static class InvoiceSteps
{
    public static readonly ValueStep<Invoice, LetterReceipt> SendLetter = Step.Value<Invoice, LetterReceipt>("send-letter", i => i.Id);
}

public static class InvoiceCounters
{
    public static readonly Counter Letters = new("letters");
}

public sealed record LetterReceipt(string TrackingNumber, DateTimeOffset SentAt);

[RunnerTask("invoices")]
public sealed class Invoices : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var world = World.Current;
        while (await Inbox<Invoice>().TakeAsync(ct) is { } item)
        {
            if (!item.Value.IsValid(out var why)) { await item.RejectAsync(why, ct); continue; }

            LetterReceipt receipt = await Step(InvoiceSteps.SendLetter, item.Value)
                .Check(ct => Task.FromResult(world.Letters.TryGetValue(item.Value.Id, out var found) ? found : null))
                .RunAsync(ct =>
                {
                    Interlocked.Increment(ref world.LettersSent);
                    var made = new LetterReceipt("TRK-" + item.Value.Id, DateTimeOffset.Parse("2026-11-07T10:00:00Z"));
                    world.Letters[item.Value.Id] = made;
                    return Task.FromResult(made);
                }, ct);
            Count(InvoiceCounters.Letters, 1);
            await item.CompleteAsync(ct);
        }
    }
}

// ---- further jobs for what the samples do not reach

public static class BigSteps
{
    public static readonly StreamStep<string> Make = Step.Stream<string>("make", name => name, "application/octet-stream");
}

public sealed record BigInput(long Bytes);

public sealed record ReadBack(long Total, int BiggestRead, int Reads, string Hash);

/// <summary>Writes <see cref="BigInput.Bytes"/> pseudo-random bytes through a streaming step, in 64 KiB writes, and counts them.</summary>
[RunnerTask("big")]
public sealed class Big : RunnerJob<BigInput, NoProgress, NoSlots>
{
    public static byte[] Block { get; } = RandomNumberGenerator.GetBytes(64 * 1024);

    protected override async Task RunAsync(CancellationToken ct)
    {
        var file = await Step(BigSteps.Make, "big").Repeatable().StreamAsync(async (sink, ct) =>
        {
            for (long written = 0; written < Input.Bytes; written += Block.Length) await sink.WriteAsync(Block.AsMemory(0, (int)Math.Min(Block.Length, Input.Bytes - written)), ct);
        }, ct);
        World.Current.LastPayload = file;
        World.Current.ReadBack = await ReadBackAsync(file, ct);
        Count(new Counter("bytes"), file.Size);
    }

    /// <summary>Reads the result back through the ref with a buffer larger than a chunk, hashing as it goes.</summary>
    private static async Task<ReadBack> ReadBackAsync(PayloadRef file, CancellationToken ct)
    {
        using var seen = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        long total = 0;
        var biggest = 0;
        var reads = 0;
        await using var stream = file.OpenRead();
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            seen.AppendData(buffer, 0, n);
            total += n;
            biggest = Math.Max(biggest, n);
            reads++;
        }

        return new ReadBack(total, biggest, reads, Convert.ToHexString(seen.GetHashAndReset()));
    }
}

public static class BackfillSteps
{
    public static readonly ValueStep<int, int> Day = Step.Value<int, int>("day", d => d.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class BackfillProgress : Progress
{
    [Checkpoint]
    public int LastDay { get; set; }

    [State]
    public string? Token { get; set; }
}

/// <summary>A backfill that commits after each day (<c>SaveProgressAsync</c>), for the conflict and the crash tests.</summary>
[RunnerTask("backfill")]
public sealed class Backfill : RunnerJob<NoInput, BackfillProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        for (var day = Progress.LastDay + 1; day <= 3; day++)
        {
            Progress.LastDay = day;
            Progress.Token = $"token-{day}";
            if (World.Current.FailOnDay == day) throw new IOException($"The source failed on day {day}.");
            await SaveProgressAsync(ct);
            if (World.Current.AfterDay is { } hook) await hook(day);
        }
    }
}

public static class StepsOfTrouble
{
    public static readonly ValueStep<string, string> Charge = Step.Value<string, string>("charge", id => id);

    public static readonly ValueStep<string, string> Inner = Step.Value<string, string>("inner", id => id);

    public static readonly ValueStep<string, string> Big = Step.Value<string, string>("big-value", id => id);
}

public sealed record TroubleInput(string Mode);

/// <summary>A job that does what the mode says, for the failure and recovery tests.</summary>
[RunnerTask("trouble")]
public sealed class Trouble : RunnerJob<TroubleInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        switch (Input.Mode)
        {
            case "repeatable":
                await Step(StepsOfTrouble.Charge, "a").Repeatable().RunAsync(ct => { Interlocked.Increment(ref World.Current.Bodies); return Task.FromResult("done"); }, ct);
                break;
            case "ask":
                await Step(StepsOfTrouble.Charge, "a").Ask().RunAsync(ct => { Interlocked.Increment(ref World.Current.Bodies); return Task.FromResult("done"); }, ct);
                break;
            case "throws":
                await Step(StepsOfTrouble.Charge, "a").Repeatable().RunAsync(ct =>
                {
                    Interlocked.Increment(ref World.Current.Bodies);
                    if (World.Current.Throw is { } ex) throw ex;
                    return Task.FromResult("done");
                }, ct);
                break;
            case "cancel":
                await Step(StepsOfTrouble.Charge, "a").Repeatable().RunAsync(async ct =>
                {
                    if (Interlocked.Increment(ref World.Current.Bodies) == 1) { World.Current.BodyStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                    return "done";
                }, ct);
                break;
            case "nested":
                await Step(StepsOfTrouble.Charge, "a").Repeatable().RunAsync(async ct =>
                    await Step(StepsOfTrouble.Inner, "b").Repeatable().RunAsync(ct => Task.FromResult("x"), ct), ct);
                break;
            case "too-big":
                await Step(StepsOfTrouble.Big, "a").Repeatable().RunAsync(ct => Task.FromResult(new string('x', 70_000)), ct);
                break;
            case "stream-repeatable":
                await Step(BigSteps.Make, "s").Repeatable().StreamAsync(async (sink, ct) => { Interlocked.Increment(ref World.Current.Bodies); await sink.WriteAsync(new byte[2048], ct); }, ct);
                break;
            case "stream-check":
                await Step(BigSteps.Make, "c").Check(ct => { Interlocked.Increment(ref World.Current.Checks); return Task.FromResult<PayloadRef?>(null); })
                    .StreamAsync(async (sink, ct) => { Interlocked.Increment(ref World.Current.Bodies); await sink.WriteAsync(new byte[2048], ct); }, ct);
                break;
            case "stream-ask":
                await Step(BigSteps.Make, "c").Ask()
                    .StreamAsync(async (sink, ct) => { Interlocked.Increment(ref World.Current.Bodies); await sink.WriteAsync(new byte[2048], ct); }, ct);
                break;
            case "files":
                var first = await Files.ClaimAsync("inbox", "in/a.csv", 10, DateTimeOffset.Parse("2026-11-07T00:00:00Z"), "hash-1", ct);
                Count(new Counter("claimed"), first is null ? 0 : 1);
                if (first is not null) await first.CompleteAsync(ct: ct);
                var again = await Files.ClaimAsync("inbox", "in/renamed.csv", 10, DateTimeOffset.Parse("2026-11-07T00:00:00Z"), "hash-1", ct);
                Count(new Counter("claimed-again"), again is null ? 0 : 1);
                break;
            case "reject":
                await RejectAsync(new Invoice("bad", -1), "The amount is negative.", ct);
                break;
            case "take-one":
                await Inbox<Invoice>().TakeAsync(ct);          // neither completed nor rejected: released when the run ends
                break;
            case "log":
                Run.Log.LogInformationLine("hello from the run");
                Status("working");
                break;
            default:
                throw new InvalidOperationException("Unknown mode " + Input.Mode);
        }
    }
}

internal static class LogExtensions
{
    public static void LogInformationLine(this Microsoft.Extensions.Logging.ILogger log, string message) =>
        log.Log(Microsoft.Extensions.Logging.LogLevel.Information, new Microsoft.Extensions.Logging.EventId(0), message, null, (m, _) => m);
}

public static class StopUnits
{
    public static readonly Unit<int> Number = Unit.Of<int>("number", n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed record StopInput(bool Stop);

[RunnerTask("stoppable")]
public sealed class Stoppable : RunnerJob<StopInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct) =>
        ForEachAsync(StopUnits.Number, Enumerable.Range(1, 6), parallelism: 1, (n, ct) =>
        {
            World.Current.Ran.Add(n);
            if (n == 2) throw new InvalidOperationException("number 2 is bad");
            return Task.CompletedTask;
        }, ct, new UnitOptions(Input.Stop ? OnFailure.Stop : OnFailure.Continue));
}

// ---- every kind of progress value

public enum Speed
{
    Fast,
    Slow,
}

public sealed class AllProgress : Progress
{
    [Checkpoint] public string? Label { get; set; }

    [Checkpoint] public long Whole { get; set; }

    [Checkpoint] public int Small { get; set; }

    [Checkpoint] public decimal Money { get; set; }

    [Checkpoint] public DateTimeOffset At { get; set; }

    [Checkpoint] public DateOnly Day { get; set; }

    [State] public bool Flag { get; set; }

    [State] public Speed Pace { get; set; }

    [State] public Guid Id { get; set; }

    [State] public List<string>? Tags { get; set; }

    [State] public int? Maybe { get; set; }

    [State] public PayloadRef? Blob { get; set; }

    public int NotKept { get; set; }
}

public static class TypedSteps
{
    public static readonly StreamStep<string> Blob = Step.Stream<string>("blob", k => k);
}

public sealed record AllSeen(string? Label, long Whole, int Small, decimal Money, DateTimeOffset At, DateOnly Day, bool Flag, Speed Pace, Guid Id, List<string>? Tags, int? Maybe, long BlobSize);

[RunnerTask("typed")]
public sealed class TypedProgress : RunnerJob<NoInput, AllProgress, NoSlots>
{
    public static readonly Guid TheId = Guid.Parse("0197f0e0-aaaa-7000-8000-000000000001");

    protected override async Task RunAsync(CancellationToken ct)
    {
        World.Current.Seen = new AllSeen(Progress.Label, Progress.Whole, Progress.Small, Progress.Money, Progress.At, Progress.Day, Progress.Flag, Progress.Pace, Progress.Id, Progress.Tags, Progress.Maybe, Progress.Blob?.Size ?? -1);
        if (Progress.Whole > 0)
        {
            Progress.Money += 1.5m;                     // a second run moves one value and leaves the others
            return;
        }

        Progress.Label = "délta/token=1";
        Progress.Whole = 9_000_000_000;
        Progress.Small = 7;
        Progress.Money = 123456.789m;
        Progress.At = DateTimeOffset.Parse("2026-11-07T08:09:10Z");
        Progress.Day = new DateOnly(2026, 11, 7);
        Progress.Flag = true;
        Progress.Pace = Speed.Slow;
        Progress.Id = TheId;
        Progress.Tags = ["a", "b"];
        Progress.Maybe = 5;
        Progress.NotKept = 99;
        Progress.Blob = await Step(TypedSteps.Blob, "b").Repeatable().StreamAsync((sink, ct) => sink.WriteAsync(new byte[3000], ct).AsTask(), ct);
    }
}
