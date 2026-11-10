using DbDataSync.Core.Config;
using DbDataSync.State;
using DbDataSync.TaskRunner.Runner;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>Plan 28c E12: what the API asks of a replication the runner owns arrives as the <c>requests</c> input of the drain and becomes rows of the drain's own queue before its first cycle.</summary>
public sealed class DrainRequestsTests
{
    private static Task Apply(string text, FakeDrainQueue queue, FakeLedger ledger) =>
        DrainRequests.ApplyAsync(text, "sales", queue, ledger, ledger, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task ABulkLoad_BecomesOneRowPerSegment_WithTheBatchInThePayload_AndARecordOfTheBatch()
    {
        var requests = new List<DrainRequest>
        {
            new(nameof(RunKind.BulkLoad), "orders", "1-10", SegmentSerializer.Serialize(new RangeSegment("Id", "1", "10")), BatchId: "b1", BatchSegments: 2, EstimatedRows: 20),
            new(nameof(RunKind.BulkLoad), "orders", "10-20", SegmentSerializer.Serialize(new RangeSegment("Id", "10", "20")), BatchId: "b1", BatchSegments: 2, EstimatedRows: 20),
        };
        var queue = new FakeDrainQueue();
        var ledger = new FakeLedger();

        await Apply(DrainRequestText.Serialize(requests), queue, ledger);

        Assert.Equal(["1-10", "10-20"], queue.Rows.Select(r => r.Part));
        Assert.All(queue.Rows, r => Assert.Equal((nameof(RunKind.BulkLoad), "orders"), (r.Kind, r.Subject)));
        Assert.All(queue.Rows, r => Assert.Contains("\"BatchId\":\"b1\"", r.Payload));
        var batch = ledger.Batches["b1"];
        Assert.Equal(("orders", 2, 20L), (batch.Mapping, batch.Segments, batch.EstimatedRows));
    }

    [Fact]
    public async Task AReconcile_KeepsItsGuardAndKinds_AndAVerification_IsAnItemWithNoSegment()
    {
        var queue = new FakeDrainQueue();
        await Apply(DrainRequestText.Serialize([
            new DrainRequest(nameof(RunKind.ReconcileDeletes), "orders", "Full", SegmentSerializer.Serialize(new FullSegment()), "KeyReconcile", "StagingTable", "KeyReconcileDelete", DeleteGuardJson: "{\"kind\":\"none\"}"),
            new DrainRequest(nameof(RunKind.Verification), "orders"),
        ]), queue, new FakeLedger());

        var sweep = queue.Rows.Single(r => r.Kind == nameof(RunKind.ReconcileDeletes));
        Assert.Contains("KeyReconcile", sweep.Payload);
        Assert.Contains("none", sweep.Payload);
        var verify = queue.Rows.Single(r => r.Kind == nameof(RunKind.Verification));
        Assert.Equal(("orders", ""), (verify.Subject, verify.Part));
    }

    private static string Item(string mapping, string sourceKey) => WatermarkCheckpoint.ItemKey(mapping, sourceKey);

    [Fact]
    public async Task APosition_SeedsAMappingThatHasNone_AndNeverReplacesTheRunsOwn()
    {
        var ledger = new FakeLedger();
        ledger.Write(Item("kept", "src.dbo.kept"), "own", null);
        var time = new DateTimeOffset(2026, 11, 9, 1, 2, 3, TimeSpan.Zero);

        await Apply(DrainRequestText.Serialize([
            new DrainRequest("Position", "new", SourceKey: "src.dbo.new", Position: "0x10", PositionTime: time, Intent: nameof(ReadIntent.Changes), Hold: nameof(ReadHold.None)),
            new DrainRequest("Position", "kept", SourceKey: "src.dbo.kept", Position: "handed"),
        ]), new FakeDrainQueue(), ledger);

        Assert.Equal(("0x10", (DateTimeOffset?)time), ledger.Read(Item("new", "src.dbo.new")));
        Assert.Equal((ReadIntent.Changes, ReadHold.None), (ledger.ReadIntentAndHold(Item("new", "src.dbo.new")).Intent!.Value, ledger.ReadIntentAndHold(Item("new", "src.dbo.new")).Hold!.Value));
        Assert.Equal("own", ledger.Read(Item("kept", "src.dbo.kept"))!.Value.Position);
    }

    [Fact]
    public async Task AResync_SetsTheIntentToAnInitialLoad_AndQueuesThePrimaryPassThatCarriesItOut()
    {
        var queue = new FakeDrainQueue();
        var ledger = new FakeLedger();
        ledger.Write(Item("orders", "src.dbo.orders"), "0x10", null);
        ledger.WriteHold(Item("orders", "src.dbo.orders"), ReadHold.Loading);

        await Apply(DrainRequestText.Serialize([new DrainRequest("Resync", "orders", SourceKey: "src.dbo.orders")]), queue, ledger);

        Assert.Equal((ReadIntent.InitialLoad, ReadHold.None), (ledger.ReadIntentAndHold(Item("orders", "src.dbo.orders")).Intent!.Value, ledger.ReadIntentAndHold(Item("orders", "src.dbo.orders")).Hold!.Value));
        Assert.Equal(nameof(RunKind.Primary), Assert.Single(queue.Rows).Kind);
    }

    [Fact]
    public async Task ARequestThatIsNotOne_FailsNamingTheInput_AndAnUnknownKindNamesTheKinds()
    {
        var notJson = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply("not json", new FakeDrainQueue(), new FakeLedger()));
        Assert.Contains("`requests`", notJson.Message);

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(DrainRequestText.Serialize([new DrainRequest("Nope", "orders")]), new FakeDrainQueue(), new FakeLedger()));
        Assert.Contains("BulkLoad", unknown.Message);
    }

    [Fact]
    public async Task ARequestOfOneId_IsAppliedOnce_HoweverOftenTheInputIsRead()
    {
        var queue = new FakeDrainQueue();
        var ledger = new FakeLedger();
        var text = DrainRequestText.Serialize([new DrainRequest(nameof(RunKind.Verification), "orders", Id: "r1"), new DrainRequest("Resync", "orders", SourceKey: "k", Id: "r2")]);

        await Apply(text, queue, ledger);
        ledger.Write("k", "0x10", null);
        ledger.WriteIntent("k", ReadIntent.Changes);
        await Apply(text, queue, ledger);          // a retried run reads its input again

        Assert.Equal(2, queue.Rows.Count);         // one verification and the resync's primary pass, not four
        Assert.Equal(ReadIntent.Changes, ledger.ReadIntentAndHold("k").Intent);    // and the resync did not set the intent a second time
        Assert.Equal(["r1", "r2"], ledger.Applied.Order());
    }

    [Fact]
    public async Task ARequestThatCameAsAWorkItem_IsAppliedByTheDrain_AndTheItemIsFinished()
    {
        var queue = new FakeDrainQueue();
        var ledger = new FakeLedger();
        var request = new DrainRequest(nameof(RunKind.Verification), "orders", Id: "r9");
        var id = await queue.EnqueueAsync(DrainRequestText.ItemKind, "orders", "r9", "", "", DrainRequestText.SerializeOne(request), CancellationToken.None);
        var item = await queue.ClaimAsync(null, CancellationToken.None);

        await DrainRequests.ApplyItemAsync(item!, "sales", queue, ledger, ledger, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Done", queue.Rows.Single(r => r.Kind == DrainRequestText.ItemKind).Status);
        Assert.Single(queue.Rows, r => r.Kind == nameof(RunKind.Verification));
        Assert.Contains("r9", ledger.Applied);
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public void NoRequests_IsNoText_AndNoTextIsNoRequests()
    {
        Assert.Equal("", DrainRequestText.Serialize([]));
        Assert.Empty(DrainRequestText.Parse(""));
        Assert.Empty(DrainRequestText.Parse(null));
    }
}
