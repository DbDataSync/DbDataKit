using DbDataKit.Config.Git;
using DbDataKit.Runner.Kit;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using DbDataSync.TaskRunner.Runner;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>An in-memory ledger and checkpoint store with the rules the drain's progress has: a promotion needs the loading hold and a pending position, and clears the pending parts.</summary>
internal sealed class FakeLedger : IDrainLedger, IDrainCheckpoints
{
    public Dictionary<string, (string? Position, DateTimeOffset? Time, ReadIntent? Intent, ReadHold? Hold, string? PendingPosition, string? PendingBatch)> Items { get; } = [];

    public Dictionary<string, BatchRecord> Batches { get; } = [];

    public Dictionary<string, (long Total, long AtSweep)> Reads { get; } = [];

    private (string? Position, DateTimeOffset? Time, ReadIntent? Intent, ReadHold? Hold, string? PendingPosition, string? PendingBatch) Get(string key) => Items.GetValueOrDefault(key);

    public (string Position, DateTimeOffset? Time)? Read(string key) => Get(key) is { Position: { } p } i ? (p, i.Time) : null;

    public void Write(string key, string position, DateTimeOffset? time) => Items[key] = Get(key) with { Position = position, Time = time };

    public (ReadIntent? Intent, ReadHold? Hold) ReadIntentAndHold(string key) => (Get(key).Intent, Get(key).Hold);

    public void WriteIntent(string key, ReadIntent intent) => Items[key] = Get(key) with { Intent = intent };

    public void WriteHold(string key, ReadHold hold) => Items[key] = Get(key) with { Hold = hold };

    public void SetPendingLoad(string key, string position, DateTimeOffset? time, string batchId) => Items[key] = Get(key) with { Hold = ReadHold.Loading, PendingPosition = position, PendingBatch = batchId };

    public bool PromotePendingLoad(string key)
    {
        var item = Get(key);
        if (item.Hold != ReadHold.Loading || item.PendingPosition is null) return false;
        Items[key] = item with { Position = item.PendingPosition, Intent = ReadIntent.Changes, Hold = ReadHold.None, PendingPosition = null, PendingBatch = null };
        return true;
    }

    public BatchRecord? ReadBatch(string batchId) => Batches.GetValueOrDefault(batchId);

    public void WriteBatch(string batchId, BatchRecord batch) => Batches[batchId] = batch;

    public void AddRowsRead(string mapping, long rows) => Reads[mapping] = (Reads.GetValueOrDefault(mapping).Total + rows, Reads.GetValueOrDefault(mapping).AtSweep);

    public long RowsReadSinceSweep(string mapping) => Reads.GetValueOrDefault(mapping).Total - Reads.GetValueOrDefault(mapping).AtSweep;

    public HashSet<string> Applied { get; } = [];

    public bool TryMarkApplied(string requestId) => Applied.Add(requestId);

    public bool WasApplied(string requestId) => Applied.Contains(requestId);

    public void MarkSweep(string mapping) => Reads[mapping] = (Reads.GetValueOrDefault(mapping).Total, Reads.GetValueOrDefault(mapping).Total);
}

/// <summary>Plan 28c E11b: <c>RequestInitialLoad</c> and <c>RequestSegmentedLoad</c> on a drain's own queue, the batch and the promotion of the captured position.</summary>
public sealed class DrainBulkLoadsTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-drainbulk-").FullName;
    private readonly ConfigRepository _config;

    public DrainBulkLoadsTests()
    {
        Repository.Init(_repoRoot);
        _config = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _config.SaveConnection(new ConnectionInput { Name = "src", DriverType = DriverIds.MsSql, Host = "localhost", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "pw" }, Author);
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "repl",
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = "src", Database = "App" }, Target = new EndpointRef { ConnectionName = "src", Database = "DW" } },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "*/5 * * * *" },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = new Dictionary<string, string> { ["watermarkColumn"] = "UpdatedAt" } },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, Author);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private void SaveMapping(string name, params BatchReloadSegment[] defaultSegmenting) =>
        _config.SaveTableMapping("repl", new TableMappingConfig
        {
            Name = name,
            Sources = [new SourceTableSpec { Schema = "dbo", Table = name }],
            Targets = [new TableSpec { Schema = "dbo", Table = name }],
            SourceColumns = [new CachedColumn("Id", "int", false, true, false)],
            TargetColumns = [new CachedColumn("Id", "int", false, true, false)],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
            DefaultSegmenting = [.. defaultSegmenting],
        }, Author);

    private sealed class Segments : IDrainSegments
    {
        public Func<IReadOnlyList<BatchReloadSegment>, IReadOnlyList<BatchReloadSegment>> Expand { get; set; } = s => s;

        public Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested, string? readerKind, CancellationToken cancellationToken) =>
            Task.FromResult(Expand(requested));

        public Task<(long? Rows, string? Caveat)> EstimateAsync(ReplicationTaskConfig task, TableMappingConfig mapping, CancellationToken cancellationToken) => Task.FromResult<(long?, string?)>((1000, null));
    }

    private (DrainBulkLoads Loads, FakeDrainQueue Queue, FakeLedger Ledger, ProtocolRunnerState State, Segments Segments) Create()
    {
        var queue = new FakeDrainQueue();
        var ledger = new FakeLedger();
        var segments = new Segments();
        var loads = new DrainBulkLoads(_config, queue, ledger, segments, NullLogger.Instance);
        return (loads, queue, ledger, new ProtocolRunnerState(ledger, NullLogger.Instance, queue, loads), segments);
    }

    private static readonly DateTimeOffset At = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnInitialLoad_QueuesTheDefaultSegments_HoldsTheMappingAndKeepsThePositionPending()
    {
        SaveMapping("big", new ListSegment("Region", ["north"]), new ListSegment("Region", ["south"]), new ListSegment("Region", ["east"]));
        var (_, queue, ledger, state, _) = Create();

        state.RequestInitialLoad("repl", "big", "src/App/big", "0x42", At);

        var rows = queue.Rows.Where(r => r.Kind == "BulkLoad").ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("big", r.Subject));
        var batch = Assert.Single(ledger.Batches);
        Assert.Equal(3, batch.Value.Segments);
        Assert.Equal(1000, batch.Value.EstimatedRows);
        Assert.All(rows, r => Assert.Contains(batch.Key, r.Payload!));
        var read = state.GetReadState("repl", "big", "src/App/big")!;
        Assert.Equal(ReadHold.Loading, read.Hold);
        Assert.Null(read.Watermark);
        Assert.Equal("0x42", ledger.Items[WatermarkCheckpoint.ItemKey("big", "src/App/big")].PendingPosition);
    }

    [Fact]
    public void AnInitialLoad_WithNoSegmenting_IsOneFullSegment()
    {
        SaveMapping("small");
        var (_, queue, ledger, state, _) = Create();

        state.RequestInitialLoad("repl", "small", "k", "7", null);

        Assert.Single(queue.Rows);
        Assert.Equal(1, ledger.Batches.Single().Value.Segments);
    }

    [Fact]
    public void ThePosition_IsPromoted_OnlyWhenTheLastSegmentSucceeded()
    {
        SaveMapping("big", new ListSegment("Region", ["north"]), new ListSegment("Region", ["south"]));
        var (_, queue, ledger, state, _) = Create();
        state.RequestInitialLoad("repl", "big", "k", "0x42", At);

        var first = Claim(state, queue);
        state.CompleteRun(first.RunId, RunStatus.Succeeded, 10, 10, null);
        state.MarkDone(first.Id);
        Assert.Null(state.GetWatermark("repl", "big", "k"));
        Assert.Equal(ReadHold.Loading, state.GetReadState("repl", "big", "k")!.Hold);

        var second = Claim(state, queue);
        state.CompleteRun(second.RunId, RunStatus.Succeeded, 5, 5, null);

        Assert.Equal("0x42", state.GetWatermark("repl", "big", "k"));
        var after = state.GetReadState("repl", "big", "k")!;
        Assert.Equal(ReadHold.None, after.Hold);
        Assert.Equal(ReadIntent.Changes, after.Intent);
        Assert.Null(ledger.Items[WatermarkCheckpoint.ItemKey("big", "k")].PendingPosition);
        Assert.Equal((2, 0, 15L, 15L), (ledger.Batches.Single().Value.Succeeded, ledger.Batches.Single().Value.Failed, ledger.Batches.Single().Value.RowsRead, ledger.Batches.Single().Value.RowsWritten));
    }

    [Fact]
    public void AFailedSegment_LeavesTheMappingLoading_AndARetryThatSucceedsCountsOnce()
    {
        SaveMapping("big", new ListSegment("Region", ["north"]), new ListSegment("Region", ["south"]));
        var (_, queue, ledger, state, _) = Create();
        state.RequestInitialLoad("repl", "big", "k", "0x42", At);

        var first = Claim(state, queue);
        state.CompleteRun(first.RunId, RunStatus.Failed, 0, 0, "boom");
        state.MarkFailed(first.Id);
        var second = Claim(state, queue);
        state.CompleteRun(second.RunId, RunStatus.Succeeded, 5, 5, null);

        Assert.Null(state.GetWatermark("repl", "big", "k"));
        Assert.Equal(ReadHold.Loading, state.GetReadState("repl", "big", "k")!.Hold);

        // the retry of the first item succeeds: its earlier failure is replaced, not added to
        state.CompleteRun(first.RunId, RunStatus.Succeeded, 10, 10, null);

        var batch = ledger.Batches.Single().Value;
        Assert.Equal((2, 0), (batch.Succeeded, batch.Failed));
        Assert.Equal("0x42", state.GetWatermark("repl", "big", "k"));
    }

    [Fact]
    public void AnInitialLoad_WhileABulkLoadOfTheMappingIsInFlight_IsRefused_AndLeavesTheHoldAsItWas()
    {
        SaveMapping("big");
        var (loads, queue, ledger, state, _) = Create();
        loads.RequestSegmentedLoad("repl", "big", SegmentSerializer.SerializeMany([new FullSegment()]));

        var refused = Assert.Throws<WorkQueueCollisionException>(() => state.RequestInitialLoad("repl", "big", "k", "0x42", At));

        Assert.Contains("already in progress", refused.Message);
        Assert.Empty(ledger.Items);
        Assert.Empty(ledger.Batches);
        Assert.Single(queue.Rows);
    }

    [Fact]
    public void ASegmentedLoad_QueuesTheResolvedSegments_AndSaysNullWhileOneIsInFlight()
    {
        SaveMapping("big");
        var (_, queue, ledger, state, _) = Create();
        var json = SegmentSerializer.SerializeMany([new ListSegment("Region", ["a"]), new ListSegment("Region", ["b"])]);

        var batch = state.RequestSegmentedLoad("repl", "big", json);

        Assert.NotNull(batch);
        Assert.Equal(2, queue.Rows.Count(r => r.Kind == "BulkLoad"));
        Assert.Empty(ledger.Batches);   // nothing waits on a segmented load of a pass: no position is pending
        Assert.Null(state.RequestSegmentedLoad("repl", "big", json));
        Assert.Equal(2, queue.Rows.Count);
    }

    [Fact]
    public void ASegmentOfAnotherMappingsBatch_IsNotCounted_AndAnUnknownBatchChangesNothing()
    {
        SaveMapping("big", new ListSegment("Region", ["north"]), new ListSegment("Region", ["south"]));
        var (loads, _, ledger, _, _) = Create();

        loads.SegmentFinished(Guid.NewGuid(), "no-such-batch", true, 1, 1);

        Assert.Empty(ledger.Batches);
    }

    [Fact]
    public void AVerificationResult_IsAnOutputOfTheRun_AndAStateWithoutOneSaysWhatIsMissing()
    {
        var ledger = new FakeLedger();
        var recorded = new List<VerificationResultRecord>();
        var withOutputs = new ProtocolRunnerState(ledger, NullLogger.Instance, outputs: new Outputs(recorded));
        var record = new VerificationResultRecord(0, Guid.NewGuid(), "repl", "big", "counts", At, At, At, 10, 0, "results/x.json");

        withOutputs.RecordVerificationResult(record);

        Assert.Equal(record, Assert.Single(recorded));
        var without = new ProtocolRunnerState(ledger, NullLogger.Instance);
        var refused = Assert.Throws<NotSupportedException>(() => without.RecordVerificationResult(record));
        Assert.Contains("RecordVerificationResult", refused.Message);
    }

    private sealed class Outputs(List<VerificationResultRecord> sink) : IDrainOutputs
    {
        public void VerificationResult(VerificationResultRecord result) => sink.Add(result);

        public void ChangeChecks(string replication, IReadOnlyList<ChangeCheckEntry> checks)
        {
        }
    }

    private static WorkItem Claim(ProtocolRunnerState state, FakeDrainQueue queue)
    {
        var item = queue.ClaimAsync(null, CancellationToken.None).GetAwaiter().GetResult() ?? throw new InvalidOperationException("nothing to claim");
        var claimed = state.Claimed("repl", item);
        state.MarkRunning(claimed.Id);
        return claimed;
    }
}
