using System.Text.Json;
using DbDataKit.Backlog;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record NewProjectBody(string Key, string Name, string? Description);

public sealed record NewItemBody(string Type, string Title, string? Description, string? Priority, decimal? Estimate, string? Assignee, Guid? Epic, string[]? Labels);

public sealed record TransitionBody(string To, string? Resolution, long? Version);

public sealed record RankBody(Guid? Before);

public sealed record SprintOfBody(Guid? Sprint);

public sealed record CommentBody(string Body);

public sealed record LinkBody(string Kind, string? TargetKind, string Target, string? Label);

public sealed record NewSprintBody(string Name, string? Goal, DateTimeOffset? Start, DateTimeOffset? End);

public sealed record CloseSprintBody(Guid? CarryTo);

public sealed record ImportBody(string Text);

/// <summary>
/// The console's backlog (plan 27a BK03 and BK04; the API of BK02 is the serving layer's and is not built, so the console hosts the same library for now): backlogs, items, the board's moves, sprints, comments, links, attachments and the history. Everything is done as the signed-in
/// person (an administrator works as a lead, a viewer reads); the rules are <c>DbDataKit.Backlog</c>'s and a refusal is an answer with the reason in words (404 not found, 403 role, 422 rule, 409 someone changed it first).
/// </summary>
[ApiController]
[Route("api/backlog")]
public sealed class BacklogController(ConsoleBacklog backlog) : ControllerBase
{
    private BacklogService Service => backlog.Service;

    private BacklogPerson Person => ConsoleBacklog.PersonOf(User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown", User.Identity?.Name, User.IsInRole(nameof(UserRole.Admin)));

    private IActionResult Run(Func<IActionResult> act)
    {
        try { return act(); }
        catch (BacklogException ex)
        {
            return ex.Problem switch
            {
                BacklogProblem.NotFound => NotFound(new { error = ex.Message }),
                BacklogProblem.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }),
                BacklogProblem.Conflict => Conflict(new { error = ex.Message }),
                _ => UnprocessableEntity(new { error = ex.Message }),
            };
        }
    }

    private static object View(BacklogItemRow i, IReadOnlyDictionary<Guid, BacklogItemRow>? by = null) => new
    {
        id = i.Id, key = i.Key, project = i.ProjectId, type = i.ItemType, title = i.Title, description = i.Description, state = i.State, priority = i.Priority, estimate = i.Estimate, assignee = i.Assignee, reporter = i.Reporter,
        epic = i.EpicId, epicKey = i.EpicId is { } e && by is not null && by.TryGetValue(e, out var parent) ? parent.Key : null, sprint = i.SprintId, labels = BacklogService.LabelsOf(i), resolution = i.Resolution,
        createdAt = i.CreatedAt, updatedAt = i.UpdatedAt, closedAt = i.ClosedAt, version = i.RowVersion, sortKey = i.SortKey,
    };

    private static object SprintView(BacklogSprintRow s) => new { id = s.Id, project = s.ProjectId, name = s.Name, goal = s.Goal, state = s.State, plannedStart = s.PlannedStartAt, plannedEnd = s.PlannedEndAt, startedAt = s.StartedAt, closedAt = s.ClosedAt, version = s.RowVersion };

    /// <summary>The workflow the backlogs use and what the signed-in person may do: the states in order with their category, the moves, and the role.</summary>
    [HttpGet("workflow")]
    [Authorize(Policies.Viewer)]
    public IActionResult Workflow() => Ok(new
    {
        initial = Service.Workflow.Initial,
        role = Person.Role.ToString().ToLowerInvariant(),
        states = Service.Workflow.States.Select(s => new { id = s.Id, title = s.Title, category = s.Category.ToString().ToLowerInvariant(), requires = s.Requires }),
        transitions = Service.Workflow.Transitions.Select(t => new { from = t.From, to = t.To, role = t.Role.ToString().ToLowerInvariant() }),
        itemTypes = BacklogService.ItemTypes, priorities = BacklogService.Priorities, linkKinds = BacklogService.LinkKinds, objectKinds = BacklogService.ObjectKinds,
    });

    [HttpGet("projects")]
    [Authorize(Policies.Viewer)]
    public IActionResult Projects() => Ok(Service.Projects().Select(p => new { id = p.Id, name = p.Name, description = p.Description, createdAt = p.CreatedAt }));

    [HttpPost("projects")]
    [Authorize(Policies.Viewer)]
    public IActionResult CreateProject([FromBody] NewProjectBody body) => Run(() =>
    {
        var project = Service.CreateProject(Person, body.Key, body.Name, body.Description);
        return Ok(new { id = project.Id, name = project.Name, description = project.Description, createdAt = project.CreatedAt });
    });

    /// <summary>The items of a backlog in backlog order, narrowed by state, sprint (<c>none</c> for the backlog proper), epic, assignee or words.</summary>
    [HttpGet("projects/{project}/items")]
    [Authorize(Policies.Viewer)]
    public IActionResult Items(string project, [FromQuery] string? state, [FromQuery] string? sprint, [FromQuery] Guid? epic, [FromQuery] string? assignee, [FromQuery] string? type, [FromQuery] string? text) => Run(() =>
    {
        if (Service.Project(project) is null) throw new BacklogException(BacklogProblem.NotFound, $"There is no backlog {project}.");
        var all = Service.Items(new BacklogItemQuery(Project: project, Limit: 100_000));
        var by = all.ToDictionary(i => i.Id);
        var states = string.IsNullOrWhiteSpace(state) ? null : state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = Service.Items(new BacklogItemQuery(project, states, Guid.TryParse(sprint, out var s) ? s : null, sprint == "none", epic, assignee, type, text, 100_000));
        return Ok(items.Select(i => View(i, by)));
    });

    [HttpPost("projects/{project}/items")]
    [Authorize(Policies.Viewer)]
    public IActionResult CreateItem(string project, [FromBody] NewItemBody body) => Run(() =>
        Ok(View(Service.CreateItem(Person, new NewItem(project, body.Type, body.Title, body.Description, body.Priority, body.Estimate, body.Assignee, body.Epic, body.Labels)))));

    [HttpGet("projects/{project}/sprints")]
    [Authorize(Policies.Viewer)]
    public IActionResult Sprints(string project) => Ok(Service.Sprints(project).Select(SprintView));

    [HttpPost("projects/{project}/sprints")]
    [Authorize(Policies.Viewer)]
    public IActionResult CreateSprint(string project, [FromBody] NewSprintBody body) => Run(() => Ok(SprintView(Service.CreateSprint(Person, project, body.Name, body.Goal, body.Start, body.End))));

    [HttpGet("projects/{project}/boards")]
    [Authorize(Policies.Viewer)]
    public IActionResult Boards(string project) => Ok(Service.Boards(project).Select(b => new { id = b.Id, name = b.Name, columns = JsonDocument.Parse(b.ColumnsDoc).RootElement }));

    /// <summary>Items to import, as a <c>work-item/1</c> file; every problem of the file is returned with its line.</summary>
    [HttpPost("projects/{project}/import")]
    [Authorize(Policies.Viewer)]
    public IActionResult Import(string project, [FromBody] ImportBody body) => Run(() =>
    {
        var read = WorkItemLoader.Load(body.Text, "work-items.yml");
        if (!read.Ok) return UnprocessableEntity(new { error = "The file is not a valid work-item/1 file.", diagnostics = read.Diagnostics.Select(d => new { code = d.Code, line = d.Line, column = d.Column, message = d.Message, fix = d.Fix }) });
        if (read.File!.Project != project) return UnprocessableEntity(new { error = $"The file is for the backlog {read.File.Project}, not {project}." });
        var made = BacklogImport.Import(Service, Person, read.File);
        return Ok(new { made = made.Count, items = made.Select(i => new { id = i.Id, key = i.Key, title = i.Title }) });
    });

    [HttpGet("projects/{project}/export")]
    [Authorize(Policies.Viewer)]
    public IActionResult Export(string project) => Service.Project(project) is null ? NotFound(new { error = $"There is no backlog {project}." }) : Content(BacklogImport.Export(Service, project), "text/yaml");

    // ---- one item ----

    /// <summary>The item page: the item, where this person can move it, its comments, links, attachments and history, and the items of an epic.</summary>
    [HttpGet("items/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Item(Guid id) => Run(() =>
    {
        var d = Service.Detail(Person, id);
        var by = Service.Items(new BacklogItemQuery(Project: d.Item.ProjectId, Limit: 100_000)).ToDictionary(i => i.Id);
        var itemKeys = d.Links.Where(l => l.TargetKind == "item" && Guid.TryParse(l.TargetRef, out _)).Select(l => Guid.Parse(l.TargetRef)).Distinct().ToDictionary(g => g, g => by.TryGetValue(g, out var other) ? other.Key : null);
        return Ok(new
        {
            item = View(d.Item, by),
            targets = d.Targets,
            comments = d.Comments.Select(c => new { id = c.Id, author = c.Author, authorName = c.AuthorName, body = c.Body, createdAt = c.CreatedAt, editedAt = c.EditedAt }),
            links = d.Links.Select(l => new { id = l.Id, kind = l.LinkKind, targetKind = l.TargetKind, target = l.TargetRef, targetKey = l.TargetKind == "item" && Guid.TryParse(l.TargetRef, out var g) ? itemKeys.GetValueOrDefault(g) : null, label = l.Label, state = l.State, createdBy = l.CreatedBy, createdAt = l.CreatedAt }),
            attachments = d.Attachments.Select(a => new { id = a.Id, fileName = a.FileName, mediaType = a.MediaType, size = a.Size, addedBy = a.AddedBy, addedAt = a.AddedAt }),
            history = d.History.Select(h => new { seq = h.Seq, actor = h.Actor, actorName = h.ActorName, kind = h.Kind, field = h.Field, oldValue = h.OldValue, newValue = h.NewValue, at = h.OccurredAt }),
            children = d.Children.Select(c => View(c, by)),
        });
    });

    private static Opt<string?> Str(JsonElement body, string name) => body.TryGetProperty(name, out var v) ? Opt.Of<string?>(v.ValueKind == JsonValueKind.Null ? null : v.GetString()) : default;

    /// <summary>Changes the fields the body names (title, description, priority, estimate, assignee, epic, labels); a field set to null is cleared. <c>version</c> is the version the screen showed.</summary>
    [HttpPatch("items/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Change(Guid id, [FromBody] JsonElement body) => Run(() =>
    {
        var patch = new ItemPatch
        {
            Title = Str(body, "title"),
            Description = Str(body, "description"),
            Priority = Str(body, "priority"),
            Assignee = Str(body, "assignee"),
            Estimate = body.TryGetProperty("estimate", out var e) ? Opt.Of(e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()) : default,
            Epic = body.TryGetProperty("epic", out var ep) ? Opt.Of(ep.ValueKind == JsonValueKind.Null ? (Guid?)null : ep.GetGuid()) : default,
            Labels = body.TryGetProperty("labels", out var l) ? Opt.Of<IReadOnlyList<string>?>(l.ValueKind == JsonValueKind.Null ? null : l.EnumerateArray().Select(x => x.GetString() ?? "").ToList()) : default,
        };
        var item = Service.UpdateItem(Person, id, patch, body.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null);
        return Ok(View(item));
    });

    [HttpPost("items/{id:guid}/transition")]
    [Authorize(Policies.Viewer)]
    public IActionResult Transition(Guid id, [FromBody] TransitionBody body) => Run(() => Ok(View(Service.Transition(Person, id, body.To, body.Resolution, body.Version))));

    /// <summary>Puts the item before another (<c>before</c>), or last when there is none.</summary>
    [HttpPost("items/{id:guid}/rank")]
    [Authorize(Policies.Viewer)]
    public IActionResult Rank(Guid id, [FromBody] RankBody body) => Run(() => Ok(View(Service.Rank(Person, id, body.Before))));

    [HttpPut("items/{id:guid}/sprint")]
    [Authorize(Policies.Viewer)]
    public IActionResult SetSprint(Guid id, [FromBody] SprintOfBody body) => Run(() => Ok(View(Service.SetSprint(Person, id, body.Sprint))));

    [HttpDelete("items/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Delete(Guid id) => Run(() =>
    {
        Service.DeleteItem(Person, id);
        return NoContent();
    });

    [HttpPost("items/{id:guid}/comments")]
    [Authorize(Policies.Viewer)]
    public IActionResult Comment(Guid id, [FromBody] CommentBody body) => Run(() => Ok(new { id = Service.AddComment(Person, id, body.Body).Id }));

    [HttpPut("comments/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult EditComment(Guid id, [FromBody] CommentBody body) => Run(() =>
    {
        Service.EditComment(Person, id, body.Body);
        return NoContent();
    });

    [HttpPost("items/{id:guid}/links")]
    [Authorize(Policies.Viewer)]
    public IActionResult Link(Guid id, [FromBody] LinkBody body) => Run(() =>
        Service.AddLink(Person, id, body.Kind, body.TargetKind, body.Target, body.Label) is { } link ? Ok(new { id = link.Id }) : Conflict(new { error = "The item has that link already." }));

    [HttpDelete("links/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Unlink(Guid id) => Run(() =>
    {
        Service.RemoveLink(Person, id);
        return NoContent();
    });

    [HttpPost("items/{id:guid}/attachments")]
    [Authorize(Policies.Viewer)]
    public IActionResult Attach(Guid id, IFormFile file) => Run(() =>
    {
        using var stream = file.OpenReadStream();
        var row = Service.AddAttachment(Person, id, file.FileName, file.ContentType, stream);
        return Ok(new { id = row.Id, fileName = row.FileName, size = row.Size });
    });

    [HttpGet("attachments/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Download(Guid id)
    {
        try
        {
            var (row, content) = Service.OpenAttachment(id);
            return File(content, row.MediaType, row.FileName);
        }
        catch (BacklogException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("attachments/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult RemoveAttachment(Guid id) => Run(() =>
    {
        Service.RemoveAttachment(Person, id);
        return NoContent();
    });

    // ---- sprints ----

    [HttpPost("sprints/{id:guid}/start")]
    [Authorize(Policies.Viewer)]
    public IActionResult Start(Guid id) => Run(() => Ok(SprintView(Service.StartSprint(Person, id))));

    [HttpPost("sprints/{id:guid}/close")]
    [Authorize(Policies.Viewer)]
    public IActionResult Close(Guid id, [FromBody] CloseSprintBody body) => Run(() =>
    {
        var (sprint, summary) = Service.CloseSprint(Person, id, body.CarryTo);
        return Ok(new { sprint = SprintView(sprint), summary = new { done = summary.Done, moved = summary.Moved, pointsDone = summary.PointsDone, pointsMoved = summary.PointsMoved, movedTo = summary.MovedTo } });
    });
}
