using DbDataKit.Connections;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Connections;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataKit.Runner.Execution.Tests;

/// <summary>
/// Plan 28c E02 with the real executor and real processes: a task's connection slots are bound per channel, a login the manifest asks to have injected is in the child's environment and nowhere else, and an unbound or
/// unresolvable slot fails the run before a process exists, naming the slot.
/// </summary>
public sealed class ConnectionSlotTests : IDisposable
{
    private const string Package = "scripts";
    private const string SrcSecret = "src-pw-7f3a9c1e";
    private const string DstRead = "dst-read-pw-51d2b8";
    private const string DstWrite = "dst-write-pw-c04e77";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatakit-slot-exec-").FullName;

    public static TheoryData<string> Engines => ExecutionEngines.Engines;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }

    private static ConnectionDefinition Postgres(string name, string password) => new()
    {
        Name = name,
        Engine = "postgres",
        Address = new AddressSpec { Host = "db.example", Database = "d" },
        Auth = new LoginSpec { Mode = AuthMode.Sql, User = "svc_run", PasswordSecret = "env:" + password },
    };

    private static ConnectionDefinition Catalogued(string name) => name switch
    {
        "src_db" => Postgres("src_db", "SRC_PW"),
        "a_db" => Postgres("a_db", "A_PW"),
        "b_db" => Postgres("b_db", "B_PW"),
        "dst_db" => new ConnectionDefinition
        {
            Name = "dst_db",
            Engine = "sqlserver",
            Address = new AddressSpec { Host = "wh.example", Database = "w" },
            ReadLogin = new LoginSpec { Mode = AuthMode.Sql, User = "svc_read", PasswordSecret = "env:DST_READ_PW" },
            WriteLogin = new LoginSpec { Mode = AuthMode.Sql, User = "svc_write", PasswordSecret = "env:DST_WRITE_PW" },
        },
        "ro_db" => new ConnectionDefinition
        {
            Name = "ro_db",
            Engine = "sqlserver",
            Access = AccessMode.Readonly,
            Address = new AddressSpec { Host = "wh.example", Database = "w" },
            Auth = new LoginSpec { Mode = AuthMode.Sql, User = "svc_read", PasswordSecret = "env:DST_READ_PW" },
        },
        _ => throw new ArgumentException(name),
    };

    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["SRC_PW"] = SrcSecret,
        ["A_PW"] = "a-pw-aaaa1111",
        ["B_PW"] = "b-pw-bbbb2222",
        ["DST_READ_PW"] = DstRead,
        ["DST_WRITE_PW"] = DstWrite,
    };

    private sealed class Rig(StateDatabase database, IRedactor redactor, IDictionary<string, string>? secrets = null)
    {
        public DefinitionPublisher Publisher { get; } = new(database, RunnerPackages.Kinds(), new PayloadStore(database), "runner");

        public RunnerSlotResolver Resolver { get; } = new(
            new DefinitionPublisher(database, RunnerPackages.Kinds(), new PayloadStore(database), "runner"),
            new InMemoryConnectionCatalog(new[] { "src_db", "a_db", "b_db", "dst_db", "ro_db" }.Select(Catalogued)),
            new SecretResolverChain([new EnvSecretProvider(n => (secrets ?? Secrets).TryGetValue(n, out var v) ? v : null)]),
            redactor);

        public void Bind(string channel, string slot, string connection) => Publisher.Bind(channel, "integration", Package, slot, connection, "test");
    }

    private static Rig RigFor(ExecutorHarness h, IDictionary<string, string>? secrets = null) => new(h.Database, h.Redactor, secrets);

    private static ExecutorHarness Harness(string engine, Action<ExecutorHarness.ExecutorSettings>? configure = null) =>
        new(engine, s =>
        {
            s.Slots = (db, redactor) => new Rig(db, redactor).Resolver;
            configure?.Invoke(s);
        });

    // ---- the child's environment

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_slots_give_the_child_each_login_in_its_environment_and_nowhere_else(string engine)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        using var h = Harness(engine);
        var rig = RigFor(h);
        rig.Bind("prod", "src", "src_db");
        rig.Bind("prod", "dst", "dst_db");
        ConnectionSlot[] slots = [new("src", "postgres", SlotEnvironmentFormats.SyncSecret), new("dst", "sqlserver", SlotEnvironmentFormats.BuildLogin)];
        var request = (await h.ScriptRequest("slots-hold.sh")) with { ConnectionHandles = rig.Resolver.Handles(new TaskRef(Package, "prod", "run"), slots), Slots = slots, CancellationGrace = TimeSpan.FromMilliseconds(300) };
        using var cancel = new CancellationTokenSource();
        var run = h.Executor.RunAsync(request, cancel.Token);
        await h.WaitForLog(request.RunId!.Value, "ready");
        var pid = await h.PidOf(request.RunId.Value);

        var environ = (await File.ReadAllTextAsync($"/proc/{pid}/environ")).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
        Assert.Equal(SrcSecret, environ["DBDATA_SECRET_DBDATA_SYNC_CONNECTION_SRC_DB"]);
        Assert.Equal(DstRead, environ["DBDATA_BUILD_DST_DB_READ"]);
        Assert.Equal(DstWrite, environ["DBDATA_BUILD_DST_DB_WRITE"]);
        // Sync's second login (plan 28c E20): the runner's one login for the connection is given under the plain name and under the run name, and nothing else
        Assert.Equal(SrcSecret, environ["DBDATA_SECRET_DBDATA_SYNC_CONNECTION_SRC_DB_RUN"]);
        Assert.Equal(["DBDATA_BUILD_DST_DB_READ", "DBDATA_BUILD_DST_DB_WRITE", "DBDATA_SECRET_DBDATA_SYNC_CONNECTION_SRC_DB", "DBDATA_SECRET_DBDATA_SYNC_CONNECTION_SRC_DB_RUN"], environ.Keys.Where(k => k.StartsWith("DBDATA_SECRET_", StringComparison.Ordinal) || k.StartsWith("DBDATA_BUILD_", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        // not in argv, not in any file of the scratch directory or the package
        var cmdline = await File.ReadAllTextAsync($"/proc/{pid}/cmdline");
        var scratch = Path.Combine(h.ScratchRoot, request.RunId.Value.ToString("N"));
        var scanned = 0;
        foreach (var secret in new[] { SrcSecret, DstRead, DstWrite })
        {
            Assert.DoesNotContain(secret, cmdline);
            foreach (var file in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith("clr-debug-pipe", StringComparison.Ordinal) || Path.GetFileName(file).StartsWith("dotnet-diagnostic", StringComparison.Ordinal)) continue;
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(file); }
                catch (IOException) { continue; }
                Assert.False(Contains(bytes, secret), $"a login is in {file}");
                scanned++;
            }
        }

        // the script printed the variable: the run log has it masked
        var log = h.Log(request.RunId.Value);
        Assert.Contains(log, l => l.Message == "leak: ***");

        await cancel.CancelAsync();
        var result = await run;
        Assert.Equal(TaskEndState.Stopped, result.End.State);
        var stored = h.Runs.Get(request.RunId.Value)!;
        foreach (var secret in new[] { SrcSecret, DstRead, DstWrite })
        {
            foreach (var line in h.Log(request.RunId.Value)) Assert.DoesNotContain(secret, line.Message);
            Assert.DoesNotContain(secret, stored.Run.Failure?.Message ?? "");
            Assert.DoesNotContain(secret, string.Join('\n', result.End.Failure?.Message));
        }

        // nor in the state database (the progress store, the run record and log, the queue): on SQLite the database file and its journal are scanned whole
        if (engine == StateEngineIds.Sqlite)
        {
            using var connection = h.Database.OpenConnection();
            var file = connection.DataSource;
            connection.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { file, file + "-wal", file + "-journal" }.Where(File.Exists))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                foreach (var secret in new[] { SrcSecret, DstRead, DstWrite }) Assert.False(Contains(memory.ToArray(), secret), $"a login is in {path}");
            }
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_read_only_connection_gets_no_write_variable_and_a_slot_without_an_injection_form_gets_none(string engine)
    {
        using var h = Harness(engine);
        var rig = RigFor(h);
        rig.Bind("prod", "ro", "ro_db");
        rig.Bind("prod", "plain", "src_db");
        ConnectionSlot[] slots = [new("ro", "sqlserver", SlotEnvironmentFormats.BuildLogin), new("plain", "postgres")];

        var handles = rig.Resolver.Handles(new TaskRef(Package, "prod", "run"), slots);
        var variables = await rig.Resolver.EnvironmentAsync(handles, slots, CancellationToken.None);

        Assert.Equal(["DBDATA_BUILD_RO_DB_READ"], variables.Keys);
        Assert.Equal(DstRead, variables["DBDATA_BUILD_RO_DB_READ"]);
        Assert.Equal(new Dictionary<string, string> { ["ro"] = "ro_db", ["plain"] = "src_db" }, handles);
    }

    // ---- ResolveConnection's facts

    [Theory, MemberData(nameof(Engines))]
    public async Task A_grant_carries_the_role_asked_for_and_a_read_request_never_gets_the_write_login(string engine)
    {
        using var h = Harness(engine);
        var rig = RigFor(h);

        var read = await rig.Resolver.ResolveAsync("dst", "dst_db", "read", CancellationToken.None);
        var write = await rig.Resolver.ResolveAsync("dst", "dst_db", "write", CancellationToken.None);
        Assert.Equal(("svc_read", DstRead, "sqlserver", "wh.example", "read"), (read.User, read.Password, read.Engine, read.Host, read.Role));
        Assert.Equal(("svc_write", DstWrite), (write.User, write.Password));

        var readOnly = await Assert.ThrowsAsync<SlotResolutionException>(() => rig.Resolver.ResolveAsync("ro", "ro_db", "write", CancellationToken.None));
        Assert.Contains("read-only", readOnly.Message, StringComparison.Ordinal);

        // a connection with a write login and no read login does not answer a read request with it
        var noRead = new RunnerSlotResolver(rig.Publisher,
            new InMemoryConnectionCatalog([new ConnectionDefinition { Name = "w_only", Engine = "sqlserver", Address = new AddressSpec { Host = "h", Database = "d" }, WriteLogin = new LoginSpec { Mode = AuthMode.Sql, User = "w", PasswordSecret = "env:DST_WRITE_PW" } }]),
            new SecretResolverChain([new EnvSecretProvider(n => Secrets.GetValueOrDefault(n))]));
        var refused = await Assert.ThrowsAsync<SlotResolutionException>(() => noRead.ResolveAsync("dst", "w_only", "read", CancellationToken.None));
        Assert.Contains("no read login", refused.Message, StringComparison.Ordinal);
    }

    // ---- through the scheduler

    private async Task<RunnerScheduler> SchedulerFor(ExecutorHarness h, Rig rig, string[] channels, params ConnectionSlot[] slots)
    {
        var task = new PackageTask("run", new TaskEntry("T"), new Schedule(), [], [], slots, ProgressDeclaration.Empty, TaskLimits.None, [], []);
        var manifest = new PackageManifest(Package, null, new PackageLaunch(LaunchKind.Executable, Command: "sh", Args: ["{package}/slots.sh"]), new BuiltAgainst(null, null, null), [task]);
        var (source, files) = await PackageFixture.ScriptFiles();
        var versions = new InMemoryVersionSource().Add(Package, "1.0.0", manifest, files);
        foreach (var channel in channels) new TaskProjections(h.Database).Replace(Package, channel, ProjectionDocuments.RowsOf(manifest, "1.0.0"));
        return new RunnerScheduler(h.Database, h.Executor, new ArtifactCache(Path.Combine(_root, "cache"), source), versions, new RunnerSchedulerOptions
        {
            Channels = channels,
            Tick = TimeSpan.FromMilliseconds(50),
            CancellationGrace = TimeSpan.FromSeconds(10),
            Slots = rig.Resolver,
        });
    }

    private static async Task Settle(RunnerScheduler scheduler)
    {
        await scheduler.TickAsync();
        await scheduler.WhenIdleAsync();
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_in_a_channel_uses_that_channels_binding_and_not_productions(string engine)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "runs sh");
        using var h = Harness(engine);
        var rig = RigFor(h);
        rig.Bind("prod", "src", "a_db");
        rig.Bind("staging", "src", "b_db");
        var scheduler = await SchedulerFor(h, rig, ["prod", "staging"], new ConnectionSlot("src", "postgres", SlotEnvironmentFormats.SyncSecret));

        scheduler.RunNow(new TaskRef(Package, "prod", "run"));
        scheduler.RunNow(new TaskRef(Package, "staging", "run"));
        await Settle(scheduler);
        await Settle(scheduler);

        string[] Vars(string channel)
        {
            var run = Assert.Single(h.Runs.Read(new RunQuery("runner", $"{Package}/{channel}/run")));
            return [.. h.Log(run.Id).Where(l => l.Message.StartsWith("var: ", StringComparison.Ordinal)).Select(l => l.Message[5..])];
        }

        Assert.Equal(["DBDATA_SECRET_DBDATA_SYNC_CONNECTION_A_DB", "DBDATA_SECRET_DBDATA_SYNC_CONNECTION_A_DB_RUN"], Vars("prod").Order(StringComparer.Ordinal));
        Assert.Equal(["DBDATA_SECRET_DBDATA_SYNC_CONNECTION_B_DB", "DBDATA_SECRET_DBDATA_SYNC_CONNECTION_B_DB_RUN"], Vars("staging").Order(StringComparer.Ordinal));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task An_unbound_slot_fails_the_run_naming_the_slot_and_the_channel_before_a_process_starts(string engine)
    {
        using var h = Harness(engine);
        var rig = RigFor(h);
        rig.Bind("prod", "src", "src_db");                       // staging binds nothing
        var scheduler = await SchedulerFor(h, rig, ["prod", "staging"],
            new ConnectionSlot("src", "postgres", SlotEnvironmentFormats.SyncSecret), new ConnectionSlot("dst", "sqlserver", SlotEnvironmentFormats.BuildLogin));

        scheduler.RunNow(new TaskRef(Package, "staging", "run"));
        await Settle(scheduler);

        var run = Assert.Single(h.Runs.Read(new RunQuery("runner", $"{Package}/staging/run")));
        Assert.Equal(RunOutcome.Failed, run.Run.Outcome);
        var message = run.Run.Failure!.Message;
        Assert.Contains("'src', 'dst'", message, StringComparison.Ordinal);
        Assert.Contains("channel 'staging'", message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Log(run.Id), l => l.Message.StartsWith("Started ", StringComparison.Ordinal));   // no process was made
        Assert.Empty(new DbDataKit.Scheduling.ModuleWorkQueueStore(h.Database, "runner").ListInFlight($"{Package}/staging"));   // a missing binding is permanent: no retries queued
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_login_missing_from_the_secret_store_fails_the_run_naming_the_slot_and_never_falls_back(string engine)
    {
        using var h = Harness(engine);
        var secrets = new Dictionary<string, string>(Secrets);
        secrets.Remove("DST_WRITE_PW");
        var rig = RigFor(h, secrets);
        rig.Bind("prod", "dst", "dst_db");
        ConnectionSlot[] slots = [new("dst", "sqlserver", SlotEnvironmentFormats.BuildLogin)];
        var request = (await h.ScriptRequest("slots-hold.sh")) with { ConnectionHandles = rig.Resolver.Handles(new TaskRef(Package, "prod", "run"), slots), Slots = slots, CancellationGrace = TimeSpan.FromMilliseconds(300) };
        using var failing = new ExecutorHarnessWith(h, rig.Resolver);

        var result = await failing.Executor.RunAsync(request);

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Null(result.ExitCode);                                            // no process
        Assert.Contains("slot 'dst'", result.End.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("env:DST_WRITE_PW", result.End.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DstRead, result.End.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An executor over the harness's state with its own resolver (the harness's was made before the test chose its secrets).</summary>
    private sealed class ExecutorHarnessWith : IDisposable
    {
        public ExecutorHarnessWith(ExecutorHarness h, ISlotResolver slots)
        {
            Executor = new TaskExecutor(h.Database, h.Host, new TaskExecutorOptions
            {
                DataDirectory = h.DataDirectory,
                TaskHostPath = PackageFixture.TaskHostPath(),
                Redactor = h.Redactor,
                Slots = slots,
            });
        }

        public TaskExecutor Executor { get; }

        public void Dispose()
        {
        }
    }

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(needle)) >= 0;
}
