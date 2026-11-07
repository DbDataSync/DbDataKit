using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Git;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Libraries;
using DbDataSync.State;
using DbDataSync.TaskRunner.Runner;
using LibGit2Sharp;
using Microsoft.Data.SqlClient;
using Xunit;
using DbDataSync.TestSupport;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// Plan 28c slice E03, the smallest real thing: one replication drained by the runner, beside the old path. A real SQL Server source (Change Tracking) and target, the engine started by a runner's executor as the
/// process of an executable package (<c>dotnet DbDataSync.TaskRunner.dll --runner</c>) from a read-only view, the position kept as the run's checkpoint in the runner's state, and the same replication run by
/// <see cref="RunExecutor"/> with Sync's own state as the comparison. Needs the engines of <c>testenv.sh env dbdatasync</c> (SQL Server) and, for the runner's state on other engines, those of
/// <c>testenv.sh env dbdatakit</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DrainOnTheRunnerTests : IAsyncLifetime
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly string _databaseName = $"DbDataSyncDrain_{Guid.NewGuid():N}";
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-drain-e2e-").FullName;
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _oldTarget = $"OldTgt_{Guid.NewGuid():N}";
    private readonly string _runnerTarget = $"RunnerTgt_{Guid.NewGuid():N}";
    private readonly List<string> _directories = [];
    private readonly MsSqlDriver _driver = new();

    private SqlConnection _admin = null!;
    private ConfigRepository _config = null!;
    private SecretStore _secrets = null!;
    private string _watermarkKey = null!;

    public async Task InitializeAsync()
    {
        Repository.Init(_repoRoot);
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap, $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        _admin = new SqlConnection(new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName }.ConnectionString);
        await _admin.OpenAsync();
        await ExecuteAsync(_admin, $"CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(_admin, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
        await CreateTargetAsync(_oldTarget);
        await CreateTargetAsync(_runnerTarget);

        _secrets = SecretStore.ForProviders(new InMemorySecretProvider());
        _config = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), _secrets);
        foreach (var name in new[] { "src-conn", "tgt-conn" })
            _config.SaveConnection(new ConnectionInput
            {
                DriverType = DriverIds.MsSql,
                Host = "localhost",
                Port = TestSupport.TestEngines.MsSql.Port,
                Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true" },
                Database = _databaseName,
                AuthMode = AuthMode.SqlAuth,
                UserId = "sa",
                Password = TestSupport.TestEngines.MsSql.Password,
                Name = name,
            }, Author);
        await SaveReplicationAsync("old-sync", _oldTarget);
        await SaveReplicationAsync("runner-sync", _runnerTarget);
        _watermarkKey = WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }, MsSqlDialect.Instance);
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await using var bootstrap = new SqlConnection(ServerConnectionString);
        await bootstrap.OpenAsync();
        await MsSqlScratch.DropDatabaseAsync(bootstrap, _databaseName);
        GitTempDirectory.DeleteRecursively(_repoRoot);
        foreach (var directory in _directories)
        {
            try { GitTempDirectory.DeleteRecursively(directory); }
            catch (Exception) { /* a temporary directory left behind is untidy, not wrong */ }
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 900;
        await command.ExecuteNonQueryAsync();
    }

    private Task CreateTargetAsync(string table) =>
        ExecuteAsync(_admin, $"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

    private async Task SaveReplicationAsync(string name, string targetTable)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "*/5 * * * *" },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.ChangeTracking },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, Author);
        var mapping = new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        };
        mapping.SourceColumns = await CachedColumnsAsync(_sourceTable);
        mapping.TargetColumns = await CachedColumnsAsync(targetTable);
        _config.SaveTableMapping(name, mapping, Author);
    }

    private async Task<List<CachedColumn>> CachedColumnsAsync(string table)
    {
        var columns = await _driver.ListColumnsAsync(_admin, _databaseName, "dbo", table, CancellationToken.None);
        return [.. columns.Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity))];
    }

    /// <summary>The position a replication starts its incremental reads from: the source's version now, before the rows the test inserts.</summary>
    private async Task<string> CurrentVersionAsync()
    {
        await using var command = _admin.CreateCommand();
        command.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION();";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }

    private Task InsertRowsAsync(int count, int from = 1) => ExecuteAsync(_admin, $"""
        INSERT INTO dbo.[{_sourceTable}] (Id, Name)
        SELECT TOP ({count}) {from - 1} + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), CONCAT(N'row-', {from - 1} + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
        FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
        """);

    private async Task<(long Count, long Sum)> TargetFingerprintAsync(string table)
    {
        await using var command = _admin.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = $"SELECT COUNT_BIG(*), COALESCE(SUM(CAST(CHECKSUM(Id, Name) AS BIGINT)), 0) FROM dbo.[{table}];";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>The replication drained by <see cref="RunExecutor"/> over Sync's own state, as the supervisor's worker does: the comparison for what the runner does.</summary>
    private async Task<(TaskRunRecord Run, string? Watermark)> DrainOnTheOldPathAsync(string startPosition)
    {
        var stateDirectory = Directory.CreateTempSubdirectory("dbdatasync-drain-old-state-").FullName;
        _directories.Add(stateDirectory);
        var stateDatabase = new StateDatabase(Path.Combine(stateDirectory, "state.db"));
        var runs = new TaskRunStore(stateDatabase);
        var queue = new WorkQueueStore(stateDatabase);
        var watermarks = new ChangeWatermarkStore(stateDatabase);
        var batches = new BulkLoadBatchStore(stateDatabase);
        var drivers = new DriverRegistry();
        drivers.Register(new MsSqlDriver());
        var executor = new RunExecutor(
            _config, drivers, _secrets, new LibraryRegistry(_repoRoot).LoadAll(), _repoRoot,
            new LocalRunnerState(runs, queue, new RunLockStore(stateDatabase), watermarks, new VerificationResultStore(stateDatabase), new LogWriter(stateDatabase), batches,
                new Lazy<IInitialLoadEnqueuer>(() => new RealInitialLoadEnqueuer(_config, queue, batches))),
            new LocalRunnerConfig(_config, Author), Scripting.ForTests(_config, _repoRoot), Path.Combine(stateDirectory, "state.db"));

        watermarks.SetWatermark("old-sync", "main", _watermarkKey, startPosition);
        watermarks.SetReadIntent("old-sync", "main", _watermarkKey, ReadIntent.Changes);
        var runId = queue.Enqueue("old-sync", RunKind.Primary, "main");
        await executor.ExecuteWorkerAsync("old-sync", WorkerLanes.Uniform(1), CancellationToken.None);
        return (runs.GetRun(runId)!, watermarks.GetWatermark("old-sync", "main", _watermarkKey));
    }

    /// <summary>A directory like the one a replication set is published from: the engine as the executable package's files, and <c>repo/</c> with the definition (<c>config/</c>) and the libraries the drivers load from.</summary>
    private async Task<string> PackageSourceAsync()
    {
        var source = Directory.CreateTempSubdirectory("dbdatasync-drain-package-").FullName;
        _directories.Add(source);
        var engine = EngineBuildOutput();
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source, "engine"), engine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopyDirectory(engine, Path.Combine(source, "engine"));
        }

        var repo = Directory.CreateDirectory(Path.Combine(source, "repo")).FullName;
        CopyDirectory(Path.Combine(_repoRoot, "config"), Path.Combine(repo, "config"));

        // the SQL Server client library, installed beside the definition the way an install does it, for the engine to load into its default context
        await BuiltInDriverLibraries.EnsureInstalledAsync(new LibraryRegistry(repo).LoadAll(), repo, DriverIds.MsSql, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(source, "package.yaml"), PackageManifestWriter.Write(RunnerRig.EngineManifest()));
        return source;
    }

    /// <summary>The engine as built: the executable's own output directory, with its runtime configuration beside it.</summary>
    private static string EngineBuildOutput()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DbDataSync.TaskRunner", "bin", configuration, framework);
            if (File.Exists(Path.Combine(candidate, "DbDataSync.TaskRunner.runtimeconfig.json"))) return candidate;
        }

        throw new InvalidOperationException("The engine's build output was not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>What the runner puts in the engine's environment so it can log in (ED11): one <c>DBDATA_SECRET_...</c> per connection of the replication.</summary>
    private IReadOnlyDictionary<string, string> Logins() =>
        new[] { "src-conn", "tgt-conn" }.Select(name => _config.LoadConnection(name).CredentialSecretRef!)
            .ToDictionary(reference => _secrets.EnvName(reference), reference => _secrets.Resolve(reference));

    private static string Describe(RunnerRig rig, TaskRunResult result) =>
        $"{result.End.State} (exit {result.ExitCode}); log:\n{rig.LogText(result.RunId)}";

    [Theory]
    [MemberData(nameof(RunnerEngines))]
    public async Task AMillionRowIncrementalDrain_OnTheRunner_EndsWhereTheOldPathEnds_AndTheSocketCarriesNoRows(string stateEngine)
    {
        var start = await CurrentVersionAsync();
        await InsertRowsAsync(1_000_000);

        var (oldRun, oldWatermark) = await DrainOnTheOldPathAsync(start);
        Assert.Equal(RunStatus.Succeeded, oldRun.Status);
        var expected = await TargetFingerprintAsync(_oldTarget);
        Assert.Equal(1_000_000, expected.Count);

        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(stateEngine, Logins());
        rig.SeedWatermark(_watermarkKey, start);
        var request = await rig.DrainRequestAsync(source, "runner-sync");

        var result = await rig.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded && result.ExitCode == 0, Describe(rig, result));
        Assert.Equal(expected, await TargetFingerprintAsync(_runnerTarget));
        Assert.Equal(oldWatermark, rig.Watermark(_watermarkKey)!.Value.Position);
        Assert.NotEqual(start, oldWatermark);

        // the runner's run row says what the old path's does: one run, the rows it moved, and the package it ran from
        var run = rig.Run(result.RunId);
        Assert.Equal(RunOutcome.Succeeded, run.Run.Outcome);
        Assert.Equal(oldRun.RowsWritten, run.Run.Rows);
        Assert.Equal(1_000_000, run.Run.Rows);
        Assert.Equal((RunnerRig.Package, "1.0.0", RunnerRig.Channel), (run.Package, run.PackageVersion, run.Channel));

        // ED5: the rows went from the source to the target and never through the runner; what crossed the socket is claims, positions, counts and log lines
        var traffic = Regex.Match(rig.LogText(result.RunId), @"runner socket traffic: sent (\d+) bytes, received (\d+) bytes");
        Assert.True(traffic.Success, "the engine did not report what crossed the socket. Log:\n" + rig.LogText(result.RunId));
        var bytes = long.Parse(traffic.Groups[1].Value, CultureInfo.InvariantCulture) + long.Parse(traffic.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.True(bytes < 1024 * 1024, $"{bytes} bytes crossed the runner's socket for 1,000,000 rows; the bound is 1 MiB.");
    }

    public static TheoryData<string> RunnerEngines => RunnerRig.Engines;

    [Fact]
    public async Task AFailingTarget_LeavesTheCheckpointWhereItWas_AndTheNextDrainDeliversTheChanges()
    {
        var start = await CurrentVersionAsync();
        await InsertRowsAsync(5);
        await ExecuteAsync(_admin, $"DROP TABLE dbo.[{_runnerTarget}];");

        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        rig.SeedWatermark(_watermarkKey, start);

        var failed = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-sync", chain: "0197f0e0-0000-7000-8000-00000000d0e4"));

        Assert.NotEqual(TaskEndState.Succeeded, failed.End.State);
        Assert.Equal("main", failed.End.FirstFailedItem);
        Assert.NotEqual(RunOutcome.Succeeded, rig.Run(failed.RunId).Run.Outcome);
        var held = rig.Watermark(_watermarkKey)!.Value;
        Assert.Equal((start, 1L), (held.Position, held.Version));

        // and the proof that it matters: with the target back, the next drain still delivers the changes the failed one read
        await CreateTargetAsync(_runnerTarget);
        var again = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-sync", chain: "0197f0e0-0000-7000-8000-00000000d0e5"));

        Assert.True(again.End.State == TaskEndState.Succeeded, Describe(rig, again));
        Assert.Equal(5, (await TargetFingerprintAsync(_runnerTarget)).Count);
        Assert.NotEqual(start, rig.Watermark(_watermarkKey)!.Value.Position);
    }

    [Fact]
    public async Task AMappingWithNoPosition_FailsNamingTheMemberItNeeds_AndDoesNotInventAPosition()
    {
        await InsertRowsAsync(3);
        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());

        var result = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-sync"));

        Assert.NotEqual(TaskEndState.Succeeded, result.End.State);
        Assert.Contains("IRunnerState.RequestInitialLoad", rig.LogText(result.RunId));
        Assert.Null(rig.Watermark(_watermarkKey));
        Assert.Equal(0, (await TargetFingerprintAsync(_runnerTarget)).Count);
    }
}
