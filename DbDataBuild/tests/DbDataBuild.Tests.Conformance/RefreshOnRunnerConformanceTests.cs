using System.Diagnostics;
using System.Reflection;
using DbDataBuild.Execution;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataRunner.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 28c, E01: one refresh plan as a task of a real dbdatarunner (in process, its task process real): published with `package install` as an executable package (the published `dbdatabuild` and the project's committed files),
/// run on the runner's schedule and by run now, beside the old path. The tracking rows of the target are what `connection refresh` writes, a failed load is a failed run that names it, a plan that does not exist is refused with
/// DDB-450, and the layout of the tracking tables is the one a run without the runner leaves.
/// </summary>
public partial class ApplyConformanceTests
{
    private const string BuildPackage = "build-proof";

    /// <summary>A runner of its own directory over SQLite, serving the channel `dev`, with a package that runs the plan `p` of the project the test set up.</summary>
    private sealed class BuildOnRunner : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("ddb-e01-").FullName;
        private readonly List<string> _log = [];
        private readonly Dictionary<string, string?> _env = [];
        private WebApplication? _app;

        public StateDatabase State { get; private set; } = null!;
        public IReadOnlyList<string> Log => _log;
        public RunnerEngine Engine => _app!.Services.GetRequiredService<RunnerEngine>();
        public static TaskRef Task { get; } = new(BuildPackage, "dev", "refresh");

        public static async Task<BuildOnRunner> StartAsync(Run run, string name, TimeSpan? every)
        {
            var runner = new BuildOnRunner();
            await runner.InitializeAsync(run, name, every);
            return runner;
        }

        private async Task InitializeAsync(Run run, string name, TimeSpan? every)
        {
            // the logins reach the task through the environment the runner is started with (test grade: the runner resolves connection slots in plan 28c E02)
            var variables = new[] { LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Read), LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Write) };
            foreach (var variable in variables)
            {
                _env[variable] = Environment.GetEnvironmentVariable(variable);
                Environment.SetEnvironmentVariable(variable, run.Engine.ConnectionString);
            }

            var package = Path.Combine(_root, "package");
            CopyProject(run.Dir, package);
            // the tests' engines have self-signed certificates, which the conformance run accepts for its own process (TestEngineTls); a task is a process of its own, so the copy of the project says so in a connection file
            var connections = Path.Combine(package, "connections");
            Directory.CreateDirectory(connections);
            File.WriteAllText(Path.Combine(connections, name + ".yaml"), $"schema: connection/1\nname: {name}\nengine: {name}\ntls:\n  encrypt: false\n  trust_server_certificate: true\n");
            Directory.CreateDirectory(Path.Combine(package, "bin"));
            File.Copy(PublishedTool(), Path.Combine(package, "bin", ToolFile));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(package, "bin", ToolFile), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var data = Path.Combine(_root, "run");
            Directory.CreateDirectory(data);
            var settings = new RunnerSettings(data, new Uri("https://127.0.0.1:0"))
            {
                Tick = TimeSpan.FromMilliseconds(100),
                CancellationGrace = TimeSpan.FromSeconds(20),
                InstanceHeartbeat = TimeSpan.FromSeconds(1),
                Channels = ["dev"],
                Staging = false,
                TaskHostPath = TaskHost(),
                TaskEnvironment = variables,
            };
            State = RunnerState.Open(data, settings.State, null, settings.Group);

            var task = new PackageTask("refresh", new TaskEntry(), every is { } e ? Schedule.Interval(e) : new Schedule(), [],
                [new TaskInput("connection", ProgressType.Text, Required: true, Default: name), new TaskInput("plan", ProgressType.Text, Default: "p")],
                [], ProgressDeclaration.Empty, new TaskLimits(Timeout: TimeSpan.FromMinutes(5), Concurrency: 1), [], []);
            var manifest = new PackageManifest(BuildPackage, null,
                new PackageLaunch(LaunchKind.Executable, Command: "bin/" + ToolFile, Args: ["--runner-task"]),
                new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), [task]);
            var manifestPath = Path.Combine(_root, "package.yaml");
            await File.WriteAllTextAsync(manifestPath, PackageManifestWriter.Write(manifest));
            DbDataKit.Runner.Api.RunnerPackageInstaller.Install(State, manifestPath, package, "dev", "test");

            _app = RunnerServer.Build(settings, RunnerServer.Certificate(data), _log.Add, State);
            await _app.StartAsync();
        }

        /// <summary>What the test project's project directory holds that a refresh reads (everything but the statement logs and git).</summary>
        private static void CopyProject(string from, string to)
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file).Replace('\\', '/');
                if (relative.StartsWith(".dbdatabuild/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal) || relative.StartsWith("plans/", StringComparison.Ordinal)) continue;
                var target = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        /// <summary>
        /// The tool as the single file `scripts/publish.sh` makes (about 285 MB for linux-x64): a package is a set of files the runner stores by hash and shows a task as symlinks, which a framework-dependent program cannot run from (the
        /// host resolves the link and looks for its runtimeconfig beside the stored object), so the one file with the runtime inside is what a package holds. `DBDATABUILD_TEST_PUBLISHED` names a directory published beforehand
        /// (a machine with a small disk publishes once); otherwise the file is published here, once per test run.
        /// </summary>
        private static string PublishedTool()
        {
            if (Environment.GetEnvironmentVariable("DBDATABUILD_TEST_PUBLISHED") is { Length: > 0 } given) return Path.Combine(given, ToolFile);
            return Published.Value;
        }

        private static string ToolFile => OperatingSystem.IsWindows() ? "dbdatabuild.exe" : "dbdatabuild";

        private static readonly Lazy<string> Published = new(() =>
        {
            var project = typeof(BuildOnRunner).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BuildCliProject").Value!;
            var output = Directory.CreateTempSubdirectory("ddb-e01-publish-").FullName;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(output, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ } };
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "publish", project, "-c", "Release", "-r", rid, "--self-contained", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:IncludeAllContentForSelfExtract=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-o", output, "--nologo", "-v", "q" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var text = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Publishing the tool failed ({process.ExitCode}):\n{text.Result}\n{errors.Result}");
            return Path.Combine(output, ToolFile);
        });

        private static string TaskHost()
        {
            var directory = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "server-output.txt")).Trim();
            return RunnerTaskHost.Require(null, directory);
        }

        /// <summary>What a run says of itself, for the message of a failed assertion: its failure and its log.</summary>
        public string Why(StoredRun r) => $"{r.Run.Outcome} {r.Run.Failure?.Message}\n" + string.Join('\n', new RunStore(State, RunnerState.EngineService).Logs(r.Id, null, 100).Select(l => $"{l.Level}: {l.Message}"));

        public void Expect(RunOutcome outcome, StoredRun r) => Assert.True(r.Run.Outcome == outcome, $"expected {outcome}, got {Why(r)}");

        public IReadOnlyList<StoredRun> Runs() => new RunStore(State, RunnerState.EngineService).Read(new RunQuery(Package: BuildPackage, Limit: 100)).OrderBy(r => r.Run.Started).ToList();

        public async Task<IReadOnlyList<StoredRun>> WaitForRunsAsync(int count, string what, int seconds = 180)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                var runs = Runs();
                if (runs.Count >= count && runs.All(r => r.Run.Finished is not null)) return runs;
                await System.Threading.Tasks.Task.Delay(200);
            }

            throw new TimeoutException($"Waited {seconds} seconds for {what}. The runner said:\n{string.Join('\n', _log)}\nRuns: {string.Join(", ", Runs().Select(r => r.Run.Outcome + (r.Run.Failure is { } f ? ":" + f.Message : "")))}");
        }

        public async Task<StoredRun> RunNowAsync(string? plan = null)
        {
            // a task never overlaps itself: while an earlier item (a scheduled run that was queued before the pause) is queued or running, run now queues nothing, so ask again until it has gone
            var until = DateTime.UtcNow.AddSeconds(120);
            RunNowResult result;
            while (!(result = Engine.Control!.RunNow(Task, plan is null ? null : new Dictionary<string, string> { ["plan"] = plan }, RunnerActor.Local())).Queued && DateTime.UtcNow < until)
                await System.Threading.Tasks.Task.Delay(500);
            Assert.True(result.Queued, "run now queued nothing within two minutes");
            var finish = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < finish)
            {
                if (Runs().SingleOrDefault(r => r.Id == result.RunId) is { Run.Finished: not null } done) return done;
                await System.Threading.Tasks.Task.Delay(200);
            }

            throw new TimeoutException($"Waited for the run {result.RunId}. The runner said:\n{string.Join('\n', _log)}");
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }

            foreach (var (variable, value) in _env) Environment.SetEnvironmentVariable(variable, value);
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
    }

    /// <summary>What an event of the plan left in the tracking tables, without its identity, times and the plan id: one line for the event, one for each step.</summary>
    private static async Task<List<string>> EventSignaturesAsync(Run run)
    {
        var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
        var events = await run.Engine.RowsAsync($"SELECT {run.Q("plan_id")} FROM {T("migration_log")} WHERE {run.Q("refresh_plan")} = 'p' AND status <> 'started' ORDER BY {run.Q("applied_utc")}");
        var signatures = new List<string>();
        foreach (var id in events)
        {
            var head = await run.Engine.RowsAsync($"SELECT CONCAT({run.Q("lane")}, '|', status, '|', {run.Q("project_hash")}, '|', {run.Q("refresh_plan")}, '|', {run.Q("selection")}, '|', {run.Q("hash_before")}, '|', {run.Q("hash_after")}) FROM {T("migration_log")} WHERE {run.Q("plan_id")} = '{id}' AND status <> 'started'");
            var steps = await run.Engine.RowsAsync($"SELECT CONCAT({run.Q("model")}, '|', {run.Q("operation")}, '|', status, '|', rows_affected, '|', {run.Q("membership")}, '|', {run.Q("load_plan_hash")}, '|', {run.Q("attempt")}, '|', {run.Q("definition_hash")}) FROM {T("run_log")} WHERE {run.Q("plan_id")} = '{id}' ORDER BY {run.Q("model")}, {run.Q("operation")}");
            var members = await run.Engine.RowsAsync($"SELECT CONCAT({run.Q("membership")}, '|', {run.Q("model")}, '|', {run.Q("load_plan_hash")}, '|', {run.Q("selected")}) FROM {T("refresh_plan_member")} WHERE {run.Q("plan_id")} = '{id}' ORDER BY {run.Q("position")}");
            signatures.Add(string.Join("\n", [.. head, .. steps, .. members]));
        }

        return signatures;
    }

    private static async Task<string> TrackingLayoutAsync(Run run) =>
        string.Join("\n", await run.Engine.RowsAsync($"SELECT CONCAT(table_name, '.', column_name, '.', data_type, '.', CAST(ordinal_position AS VARCHAR(10)), '.', is_nullable) FROM information_schema.columns WHERE table_schema = 'dbdatabuild' ORDER BY table_name, ordinal_position"));

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_refresh_plan_runs_as_a_runner_task_on_its_schedule_and_by_run_now_and_leaves_the_rows_the_command_leaves(string name)
    {
        var run = await FailureSetUp(name, "    p: { on_failure: continue }\n    q: { on_failure: continue }\n", "refresh: { p: default, q: default }\n");
        await using var engine = run.Engine;
        try
        {
            // the old path: the command, which is what the runner's task must leave behind
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            Ok(run.Cli("connection", "refresh", name, "p"), "the command");
            var baseline = await EventSignaturesAsync(run);
            Assert.Single(baseline);
            Assert.StartsWith("refresh|completed|", baseline[0]);
            var layout = await TrackingLayoutAsync(run);

            await using var runner = await BuildOnRunner.StartAsync(run, name, every: TimeSpan.FromSeconds(2));

            // on the runner's schedule: nothing asked for it
            var scheduled = (await runner.WaitForRunsAsync(1, "the first scheduled run")).First();
            runner.Engine.Control!.Pause(BuildOnRunner.Task, RunnerActor.Local());
            await runner.WaitForRunsAsync(1, "the runs in flight to end");   // a run may have started before the pause: it finishes
            runner.Expect(RunOutcome.Succeeded, scheduled);

            // by run now (the task is paused: a person's run goes ahead)
            var now = await runner.RunNowAsync();
            runner.Expect(RunOutcome.Succeeded, now);

            // the runner's run row: package, version, channel, outcome, the rows moved
            foreach (var r in runner.Runs())
            {
                Assert.Equal(BuildPackage, r.Package);
                Assert.Equal("dev", r.Channel);
                Assert.False(string.IsNullOrEmpty(r.PackageVersion));
                runner.Expect(RunOutcome.Succeeded, r);
                Assert.True(r.Run.Rows > 0, "the run row counts the rows the loads moved");
            }

            // the target's rows: every event of the runner's runs equals the command's (the same plan, the same hashes, the same rows, the same status, the lane `refresh`)
            var signatures = await EventSignaturesAsync(run);
            Assert.Equal(1 + runner.Runs().Count, signatures.Count);
            foreach (var signature in signatures.Skip(1)) Assert.Equal(baseline[0], signature);
            Assert.Equal(layout, await TrackingLayoutAsync(run));
            Assert.Equal(signatures.Count, await CountAsync(run, $"{run.Q("dbdatabuild")}.{run.Q("migration_log")}", $"{run.Q("refresh_plan")} = 'p' AND status = 'completed'"));

            // a failing load: the run is a completed-with-failures run that names it (p: the others ran), and a failed run when none finished (q)
            await engine.ExecAsync("INSERT INTO staging.flag VALUES (1)");
            var partial = await runner.RunNowAsync();
            runner.Expect(RunOutcome.CompletedWithFailures, partial);
            Assert.Equal("marts.a_orders.default", partial.Run.FailedItem);
            var failed = await runner.RunNowAsync("q");
            runner.Expect(RunOutcome.Failed, failed);
            Assert.Contains("marts.a_orders.default", failed.Run.FailedItem + failed.Run.Failure?.Message);

            // a plan that does not exist is refused at the entry, and the run says the diagnostic
            var unknown = await runner.RunNowAsync("nope");
            runner.Expect(RunOutcome.Failed, unknown);
            Assert.Contains("DDB-450", unknown.Run.Failure?.Message);
            Assert.Equal(layout, await TrackingLayoutAsync(run));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
