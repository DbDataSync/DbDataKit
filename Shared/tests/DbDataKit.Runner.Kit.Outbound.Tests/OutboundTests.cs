using System.Text;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Changes;
using DbDataKit.Runner.Kit.Testing;
using DbDataKit.Schema.Tests;
using DbDataKit.State;
using DbDataKit.Store;
using StoreChange = DbDataKit.Store.RowChange;
using Microsoft.Data.Sqlite;

namespace DbDataKit.Runner.Kit.Outbound.Tests;

/// <summary>The API orders are pushed to: it remembers what it received, ignores a batch whose idempotency key it has seen, and can be made to fail.</summary>
public sealed class OrdersApi
{
    private readonly HashSet<string> _keys = [];

    public List<string> Received { get; } = [];

    public List<string> Keys { get; } = [];

    public List<string> Deleted { get; } = [];

    public int Calls { get; private set; }

    /// <summary>The call numbers (1 is the first) that throw, once each.</summary>
    public HashSet<int> FailCalls { get; } = [];

    public Task ReceiveAsync(PushBatch batch)
    {
        Calls++;
        if (FailCalls.Remove(Calls)) throw new IOException("the API went away");
        Keys.Add(batch.IdempotencyKey);
        if (_keys.Add(batch.IdempotencyKey)) Received.AddRange(batch.Records.Select(r => $"{r.Key}={r.Values["qty"]}"));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(PushDeletion deletion)
    {
        Calls++;
        if (_keys.Add(deletion.IdempotencyKey)) Deleted.AddRange(deletion.Keys);
        return Task.CompletedTask;
    }
}

public sealed class Source
{
    public List<(string Id, int Qty)> Orders { get; set; } = [];

    public PushReport? Report { get; set; }
}

public sealed record PushInput(OrdersApi Api, Source Source, bool Deletes = false, bool AllowEmpty = false);

public sealed class PushProgress : Progress
{
    [Checkpoint] public RowSnapshot? Sent { get; set; }
}

[RunnerTask("push-orders-out")]
public sealed class PushOut : RunnerJob<PushInput, PushProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        Progress.Sent ??= new RowSnapshot();
        var push = new OutboundPush(new SnapshotLedger(Progress.Sent), new PushOptions { BatchSize = 2, Commit = SaveProgressAsync, KeyPrefix = "orders-", AllowEmptySource = Input.AllowEmpty });
        Input.Source.Report = await push.PushAsync(Records(), (batch, _) => Input.Api.ReceiveAsync(batch), Input.Deletes ? (d, _) => Input.Api.DeleteAsync(d) : null, ct);
    }

    private async IAsyncEnumerable<OutboundRecord> Records()
    {
        foreach (var (id, qty) in Input.Source.Orders) yield return new OutboundRecord(id, new Dictionary<string, object?> { ["id"] = id, ["qty"] = qty });
        await Task.CompletedTask;
    }
}

public class OutboundTests
{
    private static Source Five() => new() { Orders = [("a", 1), ("b", 2), ("c", 3), ("d", 4), ("e", 5)] };

    [Fact]
    public async Task A_push_that_fails_halfway_resumes_without_repeating_the_sent_part()
    {
        var api = new OrdersApi();
        api.FailCalls.Add(3);                                                          // batches [a,b], [c,d] go; [e] fails
        var source = Five();
        using var sim = new RunSimulator<PushOut>(new PushInput(api, source));

        await sim.RunAsync();
        Assert.NotEqual(RunOutcome.Succeeded, sim.Outcome);
        Assert.True(sim.Failure!.Retryable);
        Assert.Equal(["a=1", "b=2", "c=3", "d=4"], api.Received);
        Assert.Equal(4, sim.Progress<PushProgress>().Sent!.Count);                       // saved at each batch, though the run failed

        await sim.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(["a=1", "b=2", "c=3", "d=4", "e=5"], api.Received);                 // each exactly once
        Assert.Equal((1L, 4L), (source.Report!.Sent, source.Report.Skipped));
    }

    [Fact]
    public async Task A_second_run_with_no_change_sends_nothing_and_a_change_sends_only_that_record()
    {
        var api = new OrdersApi();
        var source = Five();
        using var sim = new RunSimulator<PushOut>(new PushInput(api, source));
        await sim.RunAsync();
        var calls = api.Calls;
        Assert.Equal(3, calls);

        await sim.RunAsync();
        Assert.True(source.Report!.NothingToDo);
        Assert.Equal(calls, api.Calls);                                                  // not one request

        source.Orders[2] = ("c", 30);
        await sim.RunAsync();
        Assert.Equal((1L, 4L), (source.Report!.Sent, source.Report.Skipped));
        Assert.Equal("c=30", api.Received[^1]);
        Assert.Equal(calls + 1, api.Calls);
    }

    [Fact]
    public async Task A_crash_between_the_send_and_the_save_repeats_one_batch_under_the_same_key_which_the_api_recognises()
    {
        var api = new OrdersApi();
        var source = Five();
        using var sim = new RunSimulator<PushOut>(new PushInput(api, source));
        sim.ConflictOnSave(2);                                                          // the second batch is sent, its save finds the checkpoint moved: nothing written

        await sim.RunAsync();
        Assert.Equal(RunOutcome.Stopped, sim.Outcome);
        Assert.Equal(["a=1", "b=2", "c=3", "d=4"], api.Received);
        Assert.Equal(2, sim.Progress<PushProgress>().Sent!.Count);                       // only the first batch was kept

        await sim.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(["a=1", "b=2", "c=3", "d=4", "e=5"], api.Received);                 // [c,d] was sent twice and received once
        Assert.Equal(api.Keys.Count - 1, api.Keys.Distinct().Count());                   // the repeated batch had the key it had before
    }

    [Fact]
    public async Task A_record_that_disappeared_is_deleted_only_when_asked_and_an_empty_source_is_refused()
    {
        var api = new OrdersApi();
        var source = Five();
        using var sim = new RunSimulator<PushOut>(new PushInput(api, source, Deletes: true));
        await sim.RunAsync();

        source.Orders.RemoveAt(1);
        await sim.RunAsync();
        Assert.Equal(["b"], api.Deleted);
        Assert.Equal((0L, 4L, 1L), (source.Report!.Sent, source.Report.Skipped, source.Report.Deleted));

        source.Orders.Clear();
        await sim.RunAsync();
        Assert.NotEqual(RunOutcome.Succeeded, sim.Outcome);                              // a source that gave nothing is a broken read
        Assert.Equal(["b"], api.Deleted);
    }

    [Fact]
    public async Task A_file_is_sent_once_per_content()
    {
        var ledger = new SnapshotLedger(new RowSnapshot());
        var push = new OutboundPush(ledger, new PushOptions { KeyPrefix = "report-" });
        var sent = new List<(string Text, string Key)>();
        async Task<bool> PushAsync(string text) => await push.PushFileAsync("daily.csv", (s, ct) => s.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask(), async (s, key, ct) =>
        {
            using var reader = new StreamReader(s);
            sent.Add((await reader.ReadToEndAsync(ct), key));
        });

        Assert.True(await PushAsync("id\n1\n"));
        Assert.False(await PushAsync("id\n1\n"));
        Assert.True(await PushAsync("id\n1\n2\n"));
        Assert.Equal(2, sent.Count);
        Assert.NotEqual(sent[0].Key, sent[1].Key);
        Assert.StartsWith("report-", sent[0].Key);
    }

    [Fact]
    public async Task The_runners_idempotency_keys_serve_as_a_ledger_for_code_that_holds_the_state_database()
    {
        var api = new OrdersApi();
        using var sim = new RunSimulator<PushOut>(new PushInput(api, Five()));
        var progress = new ProgressStore(sim.Database, new ProgressKey("simulator", "test", "outbound-by-hand"));
        var records = Enumerable.Range(1, 3).Select(i => new OutboundRecord("k" + i, new Dictionary<string, object?> { ["qty"] = i })).ToList();
        async IAsyncEnumerable<OutboundRecord> Source() { foreach (var r in records) yield return r; await Task.CompletedTask; }

        var first = await new OutboundPush(new ProgressStoreLedger(progress)).PushAsync(Source(), (b, _) => api.ReceiveAsync(b));
        var again = await new OutboundPush(new ProgressStoreLedger(progress)).PushAsync(Source(), (b, _) => api.ReceiveAsync(b));
        Assert.Equal((3L, 0L), (first.Sent, first.Skipped));
        Assert.Equal((0L, 3L), (again.Sent, again.Skipped));
        await Assert.ThrowsAsync<PushException>(() => new OutboundPush(new ProgressStoreLedger(progress)).PushAsync(Source(), (b, _) => Task.CompletedTask, (d, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task The_rows_of_a_table_or_view_are_records_keyed_by_the_named_columns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE orders(region TEXT, id INTEGER, qty INTEGER, note TEXT); INSERT INTO orders VALUES ('eu', 1, 5, NULL), ('us', 1, 7, 'rush');";
            await create.ExecuteNonQueryAsync();
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT region, id, qty, note FROM orders ORDER BY region";
        await using var reader = await select.ExecuteReaderAsync();
        var records = new List<OutboundRecord>();
        await foreach (var record in OutboundSources.FromReaderAsync(reader, ["region", "id"])) records.Add(record);

        Assert.Equal(["eu\u001f1", "us\u001f1"], records.Select(r => r.Key));
        Assert.Null(records[0].Values["note"]);
        Assert.Equal("rush", records[1].Values["note"]);
        Assert.NotEqual(records[0].ContentHash, records[1].ContentHash);
        await Assert.ThrowsAsync<ArgumentException>(async () => { await foreach (var _ in OutboundSources.FromReaderAsync(reader, ["nope"])) { } });
    }

    private const string StoreFixture = """
        schemaVersion: 1
        name: out_territories
        storage: {connection: mdm_sql}
        entities:
          territory:
            key: [territory_code]
            attributes:
              territory_code: {type: VARCHAR(10), required: true}
              name:           {type: VARCHAR(100), required: true}
              quota:          {type: "DECIMAL(10,2)", min: 0}
        """;

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task The_rows_of_a_DbDataStore_entity_are_pushed_once_and_a_changed_row_goes_again(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var loaded = DataModelLoader.Load(StoreFixture, "model.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(loaded.Model!, engine));
        var writer = new ModelWriter(loaded.Model!, engines.Connection, engine);
        var ada = new WriteActor("u-ada");
        await writer.ApplyAsync([.. Enumerable.Range(1, 5).Select(i => StoreChange.Insert("territory", new Dictionary<string, object?> { ["territory_code"] = "T-" + i, ["name"] = "Name " + i, ["quota"] = i * 10m }))], ada);

        var ledger = new SnapshotLedger(new RowSnapshot());
        var batches = new List<int>();
        Task Send(PushBatch b, CancellationToken _) { batches.Add(b.Records.Count); return Task.CompletedTask; }

        var first = await new OutboundPush(ledger, new PushOptions { BatchSize = 2 }).PushAsync(OutboundSources.FromStoreAsync(writer, loaded.Model!, "territory", pageSize: 2), Send);
        Assert.Equal((5L, 0L), (first.Sent, first.Skipped));
        Assert.Equal([2, 2, 1], batches);

        var again = await new OutboundPush(ledger, new PushOptions { BatchSize = 2 }).PushAsync(OutboundSources.FromStoreAsync(writer, loaded.Model!, "territory", pageSize: 2), Send);
        Assert.True(again.NothingToDo);

        var row = (await writer.GetAsync("territory", new Dictionary<string, object?> { ["territory_code"] = "T-3" }))!;
        await writer.UpdateAsync("territory", new Dictionary<string, object?> { ["territory_code"] = "T-3" }, row.Version, new Dictionary<string, object?> { ["quota"] = 99m }, ada);
        var changed = await new OutboundPush(ledger, new PushOptions { BatchSize = 2 }).PushAsync(OutboundSources.FromStoreAsync(writer, loaded.Model!, "territory", pageSize: 2), Send);
        Assert.Equal((1L, 4L), (changed.Sent, changed.Skipped));
    }
}
