using DbDataBuild.Cli.Runner;
using DbDataKit.Cli;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// <c>server schedule owners|observe|cutover|rollback|compare</c> (plan 28c E09): moving a refresh plan's schedule from this server's scheduler to a runner one plan at a time, and back. A plan is named <c>&lt;project&gt;/&lt;connection&gt;/&lt;plan&gt;</c>.
/// The runner is the one <c>schedule.runner</c> of <c>server.yaml</c> names, or <c>--runner</c> and <c>--channel</c> (which are then written there); the pairing is the one <c>project publish</c> uses (<c>--client-dir</c>, <c>--code</c> for the
/// first call), and the runner needs the operator role of it. The verbs work on the server's directory and take effect on a running server at its next tick. What each step does and why it cannot leave a plan scheduled twice is on <see cref="BuildOwnership"/>.
/// </summary>
internal static class ScheduleVerb
{
    public const string Usage = "owners [<project>] | observe <project>/<connection>/<plan> | cutover <project>/<connection>/<plan> [--wait <seconds>] | rollback <project>/<connection>/<plan> [--wait <seconds>] | compare <project>/<connection>/<plan> [--since <time>] [--tolerance <seconds>]   [--runner <name or address> --channel <name>] [--client-dir <folder>] [--code <pairing code>] [--reason <text>] [--repo <server directory>]";

    public static async Task<int> RunAsync(string[] args, CliContext context, Func<string, string?>? environment = null, IRunnerSchedule? runner = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "runner", "channel", "client-dir", "code", "reason", "wait", "since", "tolerance" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count is 0 or > 2)
        {
            await context.Error.WriteLineAsync($"Usage: server schedule {Usage}");
            return CliExit.Usage;
        }

        var directory = ServerCli.DirectoryOf(args);
        var (action, name) = (parsed.Words[0], parsed.Words.Count > 1 ? parsed.Words[1] : null);
        if (action is not ("owners" or "observe" or "cutover" or "rollback" or "compare") || (action != "owners" && name is null))
        {
            await context.Error.WriteLineAsync($"Usage: server schedule {Usage}");
            return CliExit.Usage;
        }

        BuildScheduleSettings settings;
        try { settings = BuildScheduleSettings.Read(directory); }
        catch (InvalidOperationException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return DbDataKit.State.Cli.StateExit.Settings;
        }

        var link = settings.Runner;
        if (parsed.Get("runner") is not null || parsed.Get("channel") is not null || parsed.Get("client-dir") is not null)
            link = new ScheduleRunnerLink(parsed.Get("runner") ?? link?.Server ?? "", parsed.Get("channel") ?? link?.Channel ?? "", parsed.Get("client-dir") ?? link?.ClientDirectory);
        if (link is null || link.Server.Length == 0 || link.Channel.Length == 0)
        {
            await context.Error.WriteLineAsync($"Say where the runner is: --runner <name or address> --channel <name>, or schedule.runner (server, channel) in {BuildState.SettingsFile(directory)}.");
            return CliExit.Usage;
        }

        TimeSpan? Seconds(string option) => parsed.Get(option) is { } text ? (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0 ? TimeSpan.FromSeconds(s) : throw new ArgumentException($"--{option} is a number of seconds, not '{text}'.")) : null;
        try
        {
            var env = environment ?? Environment.GetEnvironmentVariable;
            runner ??= new ClientRunnerSchedule(new RunnerPublishClient(new RunnerClientStore(link.ClientDirectory ?? RunnerClientStore.DefaultDirectory(env)), env), link, parsed.Get("code"));
            var projects = new ProjectsFile(directory);
            var state = BuildSchedulerSchema.Open(directory);
            var ownership = new BuildOwnership(directory, projects, state, TimeProvider.System, runner, new FileAuditLog(Path.Combine(directory, "management-audit.log")), Environment.UserName, delay);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);

            if (action == "owners")
            {
                var rows = await ownership.OwnersAsync(name, cancel.Token);
                if (rows.Count == 0) await context.Out.WriteLineAsync("No scheduled plan is hosted.");
                foreach (var r in rows)
                    await context.Out.WriteLineAsync($"{BuildScheduleSettings.KeyOf(r.Project, r.Connection, r.Plan),-48} this server: {r.Setting,-8} runner: {r.Runner ?? "(none: not active there)",-8}{(r.Mismatch ? "  DIFFERENT: the runner's word counts; find out which is right before changing either" : "")}");
                context.Data["plans"] = rows.Select(r => new Dictionary<string, object?> { ["project"] = r.Project, ["connection"] = r.Connection, ["plan"] = r.Plan, ["owner"] = r.Setting, ["runner_owner"] = r.Runner, ["runner_owner_set_by"] = r.RunnerSetBy, ["mismatch"] = r.Mismatch }).ToList();
                return rows.Any(r => r.Mismatch) ? CliExit.Failure : CliExit.Ok;
            }

            var parts = name!.Split('/');
            if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            {
                await context.Error.WriteLineAsync($"A plan is '<project>/<connection>/<plan>', not '{name}'.");
                return CliExit.Usage;
            }

            var (project, connection, plan) = (parts[0], parts[1], parts[2]);
            var reason = parsed.Get("reason");
            OwnershipChange change;
            switch (action)
            {
                case "observe":
                    change = await ownership.ObserveAsync(project, connection, plan, link, reason, cancel.Token);
                    await context.Out.WriteLineAsync($"{name} is observed: the runner records what it would queue, this server still schedules it. Compare after a few of its times with: server schedule compare {name}");
                    break;
                case "cutover":
                    change = await ownership.CutOverAsync(project, connection, plan, link, reason, Seconds("wait"), cancel.Token);
                    await context.Out.WriteLineAsync($"{name} is scheduled by the runner from {change.HandledUpTo:u}{(change.PausedBy is null ? "" : $", paused by {change.PausedBy}")}{(change.Waited ? " (a run in flight was waited for)" : "")}. Undo with: server schedule rollback {name}");
                    break;
                case "rollback":
                    change = await ownership.RollBackAsync(project, connection, plan, link, reason, Seconds("wait"), cancel.Token);
                    await context.Out.WriteLineAsync($"{name} is scheduled by this server again{(change.HandledUpTo is { } at ? $" from the runner's position {at:u}" : "")}{(change.PausedBy is null ? "" : $", paused by {change.PausedBy}")}{(change.Waited ? " (a run on the runner was waited for)" : "")}.");
                    break;
                default:
                    var comparison = await ownership.CompareAsync(project, connection, plan, parsed.Get("since") is { } since ? DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal) : null, Seconds("tolerance"), cancel.Token);
                    await context.Out.WriteLineAsync($"{comparison.Matched} decision(s) agree, {comparison.Differences.Count} differ.");
                    foreach (var d in comparison.Differences) await context.Out.WriteLineAsync($"  {d.At:u}  {d.Kind}");
                    context.Data["matched"] = comparison.Matched;
                    context.Data["differences"] = comparison.Differences.Select(d => new Dictionary<string, object?> { ["at"] = d.At.UtcDateTime.ToString("O"), ["kind"] = d.Kind, ["paused"] = d.Paused }).ToList();
                    return comparison.Agrees ? CliExit.Ok : CliExit.Failure;
            }

            context.Data["owner"] = change.To;
            context.Data["from"] = change.From;
            context.Data["package"] = change.Package;
            context.Data["task"] = change.Task;
            context.Data["handled_up_to"] = change.HandledUpTo?.UtcDateTime.ToString("O");
            context.Data["paused_by"] = change.PausedBy;
            context.Data["waited"] = change.Waited;
            return CliExit.Ok;
        }
        catch (ArgumentException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Usage;
        }
        catch (Exception ex) when (ex is OwnershipException or RunnerClientException or InvalidOperationException or FormatException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }
}
