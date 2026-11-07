using System.Data.Common;
using System.Security.Cryptography;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol.Server;
using DbDataSync.TaskRunner.Runner;
using Microsoft.Data.SqlClient;
using Npgsql;
using KitDatabase = DbDataKit.State.StateDatabase;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>A directory of files as an artifact source: what a published version is, for these tests (the files are listed by path and hash, and opened by path).</summary>
internal sealed class DirectorySource(string directory) : IArtifactSource
{
    public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default)
    {
        var files = new List<ArtifactFile>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            using var stream = File.OpenRead(path);
            files.Add(new ArtifactFile(relative, Convert.ToHexStringLower(SHA256.HashData(stream)), new FileInfo(path).Length));
        }

        return Task.FromResult<IReadOnlyList<ArtifactFile>>(files);
    }

    public Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(File.OpenRead(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar))));
}

/// <summary>
/// A runner in a test (plan 28c E03): a state database of the runner's modules on SQLite and, when the shared engines are up (<c>DBDATA_TEST_SQLSERVER</c>, <c>DBDATA_TEST_POSTGRES</c> from
/// <c>testenv.sh env dbdatakit</c>), on SQL Server and PostgreSQL; the protocol host on a real socket; and the executor that starts the engine as the process of an executable package. What is real is what the
/// runner does for a task: the token, the scratch directory, the environment, the read-only view of the package and the protocol; what is not is the scheduler, which E08 and E11 move.
/// </summary>
internal sealed class RunnerRig : IDisposable
{
    public const string Package = "sync-e03";
    public const string Channel = "prod";
    public const string Chain = "0197f0e0-0000-7000-8000-00000000d0e3";
    private static readonly object Registration = new();
    private readonly List<Action> _cleanups = [];

    private RunnerRig(KitDatabase database, RunnerProtocolHost host, TaskExecutor executor, string dataDirectory)
    {
        Database = database;
        Host = host;
        Executor = executor;
        DataDirectory = dataDirectory;
    }

    public KitDatabase Database { get; }

    public RunnerProtocolHost Host { get; }

    public TaskExecutor Executor { get; }

    public string DataDirectory { get; }

    public RunStore Runs => new(Database, "runner");

    public RunnerEnvironment Environment => new(Database, Package, Channel);

    /// <summary>The engines this machine has: SQLite always, the others when their variable is set.</summary>
    public static TheoryData<string> Engines
    {
        get
        {
            var engines = new TheoryData<string> { DbDataKit.State.StateEngineIds.Sqlite };
            if (Server(DbDataKit.State.StateEngineIds.MsSql) is not null) engines.Add(DbDataKit.State.StateEngineIds.MsSql);
            if (Server(DbDataKit.State.StateEngineIds.Postgres) is not null) engines.Add(DbDataKit.State.StateEngineIds.Postgres);
            return engines;
        }
    }

    private static string? Server(string engine) => engine switch
    {
        DbDataKit.State.StateEngineIds.MsSql => System.Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER"),
        DbDataKit.State.StateEngineIds.Postgres => System.Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES"),
        _ => null,
    };

    private static DbProviderFactory Factory(string libraryId) => libraryId switch
    {
        DbDataKit.State.MsSqlStateDialect.LibraryId => SqlClientFactory.Instance,
        DbDataKit.State.PostgresStateDialect.LibraryId => NpgsqlFactory.Instance,
        _ => throw new InvalidOperationException($"no provider for '{libraryId}'"),
    };

    /// <param name="engine">The runner's state engine.</param>
    /// <param name="taskEnvironment">What the executor adds to the engine process's environment (the logins, <c>DBDATA_SECRET_...</c>, as ED11 has them); never argv.</param>
    public static async Task<RunnerRig> StartAsync(string engine, IReadOnlyDictionary<string, string> taskEnvironment)
    {
        var cleanups = new List<Action>();
        var layout = DbDataKit.State.StateLayout.Create(
            engine,
            [DbDataKit.State.ProgressModule.Module, DbDataKit.State.RunsModule.Module, DbDataKit.Scheduling.SchedulingModule.Module, DbDataKit.State.RunnerModule.Module, DbDataKit.State.DefinitionsModule.Module],
            DbDataKit.State.StateScope.Standalone);
        KitDatabase database;
        if (engine == DbDataKit.State.StateEngineIds.Sqlite)
        {
            var directory = Directory.CreateTempSubdirectory("dbdatasync-e03-state-");
            cleanups.Add(() => Directory.Delete(directory.FullName, recursive: true));
            database = new KitDatabase(Path.Combine(directory.FullName, "state.db"), layout, null, null);
        }
        else
        {
            var server = Server(engine) ?? throw new InvalidOperationException($"the state engine {engine} needs its DBDATA_TEST_* variable");
            lock (Registration) DbDataKit.State.StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
            var name = $"dbdatasync_e03_{Guid.NewGuid():N}";
            ExecuteOn(engine, server, engine == DbDataKit.State.StateEngineIds.MsSql ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
            cleanups.Add(() =>
            {
                if (engine == DbDataKit.State.StateEngineIds.MsSql)
                {
                    SqlConnection.ClearAllPools();
                    ExecuteOn(engine, server, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
                }
                else
                {
                    NpgsqlConnection.ClearAllPools();
                    ExecuteOn(engine, server, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
                }
            });
            var connectionString = engine == DbDataKit.State.StateEngineIds.MsSql
                ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString
                : new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
            database = new KitDatabase(engine, connectionString, layout, null, null);
        }

        var data = Directory.CreateTempSubdirectory("dbdatasync-e03-data-").FullName;
        cleanups.Add(() => Directory.Delete(data, recursive: true));
        var host = await RunnerProtocolHost.StartAsync();
        var executor = new TaskExecutor(database, host, new TaskExecutorOptions
        {
            DataDirectory = data,
            DefaultMemoryMb = null,
            DefaultTimeout = TimeSpan.FromMinutes(20),
            ExtraEnvironment = taskEnvironment,
        });
        var rig = new RunnerRig(database, host, executor, data);
        rig._cleanups.AddRange(cleanups);
        return rig;
    }

    private static void ExecuteOn(string engine, string connectionString, string sql)
    {
        lock (Registration) DbDataKit.State.StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
        using var connection = DbDataKit.State.StateDialect.For(engine).CreateConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A request for the task <c>drain</c> of a package published from <paramref name="source"/> (a directory with <c>engine/</c> and <c>repo/</c>): the files are materialized through the artifact cache into the
    /// read-only view the task runs from, like a published version. The view is made of copies, not links: the .NET host resolves a link to the file it points at and looks for the runtime configuration and the
    /// dependencies beside <i>that</i>, so <c>dotnet engine.dll</c> does not start from a view of links (what Windows gets anyway; plan 28c E04 and E05 meet it again).
    /// </summary>
    public async Task<TaskRunRequest> DrainRequestAsync(string source, string replication, int degreeOfParallelism = 1, Guid? runId = null, string chain = Chain)
    {
        var cacheRoot = Directory.CreateTempSubdirectory("dbdatasync-e03-cache-").FullName;
        _cleanups.Add(() => Rewritable(cacheRoot));
        var version = await new ArtifactCache(Path.Combine(cacheRoot, "cache"), new DirectorySource(source), ArtifactLinkMode.Copy).MaterializeAsync("sync-e03@1");
        return new TaskRunRequest
        {
            Task = new TaskRef(Package, Channel, "drain"),
            PackageDirectory = version.Directory,
            PackageVersion = "1.0.0",
            Launch = new PackageLaunch(LaunchKind.Executable, Command: "dotnet", Args: ["{package}/engine/DbDataSync.TaskRunner.dll", "--runner"]),
            RunId = runId ?? Database.NewId(),
            Chain = chain,
            Inputs = new Dictionary<string, string>
            {
                ["replication"] = replication,
                ["degree_of_parallelism"] = degreeOfParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    /// <summary>
    /// The manifest of the engine's package: generated from the job's types (28b K8), launched as an executable, so the tasks name no type (the engine's own entry starts the one the runner asks for). The
    /// schedule is the publisher's; none here, the tests run the task by hand.
    /// </summary>
    public static PackageManifest EngineManifest()
    {
        var manifest = DbDataKit.Runner.Kit.ManifestGenerator.ForAssembly(
            typeof(DrainJob).Assembly, "sync-e03", new PackageLaunch(LaunchKind.Executable, Command: "dotnet", Args: ["{package}/engine/DbDataSync.TaskRunner.dll", "--runner"]));
        return manifest with { Tasks = [.. manifest.Tasks.Where(t => t.Name == "drain").Select(t => t with { Entry = new TaskEntry() })] };
    }

    /// <summary>The checkpoint of a mapping as the runner's progress store has it now.</summary>
    public (string Position, DateTimeOffset? Time, long Version)? Watermark(string key)
    {
        var checkpoint = new DbDataKit.State.ProgressStore(Database, new DbDataKit.State.ProgressKey(Package, Channel, "drain")).GetCheckpoint("watermark", key);
        if (checkpoint is null || !checkpoint.Parts.TryGetValue("position", out var position)) return null;
        DateTimeOffset? time = checkpoint.Parts.TryGetValue("position_time", out var at) ? at.AsTime : null;
        return (position.AsText, time, checkpoint.Version);
    }

    /// <summary>Stores a position for a mapping, as a publisher or an administrator would before the first drain (the engine cannot start a replication that has none: its initial load is not on the runner yet).</summary>
    public void SeedWatermark(string key, string position)
    {
        var store = new DbDataKit.State.ProgressStore(Database, new DbDataKit.State.ProgressKey(Package, Channel, "drain"));
        var parts = new Dictionary<string, DbDataKit.State.ProgressValue> { ["position"] = DbDataKit.State.ProgressValue.OfText(position) };
        if (!store.Advance("watermark", parts, expectedVersion: 0, itemKey: key, actor: "test"))
            throw new InvalidOperationException($"The checkpoint for '{key}' was already there.");
    }

    public StoredRun Run(Guid runId) => Runs.Get(runId) ?? throw new InvalidOperationException($"No run {runId}.");

    public string LogText(Guid runId) => string.Join("\n", Runs.Logs(runId, limit: 10_000).Select(l => $"[{l.Level}] {l.Message}"));

    private static void Rewritable(string directory)
    {
        // a view's directories are read-only (0555): make them writable to delete them
        if (!OperatingSystem.IsWindows())
            foreach (var d in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Prepend(directory))
                File.SetUnixFileMode(d, (UnixFileMode)0b111_111_101);
        Directory.Delete(directory, recursive: true);
    }

    public void Dispose()
    {
        Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var cleanup in Enumerable.Reverse(_cleanups))
        {
            try { cleanup(); }
            catch (Exception) { /* a scratch directory or database left behind is untidy; its name carries a GUID */ }
        }
    }
}
