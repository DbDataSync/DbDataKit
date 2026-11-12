using System.Data.Common;
using System.Globalization;
using DbDataKit.Config.Git;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;
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
using DbDataSync.Verification;
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
    private Guid _lastRunId;
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
        var result = await rig.Executor.RunAsync(await rig.DrainRequestAsync(_package!, Replication, runId: Guid.CreateVersion7(), runAll: true,
            connectionHandles: new Dictionary<string, string> { ["src_conn"] = "src-conn", ["tgt_conn"] = "tgt-conn" }));
        _lastRunId = result.RunId;
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

    /// <summary>
    /// A verification pass of the mapping, by the supervisor's worker and by a drain on the runner (plan 28c E06d): the check compares the two sides group by group (here one group per key, and one key only the target has) and writes
    /// a parquet file. On the supervisor path that is a file beside Sync's state; on the runner it is written into the run's scratch, streamed into a payload of the run before the scratch is deleted, and the result the run
    /// records names the payload. Nothing is left in the runner's data directory.
    /// </summary>
    [Theory]
    [InlineData(RunMode.Supervisor)]
    [InlineData(RunMode.Runner)]
    public async Task AVerification_WritesItsResultFile_OnTheRunnerAsAPayloadOfTheRun_AndTheScratchIsDeleted(RunMode mode)
    {
        await SetUpAsync("mysql", "postgres", verify: true);
        await InsertAsync(1, "one", 1m);
        await InsertAsync(2, "two", 2m);
        await InsertAsync(3, "three", 3m);
        await DrainAsync(mode);
        Assert.Equal(await SourceRowsAsync(), await TargetRowsAsync());
        await using (var c = _target.Open())
            await ChangeApplyTargets.ExecuteAsync(c, $"INSERT INTO {_target.Qualified(_targetTable)} ({_target.Q(_target.Column("id"))}, {_target.Q(_target.Column("name"))}) VALUES (99, 'only-here')");

        string recordedPath;
        int groups, differing;
        VerificationResultHeader header;
        if (mode == RunMode.Supervisor)
        {
            var runId = _queue.Enqueue(Replication, RunKind.Verification, "main");
            await _executor.ExecuteWorkerAsync(Replication, WorkerLanes.Uniform(1), CancellationToken.None);
            Assert.Equal(RunStatus.Succeeded, _runs.GetRun(runId)!.Status);
            var record = Assert.Single(new VerificationResultStore(new StateDatabase(Path.Combine(_repoRoot, "state.db"))).List(Replication, "main", 10));
            (recordedPath, groups, differing) = (record.ResultPath, record.GroupsCompared, record.DifferingGroups);
            Assert.Null(VerificationResultPayload.IdOf(recordedPath));
            Assert.True(File.Exists(recordedPath));
            header = await VerificationResultFile.ReadHeaderAsync(recordedPath, CancellationToken.None);
        }
        else
        {
            var chain = Guid.CreateVersion7().ToString();
            var request = await _rig!.DrainRequestAsync(_package!, Replication, runId: Guid.CreateVersion7(), chain: chain,
                requests: DrainRequestText.Serialize([new DbDataSync.State.DrainRequest(nameof(RunKind.Verification), "main")]));
            var result = await _rig.Executor.RunAsync(request);
            Assert.True(result.End.State == TaskEndState.Succeeded && result.ExitCode == 0, _rig.LogText(result.RunId));

            var steps = new DbDataKit.State.StepStore(_rig.Database, new DbDataKit.State.ProgressKey(RunnerRig.Package, RunnerRig.Channel, "drain")).List(chain);
            var recorded = System.Text.Json.JsonSerializer.Deserialize<VerificationResultRecord>(Assert.Single(steps, s => s.Name == "verification-result").ResultJson!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            var fileStep = Assert.Single(steps, s => s.Name == "verification-file");
            (recordedPath, groups, differing) = (recorded.ResultPath, recorded.GroupsCompared, recorded.DifferingGroups);
            Assert.Equal(fileStep.ResultPayload, VerificationResultPayload.IdOf(recordedPath));

            // the file is the payload: read back out of the runner's state, it is the parquet the check wrote
            var copy = Path.Combine(_repoRoot, "from-payload.parquet");
            await using (var payload = new DbDataKit.State.PayloadStore(_rig.Database).OpenRead(fileStep.ResultPayload!.Value))
            await using (var target = File.Create(copy))
                await payload.CopyToAsync(target);
            header = await VerificationResultFile.ReadHeaderAsync(copy, CancellationToken.None);

            // and nothing of it is left where the run wrote it
            Assert.Empty(Directory.EnumerateFiles(_rig.DataDirectory, "*.parquet", SearchOption.AllDirectories));
        }

        Assert.Equal("counts", header.CheckName);
        Assert.Equal(4, groups);        // the three keys both sides have, and 99
        Assert.Equal(1, differing);     // 99 is only at the target
    }

    /// <summary>The target is a shared engine and the replication's name is the same in every run: the units an earlier run left are another run's state, which this one must not meet.</summary>
    private async Task ClearOldUnitsAsync()
    {
        await using var connection = _target.Open();
        var state = new TargetUnitState(TargetUnitState.EngineOf(_target.DriverType)!.Value);
        if (!await state.ExistsAsync(connection, CancellationToken.None)) return;
        await ChangeApplyTargets.ExecuteAsync(connection, $"DELETE FROM {state.Qualified} WHERE replication_name = '{Replication}'");
    }

    private string UnitTable => new TargetUnitState(TargetUnitState.EngineOf(_target.DriverType)!.Value).Qualified;

    private async Task<string> DumpUnitsAsync()
    {
        await using var connection = _target.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT unit_seq, mapping, watermark_key, watermark_before, watermark_after, rows_staged, committed_at FROM {UnitTable} WHERE replication_name = '{Replication}' ORDER BY unit_seq";
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        return string.Join("\n", lines);
    }

    private async Task<long> TargetUnitCountAsync()
    {
        await using var connection = _target.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {UnitTable} WHERE replication_name = '{Replication}'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Plan 28c E17, the runner side: a replication whose runs the runner owns and whose file asks for <c>tracking: both</c> with the target primary has the units its passes recorded in <c>dbdatasync.unit</c> copied into the runner's
    /// state by the drain itself (the protocol writes rows and checkpoint in one transaction there); a target restored to an earlier point is a divergence recorded in the runner's state, nothing more is copied while it is open,
    /// and the runner's resolve (the shared one the runner's API calls) archives the copy's later rows so the next drain copies the target's units again.
    /// </summary>
    [Theory]
    [InlineData("sqlserver", "postgres")]
    [InlineData("postgres", "sqlserver")]
    [InlineData("sqlserver", "mysql")]
    [InlineData("postgres", "mariadb")]
    [InlineData("sqlserver", "oracle")]
    public async Task A_runner_owned_replication_keeping_its_state_on_the_target_is_followed_into_the_runners_state_and_a_restored_target_is_a_divergence_there(string source, string target)
    {
        await SetUpAsync(source, target);
        await ClearOldUnitsAsync();
        var task = _config.LoadReplicationTask(Replication);
        task.Tracking = new TrackingConfig { Mode = "both", Primary = "target" };
        _config.SaveReplicationTask(task, Author);
        await InsertAsync(1, "one", 1m);
        await InsertAsync(2, "two", 2m);
        await DrainAsync(RunMode.Runner);
        // the first drain is the initial load and may end before the pass that follows it has recorded a unit: drain again until the target holds one
        for (var again = 0; again < 3 && await TargetUnitCountAsync() == 0; again++) await DrainAsync(RunMode.Runner);
        var store = new StateTrackingStore(_rig!.Database);
        var identities = store.Identities().Where(SyncUnitCopy.IsIdentity).ToList();
        Assert.True(identities.Count == 1, _rig.LogText(_lastRunId));
        var identity = identities[0];
        Assert.EndsWith("@tgt-conn", identity);
        Assert.Equal(Replication, SyncUnitCopy.ReplicationOf(identity));
        var units = await TargetUnitCountAsync();
        Assert.True(units > 0);
        Assert.True((int)units == store.Count(identity, SyncUnitCopy.Name), $"target {units} units, copy {store.Count(identity, SyncUnitCopy.Name)}: " + _rig.LogText(_lastRunId));

        // what the pass recorded since: copied by the next drain, and only that
        await InsertAsync(3, "three", 3m);
        await DrainAsync(RunMode.Runner);
        var afterSecond = await TargetUnitCountAsync();
        Assert.True(afterSecond > units);
        Assert.True((int)afterSecond == store.Count(identity, SyncUnitCopy.Name), $"target {afterSecond} units, copy {store.Count(identity, SyncUnitCopy.Name)}:\n" + await DumpUnitsAsync() + "\n" + string.Join("\n", _rig.LogText(_lastRunId).Split('\n').Where(l => l.Contains("unit") || l.Contains("Warning"))));
        Assert.Equal(await SourceRowsAsync(), await TargetRowsAsync());

        // the target is restored to a backup taken before its newest unit: the copy holds a unit the table does not
        await using (var connection = _target.Open())
            await ChangeApplyTargets.ExecuteAsync(connection, $"DELETE FROM {UnitTable} WHERE unit_seq = (SELECT newest FROM (SELECT MAX(unit_seq) AS newest FROM {UnitTable} WHERE replication_name = '{Replication}') latest)");
        await InsertAsync(4, "four", 4m);
        await DrainAsync(RunMode.Runner);
        var open = Assert.Single(new StateTrackingDivergences(_rig.Database).List(identity), d => d.IsOpen);
        Assert.Equal(SyncUnitCopy.Name, open.Stream);
        Assert.True(SyncUnitCopy.IsUnitDivergence(_rig.Database, open.Id));
        Assert.True(open.Reason.Contains("holds rows of 'unit'"), open.Reason);
        var (_, documented) = SyncUnitCopy.Document(_rig.Database, BuildTrackingState.ProgressKey, _ => ("both", "target"));
        Assert.Single(documented, d => (long)d!["id"]! == open.Id);

        // the runner's resolve: archive the copy's rows after the common point; the next drain copies the target's units again from there
        var resolved = await SyncUnitCopy.ResolveAsync(_rig.Database, BuildTrackingState.ProgressKey, null, open.Id, DbDataKit.State.Tracking.TrackingResolution.Archive, "ada", CancellationToken.None);
        Assert.Equal(SyncUnitResolveOutcome.Resolved, resolved.Outcome);
        await InsertAsync(5, "five", 5m);
        await DrainAsync(RunMode.Runner);
        Assert.Empty(new StateTrackingDivergences(_rig.Database).List(identity).Where(d => d.IsOpen));
        Assert.Equal((int)await TargetUnitCountAsync(), store.Count(identity, SyncUnitCopy.Name));
        Assert.Equal(await SourceRowsAsync(), await TargetRowsAsync());
    }
}
