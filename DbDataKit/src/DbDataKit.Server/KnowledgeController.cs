using DbDataKit.Agents;
using DbDataKit.Knowledge;
using DbDataKit.State;
using System.Text.Json.Nodes;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record ItemBody(string Text);

public sealed record RepointBody(string Subject, string NewName);

public sealed record ChunkPreviewBody(string Project, string Id, string Text);

public sealed record ScopePreviewBody(IReadOnlyList<string>? InPlay, string? Audience, string? Agent, string? Channel);

public sealed record ContextBody(string Agent, IReadOnlyList<string>? InPlay);

public sealed record SearchBody(string Question, string? Mode = null, string? Audience = null);

public sealed record EmbeddingBody(string? Provider, string? Model);

public sealed record SubjectBody(string Type, string Name, string Status, string? NarrowingKind = null, string? NarrowingName = null);

/// <summary>
/// The console's knowledge screens (plan 27a KN28, KN36, KN37, KN38): the two projects it reads, an item to read and save (the strict reader decides), the owners' worklists, previews of how an item is split and of what would be given for what is in play, and the context an agent
/// would be given. An administrator edits; a signed-in person reads. The index over the two projects (plan 27a KN07, KN08, KN28) is built on request: words on every engine, meaning where the state engine has the vector feature and an embedding model is bound.
/// </summary>
[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController(ConsoleKnowledge knowledge, ConsoleAgents agents, ConsoleLineage lineage, ConsoleKnowledgeIndex index, ConsoleModels models) : ControllerBase
{
    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    /// <summary>The overview of the files with the index's state in it (the first line of the screen).</summary>
    private JsonObject Overview()
    {
        var overview = knowledge.Overview();
        var view = index.View();
        overview["index"] = new JsonObject { ["built"] = view.Opened, ["message"] = view.Opened ? $"The index is built: {view.Message}" : overview["index"]!["message"]!.GetValue<string>() };
        return overview;
    }

    [HttpGet("overview")]
    [Authorize(Policies.Viewer)]
    public IActionResult GetOverview() => Ok(Overview());

    [HttpPost("install")]
    [Authorize(Policies.Admin)]
    public IActionResult CreateInstall()
    {
        try { knowledge.CreateInstallProject(); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        return Ok(Overview());
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
        if (saved) index.Refresh();      // the index follows in the background: saving never waits for it or for a model
        return saved ? Ok(new { saved, diagnostics }) : UnprocessableEntity(new { saved, diagnostics });
    }

    [HttpDelete("items/{project}/{**id}")]
    [Authorize(Policies.Admin)]
    public IActionResult Retire(string project, string id)
    {
        if (!knowledge.Retire(project, id)) return NotFound();
        index.Refresh();
        return NoContent();
    }

    [HttpPost("repoint/{project}/{**id}")]
    [Authorize(Policies.Admin)]
    public IActionResult Repoint(string project, string id, [FromBody] RepointBody body)
    {
        var (done, problem) = knowledge.Repoint(project, id, body.Subject, body.NewName);
        if (done) index.Refresh();
        return done ? Ok(Overview()) : Conflict(new { error = problem });
    }

    [HttpPut("subjects")]
    [Authorize(Policies.Admin)]
    public IActionResult Subject([FromBody] SubjectBody body)
    {
        if (!KnowledgeItemLoader.SubjectTypes.ContainsKey(body.Type) || string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { error = $"A subject has a type ({string.Join(", ", KnowledgeItemLoader.SubjectTypes.Keys)}) and a name." });
        if (body.Status is not ("exists" or "gone")) return BadRequest(new { error = "A subject exists or is gone." });
        knowledge.SetSubject(body.Type, body.Name, body.Status, body.NarrowingKind ?? "", body.NarrowingName ?? "");
        return Ok(Overview());
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

    /// <summary>
    /// What a publish would check, shown before it (plan 27a KN10, KN17): per project the strict reader's diagnostics with file and line, every subject against what exists, and for each agent the project reaches what it adds to what the agent is always given against its budget.
    /// </summary>
    [HttpGet("check")]
    [Authorize(Policies.Viewer)]
    public IActionResult Check() =>
        Ok(new
        {
            projects = knowledge.Check(agents.BudgetInputs()).Select(p => new
            {
                name = p.Name,
                ok = p.Report.Ok,
                items = p.Report.Project.Items.Count,
                diagnostics = p.Report.Diagnostics.Select(DiagnosticView.Of),
                reaches = p.Report.Reaches,
            }),
        });

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

    // ---- the index (KN07, KN08, KN28) ----

    /// <summary>The state of the index: whether it is built, its words and vectors, the embedding model bound, the progress, the failures and the last run.</summary>
    [HttpGet("index")]
    [Authorize(Policies.Viewer)]
    public IActionResult IndexState() => Ok(index.View());

    /// <summary>Builds the index over the files (words, now) and starts the background step that gives chunks their vectors. Safe to press again: what did not change is not written or embedded again.</summary>
    [HttpPost("index/build")]
    [Authorize(Policies.Admin)]
    public IActionResult Build()
    {
        IndexSyncResult sync;
        try { sync = index.Sync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException or IOException) { return Conflict(new { error = $"The index could not be built: {ex.Message}" }); }
        index.Refresh();
        return Ok(new { sync, view = index.View() });
    }

    /// <summary>Tries again the chunks the model refused.</summary>
    [HttpPost("index/retry")]
    [Authorize(Policies.Admin)]
    public IActionResult Retry()
    {
        index.RetryFailures();
        return Ok(index.View());
    }

    /// <summary>Throws the vectors away and makes them again from the chunks (words stay).</summary>
    [HttpPost("index/rebuild")]
    [Authorize(Policies.Admin)]
    public IActionResult Rebuild()
    {
        index.Rebuild();
        return Ok(index.View());
    }

    /// <summary>Binds the embedding model of the index (the <c>embeddings</c> slot): one model per index. Binding another builds a second index beside the first, which keeps answering until the new one is complete. A null provider unbinds.</summary>
    [HttpPut("index/embedding")]
    [Authorize(Policies.Admin)]
    public IActionResult Embedding([FromBody] EmbeddingBody body)
    {
        var (saved, diagnostics) = models.BindEmbedding(string.IsNullOrWhiteSpace(body.Provider) ? null : body.Provider, string.IsNullOrWhiteSpace(body.Model) ? null : body.Model);
        if (!saved) return UnprocessableEntity(new { saved, diagnostics });
        index.Refresh();
        return Ok(index.View());
    }

    /// <summary>Searches the index by words, by vectors or by both, as the person who asks (an administrator reads every subject; anyone else only what is about the install itself).</summary>
    [HttpPost("search")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Search([FromBody] SearchBody body, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(body.Question)) return BadRequest(new { error = "Ask a question: a few words about what you want to know." });
        var mode = body.Mode?.ToLowerInvariant() switch { "vectors" => KnowledgeSearchMode.Vectors, "words" => KnowledgeSearchMode.Words, _ => KnowledgeSearchMode.Both };
        var result = await index.SearchAsync(body.Question, mode, string.IsNullOrWhiteSpace(body.Audience) ? "back-end" : body.Audience, IsAdmin, cancellation);
        return Ok(new { hits = result.Hits, used = result.Used.ToString().ToLowerInvariant(), note = result.Note });
    }

    [HttpPost("context")]
    [Authorize(Policies.Viewer)]
    public IActionResult Context([FromBody] ContextBody body) =>
        agents.ContextFor(body.Agent, body.InPlay ?? []) is { } report ? Ok(report) : NotFound(new { error = $"There is no agent named '{body.Agent}' that reads." });
}
