using DbDataKit.Cli;
using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>The command line of <c>dbdatarunner</c>: its help as a snapshot, the shared nouns, and the runner's own verbs (<c>package install</c>, <c>tasks</c>, <c>runs</c>) on the state a server shares.</summary>
public sealed class RunnerCliTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdr-cli-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }

    private static async Task<(int Exit, string Out, string Error)> RunIn(string directory, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        // no variable of the environment reaches the command: SQLite in the directory
        var exit = await RunnerCli.Router(_ => null).RunAsync([.. args, "--repo", directory], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private Task<(int Exit, string Out, string Error)> Run(params string[] args) => RunIn(_directory, args);

    private static string Help()
    {
        var text = new StringWriter();
        var router = RunnerCli.Router(_ => null);
        foreach (var words in new[] { new[] { "help" }, ["server", "help"], ["runs", "help"], ["tasks", "help"], ["label", "help"], ["package", "help"], ["state", "help"], ["service", "help"], ["cert", "help"], ["health", "help"] })
        {
            var output = new StringWriter();
            router.RunAsync(words, new CliContext(output, TextWriter.Null, TextReader.Null)).GetAwaiter().GetResult();
            text.WriteLine($"$ dbdatarunner {string.Join(' ', words)}");
            text.WriteLine(output.ToString().ReplaceLineEndings("\n").TrimEnd());
            text.WriteLine();
        }

        return text.ToString().ReplaceLineEndings("\n");
    }

    [Fact]
    public void The_help_of_every_noun_is_a_snapshot()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "help.snapshot.txt");
        var actual = Help();
        if (Environment.GetEnvironmentVariable("DBDATA_UPDATE_SNAPSHOTS") == "1") File.WriteAllText(Environment.GetEnvironmentVariable("DBDATA_SNAPSHOT_FILE") ?? file, actual);

        Assert.Equal(File.ReadAllText(file).ReplaceLineEndings("\n"), actual);
        // the shared nouns are there with their shared verbs, and the runner's own
        foreach (var noun in new[] { "server", "runs", "tasks", "label", "package", "state", "service", "cert", "health" }) Assert.Contains($"\n  {noun} ", actual);
        foreach (var command in new[] { "server pair", "server clients", "server unpair", "package install", "tasks run", "tasks pause", "tasks resume", "label add", "label remove", "label list", "runs list", "state show", "state test", "state grants", "service unit", "cert ensure", "cert renew" })
            Assert.Contains($"  {command}", actual);
    }

    [Fact]
    public async Task A_package_is_installed_listed_run_paused_and_resumed_from_the_command_line()
    {
        var manifest = SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok", TimeSpan.FromHours(1)), SamplePackage.Task("fails")));

        var install = await Run("package", "install", manifest, SamplePackage.Directory, "--channel", "dev");
        Assert.True(install.Exit == 0, install.Error);
        Assert.Contains("Installed sample on dev: version 1", install.Out);
        Assert.Contains("tasks ok, fails", install.Out);
        // the same content again changes nothing
        Assert.Contains("is at version 1 already", (await Run("package", "install", manifest, SamplePackage.Directory, "--channel", "dev")).Out);

        var list = await Run("tasks", "list");
        Assert.Equal(0, list.Exit);
        Assert.Contains("sample/dev/ok  v1", list.Out);
        Assert.Contains("sample/dev/fails  v1", list.Out);

        Assert.Equal(0, (await Run("tasks", "pause", "sample/dev/ok")).Exit);
        Assert.Contains("PAUSED by " + Environment.UserName, (await Run("tasks", "list")).Out);
        Assert.Equal(0, (await Run("tasks", "resume", "sample/dev/ok")).Exit);
        Assert.DoesNotContain("PAUSED", (await Run("tasks", "list")).Out);

        var run = await Run("tasks", "run", "sample/dev/ok", "note=by hand");
        Assert.Equal(0, run.Exit);
        Assert.Contains("Queued sample/dev/ok", run.Out);
        Assert.Equal(1, (await Run("tasks", "run", "sample/dev/ok")).Exit);   // already queued: nothing was queued
        Assert.Equal(1, (await Run("tasks", "run", "sample/dev/nothing")).Exit);
        Assert.Equal(2, (await Run("tasks", "run", "sample-dev-ok")).Exit);
        Assert.Equal(2, (await Run("tasks", "run", "sample/dev/ok", "noequals")).Exit);

        var entries = new AuditLog(RunnerState.Open(_directory), RunnerState.ServiceId).Read(new AuditQuery(VerbPrefix: "runner.")).Select(e => e.Entry.Verb).Reverse().ToList();
        Assert.Equal(["runner.package.install", "runner.task.pause", "runner.task.resume", "runner.task.run"], entries.Distinct());

        Assert.Contains("No run yet.", (await Run("runs", "list")).Out);
        Assert.Equal(2, (await Run("runs", "list", "--outcome", "sideways")).Exit);
        Assert.Equal(2, (await Run("runs", "list", "--task", "ok")).Exit);
    }

    [Fact]
    public async Task A_task_queued_from_the_command_line_is_run_by_a_server_on_the_same_state_and_listed_with_its_runs()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        var queued = await RunIn(h.Directory, "tasks", "run", "sample/dev/ok");
        Assert.True(queued.Exit == 0, queued.Error);
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&outcome=succeeded")).Count == 1 ? "done" : null, "the server to run what the command queued");

        var runs = await RunIn(h.Directory, "runs", "list", "--package", "sample", "--channel", "dev", "--task", "ok", "--outcome", "succeeded");
        Assert.Equal(0, runs.Exit);
        Assert.Contains("succeeded  sample/dev/ok  v1  attempt 1", runs.Out);
        Assert.Contains("sample/dev/ok  v1", (await RunIn(h.Directory, "tasks", "list")).Out);
    }

    [Fact]
    public async Task A_package_that_is_not_valid_is_refused_naming_the_file_and_the_key()
    {
        var manifest = SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok")));
        File.WriteAllText(manifest, File.ReadAllText(manifest) + "surprise: yes\n");

        var unknown = await Run("package", "install", manifest, SamplePackage.Directory);
        Assert.Equal(1, unknown.Exit);
        Assert.Contains(manifest, unknown.Error);
        Assert.Contains("surprise", unknown.Error);

        // a launch file that is not among the files
        var emptyPackage = Directory.CreateTempSubdirectory("dbdr-empty-package-").FullName;
        File.WriteAllText(Path.Combine(emptyPackage, "readme.txt"), "nothing to run");
        var missing = await Run("package", "install", SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok"))), emptyPackage);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("DbDataKit.Runner.TestPackage.dll", missing.Error);
        Assert.Contains("not among the package's files", missing.Error);

        Assert.Equal(1, (await Run("package", "install", Path.Combine(_directory, "nowhere.yaml"), SamplePackage.Directory)).Exit);
        Assert.Equal(1, (await Run("package", "install", SamplePackage.WriteManifest(SamplePackage.Manifest()), Path.Combine(_directory, "nowhere"))).Exit);
        Assert.Equal(1, (await Run("package", "install", SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok"))), SamplePackage.Directory, "--channel", "Not A Channel")).Exit);
        Assert.Equal(2, (await Run("package", "install", manifest)).Exit);
        Directory.Delete(emptyPackage, recursive: true);

        // nothing of the refused installs reached the state
        Assert.Contains("No task", (await Run("tasks", "list")).Out);
    }

    [Fact]
    public async Task A_changed_package_becomes_the_next_version_and_replaces_the_schedule_of_the_channel()
    {
        var first = SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok", TimeSpan.FromHours(1)), SamplePackage.Task("fails")));
        Assert.Equal(0, (await Run("package", "install", first, SamplePackage.Directory)).Exit);   // the dev channel is the default
        var second = SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok", TimeSpan.FromHours(2))));

        var installed = await Run("package", "install", second, SamplePackage.Directory);

        Assert.Contains("version 2", installed.Out);
        var list = (await Run("tasks", "list")).Out;
        Assert.Contains("sample/dev/ok  v2", list);
        Assert.DoesNotContain("fails", list);
    }

    [Fact]
    public async Task Pairing_state_and_cert_are_the_shared_verbs_on_the_runners_directory()
    {
        var code = await Run("server", "pair", "--max-role", "operator");
        Assert.Equal(0, code.Exit);
        Assert.Contains("Pairing code:", code.Out);
        Assert.Contains("No client is paired", (await Run("server", "clients")).Out);

        var state = await Run("state", "show");
        Assert.Equal(0, state.Exit);
        foreach (var module in new[] { "runner", "runs", "runner_progress", "scheduling", "definitions", "payloads", "audit", "notifications", "trust" }) Assert.Contains(module, state.Out);

        Assert.Equal(1, (await Run("cert", "check")).Exit);   // none yet
        Assert.Equal(0, (await Run("cert", "ensure")).Exit);
        Assert.Equal(0, (await Run("cert", "check")).Exit);
        Assert.Contains("Thumbprint", (await Run("cert", "show")).Out);
        Assert.True(File.Exists(Path.Combine(_directory, "tls.pfx")));

        var grants = await Run("state", "grants", "--login", "runner_svc", "--engine", "postgres");
        Assert.Equal(0, grants.Exit);
        Assert.Contains("GRANT", grants.Out);
    }

    [Fact]
    public async Task An_unknown_state_engine_stops_server_run_naming_the_problem()
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var router = RunnerCli.Router(name => name == RunnerSettings.StateEngineVariable ? "oracle" : null);

        var exit = await router.RunAsync(["server", "run", "--repo", _directory, "--listen", "https://127.0.0.1:0"], new CliContext(output, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        Assert.Contains("oracle", error.ToString());
    }
}
