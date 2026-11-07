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

        public List<string> Writes { get; } = [];

        public (string Position, DateTimeOffset? Time)? Read(string key) => Stored.TryGetValue(key, out var value) ? value : null;

        public void Write(string key, string position, DateTimeOffset? time)
        {
            Stored[key] = (position, time);
            Writes.Add(key);
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

        Assert.Equal(["src/db/table"], checkpoints.Writes);
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

    [Fact]
    public void ACancel_IsTheRunsToken_SoThereIsNothingToPoll()
    {
        var (state, _) = Create();
        Assert.False(state.IsCancelRequested(Guid.NewGuid()));
    }

    public static TheoryData<string> Unsupported =>
    [
        nameof(IRunnerState.HasOutstandingWork),
        nameof(IRunnerState.TryClaimNext),
        nameof(IRunnerState.SetRunSegment),
        nameof(IRunnerState.RequestInitialLoad),
        nameof(IRunnerState.RequestSegmentedLoad),
        nameof(IRunnerState.SetReadHold),
        nameof(IRunnerState.RecordVerificationResult),
        nameof(IRunnerState.SetReadIntent),
    ];

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void WhatTheDrainDoesNotDo_ThrowsNotSupported_NamingTheMember(string member)
    {
        var (state, _) = Create();
        Action call = member switch
        {
            nameof(IRunnerState.HasOutstandingWork) => () => state.HasOutstandingWork("t", RunLane.ChangeProcessing),
            nameof(IRunnerState.TryClaimNext) => () => state.TryClaimNext("t", "w", RunLane.ChangeProcessing),
            nameof(IRunnerState.SetRunSegment) => () => state.SetRunSegment(Guid.NewGuid(), "label"),
            nameof(IRunnerState.RequestInitialLoad) => () => state.RequestInitialLoad("t", "main", "src/db/table", "1", null),
            nameof(IRunnerState.RequestSegmentedLoad) => () => state.RequestSegmentedLoad("t", "main", "[]"),
            nameof(IRunnerState.SetReadHold) => () => state.SetReadHold("t", "main", "src/db/table", ReadHold.PositionExpired),
            nameof(IRunnerState.RecordVerificationResult) => () => state.RecordVerificationResult(null!),
            nameof(IRunnerState.SetReadIntent) => () => state.SetReadIntent("t", "main", "src/db/table", ReadIntent.InitialLoad),
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };

        var thrown = Assert.Throws<NotSupportedException>(call);

        Assert.Contains($"IRunnerState.{member}", thrown.Message);
    }

    [Fact]
    public void TheManifestOfTheEngineTask_DeclaresItsInputsAndTheCheckpoint()
    {
        var task = ManifestGenerator.TaskOf(typeof(DrainJob));

        Assert.Equal("drain", task.Name);
        Assert.Equal(["replication", "degree_of_parallelism", "repo_dir"], task.Inputs.Select(i => i.Name));
        Assert.Equal([true, false, false], task.Inputs.Select(i => i.Required));
        var checkpoint = Assert.Single(task.Progress.Checkpoints);
        Assert.Equal("watermark", checkpoint.Name);
        Assert.Equal(["position", "position_time"], checkpoint.Parts.Keys.Order());
        Assert.Equal(["mapping"], task.Units!.Select(u => u.Name));
        Assert.Equal(["rows", "rows_read"], task.Counters!.Order());

        var manifest = RunnerRig.EngineManifest();
        Assert.Empty(PackageManifestValidator.Problems(manifest));
    }
}
