using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.State;

namespace DbDataKit.Backlog;

/// <summary>Why the service refused: the host maps these to its own answers (404, 403, 400 or 422, 409).</summary>
public enum BacklogProblem
{
    /// <summary>There is no such project, item, sprint, comment, link or attachment.</summary>
    NotFound,

    /// <summary>The person's role is below what the act needs.</summary>
    Forbidden,

    /// <summary>The act is not allowed by the rules (a transition the workflow does not have, a missing required field, a second active sprint) or a value is not valid.</summary>
    Invalid,

    /// <summary>Someone changed the row first; read it again.</summary>
    Conflict,
}

/// <summary>A refusal with its reason in words a person can act on.</summary>
public sealed class BacklogException(BacklogProblem problem, string message) : Exception(message)
{
    public BacklogProblem Problem { get; } = problem;
}

/// <summary>The person an act is done as: their id, the name to keep with what they change, and the role the host gives them.</summary>
public sealed record BacklogPerson(string Id, string Name, BacklogRole Role);

/// <summary>A field of a change that may be left alone (<c>default</c>), set to a value or cleared (<see cref="Opt.Of{T}"/> with null).</summary>
public readonly record struct Opt<T>(bool IsSet, T? Value);

/// <summary>Makes an <see cref="Opt{T}"/> that is set.</summary>
public static class Opt
{
    public static Opt<T> Of<T>(T? value) => new(true, value);
}

/// <summary>A new item. <c>Priority</c> defaults to medium; the item starts in the workflow's initial state, last in the backlog.</summary>
public sealed record NewItem(string Project, string ItemType, string Title, string? Description = null, string? Priority = null, decimal? Estimate = null, string? Assignee = null, Guid? Epic = null, IReadOnlyList<string>? Labels = null);

/// <summary>What to change on an item: only the fields that are set.</summary>
public sealed record ItemPatch
{
    public Opt<string?> Title { get; init; }

    public Opt<string?> Description { get; init; }

    public Opt<string?> Priority { get; init; }

    public Opt<decimal?> Estimate { get; init; }

    public Opt<string?> Assignee { get; init; }

    public Opt<Guid?> Epic { get; init; }

    public Opt<IReadOnlyList<string>?> Labels { get; init; }
}

/// <summary>An item with what belongs to it, for the item page.</summary>
public sealed record BacklogItemDetail(BacklogItemRow Item, IReadOnlyList<string> Targets, IReadOnlyList<BacklogCommentRow> Comments, IReadOnlyList<BacklogLinkRow> Links, IReadOnlyList<BacklogAttachmentRow> Attachments,
    IReadOnlyList<BacklogHistoryRow> History, IReadOnlyList<BacklogItemRow> Children);

/// <summary>What closing a sprint did: finished items stay in it, the others went to the next sprint or back to the backlog.</summary>
public sealed record SprintSummary(int Done, int Moved, decimal PointsDone, decimal PointsMoved, Guid? MovedTo);

/// <summary>
/// The rules of the backlog over the state module: who may do what, which moves along the workflow are allowed and what they require, the checks that keep an epic, a sprint and a link true, and the history of each change. Everything it writes goes through
/// <see cref="BacklogStore"/> in one transaction per act with its history rows; a change made on a stale copy of an item is refused (<see cref="BacklogProblem.Conflict"/>) rather than overwriting someone's. Attachments are payloads of the payload store
/// (<paramref name="payloads"/>); without one, adding an attachment is refused in words.
/// </summary>
public sealed partial class BacklogService(BacklogStore store, WorkflowDefinition? workflow = null, PayloadStore? payloads = null)
{
    /// <summary>The kinds of work item.</summary>
    public static IReadOnlyList<string> ItemTypes { get; } = ["epic", "story", "task", "bug"];

    /// <summary>The priorities, lowest first.</summary>
    public static IReadOnlyList<string> Priorities { get; } = ["lowest", "low", "medium", "high", "highest"];

    /// <summary>The kinds of link: to another item, or to a suite object, a workspace or a pull request.</summary>
    public static IReadOnlyList<string> LinkKinds { get; } = ["relates", "blocks", "duplicates", "object", "workspace", "pull-request"];

    /// <summary>What an <c>object</c> link may name (plan 27a BK05); the identity is the lineage graph's, or the other side's own.</summary>
    public static IReadOnlyList<string> ObjectKinds { get; } = ["build-model", "sync-replication", "semantic-model", "store-entity", "app", "run", "change-request"];

    /// <summary>The largest attachment, in bytes, unless the service is told another.</summary>
    public const long DefaultMaxAttachmentBytes = 25L * 1024 * 1024;

    private const long SortStep = 1024;

    private readonly WorkflowDefinition _workflow = workflow ?? BacklogWorkflows.Default;

    public WorkflowDefinition Workflow => _workflow;

    public BacklogStore Store => store;

    [GeneratedRegex("^[A-Z][A-Z0-9]{1,9}$")]
    private static partial Regex ProjectKey();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,29}$")]
    private static partial Regex Label();

    private static BacklogException Refuse(BacklogProblem problem, string message) => new(problem, message);

    private static void Need(BacklogPerson person, BacklogRole role, string what)
    {
        if (person.Role < role) throw Refuse(BacklogProblem.Forbidden, $"{what} needs the role of a {role.ToString().ToLowerInvariant()}; {person.Name} has the role of a {person.Role.ToString().ToLowerInvariant()}.");
    }

    private static string Show(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // ---- reading ----

    private BacklogItemRow Get(Guid id) => store.Item(id) ?? throw Refuse(BacklogProblem.NotFound, "There is no such work item.");

    /// <summary>An item by what people call it (<c>DATA-12</c>).</summary>
    public BacklogItemRow? ItemByKey(string key)
    {
        var dash = key.LastIndexOf('-');
        return dash > 0 && long.TryParse(key[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? store.Item(key[..dash].ToUpperInvariant(), number) : null;
    }

    public IReadOnlyList<BacklogProjectRow> Projects() => store.Projects();

    public BacklogProjectRow? Project(string id) => store.Project(id);

    public IReadOnlyList<BacklogItemRow> Items(BacklogItemQuery query) => store.Items(query);

    public IReadOnlyList<BacklogSprintRow> Sprints(string project) => store.Sprints(project);

    public IReadOnlyList<BacklogBoardRow> Boards(string project) => store.Boards(project);

    /// <summary>The item page: the item, where it can be moved to (by this person's role), and everything that belongs to it.</summary>
    public BacklogItemDetail Detail(BacklogPerson person, Guid id)
    {
        var item = Get(id);
        return new BacklogItemDetail(item, _workflow.Targets(item.State, person.Role), store.Comments(id), store.Links(id), store.Attachments(id), store.History(id),
            item.ItemType == "epic" ? store.Items(new BacklogItemQuery(Project: item.ProjectId, Epic: item.Id)) : []);
    }

    // ---- projects ----

    /// <summary>Makes a backlog and its first board (one column per state of the workflow). A lead's act.</summary>
    public BacklogProjectRow CreateProject(BacklogPerson person, string key, string name, string? description = null)
    {
        Need(person, BacklogRole.Lead, "Making a backlog");
        key = (key ?? "").Trim().ToUpperInvariant();
        if (!ProjectKey().IsMatch(key)) throw Refuse(BacklogProblem.Invalid, $"'{key}' is not a project key: two to ten capital letters and digits, starting with a letter (DATA).");
        if (string.IsNullOrWhiteSpace(name)) throw Refuse(BacklogProblem.Invalid, "A backlog needs a name.");
        if (!store.CreateProject(key, name.Trim(), string.IsNullOrWhiteSpace(description) ? null : description.Trim(), person.Id)) throw Refuse(BacklogProblem.Invalid, $"There is already a backlog with the key {key}.");
        store.SaveBoard(new BacklogBoardRow(key, "main", "Board", JsonSerializer.Serialize(_workflow.States.Select(s => new { name = s.Title, states = new[] { s.Id } }))));
        return store.Project(key)!;
    }

    // ---- items ----

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string CheckTitle(string? title)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) throw Refuse(BacklogProblem.Invalid, "An item needs a title.");
        if (title.Length > 250) throw Refuse(BacklogProblem.Invalid, "A title is at most 250 characters; the rest belongs in the description.");
        return title;
    }

    private static string CheckPriority(string? priority)
    {
        priority = (priority ?? "medium").Trim().ToLowerInvariant();
        if (!Priorities.Contains(priority)) throw Refuse(BacklogProblem.Invalid, $"'{priority}' is not a priority ({string.Join(", ", Priorities)}).");
        return priority;
    }

    private static decimal? CheckEstimate(decimal? estimate)
    {
        if (estimate is null) return null;
        if (estimate < 0 || estimate > 1000 || estimate % 0.5m != 0) throw Refuse(BacklogProblem.Invalid, "An estimate is in points, from 0 to 1000, in steps of a half.");
        return estimate;
    }

    private static string CheckAssignee(string? assignee)
    {
        assignee = assignee!.Trim();
        if (assignee.Length > 200) throw Refuse(BacklogProblem.Invalid, "An assignee is the person's id, at most 200 characters.");
        return assignee;
    }

    private static string CheckLabels(IReadOnlyList<string>? labels)
    {
        var clean = new List<string>();
        foreach (var label in labels ?? [])
        {
            var word = label.Trim().ToLowerInvariant();
            if (!Label().IsMatch(word)) throw Refuse(BacklogProblem.Invalid, $"'{label}' is not a label: lower case letters, digits and '-', 30 characters at most.");
            if (!clean.Contains(word)) clean.Add(word);
        }

        return JsonSerializer.Serialize(clean);
    }

    /// <summary>The labels of an item as a list.</summary>
    public static IReadOnlyList<string> LabelsOf(BacklogItemRow item) => JsonSerializer.Deserialize<string[]>(item.Labels) ?? [];

    private void CheckEpic(string project, string itemType, Guid? epic, Guid? self = null)
    {
        if (epic is null) return;
        if (itemType == "epic") throw Refuse(BacklogProblem.Invalid, "An epic cannot belong to an epic.");
        var parent = store.Item(epic.Value) ?? throw Refuse(BacklogProblem.NotFound, "There is no such epic.");
        if (parent.ProjectId != project) throw Refuse(BacklogProblem.Invalid, $"{parent.Key} is in the backlog {parent.ProjectId}, not {project}: an item belongs to an epic of its own backlog.");
        if (parent.ItemType != "epic") throw Refuse(BacklogProblem.Invalid, $"{parent.Key} is a {parent.ItemType}, not an epic.");
        if (self == parent.Id) throw Refuse(BacklogProblem.Invalid, "An item cannot be its own epic.");
    }

    /// <summary>Makes an item in a backlog: in the workflow's initial state, last in the order. A member's act.</summary>
    public BacklogItemRow CreateItem(BacklogPerson person, NewItem input)
    {
        Need(person, BacklogRole.Member, "Making an item");
        var project = store.Project(input.Project) ?? throw Refuse(BacklogProblem.NotFound, $"There is no backlog {input.Project}.");
        var type = (input.ItemType ?? "").Trim().ToLowerInvariant();
        if (!ItemTypes.Contains(type)) throw Refuse(BacklogProblem.Invalid, $"'{input.ItemType}' is not a kind of item ({string.Join(", ", ItemTypes)}).");
        var title = CheckTitle(input.Title);
        var priority = CheckPriority(input.Priority);
        var estimate = CheckEstimate(input.Estimate);
        var labels = CheckLabels(input.Labels);
        CheckEpic(project.Id, type, input.Epic);
        var assignee = Clean(input.Assignee) is { } a ? CheckAssignee(a) : null;
        var now = store.Now;
        var row = new BacklogItemRow(Guid.CreateVersion7(now), project.Id, 0, type, title, Clean(input.Description), _workflow.Initial, priority, estimate, assignee, person.Id, input.Epic, null,
            store.MaxSortKey(project.Id) + SortStep, labels, null, now, now, null, 1);
        return store.InsertItem(row, person.Actor()) ?? throw Refuse(BacklogProblem.NotFound, $"There is no backlog {input.Project}.");
    }

    /// <summary>Changes the fields that are set on an item. <paramref name="expectedVersion"/> is the version the person's screen showed; null means the one stored now. A member's act.</summary>
    public BacklogItemRow UpdateItem(BacklogPerson person, Guid id, ItemPatch patch, long? expectedVersion = null)
    {
        Need(person, BacklogRole.Member, "Changing an item");
        var item = Get(id);
        var changes = new List<BacklogChange>();
        var next = item;
        if (patch.Title.IsSet) { var title = CheckTitle(patch.Title.Value); if (title != item.Title) { next = next with { Title = title }; changes.Add(new("field", "title", item.Title, title)); } }
        if (patch.Description.IsSet) { var text = Clean(patch.Description.Value); if (text != item.Description) { next = next with { Description = text }; changes.Add(new("field", "description", item.Description, text)); } }
        if (patch.Priority.IsSet) { var priority = CheckPriority(patch.Priority.Value); if (priority != item.Priority) { next = next with { Priority = priority }; changes.Add(new("field", "priority", item.Priority, priority)); } }
        if (patch.Estimate.IsSet) { var estimate = CheckEstimate(patch.Estimate.Value); if (estimate != item.Estimate) { next = next with { Estimate = estimate }; changes.Add(new("field", "estimate", Show(item.Estimate), Show(estimate))); } }
        if (patch.Assignee.IsSet)
        {
            var assignee = Clean(patch.Assignee.Value) is { } a ? CheckAssignee(a) : null;
            if (assignee is null && _workflow.State(item.State)!.Requires.Contains("assignee")) throw Refuse(BacklogProblem.Invalid, $"{item.Key} is in '{item.State}', which needs an assignee: move it out of that state first.");
            if (assignee != item.Assignee) { next = next with { Assignee = assignee }; changes.Add(new("field", "assignee", item.Assignee, assignee)); }
        }

        if (patch.Epic.IsSet)
        {
            CheckEpic(item.ProjectId, item.ItemType, patch.Epic.Value, item.Id);
            if (patch.Epic.Value != item.EpicId) { next = next with { EpicId = patch.Epic.Value }; changes.Add(new("field", "epic", Show(item.EpicId), Show(patch.Epic.Value))); }
        }

        if (patch.Labels.IsSet)
        {
            var labels = CheckLabels(patch.Labels.Value);
            if (labels != item.Labels) { next = next with { Labels = labels }; changes.Add(new("field", "labels", item.Labels, labels)); }
        }

        if (changes.Count == 0) return item;
        return Save(person, next, expectedVersion ?? item.RowVersion, changes);
    }

    private BacklogItemRow Save(BacklogPerson person, BacklogItemRow next, long expectedVersion, IReadOnlyList<BacklogChange> changes)
    {
        if (!store.UpdateItem(next, expectedVersion, person.Actor(), changes))
            throw Refuse(BacklogProblem.Conflict, $"{next.Key} was changed by someone else while you were working on it; open it again to see their change.");
        return Get(next.Id);
    }

    /// <summary>
    /// Moves an item to a state of the workflow. The workflow must have that transition and the person's role must be enough for it; the state's required fields must be on the item (<paramref name="resolution"/> may be given here, and is what the
    /// transitions that end an item ask for); an epic is not finished while it has open items. Entering a state of the done category sets the item's closed time and leaving it clears that and the resolution.
    /// </summary>
    public BacklogItemRow Transition(BacklogPerson person, Guid id, string to, string? resolution = null, long? expectedVersion = null)
    {
        Need(person, BacklogRole.Member, "Moving an item");
        var item = Get(id);
        var target = _workflow.State(to) ?? throw Refuse(BacklogProblem.Invalid, $"'{to}' is not a state of the workflow ({string.Join(", ", _workflow.States.Select(s => s.Id))}).");
        if (item.State == to) throw Refuse(BacklogProblem.Invalid, $"{item.Key} is already in '{to}'.");
        var transition = _workflow.Find(item.State, to);
        if (transition is null)
        {
            var allowed = _workflow.Targets(item.State);
            throw Refuse(BacklogProblem.Invalid, $"The workflow has no move from '{item.State}' to '{to}'." + (allowed.Count == 0 ? $" Nothing leaves '{item.State}'." : $" From '{item.State}' an item goes to {string.Join(", ", allowed.Select(a => $"'{a}'"))}."));
        }

        if (person.Role < transition.Role) throw Refuse(BacklogProblem.Forbidden, $"Moving an item from '{item.State}' to '{to}' needs the role of a {transition.Role.ToString().ToLowerInvariant()}; {person.Name} has the role of a {person.Role.ToString().ToLowerInvariant()}.");

        var next = item with { State = to };
        var changes = new List<BacklogChange> { new("transition", "state", item.State, to) };
        if (target.Category == StateCategory.Done)
        {
            if (item.ItemType == "epic" && store.Items(new BacklogItemQuery(Project: item.ProjectId, Epic: item.Id)).Any(c => !_workflow.IsDone(c.State)))
                throw Refuse(BacklogProblem.Invalid, $"{item.Key} is an epic with open items; finish or move them first.");
            var text = Clean(resolution) ?? item.Resolution;
            next = next with { Resolution = text, ClosedAt = item.ClosedAt ?? store.Now };
            if (text != item.Resolution) changes.Add(new("field", "resolution", item.Resolution, text));
        }
        else if (_workflow.IsDone(item.State))
        {
            next = next with { Resolution = null, ClosedAt = null };
            if (item.Resolution is not null) changes.Add(new("field", "resolution", item.Resolution, null));
        }

        var missing = target.Requires.Where(field => !Has(next, field)).ToList();
        if (missing.Count > 0)
            throw Refuse(BacklogProblem.Invalid, $"{item.Key} cannot move to '{to}' without " + string.Join(" and ", missing.Select(Needed)) + ".");
        return Save(person, next, expectedVersion ?? item.RowVersion, changes);
    }

    private static string Needed(string field) => field switch
    {
        "assignee" => "an assignee",
        "estimate" => "an estimate",
        "resolution" => "a resolution",
        "description" => "a description",
        "sprint" => "a sprint",
        _ => field,
    };

    private static bool Has(BacklogItemRow item, string field) => field switch
    {
        "assignee" => item.Assignee is not null,
        "estimate" => item.Estimate is not null,
        "resolution" => item.Resolution is not null,
        "description" => item.Description is not null,
        "sprint" => item.SprintId is not null,
        _ => true,
    };

    /// <summary>
    /// Puts an item before another of the same backlog, or last when <paramref name="before"/> is null (the order of the backlog and of the board's columns). One row changes; when two items have no room between them the backlog is renumbered first.
    /// A member's act.
    /// </summary>
    public BacklogItemRow Rank(BacklogPerson person, Guid id, Guid? before)
    {
        Need(person, BacklogRole.Member, "Ordering the backlog");
        var item = Get(id);
        var ordered = store.Items(new BacklogItemQuery(Project: item.ProjectId, Limit: 1_000_000)).Where(i => i.Id != id).OrderBy(i => i.SortKey).ThenBy(i => i.Number).ToList();
        long key;
        if (before is null) key = (ordered.Count == 0 ? 0 : ordered[^1].SortKey) + SortStep;
        else
        {
            var at = ordered.FindIndex(i => i.Id == before);
            if (at < 0) throw Refuse(BacklogProblem.Invalid, "The item to put it before is not in the same backlog.");
            var low = at == 0 ? 0 : ordered[at - 1].SortKey;
            var high = ordered[at].SortKey;
            if (high - low < 2)
            {
                var order = new List<Guid>(ordered.Select(i => i.Id));
                order.Insert(at, id);
                store.SetSortKeys([.. order.Select((o, i) => (o, (i + 1L) * SortStep))]);
                store.RecordChanges(id, person.Actor(), [new BacklogChange("rank", NewValue: Show(before))]);
                return Get(id);
            }

            key = low + (high - low) / 2;
        }

        if (key == item.SortKey) return item;
        return Save(person, item with { SortKey = key }, item.RowVersion, [new BacklogChange("rank", NewValue: before is null ? "last" : Show(before))]);
    }

    /// <summary>Removes an item with everything that belongs to it, and the payloads of its attachments. A lead's act.</summary>
    public void DeleteItem(BacklogPerson person, Guid id)
    {
        Need(person, BacklogRole.Lead, "Deleting an item");
        var item = Get(id);
        if (item.ItemType == "epic" && store.Items(new BacklogItemQuery(Project: item.ProjectId, Epic: item.Id)).Count > 0)
            throw Refuse(BacklogProblem.Invalid, $"{item.Key} is an epic with items; they would lose their epic. Move them out first, or cancel the epic instead.");
        var payloadIds = store.DeleteItem(id) ?? throw Refuse(BacklogProblem.NotFound, "There is no such work item.");
        foreach (var payload in payloadIds) payloads?.Delete(payload);
    }

    // ---- sprints ----

    /// <summary>Plans a sprint. A member's act.</summary>
    public BacklogSprintRow CreateSprint(BacklogPerson person, string project, string name, string? goal = null, DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        Need(person, BacklogRole.Member, "Planning a sprint");
        if (store.Project(project) is null) throw Refuse(BacklogProblem.NotFound, $"There is no backlog {project}.");
        if (string.IsNullOrWhiteSpace(name)) throw Refuse(BacklogProblem.Invalid, "A sprint needs a name.");
        if (start is not null && end is not null && end <= start) throw Refuse(BacklogProblem.Invalid, "A sprint ends after it starts.");
        var sprint = new BacklogSprintRow(Guid.CreateVersion7(store.Now), project, name.Trim(), Clean(goal), "planned", start, end, null, null, 1);
        store.InsertSprint(sprint);
        return sprint;
    }

    private BacklogSprintRow GetSprint(Guid id) => store.Sprint(id) ?? throw Refuse(BacklogProblem.NotFound, "There is no such sprint.");

    /// <summary>Puts an item in a sprint of its own backlog (not a closed one), or back in the backlog when <paramref name="sprint"/> is null. An epic is planned by its items, not itself. A member's act.</summary>
    public BacklogItemRow SetSprint(BacklogPerson person, Guid id, Guid? sprint)
    {
        Need(person, BacklogRole.Member, "Planning a sprint");
        var item = Get(id);
        BacklogSprintRow? target = null;
        if (sprint is { } sid)
        {
            target = GetSprint(sid);
            if (target.ProjectId != item.ProjectId) throw Refuse(BacklogProblem.Invalid, $"The sprint '{target.Name}' belongs to the backlog {target.ProjectId}, not {item.ProjectId}.");
            if (target.State == "closed") throw Refuse(BacklogProblem.Invalid, $"The sprint '{target.Name}' is closed.");
            if (item.ItemType == "epic") throw Refuse(BacklogProblem.Invalid, "An epic is not planned in a sprint; its items are.");
        }

        if (item.SprintId == sprint) return item;
        var old = item.SprintId is { } o ? store.Sprint(o)?.Name : null;
        return Save(person, item with { SprintId = sprint }, item.RowVersion, [new BacklogChange("sprint", NewValue: target?.Name, OldValue: old)]);
    }

    /// <summary>Starts a planned sprint; a backlog has one active sprint at a time. A lead's act.</summary>
    public BacklogSprintRow StartSprint(BacklogPerson person, Guid id)
    {
        Need(person, BacklogRole.Lead, "Starting a sprint");
        var sprint = GetSprint(id);
        if (sprint.State != "planned") throw Refuse(BacklogProblem.Invalid, $"The sprint '{sprint.Name}' is {sprint.State}; only a planned sprint can be started.");
        if (store.Sprints(sprint.ProjectId).FirstOrDefault(s => s.State == "active") is { } active)
            throw Refuse(BacklogProblem.Invalid, $"The sprint '{active.Name}' is still active; close it before starting '{sprint.Name}'.");
        var started = sprint with { State = "active", StartedAt = store.Now };
        if (!store.UpdateSprint(started, sprint.RowVersion)) throw Refuse(BacklogProblem.Conflict, $"The sprint '{sprint.Name}' was changed by someone else; open it again.");
        return GetSprint(id);
    }

    /// <summary>
    /// Closes the active sprint. Items in a state of the done category stay in it (what it delivered); the others go to <paramref name="carryTo"/> (a planned sprint of the backlog) or back to the backlog when it is null, each with a history row. A lead's act.
    /// </summary>
    public (BacklogSprintRow Sprint, SprintSummary Summary) CloseSprint(BacklogPerson person, Guid id, Guid? carryTo = null)
    {
        Need(person, BacklogRole.Lead, "Closing a sprint");
        var sprint = GetSprint(id);
        if (sprint.State != "active") throw Refuse(BacklogProblem.Invalid, $"The sprint '{sprint.Name}' is {sprint.State}; only an active sprint can be closed.");
        BacklogSprintRow? next = null;
        if (carryTo is { } nid)
        {
            next = GetSprint(nid);
            if (next.ProjectId != sprint.ProjectId || next.State != "planned" || next.Id == sprint.Id) throw Refuse(BacklogProblem.Invalid, "Unfinished items can only go to another planned sprint of the same backlog.");
        }

        var items = store.Items(new BacklogItemQuery(Project: sprint.ProjectId, Sprint: sprint.Id, Limit: 1_000_000));
        int done = 0, moved = 0;
        decimal pointsDone = 0, pointsMoved = 0;
        foreach (var item in items)
        {
            if (_workflow.IsDone(item.State)) { done++; pointsDone += item.Estimate ?? 0; continue; }
            if (!store.UpdateItem(item with { SprintId = next?.Id }, item.RowVersion, person.Actor(), [new BacklogChange("sprint", OldValue: sprint.Name, NewValue: next?.Name)]))
                throw Refuse(BacklogProblem.Conflict, $"{item.Key} was changed by someone else while the sprint was closing; nothing more was moved, close it again.");
            moved++;
            pointsMoved += item.Estimate ?? 0;
        }

        if (!store.UpdateSprint(sprint with { State = "closed", ClosedAt = store.Now }, sprint.RowVersion)) throw Refuse(BacklogProblem.Conflict, $"The sprint '{sprint.Name}' was changed by someone else; open it again.");
        return (GetSprint(id), new SprintSummary(done, moved, pointsDone, pointsMoved, next?.Id));
    }

    // ---- comments ----

    /// <summary>Adds a comment. A member's act.</summary>
    public BacklogCommentRow AddComment(BacklogPerson person, Guid itemId, string body)
    {
        Need(person, BacklogRole.Member, "Commenting");
        Get(itemId);
        if (string.IsNullOrWhiteSpace(body)) throw Refuse(BacklogProblem.Invalid, "A comment has some text.");
        return store.AddComment(itemId, body.Trim(), person.Actor());
    }

    /// <summary>Changes a comment its own author wrote.</summary>
    public void EditComment(BacklogPerson person, Guid commentId, string body)
    {
        Need(person, BacklogRole.Member, "Commenting");
        if (string.IsNullOrWhiteSpace(body)) throw Refuse(BacklogProblem.Invalid, "A comment has some text.");
        var comment = store.Comment(commentId) ?? throw Refuse(BacklogProblem.NotFound, "There is no such comment.");
        if (comment.Author != person.Id) throw Refuse(BacklogProblem.Forbidden, "A comment can be changed by the person who wrote it.");
        store.EditComment(commentId, body.Trim(), person.Id);
    }

    // ---- links ----

    /// <summary>
    /// Links an item to another item (<c>relates</c>, <c>blocks</c>, <c>duplicates</c>: <paramref name="target"/> is the other item's id), to a suite object (<c>object</c>, with <paramref name="targetKind"/> one of <see cref="ObjectKinds"/> and the identity
    /// the lineage graph or the other side uses), to a workspace (<c>workspace</c>) or to a pull request (<c>pull-request</c>, <c>owner/repo#12</c>). Null when the item has that link already. A member's act.
    /// </summary>
    public BacklogLinkRow? AddLink(BacklogPerson person, Guid itemId, string linkKind, string? targetKind, string target, string? label = null)
    {
        Need(person, BacklogRole.Member, "Linking");
        var item = Get(itemId);
        if (!LinkKinds.Contains(linkKind)) throw Refuse(BacklogProblem.Invalid, $"'{linkKind}' is not a kind of link ({string.Join(", ", LinkKinds)}).");
        target = (target ?? "").Trim();
        if (target.Length == 0 || target.Length > 400) throw Refuse(BacklogProblem.Invalid, "A link names its target: an item's id, an object's identity, a workspace or a pull request.");
        string kind;
        if (linkKind is "relates" or "blocks" or "duplicates")
        {
            kind = "item";
            if (!Guid.TryParse(target, out var other)) throw Refuse(BacklogProblem.Invalid, "A link to an item names the item by its id.");
            if (other == itemId) throw Refuse(BacklogProblem.Invalid, "An item cannot be linked to itself.");
            var linked = store.Item(other) ?? throw Refuse(BacklogProblem.NotFound, "There is no such item to link to.");
            if (linked.ProjectId != item.ProjectId) throw Refuse(BacklogProblem.Invalid, $"{linked.Key} is in another backlog ({linked.ProjectId}); links between items stay inside one backlog.");
            target = other.ToString("D", CultureInfo.InvariantCulture);
        }
        else if (linkKind == "object")
        {
            kind = (targetKind ?? "").Trim().ToLowerInvariant();
            if (!ObjectKinds.Contains(kind)) throw Refuse(BacklogProblem.Invalid, $"'{targetKind}' is not a kind of object an item can name ({string.Join(", ", ObjectKinds)}).");
        }
        else kind = linkKind;

        return store.AddLink(itemId, linkKind, kind, target, Clean(label), person.Actor());
    }

    /// <summary>Removes a link. A member's act.</summary>
    public void RemoveLink(BacklogPerson person, Guid linkId)
    {
        Need(person, BacklogRole.Member, "Linking");
        if (!store.RemoveLink(linkId, person.Actor())) throw Refuse(BacklogProblem.NotFound, "There is no such link.");
    }

    // ---- attachments ----

    /// <summary>Attaches a file: the bytes go to the payload store (the file's name and type are kept on the row), at most <paramref name="maxBytes"/>. A member's act.</summary>
    public BacklogAttachmentRow AddAttachment(BacklogPerson person, Guid itemId, string fileName, string? mediaType, Stream content, long maxBytes = DefaultMaxAttachmentBytes)
    {
        Need(person, BacklogRole.Member, "Attaching a file");
        Get(itemId);
        if (payloads is null) throw Refuse(BacklogProblem.Invalid, "This backlog has no place to keep files: the payload store is not set up.");
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/').Trim());
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw Refuse(BacklogProblem.Invalid, "An attachment has a file name of at most 200 characters.");
        var type = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType.Trim();
        var id = Guid.CreateVersion7(store.Now);
        long size;
        Guid payloadId;
        try
        {
            using var writer = payloads.OpenWrite(new PayloadOptions(MediaType: type, OwnerModule: BacklogModule.Name, OwnerRow: id.ToString("D", CultureInfo.InvariantCulture), CreatedBy: person.Id, MaxBytes: maxBytes, Label: name));
            content.CopyTo(writer);
            writer.Complete();
            payloadId = writer.Id;
            size = writer.Length;
        }
        catch (PayloadTooLargeException)
        {
            throw Refuse(BacklogProblem.Invalid, $"{name} is larger than the {maxBytes / (1024 * 1024)} MiB an attachment may be.");
        }

        return store.AddAttachment(id, itemId, name, type, size, payloadId, person.Actor());
    }

    /// <summary>Opens an attachment's bytes (the reader is the caller's to dispose).</summary>
    public (BacklogAttachmentRow Attachment, PayloadReader Content) OpenAttachment(Guid attachmentId)
    {
        var row = store.Attachment(attachmentId) ?? throw Refuse(BacklogProblem.NotFound, "There is no such attachment.");
        if (payloads is null) throw Refuse(BacklogProblem.Invalid, "This backlog has no place to keep files: the payload store is not set up.");
        return (row, payloads.OpenRead(row.PayloadId));
    }

    /// <summary>Removes an attachment and its bytes. A member's act.</summary>
    public void RemoveAttachment(BacklogPerson person, Guid attachmentId)
    {
        Need(person, BacklogRole.Member, "Attaching a file");
        var row = store.RemoveAttachment(attachmentId, person.Actor()) ?? throw Refuse(BacklogProblem.NotFound, "There is no such attachment.");
        payloads?.Delete(row.PayloadId);
    }
}

internal static class BacklogPersonExtensions
{
    public static BacklogActor Actor(this BacklogPerson person) => new(person.Id, person.Name);
}
