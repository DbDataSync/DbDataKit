using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Client;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The package `dbdatabuild project publish --to runner --out` writes, sent to real runners with the client library the console's deploy uses (the command line no longer sends it; see <see cref="Publish"/>). The runners run in
/// process, with SQLite state and a stand-in file as the runtime package (staging only materializes it). No database engine and no published tool are needed, so it runs on
/// Windows as well. Publishing the same project twice sends no file the second time, two runners both end with the version, a channel's slots are bound before the first version is published and promotion moves one stored version to another
/// channel with that channel's own bindings.
/// </summary>
[Trait("Group", "runtime-package")]
public class ProjectPublishOnRunnerTests
{
    private const string Config = "defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\nrefresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC, max_runtime: 30m }\n";
    private const string Events = "name: staging.events\nkind: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n";
    private const string Fct = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n" +
                               "loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\nrefresh: { hourly: incremental }\n";

    internal static string Project()
    {
        var dir = Directory.CreateTempSubdirectory("ddb-e07-").FullName;
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); TestFiles.WriteAllText(p, text); }
        Write("dbdatabuild.yml", Config);
        Write("models/staging/events.yml", Events);
        Write("models/marts/fct_events.yml", Fct);
        Write("models/marts/fct_events.sql", "SELECT e.event_id, e.event_ts FROM staging.events e\n");
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null);
        Assert.True(exit == 0, o + e.ToString());
        return dir;
    }

    /// <summary>A runner of its own directory serving `dev` and `test`, following staged versions, with a stand-in `dbdatabuild-runtime` active in both.</summary>
    internal sealed class Runner : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("dbdr-e07-").FullName;
        private readonly List<string> _log = [];
        private WebApplication? _app;

        public StateDatabase State { get; private set; } = null!;
        public RunnerSettings Settings { get; private set; } = null!;
        public string Url { get; private set; } = "";

        /// <summary>With <paramref name="runtimeDirectory"/> (the published tool, <see cref="BuildRuntimePackage.Directory"/>) the runtime is the real one, so a task of a published package runs in a process of its own.</summary>
        public static async Task<Runner> StartAsync(string? runtimeDirectory = null)
        {
            var runner = new Runner();
            var data = runner._root;
            runner.Settings = new RunnerSettings(data, new Uri("https://127.0.0.1:0"))
            {
                Tick = TimeSpan.FromMilliseconds(100),
                InstanceHeartbeat = TimeSpan.FromSeconds(1),
                Channels = ["dev", "test"],
                Staging = true,
                TaskHostPath = RunnerTaskHost.Require(null, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "server-output.txt")).Trim()),
            };
            runner.State = RunnerState.Open(data, runner.Settings.State, null, runner.Settings.Group);

            // the runtime package: a manifest of the shape scripts/publish-runtime.sh writes and a file for its command; nothing here runs it
            var runtime = runtimeDirectory ?? Path.Combine(data, "runtime-src");
            if (runtimeDirectory is null)
            {
                Directory.CreateDirectory(Path.Combine(runtime, "files", "bin"));
                File.WriteAllText(Path.Combine(runtime, "files", "bin", BuildRuntimePackage.ToolFile), "stand-in for the engine");
                File.WriteAllText(Path.Combine(runtime, "package.yaml"), PackageManifestWriter.Write(BuildRuntimePackage.Manifest(BuildRuntimePackage.Rid, ProductVersion())));
            }
            foreach (var channel in new[] { "dev", "test" }) RunnerPackageInstaller.Install(runner.State, Path.Combine(runtime, "package.yaml"), Path.Combine(runtime, "files"), channel, "test");

            runner._app = RunnerServer.Build(runner.Settings, RunnerServer.Certificate(data), runner._log.Add, runner.State);
            await runner._app.StartAsync();
            runner.Url = runner._app.Urls.First();
            return runner;
        }

        private static string ProductVersion() => BuildRuntimePackage.Version;

        public string Code() => new PairingFiles(Settings.ServerYaml, Group: State).Codes().Create(ServerRoles.Close(ServerRole.Admin)).Code;

        public DefinitionHead? Head(string package, string channel) => new DefinitionPublisher(State, RunnerPackages.Kinds(), null, "runner").Head(RunnerPublishing.Kind, package, channel);

        public IReadOnlyList<DefinitionBinding> Bindings(string package, string channel) => new DefinitionPublisher(State, RunnerPackages.Kinds(), null, "runner").Bindings(channel, RunnerPublishing.Kind, package);

        public IReadOnlyList<string> InstanceStates(string package, string channel) =>
            [.. new DefinitionPublisher(State, RunnerPackages.Kinds(), null, "runner").Status.List(RunnerPublishing.Kind, package, channel).Select(s => $"{s.Version}:{s.State}")];

        /// <summary>Waits until the channel's active version is <paramref name="version"/> (staging prepared it here, found every runner of the group ready and activated it).</summary>
        public async Task WaitActiveAsync(string package, string channel, int version, int seconds = 120)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (Head(package, channel)?.Active == version) return;
                await Task.Delay(200);
            }

            throw new TimeoutException($"Waited {seconds} seconds for {package} on {channel} to be at version {version}: head {Head(package, channel)}, instances {string.Join(", ", InstanceStates(package, channel))}. The runner said:\n{string.Join('\n', _log)}");
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

    /// <summary>
    /// What the console's deploy does with a Build project's package, in two steps: `project publish --to runner --out` writes the package (the command line sends nothing), and the runner client library the deploy uses
    /// (<see cref="RunnerPublishClient"/>) binds the channel's slots and stages the version. With `--promote-from` nothing is written: the client binds the target channel and promotes the version the source channel serves. The answer is
    /// the command's `data` with what the runner said added (`version`, `mode`, `changed`, `uploaded`, `skipped`, `promoted_from`); a refusal of the runner is exit code 1 with its words.
    /// </summary>
    internal static (int Exit, JsonNode Doc, string Text) Publish(string project, string clientDir, params string[] args)
    {
        string? server = null, code = null, channel = null, from = null;
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server": server = args[++i]; break;
                case "--code": code = args[++i]; break;
                case "--channel": channel = args[++i]; break;
                case "--promote-from": from = args[++i]; break;
                case "--bind": var eq = args[++i].IndexOf('='); bindings[args[i][..eq]] = args[i][(eq + 1)..]; break;
                default: rest.Add(args[i]); break;
            }
        }

        Assert.True(server is not null && channel is not null, "a publish names the runner and the channel");
        var client = new RunnerPublishClient(new RunnerClientStore(clientDir), _ => null);
        try
        {
            if (from is not null)
            {
                if (bindings.Count > 0) client.BindAsync(server!, code, "sales", channel!, bindings).GetAwaiter().GetResult();
                var promoted = client.PromoteAsync(server!, bindings.Count > 0 ? null : code, "sales", from, channel!).GetAwaiter().GetResult();
                return (0, new JsonObject { ["data"] = new JsonObject { ["package"] = "sales", ["channel"] = channel, ["version"] = promoted.Version, ["promoted_from"] = from } }, $"{promoted.State}. {promoted.Message}");
            }

            var folder = Directory.CreateTempSubdirectory("ddb-e07-out-").FullName;
            try
            {
                var o = new StringWriter(); var e = new StringWriter();
                var exit = CliApp.Run(["project", "publish", "--to", "runner", "--project", project, "--package", "sales", "--out", folder, "--format", "json", .. rest], o, e, environment: _ => null);
                if (exit != 0) return (exit, JsonNode.Parse(o.ToString())!, o + "\n" + e);
                var doc = JsonNode.Parse(o.ToString())!;
                if (bindings.Count > 0) client.BindAsync(server!, code, "sales", channel!, bindings).GetAwaiter().GetResult();
                var published = client.PublishAsync(server!, bindings.Count > 0 ? null : code, Path.Combine(folder, "package.yaml"), Path.Combine(folder, "files"), channel!, stage: true, expectedVersion: null).GetAwaiter().GetResult();
                var data = doc["data"]!;
                data["channel"] = published.Channel;
                data["version"] = published.Version;
                data["mode"] = published.Mode;
                data["changed"] = published.Changed;
                data["uploaded"] = published.Uploaded;
                data["skipped"] = published.Skipped;
                return (0, doc, o + "\n" + e);
            }
            finally { try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ } }
        }
        catch (RunnerClientException ex) { return (1, new JsonObject(), ex.Message); }
    }

    internal static JsonNode Data((int Exit, JsonNode Doc, string Text) result)
    {
        Assert.True(result.Exit == 0, result.Text);
        return result.Doc["data"]!;
    }

    [Fact]
    public async Task Publishing_the_same_project_twice_sends_no_file_the_second_time_and_the_version_stays()
    {
        var project = Project();
        var client = Path.Combine(project, "client");
        await using var runner = await Runner.StartAsync();

        var first = Data(Publish(project, client, "--server", runner.Url, "--code", runner.Code(), "--channel", "dev", "--bind", "sqlserver=wh_dev"));
        Assert.True(first["uploaded"]!.GetValue<int>() > 0);
        Assert.Equal("stage", first["mode"]!.GetValue<string>());
        Assert.True(first["changed"]!.GetValue<bool>());
        Assert.Equal(["sqlserver-hourly"], first["tasks"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        var version = first["version"]!.GetValue<int>();

        var second = Data(Publish(project, client, "--server", runner.Url, "--channel", "dev"));
        Assert.Equal(0, second["uploaded"]!.GetValue<int>());
        Assert.Equal(first["uploaded"]!.GetValue<int>() + first["skipped"]!.GetValue<int>(), second["skipped"]!.GetValue<int>());
        Assert.False(second["changed"]!.GetValue<bool>());
        Assert.Equal(version, second["version"]!.GetValue<int>());

        // staged, prepared by the runner, and activated: the project's files and its task are on the runner as the manifest says
        await runner.WaitActiveAsync("sales", "dev", version);
        Assert.Equal(new[] { "sqlserver" }, runner.Bindings("sales", "dev").Select(b => b.Slot));
    }

    [Fact]
    public async Task Staging_on_two_runners_ends_with_the_version_ready_on_both_and_the_channel_active()
    {
        var project = Project();
        var client = Path.Combine(project, "client");
        await using var one = await Runner.StartAsync();
        await using var two = await Runner.StartAsync();

        foreach (var runner in new[] { one, two })
        {
            var published = Data(Publish(project, client, "--server", runner.Url, "--code", runner.Code(), "--channel", "dev", "--bind", "sqlserver=wh_dev"));
            await runner.WaitActiveAsync("sales", "dev", published["version"]!.GetValue<int>());
            Assert.All(runner.InstanceStates("sales", "dev"), s => Assert.True(s.EndsWith(":ready", StringComparison.Ordinal) || s.EndsWith(":active", StringComparison.Ordinal), s));     // ready on this runner, and active once the group is ready (the instance reports active a pass later)
            Assert.NotEmpty(runner.InstanceStates("sales", "dev"));
        }
    }

    [Fact]
    public async Task A_version_whose_slots_are_not_bound_in_the_channel_is_refused_naming_the_slot_and_the_channel()
    {
        var project = Project();
        var client = Path.Combine(project, "client");
        await using var runner = await Runner.StartAsync();

        var refused = Publish(project, client, "--server", runner.Url, "--code", runner.Code(), "--channel", "dev");

        Assert.Equal(1, refused.Exit);
        Assert.Contains("'sqlserver'", refused.Text);
        Assert.Contains("channel 'dev'", refused.Text);
        Assert.Null(runner.Head("sales", "dev"));
    }

    [Fact]
    public async Task Promotion_moves_the_stored_version_to_another_channel_and_binds_that_channels_own_connections()
    {
        var project = Project();
        var client = Path.Combine(project, "client");
        await using var runner = await Runner.StartAsync();
        var version = Data(Publish(project, client, "--server", runner.Url, "--code", runner.Code(), "--channel", "dev", "--bind", "sqlserver=wh_dev"))["version"]!.GetValue<int>();
        await runner.WaitActiveAsync("sales", "dev", version);

        // without the channel's own binding the runner refuses, naming the slot and the channel
        var unbound = Publish(project, client, "--server", runner.Url, "--channel", "test", "--promote-from", "dev");
        Assert.Equal(1, unbound.Exit);
        Assert.Contains("'sqlserver'", unbound.Text);
        Assert.Contains("channel 'test'", unbound.Text);

        var promoted = Data(Publish(project, client, "--server", runner.Url, "--channel", "test", "--promote-from", "dev", "--bind", "sqlserver=wh_test"));
        Assert.Equal(version, promoted["version"]!.GetValue<int>());
        Assert.Equal("dev", promoted["promoted_from"]!.GetValue<string>());
        Assert.Equal(version, runner.Head("sales", "test")!.Active);
        Assert.Equal("wh_dev", runner.Bindings("sales", "dev").Single().BoundTo);
        Assert.Equal("wh_test", runner.Bindings("sales", "test").Single().BoundTo);

        // nothing to promote from a channel that serves nothing (bound first, so it is the missing version the runner-side check names)
        var none = Publish(project, client, "--server", runner.Url, "--channel", "test", "--promote-from", "nowhere", "--bind", "sqlserver=wh_test");
        Assert.Equal(1, none.Exit);
        Assert.Contains("no active version in channel 'nowhere'", none.Text);
    }

    [Fact]
    public async Task A_published_task_is_the_runners_to_schedule_from_the_first_activation_and_no_owner_is_recorded()
    {
        var project = Project();
        var client = Path.Combine(project, "client");
        await using var runner = await Runner.StartAsync();
        var owners = new TaskOwnerStore(runner.State);

        // the Build server's scheduler is gone (plan 33 SC23): nothing is handed back to it, so the runner schedules the plan as soon as the version is active
        var first = Data(Publish(project, client, "--server", runner.Url, "--code", runner.Code(), "--channel", "dev", "--bind", "sqlserver=wh_dev"));
        Assert.Null(first["owner_old"]);
        Assert.Null(owners.Get("sales", "dev", "sqlserver-hourly"));
        await runner.WaitActiveAsync("sales", "dev", first["version"]!.GetValue<int>());

        // --runner-schedules is still accepted, and changes nothing
        var plain = Data(Publish(project, client, "--server", runner.Url, "--channel", "test", "--bind", "sqlserver=wh_test", "--runner-schedules"));
        Assert.Null(plain["owner_old"]);
        Assert.Null(owners.Get("sales", "test", "sqlserver-hourly"));
    }
}
