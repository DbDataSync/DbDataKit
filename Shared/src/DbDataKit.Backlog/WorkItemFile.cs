using System.Globalization;
using System.Text;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Backlog;

/// <summary>One item of a <c>work-item/1</c> file. <see cref="Id"/> is a name local to the file, so another item can name an epic made in the same file.</summary>
public sealed record WorkItemEntry(string Id, string ItemType, string Title, string? Description, string? Priority, decimal? Estimate, string? Assignee, string? Epic, IReadOnlyList<string> Labels);

/// <summary>A read <c>work-item/1</c> file: the backlog the items are for and the items, epics first.</summary>
public sealed record WorkItemFile(string Project, IReadOnlyList<WorkItemEntry> Items);

/// <summary>The result of reading a <c>work-item/1</c> file: the items, or the diagnostics (file, line, column) that say why there are none.</summary>
public sealed class WorkItemLoadResult(WorkItemFile? file, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public WorkItemFile? File { get; } = file;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => File is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of <c>work-item/1</c> (plan 27a BK01): <c>schema</c>, <c>project</c> and <c>items</c> (id, type, title, description, priority, estimate, assignee, epic, labels). It is how items are imported (a person, an agent's proposal in BK09 or an integration task in BK08
/// writes the file; <see cref="BacklogImport.Import"/> makes the items) and how a backlog is exported. Every problem is a diagnostic with file, line and column; a newer format is refused naming the file and both numbers.
/// </summary>
public static class WorkItemLoader
{
    public const string SchemaId = "work-item/1";
    public const int Version = 1;

    public const string BadFile = "DBD-BKL-010";
    public const string BadItem = "DBD-BKL-011";

    public static WorkItemLoadResult LoadFile(string path) => Load(File.ReadAllText(path), path);

    public static WorkItemLoadResult Load(string yaml, string file = "work-items.yml")
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diagnostics);
        if (diagnostics.HasErrors()) return new(null, diagnostics);
        var reader = new Reader(file, diagnostics);
        var read = root is YamlMapping map ? reader.Read(map) : reader.NotAMapping(root);
        return new(diagnostics.HasErrors() ? null : read, diagnostics);
    }

    private sealed class Reader(string file, List<ConfigDiagnostic> diagnostics) : YamlFieldReader(file, diagnostics)
    {
        public WorkItemFile? NotAMapping(YamlNode? root)
        {
            Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(BadFile, File, root?.Line ?? 1, root?.Column ?? 1, "A work item file is a mapping with `schema: work-item/1`, `project` and `items`.", "Start the file with `schema: work-item/1`."));
            return null;
        }

        public WorkItemFile? Read(YamlMapping map)
        {
            var schema = map.Get("schema");
            if (schema is not YamlScalar { Value: var text })
            {
                Add(BadFile, schema ?? map, schema is null ? "`schema` is missing." : "`schema` is text.", $"Add `schema: {SchemaId}`.");
                return null;
            }

            if (text != SchemaId)
            {
                if (text.StartsWith("work-item/", StringComparison.Ordinal) && int.TryParse(text["work-item/".Length..], out var number) && number > Version)
                    Add(SharedCodes.SchemaVersionNewer, schema, $"`schema` is `{text}`, but this tool understands `work-item` files up to version {Version}.", "Upgrade the tool; this file was written by a newer version.");
                else Add(BadFile, schema, $"`schema` is `{text}`.", $"Use `schema: {SchemaId}`.");
                return null;
            }

            CheckKeys(map, ["schema", "project", "items"], "the work item file");
            var project = Scalar(map, "project", true, map);
            var items = new List<WorkItemEntry>();
            if (map.Get("items") is not { } node) Add(SharedCodes.MissingKey, map, "`items` is missing.", "Add `items:` with a `- ` entry for each item.");
            else if (node is not YamlSequence list) Add(SharedCodes.InvalidValue, node, "`items` is a list of items, one mapping each.");
            else
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var epics = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in list.Items)
                {
                    if (item is not YamlMapping entry) { Add(SharedCodes.InvalidValue, item, "An item is a mapping with `id`, `type` and `title`."); continue; }
                    if (Read(entry, ids) is { } read) { items.Add(read); if (read.ItemType == "epic") epics.Add(read.Id); }
                }

                foreach (var entry in list.Items.OfType<YamlMapping>())
                {
                    if (entry.Get("epic") is YamlScalar { Value: var epic } at && !epics.Contains(epic))
                        Add(BadItem, at, $"`epic` is `{epic}`, which is not an item of type epic in this file.", epics.Count == 0 ? "Make the epic an item of this file, with `type: epic`." : $"Epics of this file: {string.Join(", ", epics)}.");
                }
            }

            if (project is not null && !System.Text.RegularExpressions.Regex.IsMatch(project.Value, "^[A-Z][A-Z0-9]{1,9}$"))
                Add(BadFile, project, $"`project` is `{project.Value}`, which is not a project key: two to ten capital letters and digits, starting with a letter.");
            return project is null ? null : new WorkItemFile(project.Value, [.. items.Where(i => i.ItemType == "epic"), .. items.Where(i => i.ItemType != "epic")]);
        }

        private WorkItemEntry? Read(YamlMapping entry, HashSet<string> ids)
        {
            CheckKeys(entry, ["id", "type", "title", "description", "priority", "estimate", "assignee", "epic", "labels"], "an item");
            var id = Scalar(entry, "id", true, entry);
            var type = Scalar(entry, "type", true, entry);
            var title = Scalar(entry, "title", true, entry);
            if (id is null || type is null || title is null) return null;
            var ok = true;
            if (!ids.Add(id.Value)) { Add(BadItem, id, $"The item id `{id.Value}` is used twice in the file."); ok = false; }
            if (!BacklogService.ItemTypes.Contains(type.Value)) { Add(BadItem, type, $"`type` is `{type.Value}`; an item is an {string.Join(", ", BacklogService.ItemTypes.Take(3))} or {BacklogService.ItemTypes[^1]}." + (DidYouMean.Closest(type.Value, BacklogService.ItemTypes) is { } near ? $" Did you mean `{near}`?" : "")); ok = false; }
            if (title.Value.Length > 250) { Add(BadItem, title, "A title is at most 250 characters; the rest belongs in the description."); ok = false; }
            string? description = null;
            if (entry.Get("description") is { } d) { if (d is YamlScalar { Value.Length: > 0 } ds) description = ds.Value; else { Add(SharedCodes.InvalidValue, d, "`description` is text."); ok = false; } }
            string? priority = null;
            if (entry.Get("priority") is { } p)
            {
                if (p is YamlScalar { Value: var pv } && BacklogService.Priorities.Contains(pv)) priority = pv;
                else { Add(BadItem, p, $"`priority` is one of {string.Join(", ", BacklogService.Priorities)}."); ok = false; }
            }

            decimal? estimate = null;
            if (entry.Get("estimate") is { } e)
            {
                if (e is YamlScalar { Value: var ev } && decimal.TryParse(ev, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var points) && points >= 0 && points <= 1000 && points % 0.5m == 0) estimate = points;
                else { Add(BadItem, e, "`estimate` is a number of points from 0 to 1000, in steps of a half."); ok = false; }
            }

            string? assignee = null;
            if (entry.Get("assignee") is { } a) { if (a is YamlScalar { Value.Length: > 0 } av) assignee = av.Value; else { Add(SharedCodes.InvalidValue, a, "`assignee` is a person's id."); ok = false; } }
            string? epic = null;
            if (entry.Get("epic") is { } ep)
            {
                if (type.Value == "epic") { Add(BadItem, ep, "An epic cannot belong to an epic."); ok = false; }
                else if (ep is YamlScalar { Value.Length: > 0 } es) epic = es.Value;
                else { Add(SharedCodes.InvalidValue, ep, "`epic` is the id of an epic of this file."); ok = false; }
            }

            var labels = StringList(entry, "labels", false, unique: true)?.Select(l => l.Value).ToList() ?? [];
            foreach (var label in labels.Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, "^[a-z0-9][a-z0-9-]{0,29}$")))
            {
                Add(BadItem, entry.Get("labels")!, $"`{label}` is not a label: lower case letters, digits and `-`, 30 characters at most.");
                ok = false;
            }

            return ok ? new WorkItemEntry(id.Value, type.Value, title.Value, description, priority, estimate, assignee, epic, labels) : null;
        }
    }
}

/// <summary>Importing and exporting work item files over the service.</summary>
public static class BacklogImport
{
    /// <summary>
    /// Makes the items of a file in its backlog, epics first so an item can name its epic; each is made as <paramref name="person"/> and goes last in the backlog. Everything is checked before anything is written (a title, a type, an epic that exists);
    /// the first refusal stops the import naming the item, and what was made before it stays (the history says who made it). Returns the items made, in the order of the file.
    /// </summary>
    public static IReadOnlyList<BacklogItemRow> Import(BacklogService service, BacklogPerson person, WorkItemFile file)
    {
        if (service.Project(file.Project) is null) throw new BacklogException(BacklogProblem.NotFound, $"There is no backlog {file.Project}.");
        var made = new Dictionary<string, BacklogItemRow>(StringComparer.Ordinal);
        var result = new List<BacklogItemRow>();
        foreach (var entry in file.Items)
        {
            try
            {
                Guid? epic = entry.Epic is { } e ? made[e].Id : null;
                var item = service.CreateItem(person, new NewItem(file.Project, entry.ItemType, entry.Title, entry.Description, entry.Priority, entry.Estimate, entry.Assignee, epic, entry.Labels));
                made[entry.Id] = item;
                result.Add(item);
            }
            catch (BacklogException ex)
            {
                throw new BacklogException(ex.Problem, $"The item '{entry.Id}' ({entry.Title}) was not made: {ex.Message}" + (result.Count > 0 ? $" {result.Count} item(s) before it were made." : ""));
            }
        }

        return result;
    }

    /// <summary>Writes a backlog's items as a <c>work-item/1</c> file (state, sprint and history are not part of it: the file is what to make, not what became of it). Ids are the items' keys.</summary>
    public static string Export(BacklogService service, string project)
    {
        var items = service.Items(new BacklogItemQuery(Project: project, Limit: 1_000_000));
        var keys = items.ToDictionary(i => i.Id, i => i.Key);
        var yaml = new StringBuilder();
        yaml.Append("schema: ").Append(WorkItemLoader.SchemaId).Append('\n').Append("project: ").Append(project).Append('\n').Append("items:\n");
        foreach (var item in items.OrderBy(i => i.ItemType != "epic").ThenBy(i => i.SortKey))
        {
            yaml.Append("  - id: ").Append(item.Key).Append('\n').Append("    type: ").Append(item.ItemType).Append('\n').Append("    title: ").Append(Quote(item.Title)).Append('\n');
            if (item.Description is not null) yaml.Append("    description: ").Append(Quote(item.Description)).Append('\n');
            yaml.Append("    priority: ").Append(item.Priority).Append('\n');
            if (item.Estimate is { } points) yaml.Append("    estimate: ").Append(points.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
            if (item.Assignee is not null) yaml.Append("    assignee: ").Append(Quote(item.Assignee)).Append('\n');
            if (item.EpicId is { } epic && keys.TryGetValue(epic, out var epicKey)) yaml.Append("    epic: ").Append(epicKey).Append('\n');
            var labels = BacklogService.LabelsOf(item);
            if (labels.Count > 0) yaml.Append("    labels: [").Append(string.Join(", ", labels)).Append("]\n");
        }

        return yaml.ToString();
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
