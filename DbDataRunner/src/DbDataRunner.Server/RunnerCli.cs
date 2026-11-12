using DbDataKit.Runner.Connections;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Client;
using System.Globalization;
using System.Net.Http;
using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Server;

/// <summary>
/// The command line, in the grammar every DbData product shares (plan 13, C02): <c>dbdatarunner server run|pair|clients|unpair</c>, <c>state</c>, <c>service</c>, <c>cert</c> and <c>health check</c>, and the runner's own
/// nouns: <c>runs list</c>, <c>tasks list|run|pause|resume</c> and <c>package install</c>. <c>--repo</c> names the server's directory (<see cref="RunnerSettings.DirectoryVariable"/> by default), as it does for the other
/// products' pairing verbs. The verbs that are not <c>server run</c> read and write the same state the server does, so they work with the server stopped (what they queue, a running server runs).
/// </summary>
public static class RunnerCli
{
    public const string Program = "dbdatarunner";

    /// <summary>Where <c>health check</c> asks by default: the server's own address.</summary>
    public const string HealthUrl = "https://localhost:8495/health/live";

    private const string Where = "[--repo <server directory>]";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, CliContext.Console());

    public static async Task<int> RunAsync(string[] args, CliContext context)
    {
        // Ctrl+C and the service manager's stop both end `server run` cleanly.
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        try { return await Router().RunAsync(args, context with { Cancellation = context.Cancellation.CanBeCanceled ? context.Cancellation : stop.Token }); }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    public static NounRouter Router(Func<string, string?>? environment = null) => new(Program,
    [
        ServerNoun.Create((args, context) => RunServerAsync(args, context, environment))
            .With([.. PairingVerbs.Create(args => RunnerState.Pairing(DirectoryOf(args, environment), RunnerSettings.StateFrom(DirectoryOf(args, environment), environment), environment))]),
        RunsNoun(environment),
        TasksNoun(environment),
        RunnerLabelVerbs.Create(environment),
        RunnerLineageNoun.Create(environment),
        PackageNoun(environment),
        StateNoun(environment),
        RunnerService.Noun(RunnerServiceEnvironment.Real()),
        RunnerService.CertNoun(environment),
        HealthNoun.Create(new HttpHealthProbe(LoopbackTolerantClient()), HealthUrl),
    ], RunnerServer.Version, () => SuiteEnvironment.OldNameProblem(environment));

    /// <summary>The shared <c>state show|test|grants</c> verbs over the runner's state: SQLite in <c>state.db</c> in its directory unless the environment names a server engine (<c>--engine</c> overrides the engine), built from <see cref="RunnerState.Modules"/>.</summary>
    private static Noun StateNoun(Func<string, string?>? environment) => DbDataKit.State.Cli.StateVerbs.Create(new DbDataKit.State.Cli.StateVerbContext(
        Program, "the runner",
        args =>
        {
            var directory = DirectoryOf(args, environment);
            return DbDataKit.State.Cli.StateTarget.From(RunnerState.Resolve(directory, environment, args.Get("engine"), "--engine"), RunnerState.PathIn(directory), () => RunnerState.Secrets(environment), RunnerState.SettingsFile(directory));
        },
        RunnerState.Modules, RunnerState.ScopeOf(), ["engine", "repo"], "[--engine mssql|postgres] [--repo <server directory>]"));

    /// <summary>The server's certificate is self-signed, so a probe of its own address cannot validate it; it is accepted for a loopback address only, where the answer is this machine and the probe reads a status code and no body.</summary>
    private static HttpClient LoopbackTolerantClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri is { IsLoopback: true },
    });

    private static string DirectoryOf(CliArgs args, Func<string, string?>? environment) =>
        Path.GetFullPath(args.Get("repo") ?? RunnerSettings.DefaultDirectory(environment));

    private static string DirectoryOf(IReadOnlyList<string> args, Func<string, string?>? environment)
    {
        // the last one wins, so an option given to the service command after the registered default overrides it
        var i = -1;
        for (var k = 0; k < args.Count; k++) if (args[k] == "--repo") i = k;
        return Path.GetFullPath(i >= 0 && i + 1 < args.Count ? args[i + 1] : RunnerSettings.DefaultDirectory(environment));
    }

    private static async Task<int> RunServerAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "listen", "channels", "group" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: server run [--repo <server directory>] [--listen https://<address>:<port>] [--channels <name>[,<name>...]] [--group <name>].");
            return CliExit.Usage;
        }
        if (!Uri.TryCreate(parsed.Get("listen") ?? RunnerSettings.DefaultListen, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("--listen is an https address, such as https://127.0.0.1:8495.");
            return CliExit.Usage;
        }

        RunnerSettings settings;
        try
        {
            var resolvedState = RunnerState.Resolve(DirectoryOf(args, environment), environment);
            settings = new RunnerSettings(DirectoryOf(args, environment), listen) { State = resolvedState.Settings, StateSources = resolvedState.Sources, Channels = RunnerSettings.ChannelsFrom(environment, parsed.Get("channels")), MaxArtifactBytes = RunnerSettings.MaxArtifactBytesFrom(environment), ActivationMaxWait = RunnerSettings.ActivationMaxWaitFrom(environment), Group = RunnerSettings.GroupFrom(environment, parsed.Get("group")), TaskEnvironment = RunnerSettings.TaskEnvironmentFrom(environment) };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Usage;
        }

        Directory.CreateDirectory(settings.Directory);
        StateDatabase state;
        try { state = RunnerState.Open(settings.Directory, settings.State, environment, settings.Group); }
        catch (InvalidOperationException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }

        var app = RunnerServer.Build(settings, RunnerServer.Certificate(settings.Directory), line => context.Error.WriteLine(line), state);
        await context.Out.WriteLineAsync($"Serving on {listen} (instance {RunnerServer.InstanceId(state)}, state on {state.Dialect.Engine}, channels {string.Join(", ", settings.Channels)}). Pair a client with: dbdatarunner server pair --max-role <roles> [--repo <server directory>]");
        try
        {
            // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way; the runner stops its tasks with their grace period first
            await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        }
        catch (InvalidOperationException ex)
        {
            // for instance, the task host is missing: said once, with what to do
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }

        return CliExit.Ok;
    }

    // ---- local verbs, on the same state the server uses

    private static StateDatabase OpenState(CliArgs parsed, Func<string, string?>? environment) =>
        RunnerState.Open(DirectoryOf(parsed, environment), RunnerSettings.StateFrom(DirectoryOf(parsed, environment), environment), environment);

    private static RunnerControl ControlOf(CliArgs parsed, StateDatabase state, Func<string, string?>? environment) =>
        RunnerControl.ForCommands(state, RunnerSettings.ChannelsFrom(environment), Path.Combine(DirectoryOf(parsed, environment), "cache"));

    private static readonly string RunSchema = DataSchemas.Object(
        [("id", DataSchemas.String), ("package", DataSchemas.String), ("channel", DataSchemas.String), ("task", DataSchemas.String), ("version", DataSchemas.String), ("attempt", DataSchemas.Integer), ("outcome", DataSchemas.String), ("started", DataSchemas.String)],
        "id", "outcome", "started");

    private static Noun RunsNoun(Func<string, string?>? environment) => new("runs", "The run history of the tasks",
    [
        new Verb("list", "The runs, newest first: when, how it ended, which task and version, the attempt and why it failed", EffectClass.OfflineOnly,
            $"[--package <name>] [--channel <name>] [--task <name>] [--outcome <outcome>] [--limit <n>] {Where}",
            (args, context) => RunsListAsync(args, context, environment),
            DataSchemas.Object([("runs", DataSchemas.ListOf(RunSchema))], "runs")),
    ]);

    private static async Task<int> RunsListAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "package", "channel", "task", "outcome", "limit", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: runs list [--package <name>] [--channel <name>] [--task <name>] [--outcome <outcome>] [--limit <n>] {Where}");
            return CliExit.Usage;
        }

        RunOutcome? outcome = null;
        if (parsed.Get("outcome") is { } text)
        {
            if (!Enum.TryParse<RunOutcome>(text, ignoreCase: true, out var one) || !Enum.IsDefined(one))
            {
                await context.Error.WriteLineAsync($"'{text}' is not an outcome ({string.Join(", ", Enum.GetNames<RunOutcome>().Select(n => n.ToLowerInvariant()))}).");
                return CliExit.Usage;
            }
            outcome = one;
        }

        var limit = 20;
        if (parsed.Get("limit") is { } limitText && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 1000))
        {
            await context.Error.WriteLineAsync("--limit is a whole number from 1 to 1000.");
            return CliExit.Usage;
        }

        string? package = parsed.Get("package"), channel = parsed.Get("channel"), task = parsed.Get("task");
        if (task is not null && (package is null || channel is null))
        {
            await context.Error.WriteLineAsync("--task needs --package and --channel.");
            return CliExit.Usage;
        }

        var state = OpenState(parsed, environment);
        var runs = new RunStore(state, RunnerState.EngineService).Read(new RunQuery(RunnerState.EngineService, task is null ? null : $"{package}/{channel}/{task}", outcome, Limit: limit, Package: package, Channel: channel));
        if (runs.Count == 0) await context.Out.WriteLineAsync("No run yet.");
        foreach (var run in runs)
        {
            var failure = run.Run.Failure is { } f ? $"  {f.Kind}: {f.Message}" : "";
            await context.Out.WriteLineAsync($"{run.Run.Started.UtcDateTime:yyyy-MM-dd HH:mm:ss}  {run.Run.Outcome.ToString().ToLowerInvariant(),-10} {run.Run.JobId}  v{run.PackageVersion}  attempt {run.Attempt}  {run.Id}{failure}");
        }

        context.Data["runs"] = runs.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r.Id.ToString(),
            ["package"] = r.Package,
            ["channel"] = r.Channel,
            ["task"] = TaskRef.TryParse(r.Run.JobId, out var reference) ? reference.Task : r.Run.JobId,
            ["version"] = r.PackageVersion,
            ["attempt"] = r.Attempt,
            ["outcome"] = r.Run.Outcome.ToString().ToLowerInvariant(),
            ["started"] = r.Run.Started.UtcDateTime.ToString("O"),
        }).ToList();
        return CliExit.Ok;
    }

    private static Noun TasksNoun(Func<string, string?>? environment) => new("tasks", "The scheduled tasks of the installed packages",
    [
        new Verb("list", "The tasks of the channels this runner serves: version, next due, the last run, whether paused, and a problem with the task's row", EffectClass.OfflineOnly, Where,
            (args, context) => TasksListAsync(args, context, environment),
            DataSchemas.Object([("tasks", DataSchemas.ListOf(DataSchemas.Object([("task", DataSchemas.String), ("version", DataSchemas.String), ("paused", DataSchemas.Boolean)], "task", "version", "paused")))], "tasks")),
        new Verb("run", "Queue a run of the task now, apart from its schedule (a running server runs it); inputs are name=value words; with --dry-run the task reads what it reads and nothing is written or sent, and the log lists what would have been", EffectClass.TargetDataWrites,
            $"<package>/<channel>/<task> [name=value ...] [--dry-run] {Where}", (args, context) => TaskActAsync("run", args, context, environment),
            DataSchemas.Object([("task", DataSchemas.String), ("queued", DataSchemas.Boolean), ("run_id", DataSchemas.String), ("dry_run", DataSchemas.Boolean)], "task", "queued")),
        new Verb("pause", "Stop the task from being queued at its scheduled times (a run going is not touched; run-now still works)", EffectClass.TargetDataWrites,
            $"<package>/<channel>/<task> {Where}", (args, context) => TaskActAsync("pause", args, context, environment),
            DataSchemas.Object([("task", DataSchemas.String), ("paused", DataSchemas.Boolean)], "task", "paused")),
        new Verb("resume", "Let a paused task be queued at its scheduled times again", EffectClass.TargetDataWrites,
            $"<package>/<channel>/<task> {Where}", (args, context) => TaskActAsync("resume", args, context, environment),
            DataSchemas.Object([("task", DataSchemas.String), ("paused", DataSchemas.Boolean)], "task", "paused")),
    ]);

    private static async Task<int> TasksListAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: tasks list {Where}");
            return CliExit.Usage;
        }

        var state = OpenState(parsed, environment);
        var status = ControlOf(parsed, state, environment).Status();
        if (status.Tasks.Count == 0) await context.Out.WriteLineAsync("No task: install a package with 'package install'.");
        foreach (var t in status.Tasks)
        {
            var last = t.LastRun is { } run ? $"last {run.Run.Outcome.ToString().ToLowerInvariant()} {run.Run.Started.UtcDateTime:yyyy-MM-dd HH:mm}" : "never run";
            var next = t.NextDue is { } due ? $"next {due.UtcDateTime:yyyy-MM-dd HH:mm}" : "no schedule";
            await context.Out.WriteLineAsync($"{t.Task}  v{t.Version}  {next}  {last}{(t.Paused ? $"  PAUSED by {t.PausedBy}" : "")}{(t.Problem is null ? "" : $"  PROBLEM: {t.Problem}")}{(t.WaitingReason is null ? "" : $"  WAITING: {t.WaitingReason}")}");
        }

        context.Data["tasks"] = status.Tasks.Select(t => new Dictionary<string, object?> { ["task"] = t.Task.ToString(), ["version"] = t.Version, ["paused"] = t.Paused, ["next_due"] = t.NextDue?.UtcDateTime.ToString("O"), ["problem"] = t.Problem, ["waiting_reason"] = t.WaitingReason }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> TaskActAsync(string act, string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, act == "run" ? new HashSet<string> { "dry-run" } : new HashSet<string>(), new HashSet<string> { "repo" });
        var usage = act == "run" ? $"Usage: tasks run <package>/<channel>/<task> [name=value ...] [--dry-run] {Where}" : $"Usage: tasks {act} <package>/<channel>/<task> {Where}";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count == 0 || (act != "run" && parsed.Words.Count != 1))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        if (!TaskRef.TryParse(parsed.Words[0], out var task))
        {
            await context.Error.WriteLineAsync($"'{parsed.Words[0]}' is not a task reference: write package/channel/task, each a name such as orders-sync/prod/extract.");
            return CliExit.Usage;
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in parsed.Words.Skip(1))
        {
            var at = word.IndexOf('=');
            if (at < 1)
            {
                await context.Error.WriteLineAsync($"'{word}' is not an input: write name=value.");
                return CliExit.Usage;
            }
            inputs[word[..at]] = word[(at + 1)..];
        }

        var state = OpenState(parsed, environment);
        var control = ControlOf(parsed, state, environment);
        var actor = RunnerActor.Local();
        try
        {
            switch (act)
            {
                case "run":
                    var dry = parsed.Has("dry-run");
                    var result = control.RunNow(task, inputs, actor, dry);
                    context.Data["task"] = task.ToString();
                    context.Data["queued"] = result.Queued;
                    context.Data["dry_run"] = dry;
                    context.Data["run_id"] = result.RunId?.ToString();
                    await context.Out.WriteLineAsync(result.Queued
                        ? $"Queued {(dry ? "a dry run of " : "")}{task} (run {result.RunId}). A running server starts it within a few seconds.{(dry ? " The log of the run lists what it would have sent; nothing is written." : "")}"
                        : $"{task} is queued or running already; nothing was queued.");
                    return result.Queued ? CliExit.Ok : CliExit.Failure;
                case "pause":
                    control.Pause(task, actor);
                    context.Data["task"] = task.ToString();
                    context.Data["paused"] = true;
                    await context.Out.WriteLineAsync($"Paused {task}. A run going is not touched; 'tasks run' still starts one.");
                    return CliExit.Ok;
                default:
                    control.Resume(task, actor);
                    context.Data["task"] = task.ToString();
                    context.Data["paused"] = false;
                    await context.Out.WriteLineAsync($"Resumed {task}.");
                    return CliExit.Ok;
            }
        }
        catch (UnknownTaskException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static Noun PackageNoun(Func<string, string?>? environment) => new("package", "Packages in a runner: publish to a server's API as a paired publisher, or install into this runner's own state (the development path)",
    [
        new Verb("pair", "Pair this machine with a runner as a publisher, using the single-use code its administrator made with 'server pair --max-role publisher'; the runner's certificate is pinned", EffectClass.RepoFilesOnly,
            "<https address> --code <code> [--name <label>] [--client-dir <directory>]", (args, context) => PackagePairAsync(args, context, environment),
            DataSchemas.Object([("name", DataSchemas.String), ("url", DataSchemas.String), ("instance_id", DataSchemas.String), ("fingerprint", DataSchemas.String)], "name", "url", "instance_id", "fingerprint")),
        new Verb("publish", "Publish a package to a runner's API: uploads only the files the runner does not have, then publishes the version (served at once, or staged with --stage)", EffectClass.TargetDataWrites,
            "<manifest.yaml> <directory> --server <name or https address> --channel <name> [--code <code>] [--stage] [--expected-version <n>] [--client-dir <directory>]", (args, context) => PackagePublishAsync(args, context, environment),
            DataSchemas.Object([("package", DataSchemas.String), ("channel", DataSchemas.String), ("version", DataSchemas.Integer), ("changed", DataSchemas.Boolean), ("mode", DataSchemas.String), ("uploaded", DataSchemas.Integer), ("skipped", DataSchemas.Integer), ("signature", DataSchemas.String), ("tasks", DataSchemas.StringList)], "package", "channel", "version", "changed", "mode", "uploaded", "skipped")),
        new Verb("promote", "Promote a package to another runner group (plan 30 PB06): the same version, read from one runner and sent by hash to another (files it has are not sent again), published there in the channel asked with its signature, which the target's trust store must trust; the slots of that channel are the target's own (--bind) and an unbound one refuses naming it", EffectClass.TargetDataWrites,
            "<package> --from-server <name or https address> --server <name or https address> --channel <name> [--from-channel <name>, default prod] [--version <n>] [--stage] [--bind <slot>=<connection>[,<slot>=<connection>...]] [--from-code <code>] [--code <code>] [--client-dir <directory>]", (args, context) => PackagePromoteAsync(args, context, environment),
            DataSchemas.Object([("package", DataSchemas.String), ("from_version", DataSchemas.Integer), ("version", DataSchemas.Integer), ("channel", DataSchemas.String), ("changed", DataSchemas.Boolean), ("mode", DataSchemas.String), ("sent", DataSchemas.Integer), ("skipped", DataSchemas.Integer), ("signature", DataSchemas.String)], "package", "from_version", "version", "channel", "changed", "mode", "sent", "skipped")),
        new Verb("require-signatures", "Show, or with on or off set, whether this runner refuses a version that is not signed (plan 30 section 6.2; the signature check itself is not built yet, so 'on' refuses every publish)", EffectClass.TargetDataWrites,
            $"[on|off] {Where}", (args, context) => PackageRequireSignaturesAsync(args, context, environment),
            DataSchemas.Object([("require_signatures", DataSchemas.Boolean)], "require_signatures")),
        new Verb("require-sandbox-run", "Show, or with on or off set, whether a version reaches production only after a sandbox run: a run that succeeded in a channel other than prod, where its connections are bound to that channel's own (plan 27a IX16; a dry run does not count)", EffectClass.TargetDataWrites,
            $"[on|off] {Where}", (args, context) => PackageRequireSandboxRunAsync(args, context, environment),
            DataSchemas.Object([("require_sandbox_run", DataSchemas.Boolean)], "require_sandbox_run")),
        new Verb("allow-production-binding", "Choose, as an explicit and recorded act, that a channel other than prod uses the connection prod uses for a slot of the package (plan 27a IX17): what the task sends or writes there reaches production's far side. Without --confirm nothing changes; --revoke takes the choice back", EffectClass.TargetDataWrites,
            $"<package> <slot> <channel> [--confirm] [--revoke] [--note <text>] {Where}", (args, context) => PackageAllowProductionBindingAsync(args, context, environment),
            DataSchemas.Object([("package", DataSchemas.String), ("slot", DataSchemas.String), ("channel", DataSchemas.String), ("allowed", DataSchemas.Boolean)], "package", "slot", "channel", "allowed")),
        new Verb("install", "Put a package into this runner's own state, served at once: reads a package/1 manifest and a directory of files, stores them, makes them the channel's active version and replaces the channel's task schedule", EffectClass.TargetDataWrites,
            $"<manifest.yaml> <directory> [--channel <name>] {Where}", (args, context) => PackageInstallAsync(args, context, environment),
            DataSchemas.Object([("package", DataSchemas.String), ("channel", DataSchemas.String), ("version", DataSchemas.Integer), ("changed", DataSchemas.Boolean), ("files", DataSchemas.Integer), ("tasks", DataSchemas.StringList)], "package", "channel", "version", "changed")),
        new Verb("build", IntegrationVerbs.BuildSummary, EffectClass.RepoFilesOnly,
            $"<project directory> --out <directory> {IntegrationVerbs.Options}", (args, context) => IntegrationVerbs.BuildAsync(args, context, environment), IntegrationVerbs.BuildSchema),
        new Verb("test", IntegrationVerbs.TestSummary, EffectClass.RepoFilesOnly,
            $"<project directory> {IntegrationVerbs.Options}", (args, context) => IntegrationVerbs.TestAsync(args, context, environment), IntegrationVerbs.TestSchema),
    ]);

    private static async Task<int> PackageInstallAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "channel", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 2)
        {
            await context.Error.WriteLineAsync($"Usage: package install <manifest.yaml> <directory> [--channel <name>] {Where}");
            return CliExit.Usage;
        }

        var channel = parsed.Get("channel") ?? RunnerPackages.DevChannel;
        var state = OpenState(parsed, environment);
        try
        {
            var installed = RunnerPackageInstaller.Install(state, parsed.Words[0], parsed.Words[1], channel, "local:" + Environment.UserName);
            await context.Out.WriteLineAsync(installed.Changed
                ? $"Installed {installed.Package} on {channel}: version {installed.Version}, {installed.Files} file(s), tasks {string.Join(", ", installed.Tasks)}."
                : $"{installed.Package} on {channel} is at version {installed.Version} already with this content; its task schedule was refreshed.");
            context.Data["package"] = installed.Package;
            context.Data["channel"] = installed.Channel;
            context.Data["version"] = installed.Version;
            context.Data["changed"] = installed.Changed;
            context.Data["files"] = installed.Files;
            context.Data["tasks"] = installed.Tasks.ToList();
            return CliExit.Ok;
        }
        catch (PackageInstallException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> PackagePairAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "code", "name", "client-dir" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("code") is not { } code || !Uri.TryCreate(parsed.Words[0], UriKind.Absolute, out var url))
        {
            await context.Error.WriteLineAsync("Usage: package pair <https address> --code <code> [--name <label>] [--client-dir <directory>]");
            return CliExit.Usage;
        }

        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(parsed.Get("client-dir") ?? RunnerClientStore.DefaultDirectory(environment)), environment);
            var runner = await client.PairAsync(url, code, parsed.Get("name"), context.Cancellation);
            await context.Out.WriteLineAsync($"Paired with {runner.Name} ({runner.InstanceId}) at {runner.Url} with the roles {string.Join(", ", runner.Roles)}. Its certificate is pinned: {runner.Fingerprint}");
            context.Data["name"] = runner.Name;
            context.Data["url"] = runner.Url;
            context.Data["instance_id"] = runner.InstanceId;
            context.Data["fingerprint"] = runner.Fingerprint;
            return CliExit.Ok;
        }
        catch (RunnerClientException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> PackagePromoteAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        const string usage = "Usage: package promote <package> --from-server <name or https address> --server <name or https address> --channel <name> [--from-channel <name>] [--version <n>] [--stage] [--bind <slot>=<connection>[,...]] [--from-code <code>] [--code <code>] [--client-dir <directory>]";
        var parsed = CliArgs.Parse(args, new HashSet<string> { "stage" }, new HashSet<string> { "from-server", "server", "channel", "from-channel", "version", "bind", "from-code", "code", "client-dir" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("from-server") is not { } fromServer || parsed.Get("server") is not { } server || parsed.Get("channel") is not { } channel)
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        int? version = null;
        if (parsed.Get("version") is { } versionText)
        {
            if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            {
                await context.Error.WriteLineAsync("--version is a whole number of 1 or more.");
                return CliExit.Usage;
            }
            version = number;
        }

        Dictionary<string, string>? bindings = null;
        if (parsed.Get("bind") is { } bindText)
        {
            bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in bindText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var at = pair.IndexOf('=');
                if (at < 1 || at == pair.Length - 1)
                {
                    await context.Error.WriteLineAsync($"--bind is <slot>=<connection>[,<slot>=<connection>...]; '{pair}' is not.");
                    return CliExit.Usage;
                }
                bindings[pair[..at].Trim()] = pair[(at + 1)..].Trim();
            }
        }

        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(parsed.Get("client-dir") ?? RunnerClientStore.DefaultDirectory(environment)), environment);
            var request = new DbDataKit.State.PromoteRequest("integration", parsed.Words[0], parsed.Get("from-channel") ?? "prod", channel, version, parsed.Has("stage") ? "stage" : null, bindings);
            var result = await client.PromoteToGroupAsync(fromServer, parsed.Get("from-code"), server, parsed.Get("code"), request, line => context.Out.WriteLine(line), context.Cancellation);
            await context.Out.WriteLineAsync(
                $"{(result.Mode == "stage" ? "Staged" : "Promoted")} {result.Name} on {server} {channel}: version {result.TargetVersion} (version {result.SourceVersion} of {fromServer} {result.FromChannel}){(result.Changed ? "" : ", the same content as before, nothing changed")}, {result.Sent} file(s) sent, {result.Skipped} already there, signature {result.Signature}."
                + (result.Mode == "stage" ? " It is not run until an administrator activates it." : ""));
            context.Data["package"] = result.Name;
            context.Data["from_version"] = result.SourceVersion;
            context.Data["version"] = result.TargetVersion;
            context.Data["channel"] = result.ToChannel;
            context.Data["changed"] = result.Changed;
            context.Data["mode"] = result.Mode;
            context.Data["sent"] = result.Sent;
            context.Data["skipped"] = result.Skipped;
            context.Data["signature"] = result.Signature;
            return CliExit.Ok;
        }
        catch (RunnerClientException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> PackagePublishAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        const string usage = "Usage: package publish <manifest.yaml> <directory> --server <name or https address> --channel <name> [--code <code>] [--stage] [--expected-version <n>] [--client-dir <directory>]";
        var parsed = CliArgs.Parse(args, new HashSet<string> { "stage" }, new HashSet<string> { "server", "channel", "code", "expected-version", "client-dir" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 2 || parsed.Get("server") is not { } server || parsed.Get("channel") is not { } channel)
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        int? expected = null;
        if (parsed.Get("expected-version") is { } expectedText)
        {
            if (!int.TryParse(expectedText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                await context.Error.WriteLineAsync("--expected-version is a whole number (0 for a first publish).");
                return CliExit.Usage;
            }
            expected = number;
        }

        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(parsed.Get("client-dir") ?? RunnerClientStore.DefaultDirectory(environment)), environment);
            var result = await client.PublishAsync(server, parsed.Get("code"), parsed.Words[0], parsed.Words[1], channel, parsed.Has("stage"), expected, line => context.Out.WriteLine(line), context.Cancellation);
            await context.Out.WriteLineAsync(
                $"{(result.Mode == "stage" ? "Staged" : "Published")} {result.Package} on {channel}: version {result.Version}{(result.Changed ? "" : " (the same content as before, nothing changed)")}, {result.Uploaded} file(s) uploaded, {result.Skipped} already there, signature {result.Signature}."
                + (result.Mode == "stage" ? " It is not run until an administrator activates it." : ""));
            context.Data["package"] = result.Package;
            context.Data["channel"] = result.Channel;
            context.Data["version"] = result.Version;
            context.Data["changed"] = result.Changed;
            context.Data["mode"] = result.Mode;
            context.Data["uploaded"] = result.Uploaded;
            context.Data["skipped"] = result.Skipped;
            context.Data["signature"] = result.Signature;
            context.Data["tasks"] = result.Tasks.ToList();
            return CliExit.Ok;
        }
        catch (RunnerClientException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> PackageAllowProductionBindingAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "confirm", "revoke" }, new HashSet<string> { "repo", "note" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 3)
        {
            await context.Error.WriteLineAsync($"Usage: package allow-production-binding <package> <slot> <channel> [--confirm] [--revoke] [--note <text>] {Where}");
            return CliExit.Usage;
        }

        var (package, slot, channel) = (parsed.Words[0], parsed.Words[1], parsed.Words[2]);
        if (channel == "prod")
        {
            await context.Error.WriteLineAsync("prod is the channel that uses production's connection; the choice is for another channel.");
            return CliExit.Usage;
        }

        var guard = new PostureGuard(OpenState(parsed, environment), RunnerNames.ServiceId);
        var scope = RunnerPublishing.PostureScope(package, slot, channel);   // the one record: the bind-time confirmation and the Bindings page write the same scope
        var actor = RunnerActor.Local().Id;
        context.Data["package"] = package;
        context.Data["slot"] = slot;
        context.Data["channel"] = channel;
        if (parsed.Has("revoke"))
        {
            var revoked = guard.Revoke(PostureSetting.ProductionBindingOnNonProduction, scope, actor, parsed.Get("note"));
            context.Data["allowed"] = false;
            await context.Out.WriteLineAsync(revoked ? $"Taken back: {channel} no longer may use production's connection for {slot} of {package}." : $"There was no choice for {slot} of {package} in {channel}.");
            return CliExit.Ok;
        }

        if (!parsed.Has("confirm"))
        {
            context.Data["allowed"] = false;
            await context.Error.WriteLineAsync($"{PostureSetting.ProductionBindingOnNonProduction.Effect} Nothing was changed. To accept this for {slot} of {package} in {channel}, run again with --confirm.");
            return CliExit.Failure;
        }

        guard.Choose(PostureSetting.ProductionBindingOnNonProduction, scope, actor, parsed.Get("note"), confirmed: true);
        context.Data["allowed"] = true;
        await context.Out.WriteLineAsync($"Recorded: {channel} may use production's connection for {slot} of {package} (by {actor}, audited).");
        return CliExit.Ok;
    }

    private static async Task<int> PackageRequireSandboxRunAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 1 || (parsed.Words.Count == 1 && parsed.Words[0] is not ("on" or "off")))
        {
            await context.Error.WriteLineAsync($"Usage: package require-sandbox-run [on|off] {Where}");
            return CliExit.Usage;
        }

        var publishing = new RunnerPublishing(OpenState(parsed, environment));
        if (parsed.Words.Count == 1) publishing.SetRequireSandboxRun(parsed.Words[0] == "on", RunnerActor.Local());
        await context.Out.WriteLineAsync($"A sandbox run is {(publishing.RequireSandboxRun ? "required: a version is promoted to prod only after a run of it succeeded in another channel" : "not required: a version may be promoted to prod without one")}.");
        context.Data["require_sandbox_run"] = publishing.RequireSandboxRun;
        return CliExit.Ok;
    }

    private static async Task<int> PackageRequireSignaturesAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 1 || (parsed.Words.Count == 1 && parsed.Words[0] is not ("on" or "off")))
        {
            await context.Error.WriteLineAsync($"Usage: package require-signatures [on|off] {Where}");
            return CliExit.Usage;
        }

        var publishing = new RunnerPublishing(OpenState(parsed, environment));
        if (parsed.Words.Count == 1) publishing.SetRequireSignatures(parsed.Words[0] == "on", RunnerActor.Local());
        await context.Out.WriteLineAsync($"Signatures are {(publishing.RequireSignatures ? "required: a version that is not signed is refused" : "not required: a version is accepted unsigned and recorded as such")}.");
        context.Data["require_signatures"] = publishing.RequireSignatures;
        return CliExit.Ok;
    }
}
