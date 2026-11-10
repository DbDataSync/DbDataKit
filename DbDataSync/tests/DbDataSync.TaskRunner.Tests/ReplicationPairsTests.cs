using System.Data.Common;
using System.Globalization;
using DbDataKit.Config.Git;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Postgres;
using DbDataSync.TaskRunner.Tests.Runner;
using DbDataSync.Libraries;
using DbDataSync.State;
using LibGit2Sharp;
using Xunit;
using RunMode = DbDataSync.TaskRunner.Tests.RunExecutorIntegrationTests.RunMode;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// Plan 28c E06d: the same replication between two of the five engines Sync supports, by the supervisor's worker and by a drain on the runner (the engine started from the read-only view of a published definition,
/// the position in the runner's own state), each ending with the target holding what the source holds. The reader is the generic <c>Watermark</c> reader, which every engine has, over a version column; the writer is
/// not named in the file, so the default (<c>ChangeApply</c>) is what writes. The mapping has no position, so the first drain is also the mapping's initial load on that path; the second reads what changed since
/// (inserts, updates, a NULL in every column but the key); a third, from a position the state lost, delivers it all again and the target stays as it is. A Watermark reader cannot see a delete, and none is made here.
/// Pairs: a ring through the five engines on both paths and its reverse on the runner, so every engine is a source and a target on the runner (the page <c>DbDataSync/architecture/runner-coverage-matrix.md</c> lists them, and <see cref="CoverageMatrixTests"/> keeps it so); Change Tracking from SQL Server into each engine is <see cref="ChangeApplyReplicationTests"/>.
/// Needs the engines of <c>testenv.sh env dbdatasync</c> and PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReplicationPairsTests : IAsyncLifetime
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private const string Replication = "pairs-sync";

    /// <summary>Source then target: a ring through the five engines.</summary>
    private static readonly (string Source, string Target)[] Ring =
    [
        ("postgres", "mysql"), ("mysql", "mariadb"), ("mariadb", "oracle"), ("oracle", "sqlserver"), ("sqlserver", "postgres"),
    ];

    /// <summary>The ring the other way: each engine again as a source and a target, with a different partner. Only on the runner, which is the path this suite is here to cover; the supervisor's own suites have their pairs.</summary>
    private static readonly (string Source, string Target)[] ReverseRing =
    [
        ("postgres", "sqlserver"), ("sqlserver", "oracle"), ("oracle", "mariadb"), ("mariadb", "mysql"), ("mysql", "postgres"),
    ];

    public static TheoryData<string, string, RunMode> Pairs
    {
        get
        {
            var data = new TheoryData<string, string, RunMode>();
            foreach (var (source, target) in Ring)
                foreach (var mode in new[] { RunMode.Supervisor, RunMode.Runner })
                    data.Add(source, target, mode);
            foreach (var (source, target) in ReverseRing)
                data.Add(source, target, RunMode.Runner);
            return data;
        }
    }

    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-pairs-").FullName;
    private readonly List<string> _directories = [];
    private ChangeApplyTargets.TargetEngine _source = null!;
    private ChangeApplyTargets.TargetEngine _target = null!;
    private string _sourceTable = "";
    private string _targetTable = "";
    private string? _secondTargetTable;
    private readonly List<string> _mappings = ["main"];
    private ConfigRepository _config = null!;
    private SecretStore _secrets = null!;
    private RunExecutor _executor = null!;
    private TaskRunStore _runs = null!;
    private WorkQueueStore _queue = null!;
    private ChangeWatermarkStore _watermarks = null!;
    private LogWriter _logs = null!;
    private string _watermarkKey = "";
    private RunnerRig? _rig;
    private string? _package;
    private int _version;

    public Task InitializeAsync()
    {
        Repository.Init(_repoRoot);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _rig?.Dispose();
        if (_source is not null) await _source.DisposeAsync(_sourceTable);
        if (_target is not null) await _target.DisposeAsync(_targetTable);
        if (_target is not null && _secondTargetTable is not null) await _target.DisposeAsync(_secondTargetTable);
        GitTempDirectory.DeleteRecursively(_repoRoot);
        foreach (var directory in _directories)
        {
            try { GitTempDirectory.DeleteRecursively(directory); }
            catch (Exception) { /* a temporary directory left behind is untidy, not wrong */ }
        }
    }

    private static SqlDialect DialectOf(ChangeApplyTargets.TargetEngine engine) => engine.DriverType switch
    {
        DriverIds.MsSql => MsSqlDialect.Instance,
        DriverIds.Postgres => PostgresDialect.Instance,
        DriverIds.MySql => MySqlDialect.Instance,
        DriverIds.Oracle => OracleDialect.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private async Task SetUpAsync(string source, string target, bool verify = false)
    {
        _source = ChangeApplyTargets.Make(source);
        _target = ChangeApplyTargets.Make(target);
        await _source.PrepareAsync();
        await _target.PrepareAsync();
        _sourceTable = _source.Table($"pr_src_{Guid.NewGuid():N}");
        _targetTable = _target.Table($"pr_tgt_{Guid.NewGuid():N}");
        await using (var c = _source.Open())
        {
            await ChangeApplyTargets.ExecuteAsync(c, _source.CreateSql(_sourceTable));
            // the version the Watermark reader reads by: an integer that grows with every change, set by the statement that makes it
            await ChangeApplyTargets.ExecuteAsync(c, $"ALTER TABLE {_source.Qualified(_sourceTable)} ADD {_source.Q(_source.Column("ver"))} {(_source.DriverType == DriverIds.Oracle ? "NUMBER(10,0)" : "int")} NULL");
        }

        await using (var c = _target.Open()) await ChangeApplyTargets.ExecuteAsync(c, _target.CreateSql(_targetTable));

        var secrets = _secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);
        var state = new StateDatabase(Path.Combine(_repoRoot, "state.db"));
        _runs = new TaskRunStore(state);
        _queue = new WorkQueueStore(state);
        _watermarks = new ChangeWatermarkStore(state);
        _logs = new LogWriter(state);
        var batches = new BulkLoadBatchStore(state);
        var drivers = new DriverRegistry();
        drivers.Register(_source.Driver);
        if (_target.Driver.DriverType != _source.Driver.DriverType) drivers.Register(_target.Driver);
        _config = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), secrets);
        _executor = new RunExecutor(
            _config, drivers, secrets, new LibraryRegistry(_repoRoot).LoadAll(), _repoRoot,
            new LocalRunnerState(_runs, _queue, new RunLockStore(state), _watermarks, new VerificationResultStore(state), _logs, batches,
                new Lazy<IInitialLoadEnqueuer>(() => new RealInitialLoadEnqueuer(_config, _queue, batches))),
            new LocalRunnerConfig(_config, Author), Scripting.ForTests(_config, _repoRoot), Path.Combine(_repoRoot, "state.db"));

        _config.SaveConnection(_source.Input("src-conn"), Author);
        _config.SaveConnection(_target.Input("tgt-conn"), Author);
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = Replication,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = { ["watermarkColumn"] = _source.Column("ver") } },
                Cache = new CacheConfig { Kind = _target.DriverType == DriverIds.Postgres ? PostgresDriverKinds.CopyStaging : _target.DriverType == DriverIds.MsSql ? MsSqlDriverKinds.StagingTable : GenericDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = GenericDriverKinds.ChangeApply },
            },
        }, Author);

        // the default case: the file does not name a writer at all
        var path = Path.Combine(_repoRoot, "config", "replications", Replication, "task.yaml");
        var lines = File.ReadAllLines(path).ToList();
        var at = lines.FindIndex(l => l == "  writer:");
        Assert.True(at > 0, string.Join('\n', lines));
        var next = lines.FindIndex(at + 1, l => !l.StartsWith("    ", StringComparison.Ordinal));
        lines.RemoveRange(at, next - at);
        File.WriteAllLines(path, lines);
        Assert.True(_config.LoadReplicationTask(Replication).ChangeProcessing.WriterIsDefault);
        Assert.Equal(GenericDriverKinds.ChangeApply, PipelineResolution.Writer(_config.LoadReplicationTask(Replication), mapping: null).Kind);

        await SaveMappingAsync("main", _targetTable, verify);
        _watermarkKey = WatermarkKey.Build(new SourceTableRef { ConnectionName = "src-conn", Database = _source.Database, Schema = _source.Schema, Table = _sourceTable }, DialectOf(_source));
    }

    private async Task SaveMappingAsync(string name, string targetTable, bool verify = false)
    {
        var mapping = new TableMappingConfig
        {
            Name = name,
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _source.Database, Schema = _source.Schema, Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _target.Database, Schema = _target.Schema, Table = targetTable }],
            ColumnMappings = new[] { "id", "name", "amount" }.Select(c => new ColumnMapping { SourceColumn = _source.Column(c), TargetColumn = _target.Column(c) }).ToList(),
        };
        // a row count per key: one group for each row, so a row on one side only is a group that differs
        if (verify) mapping.Verification = [new VerificationCheckConfig { Name = "counts", Kind = VerificationCheckKind.RowCount, GroupBy = [_target.Column("id")] }];
        await using (var c = _source.Open())
            mapping.SourceColumns = [.. (await _source.Driver.ListColumnsAsync(c, _source.Database, _source.Schema, _sourceTable, CancellationToken.None)).Select(x => new CachedColumn(x.Name, x.NativeType, x.IsNullable, x.IsPrimaryKey, x.IsIdentity))];
        await using (var c = _target.Open())
            mapping.TargetColumns = [.. (await _target.Driver.ListColumnsAsync(c, _target.Database, _target.Schema, targetTable, CancellationToken.None)).Select(x => new CachedColumn(x.Name, x.NativeType, x.IsNullable, x.IsPrimaryKey, x.IsIdentity))];
        _config.SaveTableMapping(Replication, mapping, Author);
    }

    /// <summary>A second mapping of the same source table into a table of its own: what a table replicated twice (two targets, or two readers) is.</summary>
    private async Task AddSecondMappingAsync()
    {
        _secondTargetTable = _target.Table($"pr_tg2_{Guid.NewGuid():N}");
        await using (var c = _target.Open()) await ChangeApplyTargets.ExecuteAsync(c, _target.CreateSql(_secondTargetTable));
        await SaveMappingAsync("second", _secondTargetTable);
        _mappings.Add("second");
    }

    // ---- the data

    private async Task SourceAsync(string sql)
    {
        await using var c = _source.Open();
        await ChangeApplyTargets.ExecuteAsync(c, sql);
    }

    /// <summary>One statement per change: Oracle takes no multi-row <c>VALUES</c>, and a version that grows with each is what the reader's position follows.</summary>
    private Task InsertAsync(int id, string? name, decimal? amount) => SourceAsync(
        $"INSERT INTO {_source.Qualified(_sourceTable)} ({_source.Q(_source.Column("id"))}, {_source.Q(_source.Column("name"))}, {_source.Q(_source.Column("amount"))}, {_source.Q(_source.Column("ver"))}) " +
        $"VALUES ({id}, {Text(name)}, {Number(amount)}, {++_version})");

    private Task UpdateAsync(int id, string? name, decimal? amount) => SourceAsync(
        $"UPDATE {_source.Qualified(_sourceTable)} SET {_source.Q(_source.Column("name"))} = {Text(name)}, {_source.Q(_source.Column("amount"))} = {Number(amount)}, {_source.Q(_source.Column("ver"))} = {++_version} " +
        $"WHERE {_source.Q(_source.Column("id"))} = {id}");

    private static string Text(string? value) => value is null ? "NULL" : $"'{value}'";

    private static string Number(decimal? value) => value is null ? "NULL" : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string Cell(object? v) => v is null or DBNull ? "<null>" : v is decimal d ? d.ToString("0.00", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim();

    private static async Task<List<string>> RowsAsync(ChangeApplyTargets.TargetEngine engine, string table)
    {
        await using var c = engine.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {engine.Q(engine.Column("id"))}, {engine.Q(engine.Column("name"))}, {engine.Q(engine.Column("amount"))} FROM {engine.Qualified(table)}";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, 3).Select(i => i == 0 ? Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : Cell(reader.GetValue(i)))));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private Task<List<string>> SourceRowsAsync() => RowsAsync(_source, _sourceTable);

    private Task<List<string>> TargetRowsAsync() => RowsAsync(_target, _targetTable);

    private Task<List<string>> SecondTargetRowsAsync() => RowsAsync(_target, _secondTargetTable!);

    // ---- who runs it

    private IReadOnlyDictionary<string, string> Logins() =>
        new[] { "src-conn", "tgt-conn" }.Select(name => _config.LoadConnection(name).CredentialSecretRef!)
            .ToDictionary(reference => _secrets.EnvName(reference), reference => _secrets.Resolve(reference));

    /// <summary>One drain of the replication by whoever <paramref name="mode"/> says, to its end: the worker's queue with a pass of the mapping, or the runner asked to run now. A mapping with no position loads first.</summary>
    private async Task DrainAsync(RunMode mode)
    {
        if (mode == RunMode.Supervisor)
        {
            var runIds = _mappings.Select(mapping => _queue.Enqueue(Replication, RunKind.Primary, mapping)).ToList();
            await _executor.ExecuteWorkerAsync(Replication, WorkerLanes.Uniform(1), CancellationToken.None);
            foreach (var runId in runIds)
            {
                var run = _runs.GetRun(runId)!;
                if (run.Status != RunStatus.Succeeded)
                    throw new Xunit.Sdk.XunitException($"the pass ended {run.Status}: {run.ErrorSummary}. Its log: " + string.Join(" | ", _logs.GetLogs(runId).Select(e => e.Message)));
            }

            return;
        }

        await OnTheRunnerAsync(_rig ?? await StartRigAsync());
    }

    private async Task<RunnerRig> StartRigAsync()
    {
        _package ??= await PackageAsync();
        return _rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
    }

    private async Task<string> PackageAsync()
    {
        var source = await RunnerPackageSource.CreateAsync(_repoRoot, _source.Driver, _target.Driver);
        _directories.Add(source);
        return source;
    }

    private async Task OnTheRunnerAsync(RunnerRig rig)
    {
        var result = await rig.Executor.RunAsync(await rig.DrainRequestAsync(_package!, Replication, runId: Guid.CreateVersion7(), runAll: true));
        if (result.End.State != TaskEndState.Succeeded || result.ExitCode != 0)
            throw new Xunit.Sdk.XunitException($"the drain on the runner ended {result.End.State} (exit {result.ExitCode}); log:\n{rig.LogText(result.RunId)}");
    }

    private string? Position(RunMode mode, string mapping = "main") => mode == RunMode.Supervisor ? _watermarks.GetWatermark(Replication, mapping, _watermarkKey) : _rig!.Watermark(mapping, _watermarkKey)?.Position;

    /// <summary>The position the state lost: the supervisor's row is set back, the runner starts again with a state that holds the old position.</summary>
    private async Task LosePositionAsync(RunMode mode, string position)
    {
        if (mode == RunMode.Supervisor)
        {
            foreach (var mapping in _mappings) _watermarks.SetWatermark(Replication, mapping, _watermarkKey, position);
            return;
        }

        _rig!.Dispose();
        _rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        foreach (var mapping in _mappings) _rig.SeedWatermark(mapping, _watermarkKey, position);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task AnIncrementalReplication_WithTheWatermarkReaderAndTheDefaultWriter_EndsWithTheSourceRows_AndALostPositionReplaysWithoutChange(string source, string target, RunMode mode)
    {
        await SetUpAsync(source, target);
        await InsertAsync(1, "one", 1m);
        await InsertAsync(2, "two", 2m);
        await InsertAsync(3, "three", 3m);
        await InsertAsync(4, "four", 4m);
        await InsertAsync(5, "five", 5m);

        // the first drain: the mapping has no position, so it is captured and the mapping is loaded (on the runner, a view of the definition and no state of Sync's own)
        await DrainAsync(mode);
        Assert.Equal(await SourceRowsAsync(), await TargetRowsAsync());
        Assert.Equal(5, (await TargetRowsAsync()).Count);
        Assert.Equal("5", Position(mode));

        // what changed since: new rows, a row changed, a row with a NULL in every column but the key
        await InsertAsync(6, "six", 6.5m);
        await InsertAsync(7, "seven", 7m);
        await UpdateAsync(2, "two-changed", 22.25m);
        await UpdateAsync(3, null, null);
        await DrainAsync(mode);
        var expected = await SourceRowsAsync();
        Assert.Equal(expected, await TargetRowsAsync());
        Assert.Contains("2|two-changed|22.25", expected);
        Assert.Contains("3|<null>|<null>", expected);
        Assert.Equal(_version.ToString(CultureInfo.InvariantCulture), Position(mode));

        // at-least-once: the position the state holds is older than what the target has (a crash after the commit, before the position was saved): everything since is delivered again and nothing changes
        await LosePositionAsync(mode, "5");
        await DrainAsync(mode);
        Assert.Equal(expected, await TargetRowsAsync());
        Assert.Equal(expected, await SourceRowsAsync());
        Assert.Equal(_version.ToString(CultureInfo.InvariantCulture), Position(mode));
    }

    /// <summary>
    /// A table replicated twice, by two mappings of one replication: each keeps its own position. The runner once kept one checkpoint per source table, so the mapping that ran second read from the position the first had
    /// just moved to and never delivered what the first had already passed (the supervisor's state is keyed by mapping and source table, and its own tests say so).
    /// </summary>
    [Theory]
    [InlineData(RunMode.Supervisor)]
    [InlineData(RunMode.Runner)]
    public async Task TwoMappingsOfOneSourceTable_KeepTheirOwnPositions_AndEachTargetEndsWithTheSourceRows(RunMode mode)
    {
        await SetUpAsync("postgres", "mysql");
        await AddSecondMappingAsync();
        await InsertAsync(1, "one", 1m);
        await InsertAsync(2, "two", 2m);
        await InsertAsync(3, "three", 3m);
        await DrainAsync(mode);
        Assert.Equal(await SourceRowsAsync(), await TargetRowsAsync());
        Assert.Equal(await SourceRowsAsync(), await SecondTargetRowsAsync());

        await InsertAsync(4, "four", 4m);
        await UpdateAsync(2, "two-changed", 2.5m);
        await DrainAsync(mode);
        var expected = await SourceRowsAsync();
        Assert.Equal(expected, await TargetRowsAsync());
        Assert.Equal(expected, await SecondTargetRowsAsync());
        Assert.Equal(_version.ToString(CultureInfo.InvariantCulture), Position(mode, "main"));
        Assert.Equal(_version.ToString(CultureInfo.InvariantCulture), Position(mode, "second"));
    }
}
