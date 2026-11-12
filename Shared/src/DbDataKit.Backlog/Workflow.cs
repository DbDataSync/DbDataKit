using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Backlog;

/// <summary>What a person may do in a backlog. The console maps its administrators to <see cref="Lead"/> and its viewers to <see cref="Viewer"/>; an install with more roles maps them here.</summary>
public enum BacklogRole
{
    /// <summary>Reads everything and changes nothing.</summary>
    Viewer = 0,

    /// <summary>Works on the backlog: makes and edits items, comments, links, attaches, moves an item along the workflow's member transitions, plans a sprint.</summary>
    Member = 1,

    /// <summary>A member who also starts and closes sprints, makes the transitions the workflow keeps for a lead (reopening), deletes an item and makes projects.</summary>
    Lead = 2,
}

/// <summary>Where a state sits: the board and a sprint's summary count by category.</summary>
public enum StateCategory
{
    Todo,
    Doing,
    Done,
}

/// <summary>A state of the workflow. <see cref="Requires"/> names the fields an item must hold to enter it (<see cref="WorkflowLoader.RequirableFields"/>).</summary>
public sealed record WorkflowState(string Id, string Title, StateCategory Category, IReadOnlyList<string> Requires);

/// <summary>A move from one state to another (<see cref="From"/> is <c>*</c> for any state) and the lowest role that may make it.</summary>
public sealed record WorkflowTransition(string From, string To, BacklogRole Role);

/// <summary>The states of a backlog, the state a new item starts in and the moves between states.</summary>
public sealed class WorkflowDefinition(IReadOnlyList<WorkflowState> states, string initial, IReadOnlyList<WorkflowTransition> transitions)
{
    public IReadOnlyList<WorkflowState> States { get; } = states;

    public string Initial { get; } = initial;

    public IReadOnlyList<WorkflowTransition> Transitions { get; } = transitions;

    public WorkflowState? State(string id) => States.FirstOrDefault(s => s.Id == id);

    public bool IsDone(string stateId) => State(stateId)?.Category == StateCategory.Done;

    /// <summary>The transition from one state to another, or null when the workflow has none; a transition named for the state wins over one for any state.</summary>
    public WorkflowTransition? Find(string from, string to) =>
        Transitions.FirstOrDefault(t => t.From == from && t.To == to) ?? Transitions.FirstOrDefault(t => t.From == "*" && t.To == to && from != to);

    /// <summary>The states an item in <paramref name="from"/> can be moved to by someone holding <paramref name="role"/>.</summary>
    public IReadOnlyList<string> Targets(string from, BacklogRole role) =>
        [.. States.Where(s => Find(from, s.Id) is { } t && role >= t.Role).Select(s => s.Id)];

    /// <summary>The states an item in <paramref name="from"/> can be moved to by anyone at all (so what a lead could do too is listed).</summary>
    public IReadOnlyList<string> Targets(string from) => Targets(from, BacklogRole.Lead);
}

/// <summary>The result of reading a <c>backlog-workflow/1</c> file: the workflow, or the diagnostics (file, line, column) that say why there is none.</summary>
public sealed class WorkflowLoadResult(WorkflowDefinition? workflow, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public WorkflowDefinition? Workflow { get; } = workflow;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Workflow is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of <c>backlog-workflow/1</c> (plan 27a BK01): <c>schema</c>, <c>states</c> (id, title, category, requires), <c>initial</c> and <c>transitions</c> (from, to, role). A problem is a diagnostic with the file, line and column of what is wrong:
/// an unknown key (with a near suggestion), a state used twice, a transition between states that do not exist or from a state to itself, an initial state that is not declared, a workflow with no state of the done category, and a state that nothing leads to from the initial state.
/// A newer format is refused naming the file and both numbers, as every format of the suite is. In version 1 the library's own <see cref="BacklogWorkflows.Default"/> is the only workflow in use: no installation reads a file of its own.
/// </summary>
public static partial class WorkflowLoader
{
    public const string SchemaId = "backlog-workflow/1";
    public const int Version = 1;

    public const string BadFile = "DBD-BKL-001";
    public const string BadState = "DBD-BKL-002";
    public const string BadTransition = "DBD-BKL-003";
    public const string Unreachable = "DBD-BKL-004";

    /// <summary>The fields a state may require of an item that enters it.</summary>
    public static IReadOnlyList<string> RequirableFields { get; } = ["assignee", "estimate", "resolution", "description", "sprint"];

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex StateId();

    public static WorkflowLoadResult LoadFile(string path) => Load(File.ReadAllText(path), path);

    public static WorkflowLoadResult Load(string yaml, string file = "workflow.yml")
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diagnostics);
        if (diagnostics.HasErrors()) return new(null, diagnostics);
        var reader = new Reader(file, diagnostics);
        var workflow = root is YamlMapping map ? reader.Read(map) : reader.NotAMapping(root);
        return new(diagnostics.HasErrors() ? null : workflow, diagnostics);
    }

    private sealed class Reader(string file, List<ConfigDiagnostic> diagnostics) : YamlFieldReader(file, diagnostics)
    {
        public WorkflowDefinition? NotAMapping(YamlNode? root)
        {
            Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(BadFile, File, root?.Line ?? 1, root?.Column ?? 1, "A workflow file is a mapping with `schema: backlog-workflow/1`, `states`, `initial` and `transitions`.", "Start the file with `schema: backlog-workflow/1`."));
            return null;
        }

        public WorkflowDefinition? Read(YamlMapping map)
        {
            var schema = map.Get("schema");
            if (schema is not YamlScalar { Value: var text })
            {
                Add(BadFile, schema ?? map, schema is null ? "`schema` is missing." : "`schema` is text.", $"Add `schema: {SchemaId}`.");
                return null;
            }

            if (text != SchemaId)
            {
                if (text.StartsWith("backlog-workflow/", StringComparison.Ordinal) && int.TryParse(text["backlog-workflow/".Length..], out var number) && number > Version)
                    Add(SharedCodes.SchemaVersionNewer, schema, $"`schema` is `{text}`, but this tool understands `backlog-workflow` files up to version {Version}.", "Upgrade the tool; this file was written by a newer version.");
                else Add(BadFile, schema, $"`schema` is `{text}`.", $"Use `schema: {SchemaId}`.");
                return null;
            }

            CheckKeys(map, ["schema", "states", "initial", "transitions"], "the workflow file");
            var states = ReadStates(map);
            var initial = Scalar(map, "initial", true, map)?.Value;
            var known = states.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            if (initial is not null && states.Count > 0 && !known.Contains(initial))
                Add(BadState, map.Get("initial")!, $"`initial` is `{initial}`, which is not a state of this workflow.", $"States: {string.Join(", ", known)}.");
            var transitions = ReadTransitions(map, known);
            if (states.Count > 0 && !states.Any(s => s.Category == StateCategory.Done) && map.Get("states") is { } at)
                Add(BadState, at, "No state is in the `done` category, so no item could ever be finished.", "Give at least one state `category: done`.");
            if (initial is not null && known.Contains(initial) && !Diagnostics.Any(d => d.Severity == ConfigSeverity.Error)) CheckReachable(map, states, initial, transitions);
            return initial is null ? null : new WorkflowDefinition(states, initial, transitions);
        }

        private List<WorkflowState> ReadStates(YamlMapping map)
        {
            var states = new List<WorkflowState>();
            if (map.Get("states") is not { } node) { Add(SharedCodes.MissingKey, map, "`states` is missing.", "Add `states:` with a `- ` entry for each state."); return states; }
            if (node is not YamlSequence list || list.Items.Count == 0) { Add(SharedCodes.InvalidValue, node, "`states` is a list of states, one mapping each, and has at least one.", "Write `states:` with a `- ` entry for each state."); return states; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping entry) { Add(SharedCodes.InvalidValue, item, "A state is a mapping with `id`, `title`, `category` and optionally `requires`."); continue; }
                CheckKeys(entry, ["id", "title", "category", "requires"], "a state");
                var id = Scalar(entry, "id", true, entry);
                if (id is null) continue;
                if (!StateId().IsMatch(id.Value)) { Add(BadState, id, $"`{id.Value}` is not a state id: lower case letters, digits and `-`, starting with a letter, 32 characters at most."); continue; }
                if (!seen.Add(id.Value)) { Add(BadState, id, $"The state `{id.Value}` is declared twice."); continue; }
                var title = entry.Get("title") is null ? id.Value : Scalar(entry, "title", true, entry)?.Value ?? id.Value;
                var category = StateCategory.Todo;
                var categoryNode = Scalar(entry, "category", true, entry);
                if (categoryNode is not null)
                {
                    if (!Enum.TryParse(categoryNode.Value, ignoreCase: false, out category) || categoryNode.Value != categoryNode.Value.ToLowerInvariant())
                        Add(BadState, categoryNode, $"`category` is `{categoryNode.Value}`; a category is todo, doing or done.", "Use `category: todo`, `doing` or `done`.");
                }

                var requires = new List<string>();
                if (StringList(entry, "requires", false, unique: true) is { } fields)
                {
                    foreach (var field in fields)
                    {
                        if (RequirableFields.Contains(field.Value)) requires.Add(field.Value);
                        else Add(BadState, field, $"`{field.Value}` is not a field a state can require." + (DidYouMean.Closest(field.Value, RequirableFields) is { } near ? $" Did you mean `{near}`?" : ""), $"Fields: {string.Join(", ", RequirableFields)}.");
                    }
                }

                states.Add(new WorkflowState(id.Value, title, category, requires));
            }

            return states;
        }

        private List<WorkflowTransition> ReadTransitions(YamlMapping map, HashSet<string> known)
        {
            var transitions = new List<WorkflowTransition>();
            if (map.Get("transitions") is not { } node) { Add(SharedCodes.MissingKey, map, "`transitions` is missing.", "Add `transitions:` with a `- ` entry for each move."); return transitions; }
            if (node is not YamlSequence list) { Add(SharedCodes.InvalidValue, node, "`transitions` is a list of moves, one mapping each."); return transitions; }
            var seen = new HashSet<(string, string)>();
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping entry) { Add(SharedCodes.InvalidValue, item, "A transition is a mapping with `from`, `to` and `role`."); continue; }
                CheckKeys(entry, ["from", "to", "role"], "a transition");
                var from = Scalar(entry, "from", true, entry);
                var to = Scalar(entry, "to", true, entry);
                var role = Scalar(entry, "role", true, entry);
                if (from is null || to is null || role is null) continue;
                var ok = true;
                if (from.Value != "*" && !known.Contains(from.Value)) { Add(BadTransition, from, $"`from` is `{from.Value}`, which is not a state of this workflow." + (DidYouMean.Closest(from.Value, known) is { } near ? $" Did you mean `{near}`?" : ""), "Use a declared state, or `*` for any state."); ok = false; }
                if (!known.Contains(to.Value)) { Add(BadTransition, to, $"`to` is `{to.Value}`, which is not a state of this workflow." + (DidYouMean.Closest(to.Value, known) is { } near ? $" Did you mean `{near}`?" : "")); ok = false; }
                if (ok && from.Value == to.Value) { Add(BadTransition, from, $"A transition from `{from.Value}` to itself moves nothing."); ok = false; }
                BacklogRole parsed = default;
                if (role.Value is "member") parsed = BacklogRole.Member;
                else if (role.Value is "lead") parsed = BacklogRole.Lead;
                else { Add(BadTransition, role, $"`role` is `{role.Value}`; a transition is made by a member or a lead.", "Use `role: member` or `role: lead`."); ok = false; }
                if (ok && !seen.Add((from.Value, to.Value))) { Add(BadTransition, from, $"The transition from `{from.Value}` to `{to.Value}` is listed twice."); ok = false; }
                if (ok) transitions.Add(new WorkflowTransition(from.Value, to.Value, parsed));
            }

            return transitions;
        }

        private void CheckReachable(YamlMapping map, List<WorkflowState> states, string initial, List<WorkflowTransition> transitions)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal) { initial };
            var queue = new Queue<string>([initial]);
            while (queue.TryDequeue(out var at))
            {
                foreach (var t in transitions.Where(t => (t.From == at || t.From == "*") && t.To != at))
                    if (reached.Add(t.To)) queue.Enqueue(t.To);
            }

            foreach (var state in states.Where(s => !reached.Contains(s.Id)))
                Add(Unreachable, map.Get("states")!, $"The state `{state.Id}` cannot be reached from `{initial}`: no chain of transitions leads to it.", "Add a transition to it, or remove the state.");
        }
    }
}

/// <summary>The workflows of the library: the default every backlog starts with.</summary>
public static class BacklogWorkflows
{
    private static readonly Lazy<WorkflowDefinition> DefaultLazy = new(() =>
    {
        var assembly = typeof(BacklogWorkflows).Assembly;
        using var stream = assembly.GetManifestResourceStream("DbDataKit.Backlog.default-workflow.yml") ?? throw new InvalidOperationException("The default workflow is not embedded in DbDataKit.Backlog.");
        using var reader = new StreamReader(stream);
        var result = WorkflowLoader.Load(reader.ReadToEnd(), "default-workflow.yml");
        return result.Workflow ?? throw new InvalidOperationException("The default workflow of DbDataKit.Backlog does not load: " + string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
    });

    /// <summary>The default workflow: backlog, ready, in progress, in review, done and cancelled.</summary>
    public static WorkflowDefinition Default => DefaultLazy.Value;

    /// <summary>The schema of <c>backlog-workflow/1</c>, as text.</summary>
    public static string WorkflowSchema() => Schema("backlog-workflow.schema.json");

    /// <summary>The schema of <c>work-item/1</c>, as text.</summary>
    public static string WorkItemSchema() => Schema("work-item.schema.json");

    private static string Schema(string file)
    {
        var assembly = typeof(BacklogWorkflows).Assembly;
        using var stream = assembly.GetManifestResourceStream("DbDataKit.Backlog.schemas." + file) ?? throw new InvalidOperationException($"The schema {file} is not embedded in DbDataKit.Backlog.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
