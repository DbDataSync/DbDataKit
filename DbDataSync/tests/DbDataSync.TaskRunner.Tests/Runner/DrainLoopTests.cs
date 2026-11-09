using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Planning;
using DbDataSync.State;
using DbDataSync.TaskRunner.Runner;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// Plan 28c E11: the engine's loop on a runner, over the runner's queue (an in-memory queue with the same rules, <see cref="FakeDrainQueue"/>): the planner at the start of each cycle, a claim only when a slot is free, an
/// item run as a unit, the idle timeout kept for a continuous replication, a periodic drain that ends when its queue stays empty, and what a stop gives back.
/// </summary>
public sealed class DrainLoopTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-drainloop-").FullName;
    private readonly ConfigRepository _config;
    private readonly DriverRegistry _drivers = new();

    public DrainLoopTests()
    {
        Repository.Init(_repoRoot);
        _config = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _drivers.Register(new MsSqlDriver());
        _config.SaveConnection(new ConnectionInput
        {
            Name = "src", DriverType = DriverIds.MsSql, Host = "localhost", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "pw",
        }, Author);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private sealed class Checkpoints : IDrainCheckpoints
    {
        public (string Position, DateTimeOffset? Time)? Read(string key) => null;

        public void Write(string key, string position, DateTimeOffset? time)
        {
        }

        public (ReadIntent? Intent, ReadHold? Hold) ReadIntentAndHold(string key) => default;

        public void WriteIntent(string key, ReadIntent intent)
        {
        }

        public void WriteHold(string key, ReadHold hold)
        {
        }
    }

    private void Save(string name, SchedulingConfig scheduling, params string[] mappings)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = "src", Database = "App" },
                Target = new EndpointRef { ConnectionName = "src", Database = "DW" },
            },
            Scheduling = scheduling,
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = new Dictionary<string, string> { ["watermarkColumn"] = "UpdatedAt" } },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, Author);
        foreach (var mapping in mappings)
            _config.SaveTableMapping(name, new TableMappingConfig
            {
                Name = mapping,
                Sources = [new SourceTableSpec { Schema = "dbo", Table = mapping }],
                Targets = [new TableSpec { Schema = "dbo", Table = mapping }],
                SourceColumns = [new CachedColumn("Id", "int", false, true, false)],
                TargetColumns = [new CachedColumn("Id", "int", false, true, false)],
                ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
            }, Author);
    }

    private sealed class Rig
    {
        public required FakeDrainQueue Queue { get; init; }
        public required DrainLoop Loop { get; init; }
        public required ProtocolRunnerState State { get; init; }
        public List<string> Ran { get; } = [];
    }

    private Rig Create(string replication, bool runAll = false, int parallelism = 1, IReplicationPlanner? planner = null, FakeDrainQueue? queue = null)
    {
        queue ??= new FakeDrainQueue();
        var checkpoints = new Checkpoints();
        var plannerState = new ProtocolPlannerState(queue, checkpoints);
        var state = new ProtocolRunnerState(checkpoints, NullLogger.Instance, queue);
        var gate = new ChangePollingGate(new ChangeSourceResolver(_config, _drivers), new NoCounters(), plannerState, NullLogger.Instance);
        planner ??= new ReplicationPlanner(_config, plannerState, gate, _drivers, new EngineReconcileEnqueuer(_config, plannerState), NullLogger.Instance);
        var loop = new DrainLoop(replication, _config, planner, plannerState, state, queue, runAll, parallelism, NullLogger.Instance,
            cycleInterval: TimeSpan.FromMilliseconds(100), sweepInterval: TimeSpan.FromMilliseconds(20));
        return new Rig { Queue = queue, Loop = loop, State = state };
    }

    private sealed class NoCounters : IChangeCounterSource
    {
        public Task<ChangeCounterReading> FetchAsync(string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken) => Task.FromResult(new ChangeCounterReading(null));

        public Task<DateTimeOffset?> MapSourceTimeAsync(string connectionName, string sourceDatabase, string readerKind, string value, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    }

    /// <summary>What the kit does with the claimed items, and what the executor does with each: the item runs (it is marked running and, here, done) and says how it went.</summary>
    private static Func<IAsyncEnumerable<PlannedPass>, int, CancellationToken, Task> Runs(Rig rig, long rowsRead = 0, Func<PlannedPass, bool>? fail = null) =>
        async (items, _, _) =>
        {
            await foreach (var pass in items)
            {
                try
                {
                    rig.Ran.Add($"{pass.Item.RunKind}:{pass.Mapping}");
                    rig.State.MarkRunning(pass.Item.Id);
                    var failed = fail?.Invoke(pass) ?? false;
                    rig.State.CompleteRun(pass.Item.RunId, failed ? RunStatus.Failed : RunStatus.Succeeded, rowsRead, rowsRead, null);
                    var outcome = rig.State.TakeOutcome(pass.Item.RunId)!;
                    rig.Loop.NotePass(pass.Item, outcome);
                    if (failed) rig.State.MarkFailed(pass.Item.Id);
                    else rig.State.MarkDone(pass.Item.Id);
                }
                finally
                {
                    rig.Loop.PassFinished(pass);
                }
            }
        };

    [Fact]
    public async Task APeriodicDrain_PlansOnce_RunsEveryMappingOnce_AndEndsWhenItsQueueIsEmpty()
    {
        Save("periodic", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a", "b", "c");
        var rig = Create("periodic");

        await rig.Loop.RunAsync(Runs(rig), CancellationToken.None);

        Assert.Equal(["Primary:a", "Primary:b", "Primary:c"], rig.Ran);
        Assert.All(rig.Queue.Rows, r => Assert.Equal(("Primary", "Done", "ChangeProcessing", ""), (r.Kind, r.Status, r.Lane, r.Part)));
        Assert.Equal(1, rig.Loop.Cycles);
    }

    [Fact]
    public async Task APeriodicDrain_QueuesTheSameRowsTheAPIsSchedulerWould_ThePayloadAndLaneIncluded()
    {
        Save("rows", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a");
        var rig = Create("rows");

        await rig.Loop.RunAsync(Runs(rig), CancellationToken.None);

        var shape = WorkQueueStore.Describe("rows", RunKind.Primary, "a");
        var row = Assert.Single(rig.Queue.Rows);
        Assert.Equal((shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload), (row.Kind, row.Subject, row.Part, row.Resource, row.Lane, row.Payload));
    }

    [Fact]
    public async Task APeriodicDrain_StartedAgainBeforeTheNextOccurrence_PlansNothing_UnlessItIsToRunAll()
    {
        Save("again", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a", "b");
        var queue = new FakeDrainQueue();
        var first = Create("again", queue: queue);
        await first.Loop.RunAsync(Runs(first), CancellationToken.None);
        Assert.Equal(2, queue.Rows.Count);

        var second = Create("again", queue: queue);
        await second.Loop.RunAsync(Runs(second), CancellationToken.None);
        Assert.Empty(second.Ran);            // the cron's next occurrence is months away: nothing is due, as on the API's scheduler

        var runAll = Create("again", runAll: true, queue: queue);
        await runAll.Loop.RunAsync(Runs(runAll), CancellationToken.None);
        Assert.Equal(["Primary:a", "Primary:b"], runAll.Ran);      // "Run now" does not ask the schedule
        Assert.Equal(4, queue.Rows.Count);
    }

    [Fact]
    public async Task AContinuousDrain_StaysUpBetweenPasses_AndEndsAfterTheIdleTimeoutWithoutAPassReadingRows()
    {
        Save("live", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2 }, "a");
        var rig = Create("live");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await rig.Loop.RunAsync(Runs(rig, rowsRead: 0), CancellationToken.None);

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2), $"the drain ended after {watch.Elapsed}, before the idle timeout of two seconds");
        Assert.True(rig.Loop.Cycles > 1, "a continuous drain stays up and plans again");
        Assert.NotEmpty(rig.Ran);
    }

    [Fact]
    public async Task AContinuousDrain_IsNotIdleWhileItsPassesReadRows()
    {
        Save("busy", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2 }, "a");
        var rig = Create("busy");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lastProductive = TimeSpan.Zero;

        // rows for the first 2.5 seconds, then quiet: the drain ends one idle timeout after the last pass that read something, not after the first
        await rig.Loop.RunAsync(async (items, _, _) =>
        {
            await foreach (var pass in items)
            {
                var rows = watch.Elapsed < TimeSpan.FromSeconds(2.5) ? 5 : 0;
                if (rows > 0) lastProductive = watch.Elapsed;
                rig.State.MarkRunning(pass.Item.Id);
                rig.State.CompleteRun(pass.Item.RunId, RunStatus.Succeeded, rows, rows, null);
                rig.Loop.NotePass(pass.Item, rig.State.TakeOutcome(pass.Item.RunId)!);
                rig.State.MarkDone(pass.Item.Id);
                rig.Loop.PassFinished(pass);
            }
        }, CancellationToken.None);

        Assert.True(lastProductive >= TimeSpan.FromSeconds(1.5), $"the passes read rows until {lastProductive}");
        Assert.True(watch.Elapsed >= lastProductive + TimeSpan.FromSeconds(2), $"the drain ended at {watch.Elapsed}, less than the idle timeout after the last pass that read rows ({lastProductive})");
    }

    [Fact]
    public async Task ARunAllDrain_QueuesEveryMapping_WhateverThePlannerSays()
    {
        Save("all", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a", "b");
        var rig = Create("all", runAll: true, planner: new NothingIsDue());

        await rig.Loop.RunAsync(Runs(rig), CancellationToken.None);

        Assert.Equal(["Primary:a", "Primary:b"], rig.Ran);
    }

    private sealed class NothingIsDue : IReplicationPlanner
    {
        public Task<bool> TickContinuousAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TickPeriodicAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TickReconcileAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> PlanAsync(string replication, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    [Fact]
    public async Task AFailedPass_ClosesItsItemFailed_AndTheDrainGoesOnWithTheOthers()
    {
        Save("fails", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a", "b");
        var rig = Create("fails");

        await rig.Loop.RunAsync(Runs(rig, fail: p => p.Mapping == "a"), CancellationToken.None);

        Assert.Equal(["Failed", "Done"], rig.Queue.Rows.OrderBy(r => r.Subject).Select(r => r.Status));
    }

    [Fact]
    public async Task ARunTheRunnerToldToStop_ClaimsNothingMore_AndEndsWithItsItemsStillPending()
    {
        Save("stopped", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a", "b");
        var rig = Create("stopped");
        rig.Queue.Stopped = true;      // the protocol refuses a claim to a run that was told to stop

        await rig.Loop.RunAsync(Runs(rig), CancellationToken.None);

        Assert.Empty(rig.Ran);
        Assert.All(rig.Queue.Rows, r => Assert.Equal("Pending", r.Status));
        Assert.Equal(2, rig.Queue.Rows.Count);      // the planner still queued them: they are the replication's work, and the next drain runs them
    }

    [Fact]
    public async Task ACancel_StopsAtTheBoundaryOfAnItem_LetsTheOneInFlightFinish_AndGivesBackWhatWasClaimedAndNotRun()
    {
        Save("cancelled", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a", "b", "c", "d");
        var rig = Create("cancelled", parallelism: 3);
        using var cts = new CancellationTokenSource();

        // three slots: three items are claimed at once; the first runs to its end, and the runner's stop arrives before the other two begin
        await rig.Loop.RunAsync(async (items, _, ct) =>
        {
            var claimed = new List<PlannedPass>();
            await foreach (var pass in items)
            {
                claimed.Add(pass);
                if (claimed.Count == 3) break;      // all three slots are taken: a fourth claim would wait for one to be free
            }

            Assert.Equal(3, claimed.Count);
            rig.Ran.Add($"{claimed[0].Item.RunKind}:{claimed[0].Mapping}");
            rig.State.MarkRunning(claimed[0].Item.Id);
            rig.State.CompleteRun(claimed[0].Item.RunId, RunStatus.Succeeded, 1, 1, null);
            rig.State.MarkDone(claimed[0].Item.Id);
            rig.Loop.PassFinished(claimed[0]);
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        await rig.Loop.ReleaseUnfinishedAsync();

        Assert.Equal(["Primary:a"], rig.Ran);
        Assert.Equal(["Done", "Pending", "Pending", "Pending"], rig.Queue.Rows.OrderBy(r => r.Subject).Select(r => r.Status));
        Assert.DoesNotContain(rig.Queue.Rows, r => r.Status is "Claimed" or "Running");
    }

    [Fact]
    public async Task APassThatLostItsMappingsLock_GivesTheItemBack_AndTheNextSweepClaimsItAgain()
    {
        Save("locked", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a");
        var rig = Create("locked");
        var attempts = 0;

        await rig.Loop.RunAsync(async (items, _, _) =>
        {
            await foreach (var pass in items)
            {
                try
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        rig.State.ReleaseClaim(pass.Item.Id);      // what the executor does when another pass of the mapping holds the lock
                        continue;
                    }

                    rig.State.MarkRunning(pass.Item.Id);
                    rig.State.MarkDone(pass.Item.Id);
                }
                finally
                {
                    rig.Loop.PassFinished(pass);
                }
            }
        }, CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal("Done", Assert.Single(rig.Queue.Rows).Status);
    }

    [Fact]
    public async Task ADisabledReplication_QueuesNothing()
    {
        Save("off", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, "a");
        var task = _config.LoadReplicationTask("off");
        task.Enabled = false;
        _config.SaveReplicationTask(task, Author);
        var rig = Create("off");

        await rig.Loop.RunAsync(Runs(rig), CancellationToken.None);

        Assert.Empty(rig.Queue.Rows);
        Assert.Empty(rig.Ran);
    }
}
