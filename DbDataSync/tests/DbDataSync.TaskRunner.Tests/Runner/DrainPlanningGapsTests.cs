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

/// <summary>Plan 28c E11b: what the planner of a drain on the runner did not have: reconcile sweeps with divided segments and the after-change strategy, the gate's history, and a pause that a drain which is up sees.</summary>
public sealed class DrainPlanningGapsTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-drainplan-").FullName;
    private readonly ConfigRepository _config;
    private readonly DriverRegistry _drivers = new();

    public DrainPlanningGapsTests()
    {
        Repository.Init(_repoRoot);
        _config = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _drivers.Register(new MsSqlDriver());
        _config.SaveConnection(new ConnectionInput { Name = "src", DriverType = DriverIds.MsSql, Host = "localhost", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "pw" }, Author);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private ReplicationTaskConfig Save(string name, SchedulingConfig scheduling, ReconcileConfig? reconcile, params (string Name, BatchReloadSegment[] Segmenting)[] mappings)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = "src", Database = "App" }, Target = new EndpointRef { ConnectionName = "src", Database = "DW" } },
            Scheduling = scheduling,
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = new Dictionary<string, string> { ["watermarkColumn"] = "UpdatedAt" } },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
            Reconcile = reconcile ?? new ReconcileConfig(),
        }, Author);
        foreach (var (mapping, segmenting) in mappings)
            _config.SaveTableMapping(name, new TableMappingConfig
            {
                Name = mapping,
                Sources = [new SourceTableSpec { Schema = "dbo", Table = mapping }],
                Targets = [new TableSpec { Schema = "dbo", Table = mapping }],
                SourceColumns = [new CachedColumn("Id", "int", false, true, false)],
                TargetColumns = [new CachedColumn("Id", "int", false, true, false)],
                ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
                DefaultSegmenting = [.. segmenting],
            }, Author);
        return _config.LoadReplicationTask(name);
    }

    private sealed class Divides : IDrainSegments
    {
        public string? ReaderKindAsked { get; private set; }

        public Func<IReadOnlyList<BatchReloadSegment>, IReadOnlyList<BatchReloadSegment>> Expand { get; set; } = s => s;

        public Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested, string? readerKind, CancellationToken cancellationToken)
        {
            ReaderKindAsked = readerKind;
            return Task.FromResult(Expand(requested));
        }

        public Task<(long? Rows, string? Caveat)> EstimateAsync(ReplicationTaskConfig task, TableMappingConfig mapping, CancellationToken cancellationToken) => Task.FromResult<(long?, string?)>((null, null));
    }

    private sealed class NoCounters : IChangeCounterSource
    {
        public Task<ChangeCounterReading> FetchAsync(string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken) => Task.FromResult(new ChangeCounterReading("v1"));

        public Task<DateTimeOffset?> MapSourceTimeAsync(string connectionName, string sourceDatabase, string readerKind, string value, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    }

    private (ReplicationPlanner Planner, ProtocolPlannerState State, FakeDrainQueue Queue, FakeLedger Ledger, Divides Divides) Create(bool withLedger = true)
    {
        var queue = new FakeDrainQueue();
        var ledger = new FakeLedger();
        var divides = new Divides();
        var state = new ProtocolPlannerState(queue, ledger, withLedger ? ledger : null);
        var gate = new ChangePollingGate(new ChangeSourceResolver(_config, _drivers), new NoCounters(), state, NullLogger.Instance);
        return (new ReplicationPlanner(_config, state, gate, _drivers, new EngineReconcileEnqueuer(_config, state, divides), NullLogger.Instance), state, queue, ledger, divides);
    }

    private static readonly SchedulingConfig NeverDue = new() { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 };

    [Fact]
    public async Task AReconcileSweep_WithAnAutoSegment_IsDividedByTheDrain_OneItemPerBucket_WithTheReconcileReader()
    {
        var reconcile = new ReconcileConfig { Enabled = true, Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 } };
        var task = Save("sweeps", NeverDue, reconcile, ("orders", [new AutoSegment("Id", 3)]));
        var (planner, state, queue, _, divides) = Create();
        divides.Expand = _ => [new RangeSegment("Id", "1", "10"), new RangeSegment("Id", "10", "20"), new RangeSegment("Id", "20", "30")];
        await state.RefreshAsync(CancellationToken.None);

        Assert.True(await planner.TickReconcileAsync("sweeps", task, ["orders"], CancellationToken.None));

        Assert.Equal("KeyReconcile", divides.ReaderKindAsked);
        var items = queue.Rows.Where(r => r.Kind == nameof(RunKind.ReconcileDeletes)).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(3, items.Select(r => r.Part).Distinct().Count());
        Assert.True(state.HasPendingReconcile("sweeps", "orders"));
    }

    [Fact]
    public async Task AReconcileSweep_WithACustomSegment_IsDividedToo_AndAStrategyThatFailsIsRefusedNamingWhy()
    {
        var reconcile = new ReconcileConfig { Enabled = true, Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 } };
        var task = Save("custom", NeverDue, reconcile, ("orders", [new CustomSegment("lastMonths")]));
        var (planner, state, queue, _, divides) = Create();
        divides.Expand = _ => throw new InvalidOperationException("strategy 'lastMonths' is not defined");
        await state.RefreshAsync(CancellationToken.None);

        Assert.False(await planner.TickReconcileAsync("custom", task, ["orders"], CancellationToken.None));
        Assert.Empty(queue.Rows);

        divides.Expand = _ => [new ListSegment("Month", ["2026-10"])];
        Assert.True(await planner.TickReconcileAsync("custom", task, ["orders"], CancellationToken.None));
        Assert.Single(queue.Rows);
    }

    [Fact]
    public async Task AnAfterChangeSweep_FiresOnTheRowsRead_AndTheCountRestartsWhenTheSweepIsQueued()
    {
        var reconcile = new ReconcileConfig
        {
            Enabled = true,
            Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1 },
            AfterChange = new AfterAnyChangeStrategy(),
        };
        var task = Save("afterchange", NeverDue, reconcile, ("orders", []));
        var (planner, state, queue, ledger, _) = Create();
        await state.RefreshAsync(CancellationToken.None);

        // nothing read: the cadence alone does not sweep
        Assert.False(await planner.TickReconcileAsync("afterchange", task, ["orders"], CancellationToken.None));

        // a pass that read rows is what the strategy reacts to; a failed pass or a bulk load does not count
        state.NoteRowsRead("orders", 250);
        Assert.Equal(250, ledger.RowsReadSinceSweep("orders"));
        Assert.True(await planner.TickReconcileAsync("afterchange", task, ["orders"], CancellationToken.None));
        Assert.Single(queue.Rows, r => r.Kind == nameof(RunKind.ReconcileDeletes));
        Assert.Equal(0, ledger.RowsReadSinceSweep("orders"));    // accounted for by the sweep just queued

        // the sweep ends; no new rows, so no new sweep
        foreach (var row in queue.Rows) row.Status = "Done";
        await state.RefreshAsync(CancellationToken.None);
        await Task.Delay(1100);
        Assert.False(await planner.TickReconcileAsync("afterchange", task, ["orders"], CancellationToken.None));
    }

    [Fact]
    public void TheCountOfRowsRead_NeedsALedger_AndSaysSoWithoutOne()
    {
        var (_, state, _, _, _) = Create(withLedger: false);

        var refused = Assert.Throws<NotSupportedException>(() => state.GetRowsReadSincePerMapping("r", new Dictionary<string, DateTimeOffset> { ["a"] = default }));

        Assert.Contains("GetRowsReadSincePerMapping", refused.Message);
    }

    [Fact]
    public void TheGatesChecks_AreKeptWhenTheValueChangedOrAMinutePassed_AndTakenOnce()
    {
        var (_, state, _, _, _) = Create();

        state.RecordCheck("src", "App", "ChangeTracking", "10", null);
        state.RecordCheck("src", "App", "ChangeTracking", "10", null);        // the same value a moment later: not worth a second line
        state.RecordCheck("src", "App", "ChangeTracking", "11", DateTimeOffset.UtcNow);
        state.RecordCheck("src", "Other", "ChangeTracking", "10", null);       // another group is its own

        var taken = state.TakeChecks();

        Assert.Equal(["App:10", "App:11", "Other:10"], taken.Select(c => $"{c.Database}:{c.Value}"));
        Assert.Empty(state.TakeChecks());
    }

    private sealed class Outputs : IDrainOutputs
    {
        public List<(string Replication, int Count)> Checks { get; } = [];

        public void VerificationResult(VerificationResultRecord result)
        {
        }

        public void ChangeChecks(string replication, IReadOnlyList<ChangeCheckEntry> checks) => Checks.Add((replication, checks.Count));
    }

    private sealed class Counting(string value) : IChangeCounterSource
    {
        public Task<ChangeCounterReading> FetchAsync(string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken) => Task.FromResult(new ChangeCounterReading(value));

        public Task<DateTimeOffset?> MapSourceTimeAsync(string connectionName, string sourceDatabase, string readerKind, string value, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    }

    private (DrainLoop Loop, FakeDrainQueue Queue, ProtocolRunnerState State, Outputs Outputs, ProtocolPlannerState Planner) CreateLoop(string replication, bool runAll = false)
    {
        var queue = new FakeDrainQueue();
        var ledger = new FakeLedger();
        var outputs = new Outputs();
        var plannerState = new ProtocolPlannerState(queue, ledger, ledger);
        var state = new ProtocolRunnerState(ledger, NullLogger.Instance, queue);
        var gate = new ChangePollingGate(new ChangeSourceResolver(_config, _drivers), new Counting("v1"), plannerState, NullLogger.Instance);
        var planner = new ReplicationPlanner(_config, plannerState, gate, _drivers, new EngineReconcileEnqueuer(_config, plannerState), NullLogger.Instance);
        var loop = new DrainLoop(replication, _config, planner, plannerState, state, queue, runAll, 1, NullLogger.Instance,
            cycleInterval: TimeSpan.FromMilliseconds(50), sweepInterval: TimeSpan.FromMilliseconds(10), outputs: outputs,
            requestItem: item => DrainRequests.ApplyItemAsync(item, replication, queue, ledger, ledger, NullLogger.Instance, CancellationToken.None));
        return (loop, queue, state, outputs, plannerState);
    }

    private static Func<IAsyncEnumerable<PlannedPass>, int, CancellationToken, Task> RunsAndFinishes(DrainLoop loop, ProtocolRunnerState state, List<string> ran) =>
        async (items, _, _) =>
        {
            await foreach (var pass in items)
            {
                try
                {
                    ran.Add($"{pass.Item.RunKind}:{pass.Mapping}");
                    state.MarkRunning(pass.Item.Id);
                    state.CompleteRun(pass.Item.RunId, RunStatus.Succeeded, 0, 0, null);
                    loop.NotePass(pass.Item, state.TakeOutcome(pass.Item.RunId)!);
                    state.MarkDone(pass.Item.Id);
                }
                finally
                {
                    loop.PassFinished(pass);
                }
            }
        };

    [Fact]
    public async Task ADrainThatIsUp_WhenTheRunnerPausesTheTask_PlansNothingNew_ButRunsWhatIsQueued()
    {
        Save("paused", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "* * * * *" }, null, ("orders", []));
        var (loop, queue, state, _, _) = CreateLoop("paused");
        queue.Paused = true;
        // something an operator queued before the pause
        var shape = WorkQueueStore.Describe("paused", RunKind.Primary, "orders");
        await queue.EnqueueAsync(shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload, CancellationToken.None);
        var ran = new List<string>();

        await loop.RunAsync(RunsAndFinishes(loop, state, ran), CancellationToken.None);

        Assert.Equal(["Primary:orders"], ran);                  // what was queued still ran
        Assert.Single(queue.Rows);                              // and nothing was planned behind it
    }

    [Fact]
    public async Task ARequestThatCameWhileTheDrainWasUp_IsAppliedInItsSweep_AndNeverRunAsAPass()
    {
        Save("req", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, null, ("orders", []));
        var (loop, queue, state, _, _) = CreateLoop("req");
        await queue.EnqueueAsync(DrainRequestText.ItemKind, "orders", "r1", "", "", DrainRequestText.SerializeOne(new DrainRequest(nameof(RunKind.Verification), "orders", Id: "r1")), CancellationToken.None);
        var ran = new List<string>();

        await loop.RunAsync(RunsAndFinishes(loop, state, ran), CancellationToken.None);

        Assert.Contains("Verification:orders", ran);
        Assert.DoesNotContain(ran, r => r.StartsWith(DrainRequestText.ItemKind));
        Assert.Equal("Done", queue.Rows.Single(r => r.Kind == DrainRequestText.ItemKind).Status);
    }

    [Fact]
    public async Task AContinuousDrain_StaysUpUntilThePause_ThenEndsOnceItsQueueIsEmpty()
    {
        Save("stays", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 3600 }, null, ("orders", []));
        var (loop, queue, state, _, _) = CreateLoop("stays");
        var ran = new List<string>();
        var run = loop.RunAsync(RunsAndFinishes(loop, state, ran), CancellationToken.None);

        await Task.Delay(700);
        Assert.False(run.IsCompleted, "a continuous drain stays up between passes");

        queue.Paused = true;
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(run, finished);
        await run;
    }

    [Fact]
    public async Task ADrainThatIsNotPaused_PlansTheDueMapping_AndRecordsTheGatesCheck()
    {
        Save("live", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1 }, null, ("orders", []));
        var (loop, queue, state, outputs, _) = CreateLoop("live");
        var ran = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        try { await loop.RunAsync(RunsAndFinishes(loop, state, ran), stop.Token); }
        catch (OperationCanceledException) { /* the test ends the drain */ }

        Assert.Contains("Primary:orders", ran);
        Assert.NotEmpty(outputs.Checks);
        Assert.All(outputs.Checks, c => Assert.Equal("live", c.Replication));
        Assert.True(queue.Rows.Count >= 1);
    }

    [Fact]
    public async Task RunNow_StillQueuesItsPasses_WhileTheTaskIsPaused()
    {
        Save("manual", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, null, ("orders", []), ("customers", []));
        var (loop, queue, state, _, _) = CreateLoop("manual", runAll: true);
        queue.Paused = true;
        var ran = new List<string>();

        await loop.RunAsync(RunsAndFinishes(loop, state, ran), CancellationToken.None);

        Assert.Equal(["Primary:customers", "Primary:orders"], ran.Order());
    }
}
