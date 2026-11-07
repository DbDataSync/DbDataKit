using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.Monitoring;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>What reading a manifest gave: the manifest when it is usable, and every problem found, each naming the file.</summary>
public sealed class PackageManifestLoadResult(PackageManifest? manifest, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public PackageManifest? Manifest { get; } = manifest;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Manifest is not null && !Diagnostics.HasErrors();

    /// <summary>The manifest, or a <see cref="PackageManifestException"/> whose message lists every problem with its file, line and key.</summary>
    public PackageManifest ThrowIfInvalid() => Ok ? Manifest! : throw new PackageManifestException(Diagnostics);
}

/// <summary>A manifest that could not be used. The message has one line per problem, each starting with the file (and line) it is in.</summary>
public sealed class PackageManifestException(IReadOnlyList<ConfigDiagnostic> diagnostics)
    : InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.ToString())))
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Reads a <c>package/1</c> manifest (plan 28a DR2), strictly: the first key is the format id (<c>schema: package/1</c>), a key that is not part of the format is an error with the nearest key that is, a file in
/// another format or another version of this one is refused naming the file, and every problem is a diagnostic with the file, line, column and key path. Nothing here throws on a damaged file.
/// <code>
/// schema: package/1
/// name: orders-sync
/// launch: { kind: assembly, assembly: bin/Orders.dll }          # or { kind: executable, command: bin/orders, args: [--quiet] }
/// built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
/// tasks:
///   - name: extract
///     entry: { type: Orders.Tasks.Extract, method: Run }
///     schedule: { cron: "0 2 * * *", time_zone: Europe/Berlin, misfire: skip, misfire_grace: 5m }   # or every: 15m
///     triggers:
///       - after: { task: other, on: [succeeded, failed] }
///       - webhook: { source: shop }
///       - file: { connection: inbox, path: "in/*.csv", poll: 1m }
///       - store_change: { model: orders, entity: order }
///       - manual: true
///     inputs:      [ { name: since, type: date, required: false, default: "2024-01-01" } ]
///     connections: [ { name: inbox, kind: folder } ]
///     progress:
///       checkpoints: [ { name: orders, per: [customer], parts: [ { name: modified, type: time }, { name: id, type: int } ] } ]
///       state:       [ { name: last_file, type: text } ]
///     limits: { timeout: 30m, memory_mb: 512, cpu: 1.5, concurrency: 2, attempts: 3, backoff: 30s, max_backoff: 10m }
///     requires: [network:outside]
///     capabilities: [network]
/// </code>
/// </summary>
public static class PackageManifestReader
{
    /// <summary>Reads manifest text; <paramref name="file"/> is what messages call it.</summary>
    public static PackageManifestLoadResult Load(string yaml, string file = "package.yaml")
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (root is null || diags.HasErrors())
        {
            if (root is null && diags.Count == 0)
                diags.Add(DefaultDiagnosticCatalog.Instance.Create(SharedCodes.InvalidValue, file, 1, 1, $"{file} is empty; a package manifest starts with `schema: {PackageManifest.FormatId}`."));
            return new(null, diags);
        }

        var positions = new Dictionary<string, YamlNode>(StringComparer.Ordinal) { [""] = root };
        var reader = new Reader(file, diags, positions);
        var manifest = reader.Read(root);
        if (manifest is null || diags.HasErrors()) return new(null, diags);

        diags.AddRange(PackageManifestValidator.Validate(manifest, file, positions));
        return new(diags.HasErrors() ? null : manifest, diags);
    }

    /// <summary>Reads a manifest file; one that cannot be read is a diagnostic naming it.</summary>
    public static PackageManifestLoadResult LoadFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, [DefaultDiagnosticCatalog.Instance.Create(SharedCodes.FileNotFound, path, 0, 0, $"{path} cannot be read: {ex.Message}")]);
        }

        return Load(text, path);
    }

    /// <summary>Reads <paramref name="yaml"/> or throws a <see cref="PackageManifestException"/> naming <paramref name="file"/>.</summary>
    public static PackageManifest Read(string yaml, string file = "package.yaml") => Load(yaml, file).ThrowIfInvalid();

    internal static readonly (string Word, RunOutcome Outcome)[] OutcomeWords =
    [
        ("succeeded", RunOutcome.Succeeded),
        ("completed_with_failures", RunOutcome.CompletedWithFailures),
        ("failed", RunOutcome.Failed),
        ("stopped", RunOutcome.Stopped),
        ("interrupted", RunOutcome.Interrupted),
    ];

    internal static readonly (string Word, ProgressType Type)[] TypeWords = [.. Enum.GetValues<ProgressType>().Select(t => (PackageManifestValidator.TypeWord(t), t))];

    private sealed class Reader(string file, List<ConfigDiagnostic> diags, Dictionary<string, YamlNode> positions)
    {
        private static readonly Regex DurationRule = new(@"^(?<n>\d+(\.\d+)?)(?<unit>ms|s|m|h|d)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        private void Report(string code, string path, YamlNode at, string message, string? fix = null)
        {
            positions.TryAdd(path, at);
            diags.Add(DefaultDiagnosticCatalog.Instance.Create(code, file, at.Line, at.Column, path.Length == 0 ? message : $"`{path}`: {message}", fix, path.Length == 0 ? null : path));
        }

        private void Bad(string path, YamlNode at, string message, string? fix = null) => Report(SharedCodes.InvalidValue, path, at, message, fix);

        private void Keys(YamlMapping map, string path, params string[] allowed)
        {
            foreach (var entry in map.Entries)
            {
                positions.TryAdd(Join(path, entry.Key.Value), entry.Key);
                if (allowed.Contains(entry.Key.Value, StringComparer.Ordinal)) continue;
                var near = DidYouMean.Closest(entry.Key.Value, allowed);
                Report(SharedCodes.UnknownKey, Join(path, entry.Key.Value), entry.Key,
                    $"unknown key `{entry.Key.Value}`" + (path.Length == 0 ? "" : $" in `{path}`") + "." + (near is null ? "" : $" Did you mean `{near}`?"), $"Keys here: {string.Join(", ", allowed)}.");
            }
        }

        private YamlNode? Child(YamlMapping map, string key, string path)
        {
            var node = map.Get(key);
            if (node is not null) positions[Join(path, key)] = node;
            return node;
        }

        private YamlMapping? Map(YamlNode? node, string path, string shape)
        {
            if (node is null) return null;
            positions[path] = node;
            if (node is YamlMapping map) return map;
            Bad(path, node, $"has to be a set of keys, such as {shape}.");
            return null;
        }

        private YamlSequence? Seq(YamlNode? node, string path, string shape)
        {
            if (node is null) return null;
            positions[path] = node;
            if (node is YamlSequence list) return list;
            Bad(path, node, $"has to be a list, such as {shape}.");
            return null;
        }

        private string? Text(YamlMapping map, string key, string path, bool required, bool allowEmpty = false)
        {
            var node = Child(map, key, path);
            if (node is null)
            {
                if (required) Report(SharedCodes.MissingKey, Join(path, key), map, "required key is missing.", $"Add `{key}:`.");
                return null;
            }

            if (node is YamlScalar s && (allowEmpty || s.Value.Length > 0)) return s.Value;
            Bad(Join(path, key), node, "has to be text, not empty, not a list or a set of keys.");
            return null;
        }

        private bool? Flag(YamlMapping map, string key, string path)
        {
            if (Child(map, key, path) is not { } node) return null;
            if (node is YamlScalar { Value: "true" }) return true;
            if (node is YamlScalar { Value: "false" }) return false;
            Bad(Join(path, key), node, "is `true` or `false`.");
            return null;
        }

        private int? Whole(YamlMapping map, string key, string path)
        {
            if (Child(map, key, path) is not { } node) return null;
            if (node is YamlScalar s && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
            Bad(Join(path, key), node, "has to be a whole number (zero or more; the validator says where a limit has to be above zero).");
            return null;
        }

        private decimal? Number(YamlMapping map, string key, string path)
        {
            if (Child(map, key, path) is not { } node) return null;
            if (node is YamlScalar s && decimal.TryParse(s.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return n;
            Bad(Join(path, key), node, "has to be a number, written with a point (1.5).");
            return null;
        }

        private TimeSpan? Duration(YamlMapping map, string key, string path)
        {
            if (Child(map, key, path) is not { } node) return null;
            if (node is YamlScalar s && DurationRule.Match(s.Value) is { Success: true } m)
            {
                var number = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                return m.Groups["unit"].Value switch
                {
                    "ms" => TimeSpan.FromMilliseconds(number),
                    "s" => TimeSpan.FromSeconds(number),
                    "m" => TimeSpan.FromMinutes(number),
                    "h" => TimeSpan.FromHours(number),
                    _ => TimeSpan.FromDays(number),
                };
            }

            Bad(Join(path, key), node, "has to be a length of time: a number and a unit, 500ms, 30s, 5m, 2h or 1d.");
            return null;
        }

        private List<string> Strings(YamlMapping map, string key, string path, string shape)
        {
            var found = new List<string>();
            if (Seq(Child(map, key, path), Join(path, key), shape) is not { } list) return found;
            for (var i = 0; i < list.Items.Count; i++)
            {
                positions[$"{Join(path, key)}[{i}]"] = list.Items[i];
                if (list.Items[i] is YamlScalar { Value.Length: > 0 } s) found.Add(s.Value);
                else Bad($"{Join(path, key)}[{i}]", list.Items[i], "has to be text.");
            }

            return found;
        }

        private T? Choice<T>(YamlMapping map, string key, string path, IEnumerable<(string Word, T Value)> options) where T : struct
        {
            if (Child(map, key, path) is not { } node) return null;
            var list = options.ToList();
            if (node is YamlScalar s && list.FirstOrDefault(o => o.Word == s.Value) is { Word: not null } hit) return hit.Value;
            var near = node is YamlScalar text ? DidYouMean.Closest(text.Value, list.Select(o => o.Word)) : null;
            Bad(Join(path, key), node, $"{(node is YamlScalar t ? $"'{t.Value}'" : "this")} is not one of {string.Join(", ", list.Select(o => o.Word))}." + (near is null ? "" : $" Did you mean `{near}`?"));
            return null;
        }

        public PackageManifest? Read(YamlNode root)
        {
            if (root is not YamlMapping map)
            {
                Bad("", root, $"{file} is not a package manifest: it is a list or a single value, and a manifest is a set of keys starting with `schema: {PackageManifest.FormatId}`.");
                return null;
            }

            if (!Format(map)) return null;

            Keys(map, "", "schema", "name", "description", "launch", "built_against", "tasks");
            var name = Text(map, "name", "", required: true);
            var description = Text(map, "description", "", required: false);
            var launch = Launch(map);
            var built = BuiltAgainst(map);
            var tasks = Tasks(map);
            if (name is null) return null;
            return new PackageManifest(name, description, launch ?? new PackageLaunch(LaunchKind.Assembly), built, tasks);
        }

        /// <summary>The first key has to be <c>schema: package/1</c>; anything else is refused naming the file and what was found.</summary>
        private bool Format(YamlMapping map)
        {
            positions["schema"] = (YamlNode?)map.Entries.FirstOrDefault()?.Key ?? map;
            var first = map.Entries.FirstOrDefault();
            var any = map.GetEntry("schema");
            if (any is null)
            {
                var other = map.GetEntry("schemaVersion");
                Report(SharedCodes.SchemaVersionInvalid, "schema", (YamlNode?)other?.Key ?? (YamlNode?)map.Entries.FirstOrDefault()?.Key ?? map,
                    $"{file} is not a package manifest: it has no `schema` key" + (other is null ? "" : " (`schemaVersion` is the form of other file formats)") + $". A manifest starts with `schema: {PackageManifest.FormatId}`.", $"Add `schema: {PackageManifest.FormatId}` as the first line.");
                return false;
            }

            positions["schema"] = any.Value;
            if (any.Value is not YamlScalar { Value: var text })
            {
                Report(SharedCodes.SchemaVersionInvalid, "schema", any.Value, $"`schema` in {file} has to be text: `schema: {PackageManifest.FormatId}`.");
                return false;
            }

            if (!TryFormatId(text, out var kind, out var version))
            {
                Report(SharedCodes.SchemaVersionInvalid, "schema", any.Value, $"`schema` in {file} is '{text}', which is not a format id (a name, a slash and a version, like {PackageManifest.FormatId}).");
                return false;
            }

            if (kind != "package")
            {
                Report(SharedCodes.SchemaVersionInvalid, "schema", any.Value, $"{file} is a '{text}' file, not a package manifest; this reads only `{PackageManifest.FormatId}`.");
                return false;
            }

            if (version != PackageManifest.FormatVersion)
            {
                Report(version > PackageManifest.FormatVersion ? SharedCodes.SchemaVersionNewer : SharedCodes.SchemaVersionInvalid, "schema", any.Value,
                    $"{file} is format '{text}', and this reader understands only `{PackageManifest.FormatId}`." + (version > PackageManifest.FormatVersion ? " The file was written by a newer version; upgrade the tool." : " Republish the package with a current kit."));
                return false;
            }

            if (first is null || first.Key.Value != "schema")
            {
                Report(SharedCodes.InvalidValue, "schema", any.Key, $"`schema: {PackageManifest.FormatId}` has to be the first key of {file}; it is how a reader knows the format before it reads anything else.");
                return false;
            }

            return true;
        }

        /// <summary>The <c>kind/n</c> form every format id has (the rule of the schema header in <c>DbDataKit.Config</c>).</summary>
        private static bool TryFormatId(string text, out string kind, out int version)
        {
            kind = "";
            version = 0;
            var slash = text.LastIndexOf('/');
            if (slash <= 0 || slash == text.Length - 1 || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1) return false;
            kind = text[..slash];
            return true;
        }

        private PackageLaunch? Launch(YamlMapping root)
        {
            if (Child(root, "launch", "") is null) { Report(SharedCodes.MissingKey, "launch", root, "required key is missing: say how the package starts, `launch: {kind: assembly, assembly: bin/Orders.dll}`.", "Add `launch:`."); return null; }
            if (Map(root.Get("launch"), "launch", "{kind: assembly, assembly: bin/Orders.dll} or {kind: executable, command: bin/orders}") is not { } map) return null;
            var kind = Choice(map, "kind", "launch", [("assembly", LaunchKind.Assembly), ("executable", LaunchKind.Executable)]);
            if (kind is null)
            {
                if (map.Get("kind") is null) Report(SharedCodes.MissingKey, "launch.kind", map, "required key is missing: `assembly` or `executable`.", "Add `kind: assembly`.");
                Keys(map, "launch", "kind", "assembly", "command", "args");
                return null;
            }

            if (kind == LaunchKind.Assembly)
            {
                Keys(map, "launch", "kind", "assembly", "command", "args");
                return new PackageLaunch(LaunchKind.Assembly, Text(map, "assembly", "launch", required: false), Text(map, "command", "launch", required: false), Strings(map, "args", "launch", "[--quiet]") is { Count: > 0 } a ? a : null);
            }

            Keys(map, "launch", "kind", "command", "args", "assembly");
            return new PackageLaunch(LaunchKind.Executable, Text(map, "assembly", "launch", required: false), Text(map, "command", "launch", required: false), Strings(map, "args", "launch", "[--quiet]") is { Count: > 0 } args ? args : null);
        }

        private BuiltAgainst BuiltAgainst(YamlMapping root)
        {
            if (Map(Child(root, "built_against", ""), "built_against", "{dotnet: \"10.0\", protocol: dbdatakit.runner.v1}") is not { } map) return new(null, null, null);
            Keys(map, "built_against", "dotnet", "kit", "protocol");
            return new BuiltAgainst(Text(map, "dotnet", "built_against", false), Text(map, "kit", "built_against", false), Text(map, "protocol", "built_against", false));
        }

        private List<PackageTask> Tasks(YamlMapping root)
        {
            var tasks = new List<PackageTask>();
            if (Child(root, "tasks", "") is null) { Report(SharedCodes.MissingKey, "tasks", root, "required key is missing: a package has at least one task.", "Add `tasks:` with a list of tasks."); return tasks; }
            if (Seq(root.Get("tasks"), "tasks", "- name: extract") is not { } list) return tasks;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var path = $"tasks[{i}]";
                positions[path] = list.Items[i];
                if (Map(list.Items[i], path, "name, entry, schedule, triggers") is { } map && Task(map, path) is { } task) tasks.Add(task);
            }

            return tasks;
        }

        private PackageTask? Task(YamlMapping map, string path)
        {
            Keys(map, path, "name", "entry", "schedule", "triggers", "inputs", "connections", "progress", "limits", "requires", "capabilities", "allow_overlap");
            var name = Text(map, "name", path, required: true);
            var entry = Entry(map, path);
            var schedule = Schedule(map, path);
            var triggers = Triggers(map, path);
            var inputs = Inputs(map, path);
            var connections = Connections(map, path);
            var progress = Progress(map, $"{path}.progress");
            var limits = Limits(map, path);
            var requires = Strings(map, "requires", path, "[network:outside]");
            var capabilities = Strings(map, "capabilities", path, "[network]");
            var overlap = Flag(map, "allow_overlap", path) ?? false;
            return name is null ? null : new PackageTask(name, entry, schedule, triggers, inputs, connections, progress, limits, requires, capabilities, overlap);
        }

        private TaskEntry Entry(YamlMapping task, string path)
        {
            var at = Join(path, "entry");
            if (Map(Child(task, "entry", path), at, "{type: Orders.Tasks.Extract, method: Run}") is not { } map) return new TaskEntry();
            Keys(map, at, "type", "method", "args");
            var args = Strings(map, "args", at, "[--full]");
            return new TaskEntry(Text(map, "type", at, false), Text(map, "method", at, false) ?? TaskEntry.DefaultMethod, args.Count > 0 ? args : null);
        }

        private Schedule Schedule(YamlMapping task, string path)
        {
            var at = Join(path, "schedule");
            if (Map(Child(task, "schedule", path), at, "{cron: \"0 2 * * *\", time_zone: Europe/Berlin} or {every: 15m}") is not { } map) return new Schedule();
            Keys(map, at, "cron", "every", "time_zone", "misfire", "misfire_grace");
            var defaults = new Schedule();
            return new Schedule
            {
                Cron = Text(map, "cron", at, false),
                Every = Duration(map, "every", at),
                TimeZoneId = Text(map, "time_zone", at, false) ?? defaults.TimeZoneId,
                Misfire = Choice(map, "misfire", at, [("run_once", MisfirePolicy.RunOnce), ("skip", MisfirePolicy.Skip)]) ?? defaults.Misfire,
                MisfireGrace = Duration(map, "misfire_grace", at) ?? defaults.MisfireGrace,
            };
        }

        private List<TaskTrigger> Triggers(YamlMapping task, string path)
        {
            var found = new List<TaskTrigger>();
            if (Seq(Child(task, "triggers", path), Join(path, "triggers"), "- after: {task: extract}") is not { } list) return found;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var at = $"{path}.triggers[{i}]";
                positions[at] = list.Items[i];
                if (Map(list.Items[i], at, "after, webhook, file, store_change or manual (one of them)") is not { } map) continue;
                string[] kinds = ["after", "webhook", "file", "store_change", "manual"];
                Keys(map, at, kinds);
                var chosen = map.Entries.Where(e => kinds.Contains(e.Key.Value)).ToList();
                if (chosen.Count != 1)
                {
                    if (chosen.Count == 0 && map.Entries.Count == 0 || chosen.Count > 1) Bad(at, map, $"a trigger is exactly one of: {string.Join(", ", kinds)}; this one has {(chosen.Count == 0 ? "none" : string.Join(" and ", chosen.Select(c => c.Key.Value)))}.");
                    continue;
                }

                var key = chosen[0].Key.Value;
                var body = $"{at}.{key}";
                if (key == "manual")
                {
                    if (Flag(map, "manual", at) is true) found.Add(TaskTrigger.ByHand());
                    continue;
                }

                if (Map(chosen[0].Value, body, key switch { "after" => "{task: extract, on: [succeeded]}", "webhook" => "{source: shop}", "file" => "{connection: inbox, path: \"in/*.csv\"}", _ => "{model: orders}" }) is not { } trigger) continue;
                switch (key)
                {
                    case "after":
                        Keys(trigger, body, "task", "package", "on");
                        var on = Outcomes(trigger, body);
                        found.Add(new TaskTrigger(TriggerKind.AfterRun, Task: Text(trigger, "task", body, true), Package: Text(trigger, "package", body, false), On: on));
                        break;
                    case "webhook":
                        Keys(trigger, body, "source");
                        found.Add(new TaskTrigger(TriggerKind.Webhook, Source: Text(trigger, "source", body, true)));
                        break;
                    case "file":
                        Keys(trigger, body, "connection", "path", "poll");
                        found.Add(new TaskTrigger(TriggerKind.File, Connection: Text(trigger, "connection", body, true), Path: Text(trigger, "path", body, true), Poll: Duration(trigger, "poll", body)));
                        break;
                    default:
                        Keys(trigger, body, "model", "entity");
                        found.Add(new TaskTrigger(TriggerKind.StoreChange, Model: Text(trigger, "model", body, true), Entity: Text(trigger, "entity", body, false)));
                        break;
                }
            }

            return found;
        }

        private List<RunOutcome>? Outcomes(YamlMapping trigger, string body)
        {
            if (Seq(Child(trigger, "on", body), Join(body, "on"), "[succeeded, failed]") is not { } list) return null;
            var found = new List<RunOutcome>();
            for (var i = 0; i < list.Items.Count; i++)
            {
                var at = $"{Join(body, "on")}[{i}]";
                positions[at] = list.Items[i];
                var word = (list.Items[i] as YamlScalar)?.Value;
                var hit = OutcomeWords.FirstOrDefault(o => o.Word == word);
                if (hit.Word is null)
                {
                    var near = word is null ? null : DidYouMean.Closest(word, OutcomeWords.Select(o => o.Word));
                    Bad(at, list.Items[i], $"'{word}' is not an outcome: {string.Join(", ", OutcomeWords.Select(o => o.Word))}." + (near is null ? "" : $" Did you mean `{near}`?"));
                }
                else if (!found.Contains(hit.Outcome)) found.Add(hit.Outcome);
            }

            return found;
        }

        private List<TaskInput> Inputs(YamlMapping task, string path)
        {
            var found = new List<TaskInput>();
            if (Seq(Child(task, "inputs", path), Join(path, "inputs"), "- name: since") is not { } list) return found;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var at = $"{path}.inputs[{i}]";
                positions[at] = list.Items[i];
                if (Map(list.Items[i], at, "{name: since, type: date}") is not { } map) continue;
                Keys(map, at, "name", "type", "required", "default", "description");
                var name = Text(map, "name", at, true);
                var type = Choice(map, "type", at, TypeWords) ?? ProgressType.Text;
                var required = Flag(map, "required", at) ?? false;
                var def = Text(map, "default", at, false, allowEmpty: true);
                var description = Text(map, "description", at, false);
                if (name is not null) found.Add(new TaskInput(name, type, required, def, description));
            }

            return found;
        }

        private List<ConnectionSlot> Connections(YamlMapping task, string path)
        {
            var found = new List<ConnectionSlot>();
            if (Seq(Child(task, "connections", path), Join(path, "connections"), "- {name: source, kind: sqlserver}") is not { } list) return found;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var at = $"{path}.connections[{i}]";
                positions[at] = list.Items[i];
                if (Map(list.Items[i], at, "{name: source, kind: sqlserver}") is not { } map) continue;
                Keys(map, at, "name", "kind");
                var name = Text(map, "name", at, true);
                var kind = Text(map, "kind", at, true);
                if (name is not null && kind is not null) found.Add(new ConnectionSlot(name, kind));
            }

            return found;
        }

        private ProgressDeclaration Progress(YamlMapping task, string at)
        {
            if (Map(Child(task, "progress", at[..at.LastIndexOf('.')]), at, "{checkpoints: [...], state: [...]}") is not { } map) return ProgressDeclaration.Empty;
            Keys(map, at, "checkpoints", "state");
            return new ProgressDeclaration(Checkpoints(map, at), States(map, at));
        }

        private List<DeclaredCheckpoint> Checkpoints(YamlMapping progress, string path)
        {
            var found = new List<DeclaredCheckpoint>();
            if (Seq(Child(progress, "checkpoints", path), Join(path, "checkpoints"), "- {name: orders, parts: [...]}") is not { } list) return found;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var at = $"{path}.checkpoints[{i}]";
                positions[at] = list.Items[i];
                if (Map(list.Items[i], at, "{name: orders, per: [customer], parts: [{name: modified, type: time}]}") is not { } map) continue;
                Keys(map, at, "name", "per", "renamed_from", "reset", "fill", "parts");
                var name = Text(map, "name", at, true);
                var per = Strings(map, "per", at, "[customer]");
                var parts = Parts(map, at);
                var renamed = Text(map, "renamed_from", at, false, allowEmpty: true);
                var reset = Flag(map, "reset", at) ?? false;
                var fill = Text(map, "fill", at, false, allowEmpty: true);
                if (name is not null) found.Add(new DeclaredCheckpoint(name, parts, per.Count > 0 ? per : null, renamed, reset, fill));
            }

            return found;
        }

        private Dictionary<string, DeclaredPart> Parts(YamlMapping checkpoint, string path)
        {
            var found = new Dictionary<string, DeclaredPart>(StringComparer.Ordinal);
            if (Seq(Child(checkpoint, "parts", path), Join(path, "parts"), "- {name: modified, type: time}") is not { } list) return found;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var at = $"{path}.parts[{i}]";
                positions[at] = list.Items[i];
                if (Map(list.Items[i], at, "{name: modified, type: time}") is not { } map) continue;
                Keys(map, at, "name", "type", "default", "renamed_from", "format", "reset");
                var name = Text(map, "name", at, true);
                var type = Choice(map, "type", at, TypeWords);
                if (type is null && map.Get("type") is null) Report(SharedCodes.MissingKey, $"{at}.type", map, "required key is missing: text, int, decimal, time, date, bool, bytes or json.", "Add `type:`.");
                var part = new DeclaredPart(type ?? ProgressType.Text, Text(map, "default", at, false, allowEmpty: true), Text(map, "renamed_from", at, false, allowEmpty: true), Text(map, "format", at, false, allowEmpty: true), Flag(map, "reset", at) ?? false);
                if (name is null) continue;
                if (!found.TryAdd(name, part))
                    Report(SharedCodes.ValidationFailed, $"{at}.name", (YamlNode?)map.Get("name") ?? map, $"the part '{name}' is declared twice in this checkpoint (also at {path}.parts[{found.Keys.ToList().IndexOf(name)}]); each value has one name.");
            }

            return found;
        }

        private List<DeclaredState> States(YamlMapping progress, string path)
        {
            var found = new List<DeclaredState>();
            if (Seq(Child(progress, "state", path), Join(path, "state"), "- {name: last_file, type: text}") is not { } list) return found;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var at = $"{path}.state[{i}]";
                positions[at] = list.Items[i];
                if (Map(list.Items[i], at, "{name: last_file, type: text}") is not { } map) continue;
                Keys(map, at, "name", "type", "per", "default", "renamed_from", "format", "reset", "fill");
                var name = Text(map, "name", at, true);
                var type = Choice(map, "type", at, TypeWords);
                if (type is null && map.Get("type") is null) Report(SharedCodes.MissingKey, $"{at}.type", map, "required key is missing: text, int, decimal, time, date, bool, bytes or json.", "Add `type:`.");
                var per = Strings(map, "per", at, "[customer]");
                var def = Text(map, "default", at, false, allowEmpty: true);
                var renamed = Text(map, "renamed_from", at, false, allowEmpty: true);
                var format = Text(map, "format", at, false, allowEmpty: true);
                var reset = Flag(map, "reset", at) ?? false;
                var fill = Text(map, "fill", at, false, allowEmpty: true);
                if (name is not null) found.Add(new DeclaredState(name, type ?? ProgressType.Text, per.Count > 0 ? per : null, def, renamed, format, reset, fill));
            }

            return found;
        }

        private TaskLimits Limits(YamlMapping task, string path)
        {
            var at = Join(path, "limits");
            if (Map(Child(task, "limits", path), at, "{timeout: 30m, memory_mb: 512, cpu: 1.5, concurrency: 2, attempts: 3}") is not { } map) return TaskLimits.None;
            Keys(map, at, "timeout", "memory_mb", "cpu", "concurrency", "attempts", "backoff", "max_backoff");
            return new TaskLimits(Duration(map, "timeout", at), Whole(map, "memory_mb", at), Number(map, "cpu", at), Whole(map, "concurrency", at), Whole(map, "attempts", at), Duration(map, "backoff", at), Duration(map, "max_backoff", at));
        }
    }
}
