using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Planning;
using DbDataSync.State;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// Plan 28c E11: the planner as a library, called with a state that is not Sync's own state database (what the engine's loop does). The API's own tests of its scheduler and gate (<c>SchedulerService*Tests</c>,
/// <c>ChangePollingGateTests</c>) run the same code over Sync's stores and are unchanged; these say that what the planner decides does not depend on where its state is.
/// </summary>
public sealed class ReplicationPlannerTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-planner-").FullName;
    private readonly ConfigRepository _config;
    private readonly DriverRegistry _drivers = new();

    public ReplicationPlannerTests()
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

    private sealed class FakeState : IPlannerState, IChangeGateState
    {
        public Dictionary<string, DateTimeOffset> Primary { get; } = [];
        public Dictionary<string, DateTimeOffset> Reconcile { get; } = [];
        public HashSet<string> ReconcileInFlight { get; } = [];
        public Dictionary<string, MappingReadState> ReadStates { get; } = [];
        public Dictionary<string, string> Watermarks { get; } = [];
        public List<string> Queued { get; } = [];
        public List<string> Checks { get; } = [];
        public bool RowsReadAsked { get; private set; }
        public Dictionary<string, long> RowsRead { get; } = [];

        public IReadOnlyDictionary<string, DateTimeOffset> GetLastPrimaryEnqueueByMapping(string replication) => Primary;

        public IReadOnlyDictionary<string, DateTimeOffset> GetLastReconcileEnqueueByMapping(string replication) => Reconcile;

        public IReadOnlyDictionary<string, long> GetRowsReadSincePerMapping(string replication, IReadOnlyDictionary<string, DateTimeOffset> sinceByMapping)
        {
            RowsReadAsked = true;
            return RowsRead;
        }

        public bool HasPendingReconcile(string replication, string mapping) => ReconcileInFlight.Contains(mapping);

        public MappingReadState? GetReadState(string replication, string mapping, string watermarkKey) => ReadStates.GetValueOrDefault(mapping);

        public void EnqueuePrimary(string replication, string mapping)
        {
            Queued.Add(mapping);
            Primary[mapping] = DateTimeOffset.UtcNow;
        }

        public string? GetWatermark(string taskName, string mappingName, string watermarkKey) => Watermarks.GetValueOrDefault(mappingName);

        public void RecordCheck(string connection, string database, string readerKind, string? value, DateTimeOffset? sourceTimeUtc) => Checks.Add($"{readerKind}={value}");
    }

    private sealed class FakeCounters(string? value) : IChangeCounterSource
    {
        public int Fetches { get; private set; }

        public Task<ChangeCounterReading> FetchAsync(string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(new ChangeCounterReading(value));
        }

        public Task<DateTimeOffset?> MapSourceTimeAsync(string connectionName, string sourceDatabase, string readerKind, string value, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class RecordingReconcile : IScheduledReconcileEnqueuer
    {
        public List<string> Asked { get; } = [];

        public ScheduledReconcileResult Answer { get; set; } = ScheduledReconcileResult.Queued;

        public Task<ScheduledReconcileResult> EnqueueScheduledAsync(string replication, string mapping, CancellationToken cancellationToken)
        {
            Asked.Add(mapping);
            return Task.FromResult(Answer);
        }
    }

    private ReplicationTaskConfig Save(string name, SchedulingConfig scheduling, string readerKind, ReconcileConfig? reconcile = null, params string[] mappings)
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
                Reader = new ReaderConfig { Kind = readerKind, Options = readerKind == GenericDriverKinds.Watermark ? new Dictionary<string, string> { ["watermarkColumn"] = "UpdatedAt" } : [] },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
            Reconcile = reconcile ?? new ReconcileConfig(),
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
        return _config.LoadReplicationTask(name);
    }

    private ReplicationPlanner Planner(FakeState state, IChangeCounterSource? counters = null, IScheduledReconcileEnqueuer? reconcile = null) =>
        new(_config, state, new ChangePollingGate(new ChangeSourceResolver(_config, _drivers), counters ?? new FakeCounters(null), state, NullLogger.Instance), _drivers,
            reconcile ?? new RecordingReconcile(), NullLogger.Instance);

    private static IReadOnlyList<string> Names(params string[] names) => names;

    [Fact]
    public async Task APeriodicReplication_QueuesEveryMappingTheFirstTimeAndNothingUntilTheNextOccurrence()
    {
        var task = Save("periodic", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, GenericDriverKinds.Watermark, null, "a", "b");
        var state = new FakeState();
        var planner = Planner(state);

        Assert.True(await planner.PlanAsync("periodic", task, Names("a", "b"), CancellationToken.None));
        Assert.Equal(["a", "b"], state.Queued);

        // queued a moment ago: the next occurrence of the cron is months away, so the replication is not due
        state.Queued.Clear();
        Assert.False(await planner.PlanAsync("periodic", task, Names("a", "b"), CancellationToken.None));
        Assert.Empty(state.Queued);
    }

    [Fact]
    public async Task APeriodicReplication_IsDueFromTheNewestOfItsMappings_NotFromEachOne()
    {
        var task = Save("periodic2", new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 1 1 *" }, GenericDriverKinds.Watermark, null, "a", "b");
        var state = new FakeState();
        state.Primary["a"] = DateTimeOffset.UtcNow.AddDays(-400);      // one mapping was last queued long ago
        state.Primary["b"] = DateTimeOffset.UtcNow;                     // another just now: that is the replication's last queue time

        Assert.False(await Planner(state).TickPeriodicAsync("periodic2", task, Names("a", "b"), CancellationToken.None));
        Assert.Empty(state.Queued);
    }

    [Fact]
    public async Task AContinuousReplication_IsDuePerMapping_ByItsOwnLastQueueTime()
    {
        var task = Save("continuous", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 }, GenericDriverKinds.Watermark, null, "a", "b", "c");
        var state = new FakeState();
        state.Primary["a"] = DateTimeOffset.UtcNow.AddMinutes(-5);      // overdue
        state.Primary["b"] = DateTimeOffset.UtcNow.AddSeconds(-5);      // queued a moment ago
        // c was never queued: due at once

        Assert.True(await Planner(state).TickContinuousAsync("continuous", task, Names("a", "b", "c"), CancellationToken.None));

        Assert.Equal(["a", "c"], state.Queued);
    }

    [Fact]
    public async Task AHeldMapping_IsNotQueued_AndIsNeverAskedAboutAtItsSource()
    {
        var task = Save("held", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1 }, MsSqlDriverKinds.ChangeTracking, null, "a", "b");
        var state = new FakeState();
        state.ReadStates["a"] = new MappingReadState(ReadIntent.Changes, ReadHold.PositionExpired, "5", null);
        var counters = new FakeCounters("100");

        Assert.True(await Planner(state, counters).TickContinuousAsync("held", task, Names("a", "b"), CancellationToken.None));

        Assert.Equal(["b"], state.Queued);      // b has no stored position: a first pass is not the gate's to stop
        Assert.Equal(1, counters.Fetches);      // one round trip for the group, and a is not part of it
    }

    [Fact]
    public async Task TheGate_LeavesOutAMappingThatIsCaughtUp_AndKeepsOneBehindTheCounter_FromTheStatesOwnWatermark()
    {
        var task = Save("gated", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1 }, MsSqlDriverKinds.ChangeTracking, null, "behind", "caughtup");
        var state = new FakeState();
        state.Watermarks["behind"] = "90";
        state.Watermarks["caughtup"] = "100";
        var counters = new FakeCounters("100");

        Assert.True(await Planner(state, counters).TickContinuousAsync("gated", task, Names("behind", "caughtup"), CancellationToken.None));

        Assert.Equal(["behind"], state.Queued);
        Assert.Equal(1, counters.Fetches);
        Assert.Equal(["MsSqlChangeTracking=100"], state.Checks);      // the audit line went to the state the planner was given
    }

    [Fact]
    public async Task AGateThatCannotAsk_FailsOpen_AndTheMappingIsQueued()
    {
        var task = Save("failopen", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1 }, MsSqlDriverKinds.ChangeTracking, null, "a");
        var state = new FakeState();
        state.Watermarks["a"] = "100";
        var planner = new ReplicationPlanner(
            _config, state, new ChangePollingGate(new ChangeSourceResolver(_config, _drivers), new ThrowingCounters(), state, NullLogger.Instance), _drivers, new RecordingReconcile(), NullLogger.Instance);

        Assert.True(await planner.TickContinuousAsync("failopen", task, Names("a"), CancellationToken.None));

        Assert.Equal(["a"], state.Queued);
    }

    private sealed class ThrowingCounters : IChangeCounterSource
    {
        public Task<ChangeCounterReading> FetchAsync(string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken) => throw new IOException("the source is down");

        public Task<DateTimeOffset?> MapSourceTimeAsync(string connectionName, string sourceDatabase, string readerKind, string value, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    }

    [Fact]
    public async Task AReconcileSweep_IsDueByItsOwnCadence_AndAnInFlightSweepIsNotQueuedBehind()
    {
        var reconcile = new ReconcileConfig { Enabled = true, Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 } };
        var task = Save("sweeps", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 }, GenericDriverKinds.Watermark, reconcile, "due", "recent", "inflight");
        var state = new FakeState();
        state.Reconcile["due"] = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.Reconcile["recent"] = DateTimeOffset.UtcNow.AddSeconds(-5);
        state.Reconcile["inflight"] = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.ReconcileInFlight.Add("inflight");
        var enqueuer = new RecordingReconcile();

        Assert.True(await Planner(state, reconcile: enqueuer).TickReconcileAsync("sweeps", task, Names("due", "recent", "inflight"), CancellationToken.None));

        Assert.Equal(["due"], enqueuer.Asked);
        Assert.False(state.RowsReadAsked);      // a cadence needs no history of rows read
    }

    [Fact]
    public async Task AnAfterChangeSweep_AsksForTheRowsRead_AndAStateThatCannotSayIsAskedOnlyThen()
    {
        var reconcile = new ReconcileConfig
        {
            Enabled = true,
            Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 },
            AfterChange = new AfterAnyChangeStrategy(),
        };
        var task = Save("afterchange", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 }, GenericDriverKinds.Watermark, reconcile, "a");
        var state = new FakeState();
        state.Reconcile["a"] = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.RowsRead["a"] = 250;
        var enqueuer = new RecordingReconcile();

        Assert.True(await Planner(state, reconcile: enqueuer).TickReconcileAsync("afterchange", task, Names("a"), CancellationToken.None));

        Assert.True(state.RowsReadAsked);
        Assert.Equal(["a"], enqueuer.Asked);
    }

    [Fact]
    public async Task AReconcileThatCannotBeQueued_IsNotCounted_AndDoesNotStopTheOthers()
    {
        var reconcile = new ReconcileConfig { Enabled = true, Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 } };
        var task = Save("invalid", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 }, GenericDriverKinds.Watermark, reconcile, "a", "b");
        var enqueuer = new RecordingReconcile { Answer = new ScheduledReconcileResult(ScheduledReconcileOutcome.Invalid, "no") };

        Assert.False(await Planner(new FakeState(), reconcile: enqueuer).TickReconcileAsync("invalid", task, Names("a", "b"), CancellationToken.None));

        Assert.Equal(["a", "b"], enqueuer.Asked);
    }

    [Fact]
    public async Task OneCycle_IsTheModesTickThenTheReconcileTick_AndReportsEitherQueueingAnything()
    {
        var reconcile = new ReconcileConfig { Enabled = true, Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 } };
        var task = Save("cycle", new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 }, GenericDriverKinds.Watermark, reconcile, "a");
        var state = new FakeState();
        state.Primary["a"] = DateTimeOffset.UtcNow;                      // not due for a pass
        state.Reconcile["a"] = DateTimeOffset.UtcNow.AddMinutes(-10);    // due for a sweep
        var enqueuer = new RecordingReconcile();

        Assert.True(await Planner(state, reconcile: enqueuer).PlanAsync("cycle", task, Names("a"), CancellationToken.None));

        Assert.Empty(state.Queued);
        Assert.Equal(["a"], enqueuer.Asked);
    }
}
