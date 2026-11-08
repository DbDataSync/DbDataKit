using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataBuild.Cli.Server;

/// <summary>Where the runner is that a plan's schedule moves to: the paired runner (its name or address, as <c>project publish --server</c> takes it), the channel its package is served on, and the directory of the client the pairing is kept in.</summary>
internal sealed record ScheduleRunnerLink(string Server, string Channel, string? ClientDirectory);

/// <summary>
/// The <c>schedule:</c> section of the Build server's <c>server.yaml</c> (plan 28c E09, ED6): who schedules each refresh plan while the move to a runner is made one plan at a time. <c>owners</c> maps <c>&lt;project&gt;/&lt;connection&gt;/&lt;plan&gt;</c> to
/// <c>old</c> (this server schedules it, the default), <c>observe</c> (this server does, and the runner records what it would have queued) or <c>runner</c> (the runner does; this server leaves the plan alone). <c>runner</c> says which runner and channel
/// the plans are published to and is required once a plan is not <c>old</c>. Read strictly: a key that is not a setting, an owner that is not one, or a plan name that is not three names stops the start naming the file and the nearest key that is.
/// A server reads the section again at every tick, so the verbs that change an owner (<c>server schedule observe|cutover|rollback</c>) take effect on a running server without a restart.
/// </summary>
internal sealed record BuildScheduleSettings(ScheduleRunnerLink? Runner, IReadOnlyDictionary<string, string> Owners)
{
    public const string Section = "schedule";
    public const string OwnersKey = "owners";
    public const string RunnerKey = "runner";

    public static readonly BuildScheduleSettings Default = new(null, new Dictionary<string, string>());

    private static readonly string[] SectionKeys = [RunnerKey, OwnersKey];
    private static readonly string[] RunnerKeys = ["server", "channel", "client_dir"];

    /// <summary>The key of a plan in <see cref="Owners"/>.</summary>
    public static string KeyOf(string project, string connection, string plan) => $"{project}/{connection}/{plan}";

    /// <summary>Who schedules the plan: what <c>owners</c> says, else <c>old</c>.</summary>
    public string OwnerOf(string project, string connection, string plan) => Owners.TryGetValue(KeyOf(project, connection, plan), out var owner) ? owner : TaskOwners.Old;

    /// <summary>The plan's owner as the scheduler asks for it: by project and the job's subject (<c>connection/plan</c>).</summary>
    public string OwnerOf(string project, string subject) => Owners.TryGetValue($"{project}/{subject}", out var owner) ? owner : TaskOwners.Old;

    /// <summary>The setting of the server in <paramref name="serverDirectory"/>; no file or no section is <see cref="Default"/>. Raises <see cref="InvalidOperationException"/> naming <c>server.yaml</c>.</summary>
    public static BuildScheduleSettings Read(string serverDirectory)
    {
        var file = BuildState.SettingsFile(serverDirectory);
        return File.Exists(file) ? Parse(File.ReadAllText(file), file) : Default;
    }

    /// <summary>The setting in the text of a <c>server.yaml</c> (<paramref name="file"/> is the name its refusals give).</summary>
    public static BuildScheduleSettings Parse(string text, string file)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, file, diagnostics) as YamlMapping;
        if (root?.Get(Section) is not { } node) return Default;
        if (node is not YamlMapping map) throw new InvalidOperationException($"{file}: '{Section}' has to be a set of keys ({string.Join(", ", SectionKeys)}), not a single value or a list.");
        CheckKeys(file, Section, map, SectionKeys, "a schedule setting");

        ScheduleRunnerLink? runner = null;
        if (map.Get(RunnerKey) is { } runnerNode)
        {
            if (runnerNode is not YamlMapping link) throw new InvalidOperationException($"{file}: {Section}.{RunnerKey} has to be a set of keys ({string.Join(", ", RunnerKeys)}).");
            CheckKeys(file, $"{Section}.{RunnerKey}", link, RunnerKeys, "a runner setting");
            string? Text(string key) => link.Get(key) is YamlScalar { Value: { Length: > 0 } v } ? v : null;
            runner = new ScheduleRunnerLink(
                Text("server") ?? throw new InvalidOperationException($"{file}: {Section}.{RunnerKey}.server is missing: the name or address of the runner the plans are published to."),
                Text("channel") ?? throw new InvalidOperationException($"{file}: {Section}.{RunnerKey}.channel is missing: the channel the plans' package is served on."),
                Text("client_dir"));
        }

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        if (map.Get(OwnersKey) is { } ownersNode)
        {
            if (ownersNode is not YamlMapping list) throw new InvalidOperationException($"{file}: {Section}.{OwnersKey} has to be a set of '<project>/<connection>/<plan>: old|observe|runner' lines.");
            foreach (var entry in list.Entries)
            {
                var plan = entry.Key.Value;
                if (PlanKeyProblem(plan) is { } problem) throw new InvalidOperationException($"{file}: {Section}.{OwnersKey}.{plan}: {problem}");
                var owner = entry.Value is YamlScalar { Value: var v } ? v : null;
                if (!TaskOwners.IsOwner(owner))
                {
                    var near = owner is null ? null : DidYouMean.Closest(owner, TaskOwners.All);
                    throw new InvalidOperationException($"{file}: {Section}.{OwnersKey}.{plan} is '{owner}', which is not an owner (the owners are {string.Join(", ", TaskOwners.All)}){(near is null ? "" : $"; did you mean '{near}'?")}");
                }

                owners[plan] = owner!;
            }
        }

        if (runner is null && owners.FirstOrDefault(o => o.Value != TaskOwners.Old) is { Key: { } named })
            throw new InvalidOperationException($"{file}: {Section}.{OwnersKey}.{named} is {owners[named]}, which needs {Section}.{RunnerKey} (server and channel): where the runner is.");
        return new BuildScheduleSettings(runner, owners);
    }

    /// <summary>Why a name is not a plan's key (<c>project/connection/plan</c>, three names with none of the characters a path of the file's editor takes for its own), or null.</summary>
    public static string? PlanKeyProblem(string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0)) return $"'{key}' is not a plan: it is '<project>/<connection>/<plan>'.";
        if (key.Any(c => c is '.' or '[' or ']' or ':' or '#' or '"' or '\'' || char.IsWhiteSpace(c))) return $"'{key}' has a character a plan's name does not (a dot, a bracket, a colon, a quote or a space).";
        return null;
    }

    private static void CheckKeys(string file, string path, YamlMapping map, string[] keys, string what)
    {
        foreach (var entry in map.Entries)
        {
            if (keys.Contains(entry.Key.Value, StringComparer.Ordinal)) continue;
            var near = DidYouMean.Closest(entry.Key.Value, keys);
            throw new InvalidOperationException($"{file}: {path}.{entry.Key.Value} is not {what} (the keys are {string.Join(", ", keys)}){(near is null ? "" : $"; did you mean '{near}'?")}");
        }
    }

    /// <summary>
    /// Writes the owner of one plan (and the runner it names, when given) into <c>server.yaml</c>, keeping every other line, comment and section as it was (the file's editor splices; nothing is rewritten). A plan made <c>old</c> keeps its line, so the file
    /// says it was put back and not that it was never moved. The file is read back afterwards, so what was written is a setting the server will accept.
    /// </summary>
    public static void Write(string serverDirectory, string project, string connection, string plan, string owner, ScheduleRunnerLink? runner = null)
    {
        var file = BuildState.SettingsFile(serverDirectory);
        var key = KeyOf(project, connection, plan);
        if (PlanKeyProblem(key) is { } problem) throw new InvalidOperationException(problem);
        if (!TaskOwners.IsOwner(owner)) throw new ArgumentException($"'{owner}' is not an owner.", nameof(owner));
        Directory.CreateDirectory(serverDirectory);
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        string Apply(string current, string path, string value)
        {
            var result = YamlEditor.Set(current, path, new YamlValue(value), file);
            return result.Ok ? result.Text! : throw new InvalidOperationException(result.Problem!.ToString());
        }

        if (runner is not null)
        {
            text = Apply(text, $"{Section}.{RunnerKey}.server", runner.Server);
            text = Apply(text, $"{Section}.{RunnerKey}.channel", runner.Channel);
            if (runner.ClientDirectory is { } clientDirectory) text = Apply(text, $"{Section}.{RunnerKey}.client_dir", clientDirectory);
        }

        text = Apply(text, $"{Section}.{OwnersKey}.{key}", owner);
        if (Parse(text, file).OwnerOf(project, connection, plan) != owner) throw new InvalidOperationException($"{file}: the owner written for {key} did not read back as {owner}.");
        var temporary = file + ".new";
        File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
    }
}
