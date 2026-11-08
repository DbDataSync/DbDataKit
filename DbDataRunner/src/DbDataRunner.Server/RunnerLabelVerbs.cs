using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using System.Text.Json;
using DbDataKit.Cli;
using DbDataKit.Monitoring;
using DbDataKit.State;

namespace DbDataRunner.Server;

/// <summary>
/// <c>dbdatarunner label add|remove|list</c> (plan 28a R12): the administrator's labels on the instances of the group, kept in the state through <see cref="RunnerInstances"/> as source <c>admin</c>. A task that requires a label
/// is claimed only by a runner that has it; adding a label to a runner starts the tasks that waited for it within a tick. The labels a runner reports about itself (<c>network:outside</c>, <c>os:linux</c>, <c>memory_limit:watched</c>)
/// are read-only. <c>--instance</c> is an instance id (see <c>label list</c>) or <c>this</c>, the one live runner on this machine. The verbs work on the state with the server stopped.
/// </summary>
public static class RunnerLabelVerbs
{
    private const string Where = "[--repo <server directory>] [--group <name>]";
    public const string AddVerb = RunnerGroupApi.LabelAddVerb;
    public const string RemoveVerb = RunnerGroupApi.LabelRemoveVerb;

    public static Noun Create(Func<string, string?>? environment) => new("label", "Labels on the runners of the group: an administrator's labels route tasks that require them; reported capabilities are labels too, read-only",
    [
        new Verb("add", "Give a runner a label (name or name:value); tasks that require it can now run there, within a few seconds", EffectClass.TargetDataWrites,
            $"<label> --instance <id|this> {Where}", (args, context) => ChangeAsync(true, args, context, environment),
            DataSchemas.Object([("instance", DataSchemas.String), ("label", DataSchemas.String), ("changed", DataSchemas.Boolean)], "instance", "label", "changed")),
        new Verb("remove", "Take an administrator's label off a runner (a label the runner reports about itself cannot be removed)", EffectClass.TargetDataWrites,
            $"<label> --instance <id|this> {Where}", (args, context) => ChangeAsync(false, args, context, environment),
            DataSchemas.Object([("instance", DataSchemas.String), ("label", DataSchemas.String), ("changed", DataSchemas.Boolean)], "instance", "label", "changed")),
        new Verb("list", "The labels of the live runners of the group, or of one runner, each with who set it (admin or reported)", EffectClass.OfflineOnly,
            $"[--instance <id|this>] {Where}", (args, context) => ListAsync(args, context, environment),
            DataSchemas.Object([("instances", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("host", DataSchemas.String), ("labels", DataSchemas.StringList)], "id", "host", "labels")))], "instances")),
    ]);

    private static StateDatabase Open(CliArgs parsed, Func<string, string?>? environment)
    {
        var directory = Path.GetFullPath(parsed.Get("repo") ?? RunnerSettings.DefaultDirectory(environment));
        return RunnerState.Open(directory, RunnerSettings.StateFrom(environment), environment, RunnerSettings.GroupFrom(environment, parsed.Get("group")));
    }

    private static async Task<int> ChangeAsync(bool add, string[] args, CliContext context, Func<string, string?>? environment)
    {
        var verb = add ? "add" : "remove";
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "instance", "repo", "group" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("instance") is not { } which)
        {
            await context.Error.WriteLineAsync($"Usage: label {verb} <label> --instance <id|this> {Where}");
            return CliExit.Usage;
        }

        var label = RunnerLabels.Normalize(parsed.Words[0]);
        if (!RunnerGroupApi.IsLabel(label))
        {
            await context.Error.WriteLineAsync($"'{parsed.Words[0]}' is not a label: letters, digits and . _ : = - with no spaces, at most 64, such as gpu or network:outside.");
            return CliExit.Usage;
        }

        StateDatabase state;
        try { state = Open(parsed, environment); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }

        var instances = new RunnerInstances(state);
        var (instance, problem) = Resolve(instances, which);
        if (instance is null)
        {
            await context.Error.WriteLineAsync(problem);
            return CliExit.Failure;
        }

        var existing = instances.Labels(instance.Id).FirstOrDefault(l => l.Label == label);
        if (existing?.Source == InstanceLabel.Reported)
        {
            await context.Error.WriteLineAsync($"'{label}' is reported by the runner {instance.Id} itself (its capabilities) and cannot be {(add ? "set" : "removed")} by hand.");
            return CliExit.Failure;
        }

        bool changed;
        if (add)
        {
            changed = existing is null;
            instances.SetLabel(instance.Id, label, InstanceLabel.Admin);
        }
        else changed = instances.RemoveLabel(instance.Id, label);

        if (changed)
            new AuditLog(state, RunnerState.ServiceId).Record(new AuditEvent("person", RunnerActor.Local().Id, add ? AddVerb : RemoveVerb, AuditEvent.Ok, ViaKind: "command", TargetKind: "instance", TargetId: instance.Id.ToString(),
                Detail: JsonSerializer.Serialize(new { label })));
        context.Data["instance"] = instance.Id.ToString();
        context.Data["label"] = label;
        context.Data["changed"] = changed;
        await context.Out.WriteLineAsync(changed
            ? $"{(add ? "Added" : "Removed")} '{label}' {(add ? "to" : "from")} the runner {instance.Id} on {instance.Host}. A running server notices within a few seconds."
            : add ? $"The runner {instance.Id} has '{label}' already." : $"The runner {instance.Id} does not have '{label}'.");
        return CliExit.Ok;
    }

    private static async Task<int> ListAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "instance", "repo", "group" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: label list [--instance <id|this>] {Where}");
            return CliExit.Usage;
        }

        StateDatabase state;
        try { state = Open(parsed, environment); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }

        var instances = new RunnerInstances(state);
        IReadOnlyList<RunnerInstance> chosen;
        if (parsed.Get("instance") is { } which)
        {
            var (instance, problem) = Resolve(instances, which);
            if (instance is null)
            {
                await context.Error.WriteLineAsync(problem);
                return CliExit.Failure;
            }

            chosen = [instance];
        }
        else chosen = instances.ListLive(new RunnerSettings("", new Uri(RunnerSettings.DefaultListen)).InstanceLease);

        if (chosen.Count == 0) await context.Out.WriteLineAsync("No live runner in this group.");
        var data = new List<Dictionary<string, object?>>();
        foreach (var instance in chosen)
        {
            var labels = instances.Labels(instance.Id);
            await context.Out.WriteLineAsync($"{instance.Id}  {instance.Host}  {(labels.Count == 0 ? "(no labels)" : string.Join("  ", labels.Select(l => l.Source == InstanceLabel.Admin ? l.Label : $"{l.Label} (reported)")))}");
            data.Add(new Dictionary<string, object?> { ["id"] = instance.Id.ToString(), ["host"] = instance.Host, ["labels"] = labels.Select(l => l.Label).ToList(), ["admin_labels"] = labels.Where(l => l.Source == InstanceLabel.Admin).Select(l => l.Label).ToList() });
        }

        context.Data["instances"] = data;
        return CliExit.Ok;
    }

    /// <summary>An instance id, or <c>this</c>: the one live runner on this machine.</summary>
    private static (RunnerInstance? Instance, string Problem) Resolve(RunnerInstances instances, string which)
    {
        var lease = new RunnerSettings("", new Uri(RunnerSettings.DefaultListen)).InstanceLease;
        if (which == "this")
        {
            var here = instances.ListLive(lease).Where(i => i.Host == Environment.MachineName).ToList();
            return here.Count switch
            {
                1 => (here[0], ""),
                0 => (null, $"No live runner on this machine ({Environment.MachineName}) in this group: start the server, or name an instance id (label list)."),
                _ => (null, $"More than one live runner on this machine: name one of {string.Join(", ", here.Select(i => i.Id))} with --instance."),
            };
        }

        if (!Guid.TryParse(which, out var id)) return (null, $"'{which}' is not an instance id: use an id from 'label list', or 'this'.");
        var found = instances.Get(id);
        return found is null || found.State != RunnerInstance.Running
            ? (null, $"No running instance {id} in this group: see 'label list'.")
            : (found, "");
    }
}
