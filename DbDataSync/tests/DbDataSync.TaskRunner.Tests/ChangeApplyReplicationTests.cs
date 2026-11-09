using System.Data.Common;
using System.Globalization;
using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Postgres;
using DbDataSync.TaskRunner.Runner;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataSync.Libraries;
using DbDataSync.TaskRunner.Tests.Runner;
using DbDataSync.State;
using DbDataSync.TestSupport;
using LibGit2Sharp;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// Plan 28c, CW01c: an incremental replication from SQL Server (Change Tracking) into every other engine Sync supports as a target, run by <see cref="RunExecutor"/> (the supervisor path), with
/// inserts, updates, deletes, a primary key change and several changes to one key in one pass, ending with the target's rows equal to the source's. The replication's file does <b>not</b> name a
/// writer: the default for an incremental reader (<c>ChangeApply</c>) is what runs, which is the proof that the default works end to end. A second drain from an older position replays changes
/// the target already has and must leave it unchanged. Needs the engines of <c>testenv.sh env dbdatasync</c> (SQL Server, MySQL, MariaDB, Oracle) and PostgreSQL
/// (<c>DBDATASYNC_TEST_POSTGRES_SERVER</c>, or the <c>DBKIT_TEST_POSTGRES</c> of <c>env dbdatakit</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChangeApplyReplicationTests : IAsyncLifetime
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private static readonly string[] Engines = ["postgres", "mysql", "mariadb", "oracle", "sqlserver"];

    public static TheoryData<string> TargetEngines => [.. Engines];

    private readonly string _sourceDatabase = $"CaSource_{Guid.NewGuid():N}";
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-changeapply-").FullName;
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private SqlConnection _source = null!;
    private ChangeApplyTargets.TargetEngine? _target;
    private readonly List<string> _directories = [];
    private string _targetTable = "";

    private ConfigRepository _config = null!;
    private SecretStore _secrets = null!;
    private RunExecutor _executor = null!;
    private TaskRunStore _runs = null!;
    private WorkQueueStore _queue = null!;
    private ChangeWatermarkStore _watermarks = null!;
    private LogWriter _logs = null!;
    private string _watermarkKey = "";

    private static string SqlServer => ChangeApplyTargets.SqlServer;

    public async Task InitializeAsync()
    {
        Repository.Init(_repoRoot);
        await using (var bootstrap = new SqlConnection(SqlServer))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_sourceDatabase}];");
            await ExecuteAsync(bootstrap, $"ALTER DATABASE [{_sourceDatabase}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        _source = new SqlConnection(new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = _sourceDatabase }.ConnectionString);
        await _source.OpenAsync();
        // lower-case names on every side: the engines fold unquoted names differently and the drivers quote what they are given
        await ExecuteAsync(_source, $"CREATE TABLE dbo.[{_sourceTable}] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NULL, amount DECIMAL(18,2) NULL);");
        await ExecuteAsync(_source, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
    }

    public async Task DisposeAsync()
    {
        if (_target is not null) await _target.DisposeAsync(_targetTable);
        _source.Dispose();
        await using var bootstrap = new SqlConnection(SqlServer);
        await bootstrap.OpenAsync();
        await MsSqlScratch.DropDatabaseAsync(bootstrap, _sourceDatabase);
        GitTempDirectory.DeleteRecursively(_repoRoot);
        foreach (var directory in _directories)
        {
            try { GitTempDirectory.DeleteRecursively(directory); }
            catch (Exception) { /* a temporary directory left behind is untidy, not wrong */ }
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- the replication

    private async Task SetUpReplicationAsync(ChangeApplyTargets.TargetEngine target, string writer)
    {
        _target = target;
        await target.PrepareAsync();
        _targetTable = target.Table($"ca_tgt_{Guid.NewGuid():N}");
        await using (var c = target.Open()) await ExecuteAsync(c, target.CreateSql(_targetTable));

        var secrets = _secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);
        var state = new StateDatabase(Path.Combine(_repoRoot, "state.db"));
        _runs = new TaskRunStore(state);
        _queue = new WorkQueueStore(state);
        _watermarks = new ChangeWatermarkStore(state);
        _logs = new LogWriter(state);
        var batches = new BulkLoadBatchStore(state);
        var drivers = new DriverRegistry();
        drivers.Register(new MsSqlDriver());
        drivers.Register(target.Driver);
        _config = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), secrets);
        _executor = new RunExecutor(
            _config, drivers, secrets, new LibraryRegistry(_repoRoot).LoadAll(), _repoRoot,
            new LocalRunnerState(_runs, _queue, new RunLockStore(state), _watermarks, new VerificationResultStore(state), _logs, batches,
                new Lazy<IInitialLoadEnqueuer>(() => new RealInitialLoadEnqueuer(_config, _queue, batches))),
            new LocalRunnerConfig(_config, Author), Scripting.ForTests(_config, _repoRoot), Path.Combine(_repoRoot, "state.db"));

        _config.SaveConnection(new ConnectionInput
        {
            Name = "src-conn", DriverType = DriverIds.MsSql, Host = "localhost", Port = TestEngines.MsSql.Port, Database = _sourceDatabase, AuthMode = AuthMode.SqlAuth,
            UserId = TestEngines.MsSql.User, Password = TestEngines.MsSql.Password, Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true" },
        }, Author);
        _config.SaveConnection(target.Input("tgt-conn"), Author);

        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "ca-sync",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.ChangeTracking },
                Cache = new CacheConfig { Kind = target.DriverType == DriverIds.Postgres ? PostgresDriverKinds.CopyStaging : target.DriverType == DriverIds.MsSql ? MsSqlDriverKinds.StagingTable : "StagingTable" },
                Writer = new WriterConfig { Kind = writer.Length == 0 ? "ChangeApply" : writer },
            },
        }, Author);
        if (writer.Length == 0)
        {
            // the default case: the file does not name a writer at all
            var path = Path.Combine(_repoRoot, "config", "replications", "ca-sync", "task.yaml");
            var lines = File.ReadAllLines(path).ToList();
            var at = lines.FindIndex(l => l == "  writer:");
            Assert.True(at > 0, string.Join('\n', lines));
            var next = lines.FindIndex(at + 1, l => !l.StartsWith("    ", StringComparison.Ordinal));
            lines.RemoveRange(at, next - at);   // the writer's key and everything indented under it
            File.WriteAllLines(path, lines);
            Assert.DoesNotContain("writer:", File.ReadAllText(path));
            Assert.True(_config.LoadReplicationTask("ca-sync").ChangeProcessing.WriterIsDefault);
        }

        var mapping = new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _sourceDatabase, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = target.Database, Schema = target.Schema, Table = _targetTable }],
            ColumnMappings = new[] { "id", "name", "amount" }.Select(c => new ColumnMapping { SourceColumn = c, TargetColumn = target.Column(c) }).ToList(),
        };
        var sourceColumns = await new MsSqlDriver().ListColumnsAsync(_source, _sourceDatabase, "dbo", _sourceTable, CancellationToken.None);
        mapping.SourceColumns = [.. sourceColumns.Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity))];
        await using (var c = target.Open())
        {
            var targetColumns = await target.Driver.ListColumnsAsync(c, target.Database, target.Schema, _targetTable, CancellationToken.None);
            mapping.TargetColumns = [.. targetColumns.Select(x => new CachedColumn(x.Name, x.NativeType, x.IsNullable, x.IsPrimaryKey, x.IsIdentity))];
        }
        _config.SaveTableMapping("ca-sync", mapping, Author);
        _watermarkKey = WatermarkKey.Build(new SourceTableRef { ConnectionName = "src-conn", Database = _sourceDatabase, Schema = "dbo", Table = _sourceTable }, MsSqlDialect.Instance);
    }

    private async Task<string> VersionAsync()
    {
        await using var cmd = _source.CreateCommand();
        cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION();";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }

    private async Task DrainAsync()
    {
        var runId = _queue.Enqueue("ca-sync", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("ca-sync", WorkerLanes.Uniform(1), CancellationToken.None);
        var run = _runs.GetRun(runId)!;
        if (run.Status != RunStatus.Succeeded)
            throw new Xunit.Sdk.XunitException($"the pass ended {run.Status}: {run.ErrorSummary}. Its log: " + string.Join(" | ", _logs.GetLogs(runId).Select(e => e.Message)));
    }

    private static string Cell(object? v) => v is null or DBNull ? "<null>" : v is decimal d ? d.ToString("0.00", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim();

    private static async Task<List<string>> RowsAsync(DbConnection connection, string select)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = select;
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, 3).Select(i => i == 0 ? Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : Cell(reader.GetValue(i)))));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private Task<List<string>> SourceRowsAsync() => RowsAsync(_source, $"SELECT id, name, amount FROM dbo.[{_sourceTable}]");

    private async Task<List<string>> TargetRowsAsync()
    {
        await using var c = _target!.Open();
        var t = _target;
        var table = t is ChangeApplyTargets.SqlServerTargetInSource ? $"dbo.[{_targetTable}]" : $"{t.Q(t.Schema)}.{t.Q(_targetTable)}";
        if (t is ChangeApplyTargets.MySqlFamilyTarget) table = t.Q(_targetTable);
        return await RowsAsync(c, $"SELECT {t.Q(t.Column("id"))}, {t.Q(t.Column("name"))}, {t.Q(t.Column("amount"))} FROM {table}");
    }

    private async Task SeedBothAsync()
    {
        // the baseline both sides hold before the position is taken: what an initial load would have put there
        foreach (var (id, name, amount) in new (int Id, string Name, decimal Amount)[] { (1, "one", 1m), (2, "two", 2m), (3, "three", 3m), (4, "four", 4m), (5, "five", 5m) })
        {
            await ExecuteAsync(_source, $"INSERT INTO dbo.[{_sourceTable}] (id, name, amount) VALUES ({id}, N'{name}', {amount.ToString(CultureInfo.InvariantCulture)})");
            await using var c = _target!.Open();
            var t = _target;
            var table = t is ChangeApplyTargets.SqlServerTargetInSource ? $"dbo.[{_targetTable}]" : t is ChangeApplyTargets.MySqlFamilyTarget ? t.Q(_targetTable) : $"{t.Q(t.Schema)}.{t.Q(_targetTable)}";
            await ExecuteAsync(c, $"INSERT INTO {table} ({t.Q(t.Column("id"))}, {t.Q(t.Column("name"))}, {t.Q(t.Column("amount"))}) VALUES ({id}, '{name}', {amount.ToString(CultureInfo.InvariantCulture)})");
        }
    }

    private Task ChangesOneAsync() => ExecuteAsync(_source, $"""
        INSERT INTO dbo.[{_sourceTable}] (id, name, amount) VALUES (6, N'six', 6.00), (7, N'seven', 7.00);
        UPDATE dbo.[{_sourceTable}] SET name = N'two-changed' WHERE id = 2;
        UPDATE dbo.[{_sourceTable}] SET amount = NULL, name = NULL WHERE id = 3;
        DELETE FROM dbo.[{_sourceTable}] WHERE id = 4;
        UPDATE dbo.[{_sourceTable}] SET id = 50 WHERE id = 5;
        INSERT INTO dbo.[{_sourceTable}] (id, name, amount) VALUES (8, N'eight', 8.00);
        UPDATE dbo.[{_sourceTable}] SET name = N'eight-final', amount = 88.00 WHERE id = 8;
        INSERT INTO dbo.[{_sourceTable}] (id, name, amount) VALUES (9, N'nine', 9.00);
        DELETE FROM dbo.[{_sourceTable}] WHERE id = 9;
        """);

    private Task ChangesTwoAsync() => ExecuteAsync(_source, $"""
        UPDATE dbo.[{_sourceTable}] SET name = N'one-again', amount = 11.00 WHERE id = 1;
        DELETE FROM dbo.[{_sourceTable}] WHERE id = 6;
        INSERT INTO dbo.[{_sourceTable}] (id, name, amount) VALUES (4, N'four-reborn', 44.00);
        UPDATE dbo.[{_sourceTable}] SET id = 70 WHERE id = 7;
        """);

    [Theory]
    [MemberData(nameof(TargetEngines))]
    public async Task AnIncrementalReplicationWithNoWriterNamed_ChangeTrackingIntoEveryEngine_EndsWithTheSourceRows_AndAReplayChangesNothing(string engine)
    {
        await SetUpReplicationAsync(ChangeApplyTargets.Make(engine), writer: "");
        Assert.Equal("ChangeApply", PipelineResolution.Writer(_config.LoadReplicationTask("ca-sync"), mapping: null).Kind);
        await SeedBothAsync();
        var start = await VersionAsync();
        _watermarks.SetWatermark("ca-sync", "main", _watermarkKey, start);
        _watermarks.SetReadIntent("ca-sync", "main", _watermarkKey, ReadIntent.Changes);

        await ChangesOneAsync();
        await DrainAsync();
        var afterOne = await SourceRowsAsync();
        Assert.Equal(afterOne, await TargetRowsAsync());
        Assert.Contains("50|five|5.00", afterOne);        // the key change: the new key is there
        Assert.DoesNotContain("5|five|5.00", afterOne);   // and the old key is gone from the target too (compared above)
        Assert.Contains("8|eight-final|88.00", afterOne);

        await ChangesTwoAsync();
        await DrainAsync();
        var afterTwo = await SourceRowsAsync();
        Assert.Equal(afterTwo, await TargetRowsAsync());

        // at-least-once: a crash after the target committed and before the position was saved. The position goes back to where the replication started and the
        // pass is drained again; it re-delivers every net change since, which the target already has
        _watermarks.SetWatermark("ca-sync", "main", _watermarkKey, start);
        await DrainAsync();
        Assert.Equal(afterTwo, await TargetRowsAsync());
        Assert.Equal(afterTwo, await SourceRowsAsync());
    }

    // ---- the same replication drained on the runner (plan 28c, E03's way): the engine as the process of an executable package, from a read-only view, the position in the runner's own state

    /// <summary>A directory like the one a replication set is published from: the engine as the package's files, <c>repo/</c> with the definition and the libraries the drivers load from (SQL Server's and the target's).</summary>
    private async Task<string> PackageSourceAsync(ChangeApplyTargets.TargetEngine target)
    {
        var source = Directory.CreateTempSubdirectory("dbdatasync-changeapply-package-").FullName;
        _directories.Add(source);
        var engine = EngineBuildOutput();
        try { Directory.CreateSymbolicLink(Path.Combine(source, "engine"), engine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CopyDirectory(engine, Path.Combine(source, "engine")); }

        var repo = Directory.CreateDirectory(Path.Combine(source, "repo")).FullName;
        CopyDirectory(Path.Combine(_repoRoot, "config"), Path.Combine(repo, "config"));
        var registry = new LibraryRegistry(repo).LoadAll();
        await BuiltInDriverLibraries.EnsureInstalledAsync(registry, repo, DriverIds.MsSql, CancellationToken.None);
        if (target.DriverType is DriverIds.Postgres) await BuiltInDriverLibraries.EnsureInstalledAsync(registry, repo, target.DriverType, CancellationToken.None);
        else if (target.DriverType is DriverIds.MySql or DriverIds.Oracle) await InstallAsync(registry, repo, target.Driver.RequiredLibraryId!);
        await File.WriteAllTextAsync(Path.Combine(source, "package.yaml"), PackageManifestWriter.Write(RunnerRig.EngineManifest()));
        return source;
    }

    /// <summary>MySQL's and Oracle's client libraries are not part of the automatic seeding of the SQL Server and PostgreSQL ones: installed beside the definition the way <c>config library install</c> does it.</summary>
    private static async Task InstallAsync(LibraryRegistry registry, string repo, string libraryId)
    {
        if (registry.Installed.ContainsKey(libraryId)) return;
        var entry = KnownLibraries.TryGetById(libraryId)!;
        await LibraryInstaller.InstallOrDeferAsync(repo, libraryId, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType, cancellationToken: CancellationToken.None);
        registry.RegisterInstalled(libraryId);
    }

    private static string EngineBuildOutput()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DbDataSync.TaskRunner", "bin", configuration, framework);
            if (File.Exists(Path.Combine(candidate, "DbDataSync.TaskRunner.runtimeconfig.json"))) return candidate;
        }
        throw new InvalidOperationException("The engine's build output was not found above " + AppContext.BaseDirectory + ": build the test project in the tree (no --artifacts-path)");
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private IReadOnlyDictionary<string, string> Logins() =>
        new[] { "src-conn", "tgt-conn" }.Select(name => _config.LoadConnection(name).CredentialSecretRef!)
            .ToDictionary(reference => _secrets.EnvName(reference), reference => _secrets.Resolve(reference));

    private async Task DrainOnTheRunnerAsync(RunnerRig rig, string source)
    {
        var result = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "ca-sync", runId: Guid.CreateVersion7(), runAll: true));
        if (result.End.State != TaskEndState.Succeeded || result.ExitCode != 0)
            throw new Xunit.Sdk.XunitException($"the drain on the runner ended {result.End.State} (exit {result.ExitCode}); log:\n{rig.LogText(result.RunId)}");
    }

    [Theory]
    [MemberData(nameof(TargetEngines))]
    public async Task TheSameReplication_DrainedOnTheRunner_EndsWithTheSourceRows_AndALostPositionReplaysWithoutChange(string engine)
    {
        await SetUpReplicationAsync(ChangeApplyTargets.Make(engine), writer: "");
        await SeedBothAsync();
        var start = await VersionAsync();
        await ChangesOneAsync();
        var package = await PackageSourceAsync(_target!);

        using (var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins()))
        {
            rig.SeedWatermark(_watermarkKey, start);
            await DrainOnTheRunnerAsync(rig, package);
            var afterOne = await SourceRowsAsync();
            Assert.Equal(afterOne, await TargetRowsAsync());
            Assert.Contains("50|five|5.00", afterOne);
            Assert.NotEqual(start, rig.Watermark(_watermarkKey)!.Value.Position);

            await ChangesTwoAsync();
            await DrainOnTheRunnerAsync(rig, package);
            Assert.Equal(await SourceRowsAsync(), await TargetRowsAsync());
        }

        // the runner lost its position (a crash after the target committed, before the position was saved; a state restored from before): a runner that starts again from the original
        // position delivers everything since once more, and the target, which has it all, stays the same
        var expected = await SourceRowsAsync();
        using var again = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        again.SeedWatermark(_watermarkKey, start);
        await DrainOnTheRunnerAsync(again, package);
        Assert.Equal(expected, await TargetRowsAsync());
        Assert.Equal(expected, await SourceRowsAsync());
    }

    [Fact]
    public async Task AnExistingMsSqlMergeConfiguration_KeepsRunningUnchanged_AndEndsWithTheSameRows()
    {
        await SetUpReplicationAsync(ChangeApplyTargets.Make("sqlserver"), writer: MsSqlDriverKinds.Merge);
        Assert.Equal(MsSqlDriverKinds.Merge, _config.LoadReplicationTask("ca-sync").ChangeProcessing.Writer.Kind);
        await SeedBothAsync();
        var start = await VersionAsync();
        _watermarks.SetWatermark("ca-sync", "main", _watermarkKey, start);
        _watermarks.SetReadIntent("ca-sync", "main", _watermarkKey, ReadIntent.Changes);

        await ChangesOneAsync();
        await DrainAsync();
        Assert.Equal(await SourceRowsAsync(), await TargetRowsAsync());
    }
}
