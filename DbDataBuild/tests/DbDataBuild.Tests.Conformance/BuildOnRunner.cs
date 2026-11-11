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
/// The Build runtime package as the tests make it (plan 28c E05): what `scripts/publish-runtime.sh` writes, as text and files. <see cref="Manifest"/> is the manifest the script prints (a test holds the two equal), the directory is
/// `package.yaml` and `files/bin/dbdatabuild[.exe]`. `DBDATAKIT_TEST_PUBLISHED` names a directory the script (or an earlier run) made, so a machine with a small disk publishes once; otherwise the tool is published here, once per test run
/// (about 285 MB, three minutes).
/// </summary>
internal static class BuildRuntimePackage
{
    public const string Name = "dbdatabuild-runtime";

    public static string ToolFile => OperatingSystem.IsWindows() ? "dbdatabuild.exe" : "dbdatabuild";

    public static string Rid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    /// <summary>The engine's version as the manifest writes it: the Build release's three numbers.</summary>
    public static string Version => typeof(DbDataBuild.Cli.CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('-', '+')[0];

    /// <summary>The manifest of the runtime package, as `scripts/runtime-manifest.sh &lt;rid&gt; &lt;version&gt;` prints it.</summary>
    public static PackageManifest Manifest(string rid, string version, bool frameworkDependent = false) =>
        new(Name, $"The dbdatabuild engine {version} for {rid}, run by definition packages that name it",
            frameworkDependent
                ? new PackageLaunch(LaunchKind.Executable, Command: "dotnet", Args: ["{runtime}/bin/dbdatabuild.dll", "--runner-task"])
                : new PackageLaunch(LaunchKind.Executable, Command: "bin/" + (rid == "win-x64" ? "dbdatabuild.exe" : "dbdatabuild"), Args: ["--runner-task"]),
            new BuiltAgainst("10.0", "1", "dbdatakit.runner.v1"), [], IsRuntime: true, Version: version, Platform: rid);

    /// <summary>A directory of the layout `publish-runtime.sh` writes, for this machine's platform.</summary>
    public static string Directory() => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_PUBLISHED") is { Length: > 0 } given ? Checked(given) : Published.Value;

    private static string Checked(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "package.yaml")) || !File.Exists(Path.Combine(directory, "files", "bin", ToolFile)))
            throw new InvalidOperationException($"DBDATAKIT_TEST_PUBLISHED names '{directory}', which is not a runtime package: it holds no package.yaml and files/bin/{ToolFile}. Name the output of `scripts/publish-runtime.sh {Rid}`.");
        return directory;
    }

    private static readonly Lazy<string> Published = new(() =>
    {
        var project = typeof(BuildRuntimePackage).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BuildCliProject").Value!;
        var output = System.IO.Directory.CreateTempSubdirectory("ddb-runtime-publish-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { System.IO.Directory.Delete(output, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ } };
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "publish", project, "-c", "Release", "-r", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "--self-contained", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:IncludeAllContentForSelfExtract=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-o", Path.Combine(output, "files", "bin"), "--nologo", "-v", "q" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Publishing the tool failed ({process.ExitCode}):\n{text.Result}\n{errors.Result}");
        File.WriteAllText(Path.Combine(output, "package.yaml"), PackageManifestWriter.Write(Manifest(Rid, Version)));
        return output;
    });
}

/// <summary>
/// A runner of its own directory over SQLite, serving the channel `dev`, with the Build runtime package (<see cref="BuildRuntimePackage"/>, installed as `dbdatabuild-runtime`) and a definition package `build-proof` that holds the project's
/// committed files and names the runtime by range, and a task `refresh` for the plan `p` (plan 28c E01, E05).
/// </summary>
internal sealed class BuildOnRunner : IAsyncDisposable
{
    public const string DefinitionPackage = "build-proof";

    private readonly string _root = Directory.CreateTempSubdirectory("ddb-e01-").FullName;
    private readonly List<string> _log = [];
    private readonly Dictionary<string, string?> _env = [];
    private WebApplication? _app;

    public StateDatabase State { get; private set; } = null!;
    public IReadOnlyList<string> Log => _log;
    public RunnerEngine Engine => _app!.Services.GetRequiredService<RunnerEngine>();
    public static TaskRef Task { get; } = new(DefinitionPackage, "dev", "refresh");

    /// <summary>The runner's data directory: its cache (objects, views, the runtimes' extraction directories), scratch and state.</summary>
    public string DataDirectory => Path.Combine(_root, "run");

    /// <summary>Where the runtime's single-file executable unpacks its natives: one directory per runtime version, under the cache and outside every view.</summary>
    public string ExtractDirectory => Path.Combine(DataDirectory, "cache", "runtimes", BuildRuntimePackage.Name + "-" + BuildRuntimePackage.Rid + "@1");

    /// <summary>Starts the runner with the project in <paramref name="projectDirectory"/>; the login of <paramref name="connection"/> (read and write) is <paramref name="connectionString"/>, null when the project needs none.</summary>
    public static async Task<BuildOnRunner> StartAsync(string projectDirectory, string connection, string? connectionString, TimeSpan? every)
    {
        var runner = new BuildOnRunner();
        await runner.InitializeAsync(projectDirectory, connection, connectionString, every);
        return runner;
    }

    public static Task<BuildOnRunner> StartAsync(string projectDirectory, string connection, string? connectionString) => StartAsync(projectDirectory, connection, connectionString, null);

    private async Task InitializeAsync(string projectDirectory, string name, string? connectionString, TimeSpan? every)
    {
        // the logins reach the task through the environment the runner is started with (test grade: the runner resolves connection slots in plan 28c E02)
        var variables = new[] { LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Read), LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Write) };
        if (connectionString is not null)
            foreach (var variable in variables)
            {
                _env[variable] = Environment.GetEnvironmentVariable(variable);
                Environment.SetEnvironmentVariable(variable, connectionString);
            }

        var package = Path.Combine(_root, "package");
        CopyProject(projectDirectory, package);
        // the tests' engines have self-signed certificates, which the conformance run accepts for its own process (TestEngineTls); a task is a process of its own, so the copy of the project says so in a connection file
        var connections = Path.Combine(package, "connections");
        Directory.CreateDirectory(connections);
        File.WriteAllText(Path.Combine(connections, name + ".yaml"), $"schema: connection/1\nname: {name}\nengine: {name}\ntls:\n  encrypt: false\n  trust_server_certificate: true\n");

        var data = DataDirectory;
        Directory.CreateDirectory(data);
        var settings = new RunnerSettings(data, new Uri("https://127.0.0.1:0"))
        {
            Tick = TimeSpan.FromMilliseconds(100),
            CancellationGrace = TimeSpan.FromSeconds(20),
            InstanceHeartbeat = TimeSpan.FromSeconds(1),
            Channels = ["dev"],
            Staging = false,
            TaskHostPath = TaskHost(),
            TaskEnvironment = connectionString is null ? [] : variables,
        };
        State = RunnerState.Open(data, settings.State, null, settings.Group);

        // the runtime package: the published directory as it is (package.yaml and files/), served on the channel; then the definition, which names it by range
        var runtime = BuildRuntimePackage.Directory();
        DbDataKit.Runner.Api.RunnerPackageInstaller.Install(State, Path.Combine(runtime, "package.yaml"), Path.Combine(runtime, "files"), "dev", "test");

        var task = new PackageTask("refresh", new TaskEntry(), every is { } e ? Schedule.Interval(e) : new Schedule(), [],
            [new TaskInput("connection", ProgressType.Text, Required: true, Default: name), new TaskInput("plan", ProgressType.Text, Default: "p")],
            [], ProgressDeclaration.Empty, new TaskLimits(Timeout: TimeSpan.FromMinutes(5), Concurrency: 1), [], []);
        var manifest = new PackageManifest(DefinitionPackage, null,
            new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference(BuildRuntimePackage.Name, VersionRangeOf(BuildRuntimePackage.Version))),
            new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), [task]);
        var manifestPath = Path.Combine(_root, "package.yaml");
        await File.WriteAllTextAsync(manifestPath, PackageManifestWriter.Write(manifest));
        DbDataKit.Runner.Api.RunnerPackageInstaller.Install(State, manifestPath, package, "dev", "test");

        _app = RunnerServer.Build(settings, RunnerServer.Certificate(data), _log.Add, State);
        await _app.StartAsync();
    }

    /// <summary>The range a project says for an engine of this version: its minor line (`0.4.x`), which is what a project published for 0.4 reads.</summary>
    internal static string VersionRangeOf(string version) => string.Join('.', version.Split('.').Take(2)) + ".x";

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

    private static string TaskHost()
    {
        var directory = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "server-output.txt")).Trim();
        return RunnerTaskHost.Require(null, directory);
    }

    /// <summary>What a run says of itself, for the message of a failed assertion: its failure and its log.</summary>
    public string Why(StoredRun r) => $"{r.Run.Outcome} {r.Run.Failure?.Message}\n" + string.Join('\n', new RunStore(State, RunnerState.EngineService).Logs(r.Id, null, 100).Select(l => $"{l.Level}: {l.Message}"));

    public void Expect(RunOutcome outcome, StoredRun r) => Assert.True(r.Run.Outcome == outcome, $"expected {outcome}, got {Why(r)}");

    public IReadOnlyList<StoredRun> Runs() => new RunStore(State, RunnerState.EngineService).Read(new RunQuery(Package: DefinitionPackage, Limit: 100)).OrderBy(r => r.Run.Started).ToList();

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

    /// <summary>Waits until no view of a package is left in the cache: the run record is finished a moment before the executor lets go of the views, and a view that stays is a leak.</summary>
    public async Task WaitForNoViewsAsync()
    {
        var views = Path.Combine(DataDirectory, "cache", "views");
        for (var i = 0; i < 100 && Directory.EnumerateFileSystemEntries(views).Any(); i++) await System.Threading.Tasks.Task.Delay(100);
        Assert.Empty(Directory.EnumerateFileSystemEntries(views));
    }

    /// <summary>The files of a directory with their sizes and times, as one text: two equal texts mean nothing in the tree was written, replaced or removed in between.</summary>
    public static string Snapshot(string directory) => !Directory.Exists(directory) ? "" : string.Join('\n',
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(directory, p) + (File.Exists(p) ? $"|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}" : "/")));

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        foreach (var (variable, value) in _env) Environment.SetEnvironmentVariable(variable, value);
        try
        {
            // views and objects are read-only on purpose: the directories (Linux) and the files (Windows) are made writable for the removal
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
