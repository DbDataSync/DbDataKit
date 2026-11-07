using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>One thing wrong in a manifest: where (<see cref="KeyPath"/>, as <c>tasks[0].limits.timeout</c>) and what, in words.</summary>
public sealed record ManifestProblem(string KeyPath, string Message);

/// <summary>
/// Says what is wrong with a <see cref="PackageManifest"/> that is well formed but cannot work: a bad name, a schedule that does not parse, a connection slot a trigger uses and the task does not declare,
/// a value declared twice in the progress, a limit of zero, a trigger loop. Works on the model, so a manifest built in code is checked the same way as one read from a file. Key paths use positions
/// (<c>tasks[1].progress.state[0].name</c>) so a path names exactly one place; the words name the task and the value.
/// </summary>
public static class PackageManifestValidator
{
    /// <summary>The rule for the names of packages and tasks, which are also folder and address names.</summary>
    public static readonly Regex NameRule = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The rule for slots, inputs, progress values and the parts of a key: a lower-case identifier, because the kit makes typed accessors from them.</summary>
    public static readonly Regex IdentifierRule = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypeNameRule = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MethodRule = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionRule = new(@"^\d+(\.\d+){0,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProtocolRule = new(@"^dbdatakit\.runner\.v[1-9][0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LabelRule = new(@"^[A-Za-z0-9][A-Za-z0-9_.:=-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Everything wrong with <paramref name="manifest"/>; empty when it is usable.</summary>
    public static IReadOnlyList<ManifestProblem> Problems(PackageManifest manifest)
    {
        var problems = new List<ManifestProblem>();
        void Add(string path, string message) => problems.Add(new ManifestProblem(path, message));

        Name(manifest.Name, "name", "a package", Add);
        Launch(manifest, Add);
        Contracts(manifest, Add);

        if (manifest.Tasks.Count == 0) Add("tasks", "A package has at least one task.");
        foreach (var duplicate in Duplicates(manifest.Tasks.Select(t => t.Name)))
            Add($"tasks[{duplicate.Index}].name", $"The task name '{duplicate.Name}' is used twice (also at tasks[{duplicate.First}]); a task's name is how it is found.");

        for (var i = 0; i < manifest.Tasks.Count; i++) Task(manifest, manifest.Tasks[i], $"tasks[{i}]", Add);

        if (TriggerGraph.FindCycle(manifest) is { } cycle)
        {
            var first = manifest.Tasks.ToList().FindIndex(t => t.Name == cycle[0]);
            var previous = cycle[^2];
            var triggerIndex = first < 0 ? -1 : manifest.Tasks[first].Triggers.ToList().FindIndex(t => IsOwnAfterRun(manifest, t) && t.Task == previous);
            Add(first < 0 || triggerIndex < 0 ? "tasks" : $"tasks[{first}].triggers[{triggerIndex}].after", $"The triggers form a loop, so the tasks would start each other for ever: {string.Join(" runs, then ", cycle)}.");
        }

        return problems;
    }

    /// <summary>The same as <see cref="Problems"/>, as diagnostics that name <paramref name="file"/>. <paramref name="positions"/> says where in the file each key path is, when the manifest was read from it.</summary>
    public static IReadOnlyList<ConfigDiagnostic> Validate(PackageManifest manifest, string file, IReadOnlyDictionary<string, YamlNode>? positions = null) =>
        [.. Problems(manifest).Select(p =>
        {
            var at = Locate(positions, p.KeyPath);
            return DefaultDiagnosticCatalog.Instance.Create(SharedCodes.ValidationFailed, file, at?.Line ?? 0, at?.Column ?? 0, $"`{p.KeyPath}`: {p.Message}", null, p.KeyPath);
        })];

    /// <summary>The node of a key path, or of the nearest key above it that exists.</summary>
    internal static YamlNode? Locate(IReadOnlyDictionary<string, YamlNode>? positions, string path)
    {
        if (positions is null) return null;
        while (path.Length > 0)
        {
            if (positions.TryGetValue(path, out var node)) return node;
            var cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
            path = cut < 0 ? "" : path[..cut];
        }

        return positions.GetValueOrDefault("");
    }

    private static void Name(string name, string path, string what, Action<string, string> add)
    {
        if (!NameRule.IsMatch(name ?? ""))
            add(path, $"'{name}' is not a name for {what}: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64.");
    }

    private static void Identifier(string name, string path, string what, Action<string, string> add)
    {
        if (!IdentifierRule.IsMatch(name ?? ""))
            add(path, $"'{name}' is not a name for {what}: lower-case letters, digits and '_', starting with a letter, at most 64.");
    }

    private static void Launch(PackageManifest manifest, Action<string, string> add)
    {
        var launch = manifest.Launch;
        if (launch.Kind == LaunchKind.Assembly)
        {
            if (string.IsNullOrWhiteSpace(launch.Assembly)) add("launch.assembly", "An assembly package names the assembly to load (a path inside the package, such as bin/Orders.dll).");
            else if (!SafePath(launch.Assembly)) add("launch.assembly", $"'{launch.Assembly}' is not a path inside the package: use a relative path with forward slashes and no '..'.");
            if (launch.Command is not null) add("launch.command", "A command belongs to an executable package; an assembly package names `assembly`.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(launch.Command)) add("launch.command", "An executable package names the command to run (a path inside the package, or a program on the runner).");
            if (launch.Assembly is not null) add("launch.assembly", "An assembly belongs to an assembly package; an executable package names `command`.");
        }
    }

    private static bool SafePath(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.StartsWith('\\') && !path.Contains(':') && !path.Contains('\\') && !path.Split('/').Any(part => part is ".." or "");

    private static void Contracts(PackageManifest manifest, Action<string, string> add)
    {
        var built = manifest.BuiltAgainst;
        if (built.Protocol is null) add("built_against.protocol", "The manifest says which runner protocol it was built against, for example `protocol: dbdatakit.runner.v1`, so a runner can refuse a package it cannot serve.");
        else if (!ProtocolRule.IsMatch(built.Protocol)) add("built_against.protocol", $"'{built.Protocol}' is not a protocol name: write it as dbdatakit.runner.v1.");
        if (built.Dotnet is null) { if (manifest.Launch.Kind == LaunchKind.Assembly) add("built_against.dotnet", "An assembly package says which .NET runtime it was built for, for example `dotnet: \"10.0\"`."); }
        else if (!VersionRule.IsMatch(built.Dotnet)) add("built_against.dotnet", $"'{built.Dotnet}' is not a .NET version: write it as 10.0.");
        if (built.Kit is not null && !VersionRule.IsMatch(built.Kit)) add("built_against.kit", $"'{built.Kit}' is not a kit version: write it as 1 or 1.2.");
    }

    private static void Task(PackageManifest manifest, PackageTask task, string at, Action<string, string> add)
    {
        Name(task.Name, $"{at}.name", "a task", add);
        var named = $"task '{task.Name}'";

        Entry(manifest, task, at, named, add);

        if (task.Schedule.Validate() is { } scheduleProblem)
        {
            var field = scheduleProblem.Contains("not both", StringComparison.Ordinal) ? "every"
                : scheduleProblem.Contains("time zone", StringComparison.Ordinal) ? "time_zone"
                : scheduleProblem.Contains("cron", StringComparison.Ordinal) ? "cron"
                : scheduleProblem.Contains("grace", StringComparison.Ordinal) ? "misfire_grace"
                : null;
            add(field is null ? $"{at}.schedule" : $"{at}.schedule.{field}", $"The schedule of {named}: {scheduleProblem}");
        }

        var slots = Slots(task, at, named, add);
        Inputs(task, at, named, add);
        Triggers(manifest, task, at, named, slots, add);
        Progress(task.Progress, $"{at}.progress", named, add);
        Declared(task, at, named, add);
        Limits(task.Limits, $"{at}.limits", named, add);
        Labels(task.Requires, $"{at}.requires", named, "required label", add);
        Labels(task.Capabilities, $"{at}.capabilities", named, "capability", add);
    }

    private static void Entry(PackageManifest manifest, PackageTask task, string at, string named, Action<string, string> add)
    {
        if (manifest.Launch.Kind == LaunchKind.Assembly)
        {
            if (string.IsNullOrWhiteSpace(task.Entry.Type)) add($"{at}.entry.type", $"The entry of {named} names the type that holds it, with its namespace (Orders.Tasks.Extract); the package loads an assembly.");
            else if (!TypeNameRule.IsMatch(task.Entry.Type)) add($"{at}.entry.type", $"'{task.Entry.Type}' is not a type name: write the namespace and the type, such as Orders.Tasks.Extract.");
            if (string.IsNullOrWhiteSpace(task.Entry.Method) || !MethodRule.IsMatch(task.Entry.Method)) add($"{at}.entry.method", $"'{task.Entry.Method}' is not a method name for the entry of {named}.");
        }
        else if (task.Entry.Type is not null)
            add($"{at}.entry.type", $"An executable package starts {named} through the protocol, not through a type; leave `type` out (extra arguments go in `args`).");
    }

    private static HashSet<string> Slots(PackageTask task, string at, string named, Action<string, string> add)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < task.Connections.Count; i++)
        {
            var slot = task.Connections[i];
            Identifier(slot.Name, $"{at}.connections[{i}].name", "a connection slot", add);
            if (!declared.Add(slot.Name)) add($"{at}.connections[{i}].name", $"The connection slot '{slot.Name}' is declared twice in {named}.");
            if (string.IsNullOrWhiteSpace(slot.Kind)) add($"{at}.connections[{i}].kind", $"The connection slot '{slot.Name}' of {named} says what kind of connection it takes (sqlserver, postgres, folder, ...).");
            if (slot.Env is not null && !SlotEnvironmentFormats.IsKnown(slot.Env)) add($"{at}.connections[{i}].env", $"The connection slot '{slot.Name}' of {named} names the injection form '{slot.Env}', which the runner does not know. The forms: {string.Join(", ", SlotEnvironmentFormats.All)}.");
        }

        return declared;
    }

    private static void Inputs(PackageTask task, string at, string named, Action<string, string> add)
    {
        foreach (var duplicate in Duplicates(task.Inputs.Select(i => i.Name)))
            add($"{at}.inputs[{duplicate.Index}].name", $"The input '{duplicate.Name}' is declared twice in {named} (also at inputs[{duplicate.First}]).");
        for (var i = 0; i < task.Inputs.Count; i++)
        {
            var input = task.Inputs[i];
            Identifier(input.Name, $"{at}.inputs[{i}].name", "an input", add);
            if (input.Default is not null && DefaultProblem(input.Type, input.Default) is { } problem)
                add($"{at}.inputs[{i}].default", $"The default of the input '{input.Name}' of {named}: {problem}");
        }
    }

    private static void Triggers(PackageManifest manifest, PackageTask task, string at, string named, HashSet<string> slots, Action<string, string> add)
    {
        for (var i = 0; i < task.Triggers.Count; i++)
        {
            var trigger = task.Triggers[i];
            var key = TriggerKey(trigger.Kind);
            var path = $"{at}.triggers[{i}].{key}";
            switch (trigger.Kind)
            {
                case TriggerKind.AfterRun:
                    if (string.IsNullOrWhiteSpace(trigger.Task)) { add($"{path}.task", $"An after-run trigger of {named} says which task it follows."); break; }
                    if (trigger.On is { Count: 0 }) add($"{path}.on", $"`on` of the after-run trigger of {named} lists the outcomes it fires on; leave it out for succeeded only.");
                    if (IsOwnAfterRun(manifest, trigger) && !manifest.Tasks.Any(t => t.Name == trigger.Task))
                        add($"{path}.task", $"{Capital(named)} follows the task '{trigger.Task}', which this package does not have (tasks: {string.Join(", ", manifest.Tasks.Select(t => t.Name))}). To follow a task of another package, add `package:`.");
                    if (trigger.Package is not null) Name(trigger.Package, $"{path}.package", "a package", add);
                    break;
                case TriggerKind.Webhook:
                    if (string.IsNullOrWhiteSpace(trigger.Source)) add($"{path}.source", $"A webhook trigger of {named} names the source the webhook comes from.");
                    if (trigger.Secret is { } secret && string.IsNullOrWhiteSpace(secret)) add($"{path}.secret", $"`secret` of the webhook trigger of {named} names a secret of the runner's secret store (for example `shop_webhook`); leave it out for a webhook that arrives through the serving layer only.");
                    break;
                case TriggerKind.File:
                    if (string.IsNullOrWhiteSpace(trigger.Connection)) add($"{path}.connection", $"A file trigger of {named} names the connection slot whose folder it watches.");
                    else if (!slots.Contains(trigger.Connection))
                        add($"{path}.connection", $"{Capital(named)} watches the connection '{trigger.Connection}', but does not declare a slot of that name" + (slots.Count == 0 ? " (it declares none): add it under `connections:`." : $" (it declares {string.Join(", ", slots.Order())})."));
                    if (string.IsNullOrWhiteSpace(trigger.Path)) add($"{path}.path", $"A file trigger of {named} says which files it waits for (a path or a pattern in the connection's folder).");
                    if (trigger.Poll is { } poll && poll <= TimeSpan.Zero) add($"{path}.poll", $"`poll` of the file trigger of {named} has to be above zero.");
                    break;
                case TriggerKind.StoreChange:
                    if (string.IsNullOrWhiteSpace(trigger.Model)) add($"{path}.model", $"A store-change trigger of {named} names the model whose changes it follows.");
                    break;
            }
        }
    }

    private static string TriggerKey(TriggerKind kind) => kind switch
    {
        TriggerKind.AfterRun => "after",
        TriggerKind.Webhook => "webhook",
        TriggerKind.File => "file",
        TriggerKind.StoreChange => "store_change",
        _ => "manual",
    };

    private static bool IsOwnAfterRun(PackageManifest manifest, TaskTrigger trigger) =>
        trigger.Kind == TriggerKind.AfterRun && (trigger.Package is null || trigger.Package == manifest.Name);

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static void Progress(ProgressDeclaration progress, string at, string named, Action<string, string> add)
    {
        var checkpoints = progress.Checkpoints;
        var states = progress.States;
        foreach (var duplicate in Duplicates(checkpoints.Select(c => c.Name)))
            add($"{at}.checkpoints[{duplicate.Index}].name", $"The checkpoint '{duplicate.Name}' is declared twice in {named} (also at checkpoints[{duplicate.First}]); each value of the progress has one name.");
        foreach (var duplicate in Duplicates(states.Select(s => s.Name)))
            add($"{at}.state[{duplicate.Index}].name", $"The state value '{duplicate.Name}' is declared twice in {named} (also at state[{duplicate.First}]); each value of the progress has one name.");
        var checkpointNames = checkpoints.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < states.Count; i++)
            if (checkpointNames.Contains(states[i].Name))
                add($"{at}.state[{i}].name", $"'{states[i].Name}' is both a checkpoint and a state value in {named}; the typed accessors the kit makes would collide. Rename one.");

        for (var i = 0; i < checkpoints.Count; i++)
        {
            var checkpoint = checkpoints[i];
            var path = $"{at}.checkpoints[{i}]";
            Identifier(checkpoint.Name, $"{path}.name", "a checkpoint", add);
            Key(checkpoint.Per, checkpoint.KeyFill, checkpoint.RenamedFrom, checkpoint.Name, path, named, add);
            if (checkpoint.Parts.Count == 0) add($"{path}.parts", $"The checkpoint '{checkpoint.Name}' of {named} has no parts; it holds at least one value (a time, an id).");
            var p = 0;
            foreach (var (name, part) in checkpoint.Parts)
            {
                var partPath = $"{path}.parts[{p++}]";
                Identifier(name, $"{partPath}.name", "a part", add);
                Value(part.Type, part.Default, part.Format, part.RenamedFrom, partPath, $"The part '{checkpoint.Name}.{name}' of {named}", add);
            }
        }

        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var path = $"{at}.state[{i}]";
            Identifier(state.Name, $"{path}.name", "a state value", add);
            Key(state.Per, state.KeyFill, state.RenamedFrom, state.Name, path, named, add);
            Value(state.Type, state.Default, state.Format, state.RenamedFrom, path, $"The state value '{state.Name}' of {named}", add);
        }
    }

    /// <summary>The steps, units and counters a task lists for the console: each has a name of its own kind (the console groups by it), and a name is listed once.</summary>
    private static void Declared(PackageTask task, string at, string named, Action<string, string> add)
    {
        foreach (var duplicate in Duplicates(task.StepList.Select(s => s.Name)))
            add($"{at}.steps[{duplicate.Index}].name", $"The step '{duplicate.Name}' is listed twice in {named} (also at steps[{duplicate.First}]); step names are unique per task.");
        for (var i = 0; i < task.StepList.Count; i++) Name(task.StepList[i].Name, $"{at}.steps[{i}].name", "a step", add);
        foreach (var duplicate in Duplicates(task.UnitList.Select(u => u.Name)))
            add($"{at}.units[{duplicate.Index}].name", $"The unit '{duplicate.Name}' is listed twice in {named} (also at units[{duplicate.First}]); unit names are unique per task.");
        for (var i = 0; i < task.UnitList.Count; i++) Name(task.UnitList[i].Name, $"{at}.units[{i}].name", "a unit", add);
        foreach (var duplicate in Duplicates(task.CounterList))
            add($"{at}.counters[{duplicate.Index}]", $"The counter '{duplicate.Name}' is listed twice in {named} (also at counters[{duplicate.First}]).");
        for (var i = 0; i < task.CounterList.Count; i++) Name(task.CounterList[i], $"{at}.counters[{i}]", "a counter", add);
    }

    private static void Key(IReadOnlyList<string>? per, string? fill, string? renamedFrom, string name, string path, string named, Action<string, string> add)
    {
        foreach (var duplicate in Duplicates(per ?? []))
            add($"{path}.per[{duplicate.Index}]", $"'{duplicate.Name}' is listed twice in `per` of '{name}' in {named}.");
        for (var k = 0; k < (per?.Count ?? 0); k++) Identifier(per![k], $"{path}.per[{k}]", "a part of a key", add);
        if (fill is not null && per is not { Count: > 0 }) add($"{path}.fill", $"'{name}' in {named} has a `fill` rule but is not kept per key; `fill` says what existing keys get when a key part is added.");
        if (renamedFrom is not null) Identifier(renamedFrom, $"{path}.renamed_from", "the old name", add);
        if (renamedFrom is not null && renamedFrom == name) add($"{path}.renamed_from", $"'{name}' in {named} is said to be renamed from itself.");
    }

    private static void Value(ProgressType type, string? @default, string? format, string? renamedFrom, string path, string subject, Action<string, string> add)
    {
        if (@default is not null && DefaultProblem(type, @default) is { } problem) add($"{path}.default", $"{subject}: {problem}");
        if (format is not null && type is not (ProgressType.Date or ProgressType.Time))
            add($"{path}.format", $"{subject} has a `format`, which only says how text becomes a date or a time; its type is {TypeWord(type)}.");
        if (format is { Length: 0 }) add($"{path}.format", $"{subject} has an empty `format`.");
        if (renamedFrom is not null && renamedFrom.Length == 0) add($"{path}.renamed_from", $"{subject} has an empty `renamed_from`.");
    }

    /// <summary>The type as the manifest writes it: <c>text</c>, <c>int</c>, <c>decimal</c>, <c>time</c>, <c>date</c>, <c>bool</c>, <c>bytes</c>, <c>json</c>.</summary>
    public static string TypeWord(ProgressType type) => type.ToString().ToLowerInvariant();

    private static string? DefaultProblem(ProgressType type, string text)
    {
        switch (type)
        {
            case ProgressType.Text: return null;
            case ProgressType.Bytes: return "a value of type bytes is a payload and has no default.";
            case ProgressType.Bool: return text is "true" or "false" ? null : $"'{text}' is not a bool: write true or false.";
            case ProgressType.Int when !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _): return $"'{text}' is not a whole number.";
            case ProgressType.Decimal when !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _): return $"'{text}' is not a decimal number (write it with a point: 12.5).";
            case ProgressType.Date when !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _): return $"'{text}' is not a date: write yyyy-MM-dd.";
            case ProgressType.Time when !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _): return $"'{text}' is not a time: write it as 2024-01-31T00:00:00Z.";
            case ProgressType.Json:
                try { using var _ = System.Text.Json.JsonDocument.Parse(text); return null; }
                catch (System.Text.Json.JsonException) { return $"'{text}' is not JSON."; }
            default: return null;
        }
    }

    private static void Limits(TaskLimits limits, string at, string named, Action<string, string> add)
    {
        void Above(string key, bool bad, string shown) { if (bad) add($"{at}.{key}", $"`{key}` of {named} is {shown}; a limit is above zero (leave it out for the runner's default)."); }
        Above("timeout", limits.Timeout is { } t && t <= TimeSpan.Zero, limits.Timeout?.ToString() ?? "");
        Above("memory_mb", limits.MemoryMb is <= 0, limits.MemoryMb?.ToString(CultureInfo.InvariantCulture) ?? "");
        Above("cpu", limits.Cpu is <= 0, limits.Cpu?.ToString(CultureInfo.InvariantCulture) ?? "");
        Above("concurrency", limits.Concurrency is <= 0, limits.Concurrency?.ToString(CultureInfo.InvariantCulture) ?? "");
        Above("attempts", limits.Attempts is <= 0, limits.Attempts?.ToString(CultureInfo.InvariantCulture) ?? "");
        Above("backoff", limits.Backoff is { } b && b <= TimeSpan.Zero, limits.Backoff?.ToString() ?? "");
        Above("max_backoff", limits.MaxBackoff is { } m && m <= TimeSpan.Zero, limits.MaxBackoff?.ToString() ?? "");
        if (limits.Backoff is { } from && limits.MaxBackoff is { } to && from > TimeSpan.Zero && to > TimeSpan.Zero && to < from)
            add($"{at}.max_backoff", $"`max_backoff` of {named} is shorter than `backoff`; the wait grows from backoff up to max_backoff.");
    }

    private static void Labels(IReadOnlyList<string> values, string at, string named, string what, Action<string, string> add)
    {
        foreach (var duplicate in Duplicates(values)) add($"{at}[{duplicate.Index}]", $"The {what} '{duplicate.Name}' is listed twice for {named}.");
        for (var i = 0; i < values.Count; i++)
            if (!LabelRule.IsMatch(values[i])) add($"{at}[{i}]", $"'{values[i]}' is not a {what} for {named}: letters, digits and . _ : = - with no spaces, at most 64.");
    }

    private static IEnumerable<(int Index, int First, string Name)> Duplicates(IEnumerable<string> names)
    {
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var name in names)
        {
            if (first.TryGetValue(name ?? "", out var earlier)) yield return (index, earlier, name ?? "");
            else first[name ?? ""] = index;
            index++;
        }
    }
}

/// <summary>Which tasks of a package start which: the after-run triggers between its own tasks.</summary>
public static class TriggerGraph
{
    /// <summary>
    /// A loop of after-run triggers among the tasks of <paramref name="manifest"/> (A follows B, B follows A), as the task names in order with the first repeated at the end
    /// (<c>["a", "b", "a"]</c>: a runs, then b, then a again); null when there is none. Only a package's own tasks are followed; a trigger on another package's task cannot be judged here.
    /// </summary>
    public static IReadOnlyList<string>? FindCycle(PackageManifest manifest)
    {
        // an edge runs from the task that ends to the task it starts
        var starts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var task in manifest.Tasks)
            foreach (var trigger in task.Triggers.Where(t => t.Kind == TriggerKind.AfterRun && (t.Package is null || t.Package == manifest.Name) && t.Task is not null))
            {
                if (!starts.TryGetValue(trigger.Task!, out var list)) starts[trigger.Task!] = list = [];
                list.Add(task.Name);
            }

        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in manifest.Tasks)
            if (Walk(task.Name, starts, done, []) is { } cycle) return cycle;
        return null;
    }

    /// <summary>
    /// A loop of after-run triggers among the tasks of every package projected on a channel (a trigger's <c>package</c> names the other package, none means its own), or null. A loop inside one package is found here too;
    /// <see cref="FindCycle(PackageManifest)"/> finds that one when a manifest is read, this finds the loops that only exist once two packages are active together. Channels do not trigger each other.
    /// </summary>
    public static TriggerCycle? FindCycle(IEnumerable<ProjectedTriggers> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);
        foreach (var channel in projections.GroupBy(t => t.Channel, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // an edge runs from the task that ends to the task it starts
            var starts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var all = new List<string>();
            foreach (var task in channel.OrderBy(t => t.Package, StringComparer.Ordinal).ThenBy(t => t.Task, StringComparer.Ordinal))
            {
                var name = task.Package + "/" + task.Task;
                all.Add(name);
                foreach (var trigger in task.Triggers.Where(t => t.Kind == TriggerKind.AfterRun && t.Task is not null))
                {
                    var from = (trigger.Package ?? task.Package) + "/" + trigger.Task;
                    if (!starts.TryGetValue(from, out var list)) starts[from] = list = [];
                    list.Add(name);
                }
            }

            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in all)
                if (Walk(start, starts, done, []) is { } cycle) return new TriggerCycle(channel.Key, cycle);
        }

        return null;
    }

    private static List<string>? Walk(string task, Dictionary<string, List<string>> starts, HashSet<string> done, List<string> path)
    {
        var at = path.IndexOf(task);
        if (at >= 0) return [.. path.Skip(at), task];
        if (!done.Add(task)) return null;
        path.Add(task);
        foreach (var next in starts.GetValueOrDefault(task) ?? [])
            if (Walk(next, starts, done, path) is { } found) return found;
        path.RemoveAt(path.Count - 1);
        return null;
    }
}
