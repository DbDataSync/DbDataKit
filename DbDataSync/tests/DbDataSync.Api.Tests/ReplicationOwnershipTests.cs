using DbDataKit.Config.Git;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Runner;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataSync.Api.Tests;

/// <summary>Plan 28c E12: the owner of a replication (old, observe, runner), the cutover and rollback of 7.3, what a request does for a replication the runner owns, and the guard that keeps both paths from processing a mapping.</summary>
public sealed class ReplicationOwnershipTests : IDisposable
{
    private const string Replication = "Sales_Orders";
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-ownership-").FullName;
    private readonly ConfigRepository _config;
    private readonly StateDatabase _database;
    private readonly TaskRunStore _taskRuns;
    private readonly WorkQueueStore _queue;
    private readonly ChangeWatermarkStore _watermarks;
    private readonly DriverRegistry _drivers = new();
    private readonly RunnerOwnerFile _file;
    private readonly FakeSync _sync = new();
    private readonly ManualClock _clock = new();
    private int _workerPolls;

    public ReplicationOwnershipTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _drivers.Register(new MsSqlDriver());
        _config.SaveConnection(new ConnectionInput { Name = "src", DriverType = DriverIds.MsSql, Host = "localhost", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "pw" }, Author);
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = Replication,
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = "src", Database = "App" }, Target = new EndpointRef { ConnectionName = "src", Database = "DW" } },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 60 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = new Dictionary<string, string> { ["watermarkColumn"] = "UpdatedAt" } },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, Author);
        _config.SaveTableMapping(Replication, new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = "orders" }],
            Targets = [new TableSpec { Schema = "dbo", Table = "orders" }],
            SourceColumns = [new CachedColumn("Id", "int", false, true, false)],
            TargetColumns = [new CachedColumn("Id", "int", false, true, false)],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
        }, Author);
        _database = new StateDatabase(Path.Combine(_root, "state.db"));
        _taskRuns = new TaskRunStore(_database);
        _queue = new WorkQueueStore(_database);
        _watermarks = new ChangeWatermarkStore(_database);
        _file = new RunnerOwnerFile(Path.Combine(_root, "runner-owners.json"));
        _file.SetLink(new RunnerLink("runner-1", "prod", "sales-set"));
    }

    public void Dispose()
    {
        GitTempDirectory.DeleteRecursively(_root);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    private sealed class FakeSync : IRunnerSync
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, string>? LastInputs { get; private set; }

        public ClientSeed? LastSeed { get; private set; }

        public string? Version { get; set; } = "1";

        public string? RunId { get; set; } = Guid.NewGuid().ToString();

        public Func<int, (int Running, int Queued)> Busy { get; set; } = _ => (0, 0);

        public bool RunnerPaused { get; set; }

        private int _statusReads;

        public Task<ClientTaskSchedule> ScheduleOfAsync(string task, CancellationToken ct)
        {
            Calls.Add("schedule");
            return Task.FromResult(new ClientTaskSchedule(task, "old", null, null, null, null, RunnerPaused, null, null, 0, 0, Version));
        }

        public Task SetOwnerAsync(string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct)
        {
            Calls.Add("owner:" + owner);
            LastSeed = seed ?? LastSeed;
            return Task.CompletedTask;
        }

        public Task<string?> RunNowAsync(string task, IReadOnlyDictionary<string, string>? inputs, CancellationToken ct)
        {
            Calls.Add("run:" + task);
            LastInputs = inputs?.ToDictionary(p => p.Key, p => p.Value);
            return Task.FromResult(RunId);
        }

        public Task PauseAsync(string task, CancellationToken ct) { Calls.Add("pause"); return Task.CompletedTask; }

        public Task ResumeAsync(string task, CancellationToken ct) { Calls.Add("resume"); return Task.CompletedTask; }

        public Task<ClientTaskStatus?> StatusAsync(string task, CancellationToken ct)
        {
            var (running, queued) = Busy(_statusReads++);
            return Task.FromResult<ClientTaskStatus?>(new ClientTaskStatus(task, "runner", null, null, running, queued, RunnerPaused, null));
        }
    }

    private ReplicationOwnership Ownership(Func<int, bool>? workerRunning = null) => new(
        _config, _taskRuns, _queue, _watermarks, _drivers,
        _ => workerRunning?.Invoke(_workerPolls++) ?? false,
        new RunnerAccess(_file, _ => _sync), _clock, (span, _) => { _clock.Advance(span); return Task.CompletedTask; });

    private string KeyOfOrders()
    {
        var task = _config.LoadReplicationTask(Replication);
        var mapping = _config.LoadTableMapping(Replication, "orders");
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        return WatermarkKey.Build(source, ((IDialectProvider)_drivers.Get(DriverIds.MsSql)).Dialect);
    }

    [Fact]
    public async Task Cutover_WaitsForTheWorker_HandsOverThePositions_ThenSetsTheOwnerOnTheRunner()
    {
        _watermarks.SetWatermark(Replication, "orders", KeyOfOrders(), "0x2A", new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero));

        var change = await Ownership(workerRunning: poll => poll < 3).CutoverAsync(Replication, "ann", null, CancellationToken.None);

        Assert.True(change.Waited);
        Assert.Equal(("old", "runner", "sales_orders", 1), (change.From, change.To, change.Task, change.PositionsHandedOver));
        Assert.Equal(TaskOwners.Runner, _file.OwnerOf(Replication));
        // the first drain starts with the positions, and only then is the runner's schedule made active
        Assert.Equal(["schedule", "run:sales_orders", "owner:runner"], _sync.Calls);
        var seeds = DrainRequestText.Parse(_sync.LastInputs!["requests"]);
        var seed = Assert.Single(seeds);
        Assert.Equal(("Position", "orders", "0x2A", KeyOfOrders()), (seed.Kind, seed.Mapping, seed.Position, seed.SourceKey));
        Assert.NotNull(_sync.LastSeed);
    }

    [Fact]
    public async Task Cutover_ThatCannotFinish_LeavesTheOwnerAsItWas_AndTheRunnerUntouched()
    {
        // the worker never ends
        var stuck = await Assert.ThrowsAsync<OwnershipException>(() => Ownership(_ => true).CutoverAsync(Replication, "ann", TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("still running", stuck.Message);
        Assert.Equal(TaskOwners.Old, _file.OwnerOf(Replication));
        Assert.DoesNotContain(_sync.Calls, c => c.StartsWith("owner:") || c.StartsWith("run:"));

        // work left in the queue with no worker to run it
        _queue.Enqueue(Replication, RunKind.Primary, "orders");
        var left = await Assert.ThrowsAsync<OwnershipException>(() => Ownership().CutoverAsync(Replication, "ann", null, CancellationToken.None));
        Assert.Contains("work left in this API's queue", left.Message);
        Assert.Equal(TaskOwners.Old, _file.OwnerOf(Replication));
    }

    [Fact]
    public async Task Cutover_RefusesAMappingThatIsLoading_AndARunnerThatHasNoActiveVersion_AndARunThatIsThereAlready()
    {
        _watermarks.SetReadIntentAndHold(Replication, "orders", KeyOfOrders(), ReadIntent.InitialLoad, ReadHold.Loading);
        var loading = await Assert.ThrowsAsync<OwnershipException>(() => Ownership().CutoverAsync(Replication, "ann", null, CancellationToken.None));
        Assert.Contains("'orders' is loading", loading.Message);
        Assert.Equal(TaskOwners.Old, _file.OwnerOf(Replication));

        _watermarks.SetReadIntentAndHold(Replication, "orders", KeyOfOrders(), ReadIntent.Changes, ReadHold.None);
        _watermarks.SetWatermark(Replication, "orders", KeyOfOrders(), "0x01");
        _sync.RunId = null;
        var there = await Assert.ThrowsAsync<OwnershipException>(() => Ownership().CutoverAsync(Replication, "ann", null, CancellationToken.None));
        Assert.Contains("queued or running on the runner already", there.Message);
        Assert.Equal(TaskOwners.Old, _file.OwnerOf(Replication));
        Assert.DoesNotContain("owner:runner", _sync.Calls);

        _sync.Version = null;
        var none = await Assert.ThrowsAsync<OwnershipException>(() => Ownership().CutoverAsync(Replication, "ann", null, CancellationToken.None));
        Assert.Contains("no active version", none.Message);
    }

    [Fact]
    public async Task Rollback_LetsTheRunnerGo_WaitsForItsDrain_CopiesThePause_AndReturnsTheOwner()
    {
        _file.SetOwner(Replication, TaskOwners.Runner);
        _sync.Busy = read => read < 2 ? (1, 0) : (0, 0);
        _sync.RunnerPaused = true;

        var change = await Ownership().RollbackAsync(Replication, "ann", null, CancellationToken.None);

        Assert.True(change.Waited);
        Assert.Equal(TaskOwners.Old, _file.OwnerOf(Replication));
        Assert.Equal("owner:old", _sync.Calls.First(c => c.StartsWith("owner:")));
        Assert.True(_taskRuns.GetPauseState(Replication).Paused);
    }

    [Fact]
    public async Task Rollback_ThatRunsOutOfWait_GivesTheScheduleBackToTheRunner()
    {
        _file.SetOwner(Replication, TaskOwners.Runner);
        _sync.Busy = _ => (1, 0);

        var ex = await Assert.ThrowsAsync<OwnershipException>(() => Ownership().RollbackAsync(Replication, "ann", TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Contains("still running on the runner", ex.Message);
        Assert.Equal(TaskOwners.Runner, _file.OwnerOf(Replication));
        Assert.Equal(["owner:old", "owner:runner"], _sync.Calls.Where(c => c.StartsWith("owner:")));
    }

    [Fact]
    public async Task Observe_RecordsTheOwnerOnBothSides_AndTheRunnerDoesNotOwnTheDrain()
    {
        var change = await Ownership().ObserveAsync(Replication, "ann", CancellationToken.None);

        Assert.Equal("observe", change.To);
        Assert.Equal(TaskOwners.Observe, _file.OwnerOf(Replication));
        Assert.False(new RunnerDispatch(new RunnerAccess(_file, _ => _sync)).RunnerOwns(Replication));
        Assert.Contains("owner:observe", _sync.Calls);
    }

    [Fact]
    public async Task ARequestForAReplicationTheRunnerOwns_IsARunNowOfItsDrain_WithTheRequestsAndRunAll()
    {
        _file.SetOwner(Replication, TaskOwners.Runner);
        var dispatch = new RunnerDispatch(new RunnerAccess(_file, _ => _sync));
        Assert.True(dispatch.RunnerOwns(Replication));

        var all = await dispatch.SendAsync(Replication, [], runAll: true, CancellationToken.None);
        Assert.Equal(TriggerOutcome.Started, all.Outcome);
        Assert.Equal(["run_all"], _sync.LastInputs!.Keys);

        var reload = await dispatch.SendAsync(Replication, [new DrainRequest(nameof(RunKind.BulkLoad), "orders", "Full", "{}", BatchId: "b", BatchSegments: 1)], runAll: false, CancellationToken.None);
        Assert.Equal(TriggerOutcome.Started, reload.Outcome);
        Assert.Equal("BulkLoad", Assert.Single(DrainRequestText.Parse(_sync.LastInputs!["requests"])).Kind);
    }

    [Fact]
    public async Task WhenADrainIsUpAlready_APlainRunNowIsDone_AndARequestThatCarriesWorkIsRefusedSayingWhy()
    {
        _file.SetOwner(Replication, TaskOwners.Runner);
        _sync.RunId = null;
        var dispatch = new RunnerDispatch(new RunnerAccess(_file, _ => _sync));

        Assert.Equal(TriggerOutcome.Started, (await dispatch.SendAsync(Replication, [], runAll: true, CancellationToken.None)).Outcome);
        var refused = await dispatch.SendAsync(Replication, [new DrainRequest(nameof(RunKind.Verification), "orders")], runAll: false, CancellationToken.None);

        Assert.Equal(TriggerOutcome.FailedToStart, refused.Outcome);
        Assert.Contains("queued or running on the runner already", refused.Reason);
    }

    [Fact]
    public void TheSupervisorStartsNoWorkerForAReplicationTheRunnerOwns()
    {
        _file.SetOwner(Replication, TaskOwners.Runner);
        var options = new ApiOptions { RepoRoot = _root, StateDbPath = Path.Combine(_root, "state.db"), TaskRunnerDllPath = "unused", CliDllPath = "unused" };
        var supervisor = new ProcessSupervisor(options, _config, _taskRuns, new RunLockStore(_database), _queue, null!, null!, null!, NullLogger<ProcessSupervisor>.Instance, _file);

        var result = supervisor.EnsureWorkerRunning(Replication);

        Assert.Equal(TriggerOutcome.FailedToStart, result.Outcome);
        Assert.Contains("owned by the runner", result.Reason);
        Assert.False(supervisor.DescribeStatus(Replication).Running);
    }

    [Fact]
    public void TheOwnerFile_IsReadAtEveryUse_AndAFileThatDoesNotReadIsRefusedNamingIt()
    {
        _file.SetOwner(Replication, TaskOwners.Observe);
        Assert.Equal(TaskOwners.Observe, new RunnerOwnerFile(_file.Path).OwnerOf(Replication));
        _file.SetOwner(Replication, TaskOwners.Old);
        Assert.Equal(TaskOwners.Old, _file.OwnerOf(Replication));
        Assert.DoesNotContain(Replication, File.ReadAllText(_file.Path));     // old is the default and is not written

        File.WriteAllText(_file.Path, "{\"owners\": {\"x\": \"both\"}}");
        var bad = Assert.Throws<InvalidOperationException>(() => _file.Read());
        Assert.Contains("runner-owners.json", bad.Message);
        Assert.Contains("both", bad.Message);
    }
}
