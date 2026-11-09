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
using DbDataSync.Planning;
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
public sealed class DrainOnTheRunnerTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
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

    private async Task SaveReplicationAsync(string name, string targetTable, bool provision = false, SchedulingConfig? scheduling = null)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = scheduling ?? new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "*/5 * * * *" },
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
        if (provision) mapping.Provisioning = new ProvisioningConfig { CreateTargetTableIfMissing = true };
        else mapping.TargetColumns = await CachedColumnsAsync(targetTable);
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
    private async Task<(TaskRunRecord Run, string? Watermark, IReadOnlyList<DbDataKit.Scheduling.WorkItem> Rows)> DrainOnTheOldPathAsync(string startPosition, string replication = "old-sync")
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

        watermarks.SetWatermark(replication, "main", _watermarkKey, startPosition);
        watermarks.SetReadIntent(replication, "main", _watermarkKey, ReadIntent.Changes);

        // what the API's scheduler does at its tick: the planner decides what is due and queues it in Sync's own queue (the same library the drain on the runner calls)
        var planner = new ReplicationPlanner(
            _config, new LocalPlannerState(runs, queue, watermarks),
            new ChangePollingGate(new ChangeSourceResolver(_config, drivers), new NoCounter(), watermarks, new ChangeCheckStore(stateDatabase), Microsoft.Extensions.Logging.Abstractions.NullLogger<ChangePollingGate>.Instance),
            drivers, new NoReconcile(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.True(await planner.PlanAsync(replication, _config.LoadReplicationTask(replication), ["main"], CancellationToken.None));
        await executor.ExecuteWorkerAsync(replication, WorkerLanes.Uniform(1), CancellationToken.None);
        var rows = new DbDataKit.Scheduling.ModuleWorkQueueStore(stateDatabase, WorkQueueStore.Service).ListAll(replication);
        var primary = Assert.Single(rows, r => r.Kind == nameof(RunKind.Primary));
        return (runs.GetRun(primary.RunId)!, watermarks.GetWatermark(replication, "main", _watermarkKey), rows);
    }

    /// <summary>A source that has no counter to state (the gate admits what it cannot compare), so a tick queues what the schedule says.</summary>
    private sealed class NoCounter : IChangeCounterSource
    {
        public Task<ChangeCounterReading> FetchAsync(string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken) => Task.FromResult(new ChangeCounterReading(null));

        public Task<DateTimeOffset?> MapSourceTimeAsync(string connectionName, string sourceDatabase, string readerKind, string value, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class NoReconcile : IScheduledReconcileEnqueuer
    {
        public Task<ScheduledReconcileResult> EnqueueScheduledAsync(string replication, string mapping, CancellationToken cancellationToken) => throw new InvalidOperationException("no reconcile is configured");
    }

    /// <summary>What a queue holds of one unit of work, without the ids and the owner: the rows two queues are compared by.</summary>
    private static IEnumerable<(string Kind, string Subject, string Part, string Resource, string Lane, string Status, string? Payload)> Shapes(IEnumerable<DbDataKit.Scheduling.WorkItem> rows) =>
        rows.Select(r => (r.Kind, r.Subject, r.Part, r.Resource, r.Lane, r.Status.ToString(), r.PayloadJson));

    /// <summary>A directory like the one a replication set is published from: the engine as the executable package's files, and <c>repo/</c> with the definition (<c>config/</c>) and the libraries the drivers load from.</summary>
    private async Task<string> PackageSourceAsync(bool withEngine = true)
    {
        var source = Directory.CreateTempSubdirectory("dbdatasync-drain-package-").FullName;
        _directories.Add(source);
        if (withEngine)
        {
            var engine = EngineBuildOutput();
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(source, "engine"), engine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CopyDirectory(engine, Path.Combine(source, "engine"));
            }
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

        var (oldRun, oldWatermark, oldRows) = await DrainOnTheOldPathAsync(start);
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

        // the queue holds what the supervisor path's holds: one primary pass of the mapping, finished, in the same lane with the same payload; and the pass is a unit of the run with the counts of the old run
        Assert.Equal(Shapes(oldRows), Shapes(rig.Work.ListAll(RunnerRig.WorkOwner)));
        var unit = Assert.Single(new DbDataKit.State.UnitStore(rig.Database, new DbDataKit.State.ProgressKey(RunnerRig.Package, RunnerRig.Channel, "drain")).List(RunnerRig.Chain));
        Assert.Equal(("mapping", "completed"), (unit.Name, unit.State));
        Assert.StartsWith("main#", unit.Key, StringComparison.Ordinal);
        Assert.Equal((oldRun.RowsRead, oldRun.RowsWritten), (unit.Counts["rows_read"], unit.Counts["rows"]));

        // ED5: the rows went from the source to the target and never through the runner; what crossed the socket is claims, positions, counts and log lines
        var traffic = Regex.Match(rig.LogText(result.RunId), @"runner socket traffic: sent (\d+) bytes, received (\d+) bytes");
        Assert.True(traffic.Success, "the engine did not report what crossed the socket. Log:\n" + rig.LogText(result.RunId));
        var bytes = long.Parse(traffic.Groups[1].Value, CultureInfo.InvariantCulture) + long.Parse(traffic.Groups[2].Value, CultureInfo.InvariantCulture);
        output.WriteLine($"{stateEngine}: {bytes} bytes crossed the runner's socket for {run.Run.Rows} rows ({traffic.Value}).");
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
        Assert.StartsWith("main#", failed.End.FirstFailedItem, StringComparison.Ordinal);      // the unit of the pass: the mapping and the claim
        Assert.Equal("Failed", Assert.Single(rig.Work.ListAll(RunnerRig.WorkOwner)).Status.ToString());
        Assert.NotEqual(RunOutcome.Succeeded, rig.Run(failed.RunId).Run.Outcome);
        var held = rig.Watermark(_watermarkKey)!.Value;
        Assert.Equal((start, 1L), (held.Position, held.Version));

        // and the proof that it matters: with the target back, the next drain still delivers the changes the failed one read
        await CreateTargetAsync(_runnerTarget);
        // ("Run now": a cron's next occurrence is not for minutes, and the planner would say nothing is due)
        var again = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-sync", chain: "0197f0e0-0000-7000-8000-00000000d0e5", runAll: true));

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
        Assert.True(rig.LogText(result.RunId).Contains("IRunnerState.RequestInitialLoad", StringComparison.Ordinal), Describe(rig, result));
        Assert.Null(rig.Watermark(_watermarkKey));
        Assert.Equal(0, (await TargetFingerprintAsync(_runnerTarget)).Count);
    }

    /// <summary>The names and contents of everything under a directory, to say afterwards that nothing in it was written, added or removed.</summary>
    private static SortedDictionary<string, string> Snapshot(string directory) =>
        new(Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).ToDictionary(
            entry => Path.GetRelativePath(directory, entry),
            entry => File.Exists(entry) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry))) : "<directory>"));

    [Fact]
    public async Task AProvisioningReport_IsARunOutput_NotAWriteIntoTheView_AndNothingInTheViewChanges()
    {
        // plan 28c ED8 and ED9: the target table does not exist and the mapping has no cached target columns, so the pass creates the table and reports its columns; the definition is a read-only view
        var provisioned = $"Provisioned_{Guid.NewGuid():N}";
        await SaveReplicationAsync("provision-sync", provisioned, provision: true);
        var start = await CurrentVersionAsync();
        await InsertRowsAsync(7);
        await ExecuteAsync(_admin, $"DROP TABLE IF EXISTS dbo.[{provisioned}];");

        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        rig.SeedWatermark(_watermarkKey, start);
        var request = await rig.DrainRequestAsync(source, "provision-sync");
        var before = Snapshot(request.PackageDirectory!);

        var result = await rig.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded && result.ExitCode == 0, Describe(rig, result));
        Assert.Equal(7, (await TargetFingerprintAsync(provisioned)).Count);

        // no file of the view was written, created or removed (the view's modes forbid it for a user that is not root; this holds for root too)
        Assert.Equal(before, Snapshot(request.PackageDirectory!));
        Assert.DoesNotContain(before.Keys, k => k.Contains(".git", StringComparison.Ordinal) || k.EndsWith("secrets", StringComparison.Ordinal) || k.EndsWith(".gitignore", StringComparison.Ordinal));

        // the report is a step of the run: its result carries the columns the target was created with
        var steps = new DbDataKit.State.StepStore(rig.Database, new DbDataKit.State.ProgressKey(RunnerRig.Package, RunnerRig.Channel, "drain")).List(RunnerRig.Chain);
        var step = Assert.Single(steps, s => s.Name == "provisioned-columns");
        Assert.Equal(DbDataKit.State.StepState.Completed, step.State);
        var report = System.Text.Json.JsonSerializer.Deserialize<ProvisionedColumnsReport>(step.ResultJson!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(("provision-sync", "main"), (report.Replication, report.Mapping));
        Assert.Equal(["Id", "Name"], report.Columns.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ADefinitionPackage_RunsOnTheRuntimePackage_FromTwoReadOnlyViews_AndWritesIntoNeither()
    {
        // plan 28c E06: the engine is the runtime package (files/bin of publish-runtime.sh, here the build output of the same project), the definition holds only config/ and libraries/
        var start = await CurrentVersionAsync();
        await InsertRowsAsync(25);
        var runtimeSource = Directory.CreateTempSubdirectory("dbdatasync-runtime-package-").FullName;
        _directories.Add(runtimeSource);
        CopyDirectory(EngineBuildOutput(), Path.Combine(runtimeSource, "bin"));
        var source = await PackageSourceAsync(withEngine: false);
        Assert.False(Directory.Exists(Path.Combine(source, "engine")));
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        rig.SeedWatermark(_watermarkKey, start);
        var request = await rig.DrainRequestAsync(source, "runner-sync", runtimeSource: runtimeSource);
        var definitionBefore = Snapshot(request.PackageDirectory!);
        var runtimeBefore = Snapshot(request.RuntimeDirectory!);

        var result = await rig.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded && result.ExitCode == 0, Describe(rig, result));
        Assert.Equal(25, (await TargetFingerprintAsync(_runnerTarget)).Count);
        Assert.Equal(definitionBefore, Snapshot(request.PackageDirectory!));
        Assert.Equal(runtimeBefore, Snapshot(request.RuntimeDirectory!));
    }

    // ---- plan 28c E11: the planner and the engine's loop on the runner

    private static readonly SchedulingConfig Live = new() { Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 3 };

    [Fact]
    public async Task AContinuousReplication_OnTheRunner_PlansClaimsRunsAndFinishes_LikeTheSupervisorPath_StaysUpBetweenPasses_AndEndsAtItsIdleTimeout()
    {
        await SaveReplicationAsync("old-live", _oldTarget, scheduling: Live);
        await SaveReplicationAsync("runner-live", _runnerTarget, scheduling: Live);
        var start = await CurrentVersionAsync();
        await InsertRowsAsync(40);

        var (oldRun, oldWatermark, oldRows) = await DrainOnTheOldPathAsync(start, "old-live");
        Assert.Equal(40, oldRun.RowsWritten);

        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        rig.SeedWatermark(_watermarkKey, start);
        var drain = rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-live"));

        // the first cycle plans the mapping (never queued: due; the source's counter is ahead of its position, so the gate admits it), claims it, runs it and finishes it; the drain stays up
        await UntilAsync(() => rig.Work.ListAll(RunnerRig.WorkOwner).Count(r => r.Status == DbDataKit.Scheduling.WorkItemStatus.Done) == 1, "the first pass to be done", drain);
        Assert.False(drain.IsCompleted, "a continuous drain stays up between passes");

        // rows that arrive while it is up are read by the next due pass (one second later, through the gate: the counter has moved)
        await InsertRowsAsync(10, from: 41);
        var result = await drain;

        Assert.True(result.End.State == TaskEndState.Succeeded && result.ExitCode == 0, Describe(rig, result));
        Assert.True(result.Duration >= TimeSpan.FromSeconds(3), $"the drain ended after {result.Duration}, before the idle timeout of 3 s");
        Assert.Equal(50, (await TargetFingerprintAsync(_runnerTarget)).Count);
        var rows = rig.Work.ListAll(RunnerRig.WorkOwner);
        Assert.All(rows, r => Assert.Equal(DbDataKit.Scheduling.WorkItemStatus.Done, r.Status));
        Assert.True(rows.Count >= 2, $"{rows.Count} pass(es) were queued; the rows inserted while the drain was up need a second");
        // the first pass is the one the supervisor path queued and ran: same keys, lane and payload, same rows
        Assert.Equal(Shapes(oldRows).Single(), Shapes([rows[0]]).Single());
        var units = new DbDataKit.State.UnitStore(rig.Database, new DbDataKit.State.ProgressKey(RunnerRig.Package, RunnerRig.Channel, "drain")).List(RunnerRig.Chain).OrderBy(u => u.StartedAt).ToList();
        Assert.Equal(oldRun.RowsWritten, units[0].Counts["rows"]);
        Assert.Equal(rows.Count, units.Count);
        Assert.All(units, u => Assert.Equal("completed", u.State));
        Assert.Equal(oldRun.RowsWritten + 10, units.Sum(u => u.Counts["rows"]));
        Assert.NotEqual(oldWatermark, rig.Watermark(_watermarkKey)!.Value.Position);
    }

    /// <summary>A mapping of its own over a source table with Change Tracking and a target table, for a replication that has several; its position key and both table names.</summary>
    private async Task<(string Source, string Target, string Key, string Start)> AddMappingAsync(string replication, string mapping)
    {
        var source = $"Src_{mapping}_{Guid.NewGuid():N}";
        var target = $"Tgt_{mapping}_{Guid.NewGuid():N}";
        await ExecuteAsync(_admin, $"CREATE TABLE dbo.[{source}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(_admin, $"ALTER TABLE dbo.[{source}] ENABLE CHANGE_TRACKING;");
        var start = await CurrentVersionAsync();      // a table's history begins when its tracking was enabled: a position from before is not covered
        await CreateTargetAsync(target);
        var mappingConfig = new TableMappingConfig
        {
            Name = mapping,
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = source }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = target }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }, new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" }],
            SourceColumns = await CachedColumnsAsync(source),
            TargetColumns = await CachedColumnsAsync(target),
        };
        _config.SaveTableMapping(replication, mappingConfig, Author);
        return (source, target, WatermarkKey.Build(new SourceTableRef { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = source }, MsSqlDialect.Instance), start);
    }

    private Task InsertRowsIntoAsync(string table, int count) => ExecuteAsync(_admin, $"""
        INSERT INTO dbo.[{table}] (Id, Name)
        SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), CONCAT(N'row-', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
        FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
        """);

    [Fact]
    public async Task ADrainTheRunnerCancels_FinishesTheItemsInFlight_ClaimsNothingMore_AndTheNextDrainRunsWhatWasLeft()
    {
        await SaveReplicationAsync("runner-multi", _runnerTarget);
        var start = await CurrentVersionAsync();
        var mappings = new List<(string Source, string Target, string Key, string Start)>();
        foreach (var name in new[] { "m1", "m2", "m3" })
        {
            var added = await AddMappingAsync("runner-multi", name);
            await InsertRowsIntoAsync(added.Source, 300_000);
            mappings.Add(added);
        }

        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        rig.SeedWatermark(_watermarkKey, start);
        foreach (var added in mappings) rig.SeedWatermark(added.Key, added.Start);
        using var cts = new CancellationTokenSource();
        var drain = rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-multi", degreeOfParallelism: 2), cts.Token);

        // two passes are running (two slots); the runner is asked to stop now
        await UntilAsync(() => rig.Work.ListAll(RunnerRig.WorkOwner).Count(r => r.Status == DbDataKit.Scheduling.WorkItemStatus.Running) == 2, "two passes to be running", drain);
        await cts.CancelAsync();
        var stopped = await drain;

        output.WriteLine($"stopped: {stopped.End.State}, exit {stopped.ExitCode}, killed {stopped.KilledByExecutor}; {rig.LogText(stopped.RunId)}");
        Assert.NotEqual(TaskEndState.Succeeded, stopped.End.State);
        Assert.False(stopped.KilledByExecutor, "the passes in flight finish within the grace period; the process is not killed");
        var rows = rig.Work.ListAll(RunnerRig.WorkOwner);
        Assert.DoesNotContain(rows, r => r.Status is DbDataKit.Scheduling.WorkItemStatus.Claimed or DbDataKit.Scheduling.WorkItemStatus.Running);
        Assert.Equal(2, rows.Count(r => r.Status == DbDataKit.Scheduling.WorkItemStatus.Done));      // the two that were in flight ran to their end
        Assert.Equal(2, rows.Count(r => r.Status == DbDataKit.Scheduling.WorkItemStatus.Pending));   // and nothing more was claimed: the rest is waiting, not failed
        Assert.DoesNotContain(rows, r => r.Status == DbDataKit.Scheduling.WorkItemStatus.Failed);

        // nothing is due for the planner (the cron's next occurrence is minutes away), but the queue still holds the work: the next drain claims it
        var rest = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-multi", degreeOfParallelism: 2, chain: "0197f0e0-0000-7000-8000-00000000d0e6"));

        Assert.True(rest.End.State == TaskEndState.Succeeded && rest.ExitCode == 0, Describe(rig, rest));
        Assert.All(rig.Work.ListAll(RunnerRig.WorkOwner), r => Assert.Equal(DbDataKit.Scheduling.WorkItemStatus.Done, r.Status));
        foreach (var added in mappings)
            Assert.Equal(300_000, (await TargetFingerprintAsync(added.Target)).Count);
    }

    [Fact]
    public async Task ADrainStoppedAfterItsGracePeriod_GivesItsClaimBack_AndTheNextDrainFinishesThePass()
    {
        var start = await CurrentVersionAsync();
        await InsertRowsAsync(1_000_000);
        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        rig.SeedWatermark(_watermarkKey, start);
        using var cts = new CancellationTokenSource();
        var drain = rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-sync", cancellationGrace: TimeSpan.FromSeconds(1)), cts.Token);

        await UntilAsync(() => rig.Work.ListAll(RunnerRig.WorkOwner).Any(r => r.Status == DbDataKit.Scheduling.WorkItemStatus.Running), "the pass to be running", drain);
        await cts.CancelAsync();
        var killed = await drain;

        // the pass did not end within the grace period of one second: the task ended the run itself and its process went (or the runner killed it), and the claim the process held went back with the run
        Assert.NotEqual(TaskEndState.Succeeded, killed.End.State);
        var held = Assert.Single(rig.Work.ListAll(RunnerRig.WorkOwner));
        Assert.Equal(DbDataKit.Scheduling.WorkItemStatus.Pending, held.Status);
        Assert.Equal(start, rig.Watermark(_watermarkKey)!.Value.Position);      // the position is still the one before the pass: its commit was never made

        var again = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-sync", chain: "0197f0e0-0000-7000-8000-00000000d0e7"));

        Assert.True(again.End.State == TaskEndState.Succeeded && again.ExitCode == 0, Describe(rig, again));
        Assert.Equal(DbDataKit.Scheduling.WorkItemStatus.Done, Assert.Single(rig.Work.ListAll(RunnerRig.WorkOwner)).Status);
        Assert.Equal(1_000_000, (await TargetFingerprintAsync(_runnerTarget)).Count);
    }

    [Fact]
    public async Task ABulkLoadItemInTheRunnersQueue_IsClaimedAndRunByTheDrain_AndTheSocketStaysUnderItsBound()
    {
        // plan 28c E03's bound with the unit of a bulk load: a full reload of a million rows is an item queued for the replication (what E12's reload request will do)
        var start = await CurrentVersionAsync();
        await InsertRowsAsync(1_000_000);
        var now = await CurrentVersionAsync();
        var source = await PackageSourceAsync();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, Logins());
        rig.SeedWatermark(_watermarkKey, now);
        var full = new FullSegment();
        var shape = WorkQueueStore.Describe("runner-sync", RunKind.BulkLoad, "main", full.Describe(), SegmentSerializer.Serialize(full), WorkItemKinds.FromConfig);
        rig.Work.Enqueue(RunnerRig.WorkOwner, shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload);

        var result = await rig.Executor.RunAsync(await rig.DrainRequestAsync(source, "runner-sync"));

        Assert.True(result.End.State == TaskEndState.Succeeded && result.ExitCode == 0, Describe(rig, result));
        Assert.Equal(1_000_000, (await TargetFingerprintAsync(_runnerTarget)).Count);
        var rows = rig.Work.ListAll(RunnerRig.WorkOwner);
        // the planner queued no scheduled pass: the cron fired, but the mapping's position is the source's counter (nothing was written since), so the gate left it out, as it does on the API's scheduler
        var bulk = Assert.Single(rows);
        Assert.Equal(("BulkLoad", DbDataKit.Scheduling.WorkItemStatus.Done), (bulk.Kind, bulk.Status));
        var traffic = Regex.Match(rig.LogText(result.RunId), @"runner socket traffic: sent (\d+) bytes, received (\d+) bytes");
        Assert.True(traffic.Success, "the engine did not report what crossed the socket. Log:\n" + rig.LogText(result.RunId));
        var bytes = long.Parse(traffic.Groups[1].Value, CultureInfo.InvariantCulture) + long.Parse(traffic.Groups[2].Value, CultureInfo.InvariantCulture);
        output.WriteLine($"{bytes} bytes crossed the runner's socket for a bulk load of 1,000,000 rows ({traffic.Value}); start {start}.");
        Assert.True(bytes < 1024 * 1024, $"{bytes} bytes crossed the runner's socket for a bulk load of 1,000,000 rows; the bound is 1 MiB.");
    }

    private static async Task UntilAsync(Func<bool> condition, string what, Task<TaskRunResult>? running = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (!condition())
        {
            if (running is { IsCompleted: true }) throw new Xunit.Sdk.XunitException($"The run ended before {what}.");
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(100);
        }
    }
}
