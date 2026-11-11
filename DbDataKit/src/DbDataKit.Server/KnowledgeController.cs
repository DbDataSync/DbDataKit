using DbDataKit.Agents;
using DbDataKit.Knowledge;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record ItemBody(string Text);

public sealed record RepointBody(string Subject, string NewName);

public sealed record ChunkPreviewBody(string Project, string Id, string Text);

public sealed record ScopePreviewBody(IReadOnlyList<string>? InPlay, string? Audience, string? Agent, string? Channel);

public sealed record ContextBody(string Agent, IReadOnlyList<string>? InPlay);

public sealed record SubjectBody(string Type, string Name, string Status, string? NarrowingKind = null, string? NarrowingName = null);

/// <summary>
/// The console's knowledge screens (plan 27a KN28, KN36, KN37, KN38): the two projects it reads, an item to read and save (the strict reader decides), the owners' worklists, previews of how an item is split and of what would be given for what is in play, and the context an agent
/// would be given. An administrator edits; a signed-in person reads. There is no search here: the index is not built.
/// </summary>
[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController(ConsoleKnowledge knowledge, ConsoleAgents agents, ConsoleLineage lineage) : ControllerBase
{
    [HttpGet("overview")]
    [Authorize(Policies.Viewer)]
    public IActionResult Overview() => Ok(knowledge.Overview());

    [HttpPost("install")]
    [Authorize(Policies.Admin)]
    public IActionResult CreateInstall()
    {
        try { knowledge.CreateInstallProject(); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        return Ok(knowledge.Overview());
    }

    [HttpGet("items/{project}/{**id}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Item(string project, string id) =>
        knowledge.ReadItem(project, id) is { } text ? Ok(new { project, id, text }) : NotFound(new { error = $"There is no item '{id}' in the {project} project." });

    [HttpPut("items/{project}/{**id}")]
    [Authorize(Policies.Admin)]
    public IActionResult Save(string project, string id, [FromBody] ItemBody body)
    {
        var (saved, diagnostics, problem) = knowledge.SaveItem(project, id, body.Text);
        if (problem is not null) return BadRequest(new { error = problem });
        return saved ? Ok(new { saved, diagnostics }) : UnprocessableEntity(new { saved, diagnostics });
    }

    [HttpDelete("items/{project}/{**id}")]
    [Authorize(Policies.Admin)]
    public IActionResult Retire(string project, string id) => knowledge.Retire(project, id) ? NoContent() : NotFound();

    [HttpPost("repoint/{project}/{**id}")]
    [Authorize(Policies.Admin)]
    public IActionResult Repoint(string project, string id, [FromBody] RepointBody body)
    {
        var (done, problem) = knowledge.Repoint(project, id, body.Subject, body.NewName);
        return done ? Ok(knowledge.Overview()) : Conflict(new { error = problem });
    }

    [HttpPut("subjects")]
    [Authorize(Policies.Admin)]
    public IActionResult Subject([FromBody] SubjectBody body)
    {
        if (!KnowledgeItemLoader.SubjectTypes.ContainsKey(body.Type) || string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { error = $"A subject has a type ({string.Join(", ", KnowledgeItemLoader.SubjectTypes.Keys)}) and a name." });
        if (body.Status is not ("exists" or "gone")) return BadRequest(new { error = "A subject exists or is gone." });
        knowledge.SetSubject(body.Type, body.Name, body.Status, body.NarrowingKind ?? "", body.NarrowingName ?? "");
        return Ok(knowledge.Overview());
    }

    /// <summary>Reads what exists to be about from the joined lineage graph of the servers, as the person who asks (plan 27a KN02): what it shows exists, what it showed before and no longer does is gone (only when every server answered).</summary>
    [HttpPost("subjects/refresh")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> RefreshSubjects(CancellationToken cancellation)
    {
        var joined = await lineage.JoinedAsync(refresh: true, cancellation);
        var problems = joined.Servers.Where(s => s.Error is not null).Select(s => $"{s.Server}: {s.Error}").Concat(joined.JoinError is { } j ? [j] : []).ToList();
        var result = knowledge.RefreshFromLineage(joined.Graph, complete: problems.Count == 0 && joined.Servers.Count > 0);   // nothing answered is not "everything was dropped"
        return Ok(new { result.Found, result.NowGone, result.Complete, result.Total, problems, overview = knowledge.Overview() });
    }

    /// <summary>How the item as typed would be split, what changed against the saved version, and which agents it would reach and whether it would push one over its budget (KN36, KN38).</summary>
    [HttpPost("preview/chunks")]
    [Authorize(Policies.Viewer)]
    public IActionResult Chunks([FromBody] ChunkPreviewBody body)
    {
        var preview = knowledge.PreviewChunks(body.Project, body.Id, body.Text);
        var item = KnowledgeItemLoader.Load(body.Text, body.Id, body.Id).Item;
        var reach = new List<object>();
        if (item is not null)
        {
            var tokens = EstimateCounter.Instance.Count($"### {item.Title}\n{item.Body.Trim()}");
            foreach (var listing in agents.List())
            {
                if (ConsoleKnowledge.AgentsReached(item, [(listing.Name, listing.Audience == "back-end")]).Count == 0) continue;
                var space = agents.Read(listing.Name)?.Space;
                var always = item.Role != KnowledgeRole.Reference;
                var over = space is not null && always && space.TotalTokens + tokens > space.BudgetTokens;
                reach.Add(new { agent = listing.Name, adds = always ? tokens : 0, used = space?.TotalTokens, budget = space?.BudgetTokens, pushesOver = over });
            }
        }
        return Ok(new { preview, reaches = reach });
    }

    [HttpPost("preview/scope")]
    [Authorize(Policies.Viewer)]
    public IActionResult Scope([FromBody] ScopePreviewBody body) =>
        Ok(new { preview = knowledge.PreviewScope(body.InPlay ?? [], body.Audience, body.Agent, body.Channel), scopes = KnowledgeInsight.ScopeNames });

    [HttpPost("context")]
    [Authorize(Policies.Viewer)]
    public IActionResult Context([FromBody] ContextBody body) =>
        agents.ContextFor(body.Agent, body.InPlay ?? []) is { } report ? Ok(report) : NotFound(new { error = $"There is no agent named '{body.Agent}' that reads." });
}
