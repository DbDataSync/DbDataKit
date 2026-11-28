using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.Runner.Api;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;
using DbDataSync.TaskRunner.Runner;
using Microsoft.AspNetCore.Builder;
using SyncRoot = DbDataSync.Cli.PackageCommand;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Plan 28c E08, the staging proof: the package `dbdatasync package publish --to runner --out` writes, sent with the runner client the console's deploy uses (<see cref="RunnerPublishClient"/>; the command line no longer sends it),
/// against a real runner host (DbDataRunner.Server in this process, SQLite state, its own HTTPS listener and pairing; the `dbdatasync-runtime` package a stand-in file, because staging
/// only materializes it). A replication set of three replications is published and the version stages, is prepared and goes ready and is served; the manifest the runner stored declares each task's checkpoint and
/// <see cref="ProgressMigration.Plan"/> reads it; a change to one replication sends only its files; a repository with a library script, its `library.lock` and a `.csx` script stages with the locked closure in the version; and a slot nobody bound
/// refuses the publish naming the slot and the channel. No database engine is needed (nothing reads or writes a database), so it runs on Windows as well; the library closure comes from the machine's NuGet folder, as the loader tests' does.
/// </summary>
public sealed class PackagePublishOnRealRunnerTests : IDisposable
{
    private const string Package = "sales-set";
    private const string Channel = "prod";
    private static readonly string[] Binds = ["--bind", "crm_src=crm-src", "--bind", "dwh=dwh"];
    private readonly ReplicationSetRepo _repo = new("dbdatasync-realrunner-");
    private readonly string _client = Directory.CreateTempSubdirectory("dbdatasync-realrunner-client-").FullName;

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_client, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }

    /// <summary>A runner of its own directory serving `prod`, following staged versions, with a stand-in `dbdatasync-runtime` active in it.</summary>
    private sealed class Runner : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("dbdr-e08-").FullName;
        private readonly List<string> _log = [];
        private WebApplication? _app;

        public DbDataKit.State.StateDatabase State { get; private set; } = null!;
        public RunnerSettings Settings { get; private set; } = null!;
        public string Url { get; private set; } = "";

        public static async Task<Runner> StartAsync()
        {
            var runner = new Runner();
            var data = runner._root;
            runner.Settings = new RunnerSettings(data, new Uri("https://127.0.0.1:0"))
            {
                Tick = TimeSpan.FromMilliseconds(100),
                InstanceHeartbeat = TimeSpan.FromSeconds(1),
                Channels = [Channel],
                Staging = true,
                TaskHostPath = RunnerTaskHost.Require(null, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "server-output.txt")).Trim()),
            };
            runner.State = RunnerState.Open(data, runner.Settings.State, null, runner.Settings.Group);

            // the runtime package: a manifest of the shape scripts/runtime-manifest.sh writes and a file for its command; nothing here runs it
            var runtime = Path.Combine(data, "runtime-src");
            Directory.CreateDirectory(Path.Combine(runtime, "files", "bin"));
            File.WriteAllText(Path.Combine(runtime, "files", "bin", "DbDataSync.TaskRunner.dll"), "stand-in for the engine");
            var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            File.WriteAllText(Path.Combine(runtime, "package.yaml"), PackageManifestWriter.Write(new PackageManifest(ReplicationSetPackage.RuntimeName, "stand-in",
                new PackageLaunch(LaunchKind.Executable, Command: "dotnet", Args: ["{runtime}/bin/DbDataSync.TaskRunner.dll", "--runner"]), new BuiltAgainst("10.0", "1", RunnerProtocol.V1), [], IsRuntime: true, Version: RuntimeVersion(), Platform: rid)));
            RunnerPackageInstaller.Install(runner.State, Path.Combine(runtime, "package.yaml"), Path.Combine(runtime, "files"), Channel, "test");

            runner._app = RunnerServer.Build(runner.Settings, RunnerServer.Certificate(data), runner._log.Add, runner.State);
            await runner._app.StartAsync();
            runner.Url = runner._app.Urls.First();
            return runner;
        }

        /// <summary>A version of the runtime in the line the publishing tool accepts (its range is the first two numbers of its own version).</summary>
        private static string RuntimeVersion() => ReplicationSetPackage.RuntimeRange(PublisherVersion()) is var range && range.EndsWith(".x", StringComparison.Ordinal) ? range[..^1] + "1" : "1.0.0";

        public string Code() => new PairingFiles(Settings.ServerYaml, Group: State).Codes().Create(ServerRoles.Close(ServerRole.Admin)).Code;

        private DefinitionPublisher Publisher => new(State, RunnerPackages.Kinds(), null, "runner");

        public DefinitionHead? Head() => Publisher.Head(RunnerPublishing.Kind, Package, Channel);

        public IReadOnlyList<DefinitionArtifact> Artifacts(int version) => Publisher.Artifacts(RunnerPublishing.Kind, Package, version);

        public PackageManifest Manifest(int version)
        {
            var stored = new DefinitionStore(State).Get(RunnerPublishing.Kind, Package, version)!;
            var load = PackageManifestReader.Load(stored.Content, "package.yaml");
            Assert.True(load.Ok, string.Join("\n", load.Diagnostics));
            return load.Manifest!;
        }

        public IReadOnlyList<string> InstanceStates() => [.. Publisher.Status.List(RunnerPublishing.Kind, Package, Channel).Select(s => $"{s.Version}:{s.State}")];

        /// <summary>Waits until the channel's active version is <paramref name="version"/>: staging prepared it here, found every runner of the group ready and activated it.</summary>
        public async Task WaitActiveAsync(int version, int seconds = 120)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (Head()?.Active == version) return;
                await Task.Delay(200);
            }

            throw new TimeoutException($"Waited {seconds} seconds for {Package} on {Channel} to be at version {version}: head {Head()}, instances {string.Join(", ", InstanceStates())}. The runner said:\n{string.Join('\n', _log)}");
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                {
                    if (OperatingSystem.IsWindows()) { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); }
                    else if (Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
    }

    private static string PublisherVersion() =>
        typeof(SyncRoot).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0";

    /// <summary>
    /// Writes the package with the verb (<c>--out</c>, <c>--json</c>) and sends it with the runner client from this test's client directory: pairing with <paramref name="code"/> when given, binding the slots
    /// <c>--bind slot=connection</c> names in <paramref name="extra"/>, then staging the version. The answer is the verb's document of what the package holds with what the runner made of it (version, changed, uploaded,
    /// skipped), or the refusal.
    /// </summary>
    private async Task<(int Exit, JsonNode? Doc, string Error)> PublishAsync(Runner runner, string? code, params string[] extra)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var verbExtra = new List<string>();
        for (var i = 0; i < extra.Length; i++)
        {
            if (extra[i] == "--bind" && i + 1 < extra.Length) { var eq = extra[++i].IndexOf('='); bindings[extra[i][..eq]] = extra[i][(eq + 1)..]; }
            else verbExtra.Add(extra[i]);
        }

        var folder = Directory.CreateTempSubdirectory("dbdatasync-realrunner-out-").FullName;
        var (originalOut, originalErr) = (Console.Out, Console.Error);
        var (o, e) = (new StringWriter(), new StringWriter());
        Console.SetOut(o);
        Console.SetError(e);
        int exit;
        try { exit = await PackageCommand.RunAsync(["publish", "--to", "runner", "--json", "--out", folder, "--package", Package, "--repo", _repo.Root, .. verbExtra]); }
        finally { Console.SetOut(originalOut); Console.SetError(originalErr); }
        try
        {
            if (exit != 0) return (exit, null, e.ToString());
            var doc = JsonNode.Parse(o.ToString().Trim())!;
            var client = new RunnerPublishClient(new RunnerClientStore(_client), _ => null);
            try
            {
                if (bindings.Count > 0) await client.BindAsync(runner.Url, code, Package, Channel, bindings);
                var published = await client.PublishAsync(runner.Url, bindings.Count > 0 ? null : code, Path.Combine(folder, "package.yaml"), Path.Combine(folder, "files"), Channel, stage: true, expectedVersion: null);
                doc["mode"] = published.Mode;
                doc["version"] = published.Version;
                doc["changed"] = published.Changed;
                doc["uploaded"] = published.Uploaded;
                doc["skipped"] = published.Skipped;
                return (0, doc, "");
            }
            catch (RunnerClientException ex) { return (1, null, ex.Message); }
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ } }
    }

    private static JsonNode Ok((int Exit, JsonNode? Doc, string Error) result)
    {
        Assert.True(result.Exit == 0 && result.Doc is not null, result.Error);
        return result.Doc!;
    }

    [Fact]
    public async Task ThreeReplicationsStage_AreReadyOnTheRunner_AndTheManifestDeclaresTheCheckpointThatTheMigrationPlannerReads()
    {
        await using var runner = await Runner.StartAsync();

        var first = Ok(await PublishAsync(runner, runner.Code(), Binds));

        Assert.Equal("stage", first["mode"]!.GetValue<string>());
        Assert.True(first["changed"]!.GetValue<bool>());
        Assert.True(first["uploaded"]!.GetValue<int>() > 0);
        var version = first["version"]!.GetValue<int>();
        Assert.Equal(["customers", "events", "orders"], first["replications"]!.AsArray().Select(r => r!["task"]!.GetValue<string>()).Order());

        // the version is prepared by the runner (ready) and, being staged, is served once every runner of the group has it ready
        await runner.WaitActiveAsync(version);
        Assert.Contains(runner.InstanceStates(), s => s == $"{version}:ready" || s == $"{version}:active");       // prepared (ready) and then, every runner being ready, served (the state follows the head a moment later)
        Assert.Null(runner.Head()!.Staged);

        // what the runner stored: three tasks of the drain, each declaring the engine's checkpoint, which the planner reads
        var manifest = runner.Manifest(version);
        Assert.Equal(["customers", SetCatalogSteps.Describe, SetCatalogSteps.Discover, "events", "orders", ReplicationSetPackage.PreflightTask, PreviewTasks.Preview, ReplicationSetPackage.ProvisionTask, ReplicationSetPackage.ScriptTestTask, PreviewTasks.TryStrategy], manifest.Tasks.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ReplicationSetPackage.RuntimeName, manifest.Launch.Runtime!.Name);
        // plan 28c E21: the set's manual provisioning task, over every connection of the set
        Assert.Equal(["crm_src", "dwh"], manifest.Tasks.Single(t => t.Name == ReplicationSetPackage.ProvisionTask).Connections.Select(c => c.Name));
        Assert.Equal(["crm_src", "dwh"], manifest.Tasks.Single(t => t.Name == ReplicationSetPackage.PreflightTask).Connections.Select(c => c.Name));
        Assert.All(ReplicationSetPackage.PreviewTaskNames, name => Assert.Equal(["crm_src", "dwh"], manifest.Tasks.Single(t => t.Name == name).Connections.Select(c => c.Name)));
        Assert.Empty(manifest.Tasks.Single(t => t.Name == ReplicationSetPackage.ScriptTestTask).Connections);   // the test of a script opens no connection
        Assert.All(ReplicationSetPackage.CatalogTaskNames, name => Assert.Equal(["crm_src", "dwh"], manifest.Tasks.Single(t => t.Name == name).Connections.Select(c => c.Name)));
        foreach (var task in manifest.Tasks.Where(t => t.Name is not (ReplicationSetPackage.ProvisionTask or ReplicationSetPackage.PreflightTask or PreviewTasks.Preview or PreviewTasks.TryStrategy or SetCatalogSteps.Discover or SetCatalogSteps.Describe or ReplicationSetPackage.ScriptTestTask)))
        {
            var watermark = Assert.Single(task.Progress.Checkpoints, c => c.Name == "watermark");
            Assert.Contains("position", watermark.Parts.Keys);
            Assert.Contains("position_time", watermark.Parts.Keys);
            Assert.Equal(["crm_src", "dwh"], task.Connections.Select(c => c.Name));
            Assert.Equal("repo", task.Inputs.Single(i => i.Name == "repo_dir").Default);
            Assert.Equal(task.Name, task.Inputs.Single(i => i.Name == "replication").Default);

            var fromNothing = ProgressMigration.Plan(ProgressDeclaration.Empty, task.Progress);
            Assert.Contains(fromNothing.Steps, s => s.Kind == MigrationKind.AddValue && s.Target == "watermark");
            Assert.Empty(fromNothing.Losses);
            Assert.True(ProgressMigration.Plan(task.Progress, task.Progress).IsEmpty);     // the same declaration loses nothing and changes nothing
        }

        // the runner's slots are the ones bound in the channel, the files are the definition and none is a secret
        var paths = runner.Artifacts(version).Select(a => a.Path).ToList();
        Assert.Contains("repo/config/replications/orders/task.yaml", paths);
        Assert.Contains("repo/config/connections/crm-src.yaml", paths);
        Assert.DoesNotContain(paths, p => p.Contains("/secrets/") || p.Contains("/.git/"));
    }

    [Fact]
    public async Task AChangeToOneReplicationRepublishesOnlyItsFiles_AndTheSameSetAgainSendsNothing()
    {
        await using var runner = await Runner.StartAsync();
        var first = Ok(await PublishAsync(runner, runner.Code(), Binds));
        var v1 = first["version"]!.GetValue<int>();
        await runner.WaitActiveAsync(v1);
        var before = runner.Artifacts(v1).ToDictionary(a => a.Path, a => a.Sha256);

        var same = Ok(await PublishAsync(runner, null));
        Assert.False(same["changed"]!.GetValue<bool>());
        Assert.Equal(0, same["uploaded"]!.GetValue<int>());
        Assert.Equal(v1, same["version"]!.GetValue<int>());

        _repo.Replication("orders", DbDataKit.Sync.Core.Config.ScheduleMode.Periodic, cron: "*/15 * * * *", sourceTable: "OrdersRenamed");   // one mapping of one replication changes
        var second = Ok(await PublishAsync(runner, null));
        var v2 = second["version"]!.GetValue<int>();

        Assert.True(second["changed"]!.GetValue<bool>());
        Assert.True(v2 > v1);
        Assert.Equal(2, second["uploaded"]!.GetValue<int>());                                  // the one mapping file and the design lineage that names its table (plan 18 G18); everything else the runner had
        Assert.Equal(before.Count - 2, second["skipped"]!.GetValue<int>());
        var after = runner.Artifacts(v2).ToDictionary(a => a.Path, a => a.Sha256);
        var changed = after.Keys.Union(before.Keys).Where(k => !before.TryGetValue(k, out var x) || !after.TryGetValue(k, out var y) || x != y).ToList();
        Assert.Equal(["lineage/design.json", "repo/config/replications/orders/table-mappings/t1.yaml"], changed.Order(StringComparer.Ordinal));
        await runner.WaitActiveAsync(v2);
    }

    private static string? NuGetFeed() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    [Fact]
    public async Task ARepositoryWithALibraryItsLockAndACsxScriptStagesWithTheLockedClosureInTheVersion()
    {
        var packages = Path.Combine(NuGetFeed()!, "microsoft.data.sqlite");
        Assert.True(Directory.Exists(packages), "Microsoft.Data.Sqlite is not in the machine's NuGet package folder: restore a project that uses it first");
        var version = Directory.GetDirectories(packages).Select(Path.GetFileName).Where(n => n is not null && !n!.Contains('-')).OrderBy(n => Version.TryParse(n, out var v) ? v : new Version(0, 0)).Last()!;
        var library = Path.Combine(_repo.Root, "libraries", "Microsoft.Data.Sqlite");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "library.csx"), $"#r \"nuget: Microsoft.Data.Sqlite, {version}\"\n");
        // the built-in drivers' libraries, held by the repository at the versions the machine's folder has: without them the publish restores the catalog's pinned versions into the package (plan 33 SC23d2), which
        // this offline source does not have
        foreach (var (id, package) in new[] { ("microsoft-data-sqlclient", "Microsoft.Data.SqlClient"), ("npgsql", "Npgsql") })
        {
            var held = Directory.GetDirectories(Path.Combine(NuGetFeed()!, package.ToLowerInvariant())).Select(Path.GetFileName).Where(n => n is not null && !n!.Contains('-')).OrderBy(n => Version.TryParse(n, out var parsed) ? parsed : new Version(0, 0)).Last()!;
            Directory.CreateDirectory(Path.Combine(_repo.Root, "libraries", id));
            File.WriteAllText(Path.Combine(_repo.Root, "libraries", id, "library.csx"), $"#r \"nuget: {package}, {held}\"\n");
        }
        var scripts = Path.Combine(_repo.Root, "config", "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "normalize.csx"), "// a transform script of the replication set\nreturn Value?.ToString()?.Trim();\n");

        await using var runner = await Runner.StartAsync();
        var published = Ok(await PublishAsync(runner, runner.Code(), [.. Binds, "--source", NuGetFeed()!]));
        var v = published["version"]!.GetValue<int>();
        await runner.WaitActiveAsync(v);

        var files = runner.Artifacts(v).ToDictionary(a => a.Path, a => a.Sha256);
        Assert.Contains("repo/config/scripts/normalize.csx", files.Keys);
        Assert.Contains("repo/libraries/Microsoft.Data.Sqlite/library.csx", files.Keys);
        Assert.Contains("repo/libraries/Microsoft.Data.Sqlite/library.lock", files.Keys);       // the pins the publish wrote: what the runner restores against
        Assert.Contains("repo/driver-libraries.json", files.Keys);
        var closure = files.Keys.Where(p => p.StartsWith("repo/libraries/Microsoft.Data.Sqlite/lib/", StringComparison.Ordinal)).ToList();
        Assert.Contains(closure, p => p.EndsWith("/Microsoft.Data.Sqlite.dll", StringComparison.Ordinal));       // the locked closure is in the version, restored for the platform at publish
        Assert.Contains(closure, p => p.Contains("/runtimes/", StringComparison.Ordinal) && p.Contains("/native/", StringComparison.Ordinal));
        Assert.Contains(runner.InstanceStates(), s => s == $"{v}:ready" || s == $"{v}:active");
    }

    [Fact]
    public async Task ASlotNobodyBoundRefusesTheVersionNamingTheSlotsAndTheChannel_AndNothingIsStaged()
    {
        await using var runner = await Runner.StartAsync();

        var refused = await PublishAsync(runner, runner.Code());       // no --bind: the runner has nothing bound in the channel

        Assert.Equal(1, refused.Exit);
        Assert.Contains("needs the slots 'crm_src', 'dwh' bound in channel 'prod'", refused.Error);
        Assert.Contains("The runner refused the package sales-set", refused.Error);
        Assert.Null(runner.Head());

        // binding only one of the two names the other
        var partly = await PublishAsync(runner, null, "--bind", "crm_src=crm-src");
        Assert.Equal(1, partly.Exit);
        Assert.Contains("needs the slot 'dwh' bound in channel 'prod'", partly.Error);
        Assert.Null(runner.Head());
    }
}
