using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataSync.Core.Config;
using DbDataSync.State;
using DbDataSync.TaskRunner.Runner;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>Plan 28c E03: what <see cref="ProtocolRunnerState"/> answers, and what it refuses by name.</summary>
public sealed class ProtocolRunnerStateTests
{
    private sealed class Checkpoints : IDrainCheckpoints
    {
        public Dictionary<string, (string Position, DateTimeOffset? Time)> Stored { get; } = [];

        public Dictionary<string, (ReadIntent? Intent, ReadHold? Hold)> Held { get; } = [];

        public List<string> Writes { get; } = [];

        public (string Position, DateTimeOffset? Time)? Read(string key) => Stored.TryGetValue(key, out var value) ? value : null;

        public void Write(string key, string position, DateTimeOffset? time)
        {
            Stored[key] = (position, time);
            Writes.Add(key);
        }

        public (ReadIntent? Intent, ReadHold? Hold) ReadIntentAndHold(string key) => Held.GetValueOrDefault(key);

        public void WriteIntent(string key, ReadIntent intent)
        {
            Held[key] = (intent, Held.GetValueOrDefault(key).Hold);
            Writes.Add(key + ":intent");
        }

        public void WriteHold(string key, ReadHold hold)
        {
            Held[key] = (Held.GetValueOrDefault(key).Intent, hold);
            Writes.Add(key + ":hold");
        }
    }

    private static (ProtocolRunnerState State, Checkpoints Checkpoints) Create()
    {
        var checkpoints = new Checkpoints();
        return (new ProtocolRunnerState(checkpoints, NullLogger.Instance), checkpoints);
    }

    [Fact]
    public void ThePosition_IsTheCheckpoint_AndAMappingWithNoneHasNoReadState()
    {
        var (state, checkpoints) = Create();
        Assert.Null(state.GetReadState("t", "main", "src/db/table"));
        Assert.Null(state.GetWatermark("t", "main", "src/db/table"));

        var at = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        state.SetWatermark("t", "main", "src/db/table", "42", at);
        state.SetReadIntent("t", "main", "src/db/table", ReadIntent.Changes);

        Assert.Equal(["src/db/table", "src/db/table:intent"], checkpoints.Writes);
        Assert.Equal(new MappingReadState(ReadIntent.Changes, ReadHold.None, "42", at), state.GetReadState("t", "main", "src/db/table"));
        Assert.Equal("42", state.GetWatermark("t", "main", "src/db/table"));
    }

    [Fact]
    public void AMappingsLock_IsHeldInMemory_OnePassAtATime()
    {
        var (state, _) = Create();
        var first = Guid.NewGuid();
        Assert.True(state.TryAcquireLock("t", RunKind.Primary, "main", first));
        Assert.False(state.TryAcquireLock("t", RunKind.Primary, "main", Guid.NewGuid()));
        Assert.True(state.TryAcquireLock("t", RunKind.Primary, "other", Guid.NewGuid()));

        state.ReleaseLock("t", RunKind.Primary, "main");

        Assert.True(state.TryAcquireLock("t", RunKind.Primary, "main", Guid.NewGuid()));
    }

    [Fact]
    public void APassOutcome_IsTakenOnce()
    {
        var (state, _) = Create();
        var run = Guid.NewGuid();
        state.CompleteRun(run, RunStatus.Failed, 10, 4, "the target said no", RunFailureKinds.MetadataNotCached);

        var outcome = state.TakeOutcome(run)!;

        Assert.Equal((RunStatus.Failed, 10L, 4L, "the target said no", RunFailureKinds.MetadataNotCached), (outcome.Status, outcome.RowsRead, outcome.RowsWritten, outcome.Summary, outcome.FailureKind));
        Assert.Null(state.TakeOutcome(run));
    }

    private static (ProtocolRunnerState State, Checkpoints Checkpoints, FakeDrainQueue Queue) CreateWithQueue()
    {
        var checkpoints = new Checkpoints();
        var queue = new FakeDrainQueue();
        return (new ProtocolRunnerState(checkpoints, NullLogger.Instance, queue), checkpoints, queue);
    }

    [Fact]
    public async Task AClaim_IsTheQueuesItem_AsSyncsOwn_AndItsMarksAreTheQueuesMarks()
    {
        var (state, _, queue) = CreateWithQueue();
        var shape = WorkQueueStore.Describe("sales", RunKind.ReconcileDeletes, "main", "id:1-9", "{\"segment\":1}", new WorkItemKinds("KeyReconcile", "StagingTable", "KeyReconcileDelete"), "{\"guard\":1}");
        await queue.EnqueueAsync(shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload, CancellationToken.None);
        Assert.True(state.HasOutstandingWork("sales", RunLane.ChangeProcessing));

        var item = state.TryClaimNext("sales", "worker", RunLane.BulkLoad)!;

        // the replication is the drain's, the kinds, the segment and the guard come from the payload the planner or the reconcile wrote
        Assert.Equal(("sales", RunKind.ReconcileDeletes, "main", "id:1-9", "{\"segment\":1}", "{\"guard\":1}"), (item.TaskName, item.RunKind, item.MappingName, item.SegmentLabel, item.SegmentJson, item.DeleteGuardJson));
        Assert.Equal(new WorkItemKinds("KeyReconcile", "StagingTable", "KeyReconcileDelete"), item.Kinds);
        Assert.Equal(queue.Rows.Single().RunId, item.RunId);
        Assert.Null(state.TryClaimNext("sales", "worker", RunLane.BulkLoad));

        state.MarkRunning(item.Id);
        Assert.Equal("Running", queue.Rows.Single().Status);
        state.MarkDone(item.Id);
        Assert.Equal("Done", queue.Rows.Single().Status);
        Assert.False(state.HasOutstandingWork("sales", RunLane.ChangeProcessing));
    }

    [Fact]
    public async Task AFailedPass_ClosesItsItemFailed_AndAReleasedClaimGoesBackToPending()
    {
        var (state, _, queue) = CreateWithQueue();
        await queue.EnqueueAsync("Primary", "one", "", "", "ChangeProcessing", null, CancellationToken.None);
        await queue.EnqueueAsync("Primary", "two", "", "", "ChangeProcessing", null, CancellationToken.None);
        var one = state.TryClaimNext("t", "w", RunLane.ChangeProcessing)!;
        var two = state.TryClaimNext("t", "w", RunLane.ChangeProcessing)!;

        state.MarkRunning(one.Id);
        state.MarkFailed(one.Id);
        state.ReleaseClaim(two.Id);

        Assert.Equal(["Failed", "Pending"], queue.Rows.Select(r => r.Status));
        Assert.True(state.WasReleased(two.Id));
        Assert.False(state.WasReleased(one.Id));
    }

    [Fact]
    public async Task ALostClaim_IsNotRun_AndSaysWhichItemItWas()
    {
        var (state, _, queue) = CreateWithQueue();
        await queue.EnqueueAsync("Primary", "main", "", "", "ChangeProcessing", null, CancellationToken.None);
        var item = state.TryClaimNext("t", "w", RunLane.ChangeProcessing)!;
        queue.Rows.Single().Status = "Pending";      // the lease ran out and the queue gave the item back

        var lost = Assert.Throws<WorkClaimLostException>(() => state.MarkRunning(item.Id));

        Assert.Contains("'main'", lost.Message, StringComparison.Ordinal);
        Assert.Contains($"{item.Id}", lost.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancel_OfAnItem_IsTheQueuesCancelMark()
    {
        var (state, _, queue) = CreateWithQueue();
        await queue.EnqueueAsync("Primary", "main", "", "", "ChangeProcessing", null, CancellationToken.None);
        var item = state.TryClaimNext("t", "w", RunLane.ChangeProcessing)!;
        Assert.False(state.IsCancelRequested(item.RunId));

        queue.Rows.Single().CancelRequested = true;

        Assert.True(state.IsCancelRequested(item.RunId));
        Assert.False(state.IsCancelRequested(Guid.NewGuid()));
    }

    [Fact]
    public void TheIntentAndTheHold_AreKeptBesideThePosition_AndAHoldAloneIsAReadState()
    {
        var (state, checkpoints) = Create();
        const string key = "src/db/table";
        Assert.Null(state.GetReadState("t", "main", key));

        state.SetReadHold("t", "main", key, ReadHold.PositionExpired);

        Assert.Equal(new MappingReadState(ReadIntent.Changes, ReadHold.PositionExpired, null, null), state.GetReadState("t", "main", key));
        state.SetWatermark("t", "main", key, "7", null);
        state.SetReadIntent("t", "main", key, ReadIntent.ChangesFromEarliest);
        Assert.Equal(new MappingReadState(ReadIntent.ChangesFromEarliest, ReadHold.PositionExpired, "7", null), state.GetReadState("t", "main", key));
        Assert.Equal(["src/db/table:hold", "src/db/table", "src/db/table:intent"], checkpoints.Writes);
    }

    [Fact]
    public void ASegmentAPassReads_IsALineOfTheRunsLog()
    {
        var (state, _) = Create();
        state.SetRunSegment(Guid.NewGuid(), "id:1-9");      // does not throw: cosmetic
    }

    public static TheoryData<string> Unsupported =>
    [
        nameof(IRunnerState.RequestInitialLoad),
        nameof(IRunnerState.RequestSegmentedLoad),
        nameof(IRunnerState.RecordVerificationResult),
        nameof(IRunnerState.TryClaimNext),
        nameof(IRunnerState.HasOutstandingWork),
    ];

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void WhatTheDrainDoesNotDo_ThrowsNotSupported_NamingTheMember(string member)
    {
        // a state made without the runner's queue (nothing here claims) and the members that are not served even with one
        var (state, _) = Create();
        Action call = member switch
        {
            nameof(IRunnerState.RequestInitialLoad) => () => state.RequestInitialLoad("t", "main", "src/db/table", "1", null),
            nameof(IRunnerState.RequestSegmentedLoad) => () => state.RequestSegmentedLoad("t", "main", "[]"),
            nameof(IRunnerState.RecordVerificationResult) => () => state.RecordVerificationResult(null!),
            nameof(IRunnerState.TryClaimNext) => () => state.TryClaimNext("t", "w", RunLane.ChangeProcessing),
            nameof(IRunnerState.HasOutstandingWork) => () => state.HasOutstandingWork("t", RunLane.ChangeProcessing),
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };

        var thrown = Assert.Throws<NotSupportedException>(call);

        Assert.Contains("IRunnerState", thrown.Message);
        if (member is not (nameof(IRunnerState.TryClaimNext) or nameof(IRunnerState.HasOutstandingWork)))
            Assert.Contains($"IRunnerState.{member}", thrown.Message);
    }

    [Fact]
    public void TheManifestOfTheEngineTask_DeclaresItsInputsAndTheCheckpoint()
    {
        var task = ManifestGenerator.TaskOf(typeof(DrainJob));

        Assert.Equal("drain", task.Name);
        Assert.Equal(["replication", "degree_of_parallelism", "repo_dir", "run_all"], task.Inputs.Select(i => i.Name));
        Assert.Equal([true, false, false, false], task.Inputs.Select(i => i.Required));
        // the position (the declared checkpoint of Sync's own state, part for part), the batches of initial loads and the rows each mapping read (plan 28c E11b)
        Assert.Equal(["batch", "reads", "watermark"], task.Progress.Checkpoints.Select(c => c.Name).Order());
        var checkpoint = task.Progress.Checkpoints.Single(c => c.Name == "watermark");
        Assert.Equal(WatermarkCheckpoint.Declaration.Parts.Keys.Order(), checkpoint.Parts.Keys.Order());
        Assert.Equal(WatermarkCheckpoint.Declaration.Parts.Select(p => (p.Key, p.Value.Type)).Order(), checkpoint.Parts.Select(p => (p.Key, p.Value.Type)).Order());
        Assert.Equal(["mapping"], task.Units!.Select(u => u.Name));
        Assert.Equal(["rows", "rows_read"], task.Counters!.Order());

        var manifest = RunnerRig.EngineManifest();
        Assert.Empty(PackageManifestValidator.Problems(manifest));
    }
}
