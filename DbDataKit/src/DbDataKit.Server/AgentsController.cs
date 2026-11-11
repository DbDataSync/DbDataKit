using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbDataKit.Agents;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record YamlBody(string Yaml, bool Confirmed = false);

public sealed record StartConversationBody(string Agent, string? Title = null, string? Server = null, string? Project = null, string? Workspace = null);

public sealed record InstallBuildHelperBody(string Server, string Project);

public sealed record SendMessageBody(string Text);

public sealed record KeyBody(string Key);

public sealed record TestModelBody(string Model);

public sealed record TokenBody(string? Name, int? Days = null);

public sealed record AnswerBody(bool Approve, string? Note = null);

public sealed record OutsideAskBody(string Agent, string AgentVersion, string Tool, string Preview, string? Arguments, string Traits);

public sealed record OutsideEvent(string Kind, string Agent, string AgentVersion, string? Tool, string? Arguments, string? Detail);

public sealed record OutsideEventsBody(IReadOnlyList<OutsideEvent> Events);

/// <summary>
/// The console's agents (plan 27a AG14 to AG17, AG29 to AG32): the definitions and what each tool will ask, the model's space and the skills; the install policy; conversations with an agent; the approval inbox; the record of what agents did; and the console's half of an outside
/// agent's approvals. <b>An administrator talks to an agent</b> (its tools run with the console's authority, so only reads are offered); a person sees their own conversations, steps and record, an administrator all. A step is answered only by the person it is for.
/// </summary>
[ApiController]
[Route("api/agents")]
public sealed class AgentsController(ConsoleAgents agents, ConsoleModels models, IUserAccounts users, ConsoleInstances instances, IBuildRelay? relay = null) : ControllerBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The result with the names of the people in it: a row names a person by the id the sign-in gave, and a screen shows the name (<c>personName</c> beside <c>person</c>, <c>answeredByName</c> beside <c>answeredBy</c>).</summary>
    private IActionResult Named(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, Web);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string NameOf(string id) => names.TryGetValue(id, out var n) ? n : names[id] = users.Get(id)?.DisplayName ?? id;
        void Walk(JsonNode? n)
        {
            if (n is JsonArray array) { foreach (var item in array) Walk(item); return; }
            if (n is not JsonObject o) return;
            foreach (var key in o.Select(p => p.Key).ToList())
            {
                if (key == "person" && o[key] is JsonValue pv && pv.TryGetValue<string>(out var person) && !o.ContainsKey("personName")) o["personName"] = NameOf(person);
                else if (key == "answeredBy" && o[key] is JsonValue av && av.TryGetValue<string>(out var by)) o["answeredByName"] = NameOf(by);
                else Walk(o[key]);
            }
        }
        Walk(node);
        return Content(node?.ToJsonString() ?? "null", "application/json");
    }

    private string PersonId => User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";

    private string PersonName => User.Identity?.Name ?? PersonId;

    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    // ---- definitions (AG16 in the console's folder, AG30, AG31, AG32) ----

    [HttpGet("definitions")]
    [Authorize(Policies.Viewer)]
    public IActionResult Definitions() => Ok(agents.List());

    [HttpGet("definitions/{name}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Definition(string name) => agents.Read(name) is { } detail ? Ok(detail) : NotFound(new { error = $"There is no agent named '{name}'." });

    [HttpPost("definitions/{name}/check")]
    [Authorize(Policies.Admin)]
    public IActionResult Check(string name, [FromBody] YamlBody body) => AgentDefinitionLoader.IsName(name) ? Ok(agents.Describe(name, body.Yaml)) : BadRequest(new { error = "An agent's name is lower case letters, digits and hyphens." });

    [HttpPut("definitions/{name}")]
    [Authorize(Policies.Admin)]
    public IActionResult Save(string name, [FromBody] YamlBody body)
    {
        if (!AgentDefinitionLoader.IsName(name)) return BadRequest(new { error = "An agent's name is lower case letters, digits and hyphens." });
        var (saved, detail) = agents.Save(name, body.Yaml);
        if (saved) agents.Store.RecordAudit(null, null, "agent-saved", PersonId, name, "1", "console", null, null, "the definition was saved");
        return saved ? Ok(detail) : UnprocessableEntity(detail);
    }

    // ---- the install policy (AG17) ----

    [HttpGet("policy")]
    [Authorize(Policies.Viewer)]
    public IActionResult Policy()
    {
        agents.Policy(out var diagnostics);
        return Ok(new { yaml = agents.PolicyText(), diagnostics });
    }

    [HttpPost("policy/preview")]
    [Authorize(Policies.Admin)]
    public IActionResult PreviewPolicy([FromBody] YamlBody body)
    {
        var (_, diagnostics, changes) = agents.PreviewPolicy(body.Yaml);
        return Ok(new { diagnostics, changes, loosens = changes.Any(c => c.Loosens) });
    }

    [HttpPut("policy")]
    [Authorize(Policies.Admin)]
    public IActionResult SavePolicy([FromBody] YamlBody body)
    {
        var (saved, diagnostics, changes, needsConfirmation) = agents.SavePolicy(body.Yaml, body.Confirmed, PersonName);
        if (needsConfirmation) return Conflict(new { error = "This change makes the install less strict. Confirm it to save it; who confirmed and what changed is recorded.", needsConfirmation = true, diagnostics, changes });
        return saved ? Ok(new { saved, diagnostics, changes }) : UnprocessableEntity(new { saved, diagnostics, changes });
    }

    // ---- conversations (AG15) ----

    [HttpGet("conversations")]
    [Authorize(Policies.Viewer)]
    public IActionResult Conversations([FromQuery] bool all = false) => Named(agents.Store.Conversations(person: all && IsAdmin ? null : PersonId));

    [HttpPost("conversations")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Start([FromBody] StartConversationBody body, CancellationToken cancellation)
    {
        var target = body.Server is { Length: > 0 } && body.Project is { Length: > 0 } ? new BuildTarget(body.Server, body.Project, body.Workspace is { Length: > 0 } w ? w : null) : null;
        var (conversation, problem) = await agents.StartAsync(body.Agent, PersonId, PersonName, body.Title, target, cancellation);
        return conversation is null ? Conflict(new { error = problem }) : Ok(conversation);
    }

    /// <summary>The Build servers this console manages, and for one of them the projects and the workspaces of the person who asks (the server is asked as them): what a conversation with a Build agent is started on (plan 27a AG14, AG15).</summary>
    [HttpGet("build-targets")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> BuildTargets([FromQuery] string? server, [FromQuery] string? project, CancellationToken cancellation)
    {
        var servers = instances.Registry.All().Select(i => new { id = i.InstanceId, name = i.Name }).ToList();
        if (server is null || relay is null) return Ok(new { servers, projects = Array.Empty<object>(), workspaces = Array.Empty<object>() });
        var who = new RelayPerson(PersonId, PersonName, IsAdmin ? UserRole.Admin : UserRole.Viewer);
        var (status, projects) = await relay.CallAsync(server, who, HttpMethod.Get, "/api/v1/projects", null, cancellation);
        if (status != 200) return Conflict(new { error = (string?)projects?["error"] ?? $"The server answered {status}." });
        JsonNode? workspaces = null;
        if (project is not null)
        {
            var (wStatus, found) = await relay.CallAsync(server, who, HttpMethod.Get, $"/api/v1/projects/{Uri.EscapeDataString(project)}/workspaces", null, cancellation);
            if (wStatus == 200) workspaces = found?["workspaces"];
        }
        return Ok(new { servers, projects = projects?["projects"], workspaces });
    }

    /// <summary>Makes or refreshes the <c>build-helper</c> agent from what a Build server offers an agent, and its skill (plan 27a AG14).</summary>
    [HttpPost("build-helper/install")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> InstallBuildHelper([FromBody] InstallBuildHelperBody body, CancellationToken cancellation)
    {
        var (done, problem) = await agents.InstallBuildHelperAsync(new BuildTarget(body.Server, body.Project, null), PersonId, cancellation);
        return done ? Ok(agents.Read(BuildAgentTools.HelperName)) : Conflict(new { error = problem });
    }

    [HttpGet("conversations/{id}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Conversation(string id) =>
        agents.View(id) is { } view && (view.Conversation.Person == PersonId || IsAdmin) ? Named(view) : NotFound(new { error = "There is no such conversation." });

    [HttpPost("conversations/{id}/messages")]
    [Authorize(Policies.Admin)]
    public IActionResult Send(string id, [FromBody] SendMessageBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Text)) return BadRequest(new { error = "Say something." });
        var (accepted, problem) = agents.Send(id, PersonId, body.Text);
        return accepted ? Accepted() : Conflict(new { error = problem });
    }

    [HttpPost("conversations/{id}/cancel")]
    [Authorize(Policies.Admin)]
    public IActionResult Cancel(string id) => agents.Cancel(id, PersonId) ? Accepted() : NotFound();

    // ---- the inbox (AG15, AG29) ----

    [HttpGet("approvals")]
    [Authorize(Policies.Viewer)]
    public IActionResult Approvals([FromQuery] bool pending = true, [FromQuery] bool all = false)
    {
        agents.ExpireOld();
        return Named(agents.Store.Approvals(pending, all && IsAdmin ? null : PersonId));
    }

    /// <summary>The person's own answer. Only the person a step is for may give it; there is no override.</summary>
    [HttpPost("approvals/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Answer(Guid id, [FromBody] AnswerBody body) =>
        agents.Answer(id, body.Approve, PersonId, body.Note, anyPerson: false) ? Named(agents.Store.Approval(id)!) : Conflict(new { error = "That step is not waiting for you (it was answered, it expired, or it is another person's)." });

    // ---- the record (AG29) ----

    [HttpGet("audit")]
    [Authorize(Policies.Viewer)]
    public IActionResult Audit([FromQuery] string? person, [FromQuery] string? agent, [FromQuery] string? origin, [FromQuery] string? kind, [FromQuery] int limit = 300)
    {
        var who = IsAdmin ? person : PersonId;
        return Named(agents.Store.Audit(person: who, agent: agent, kind: kind, origin: origin, limit: Math.Clamp(limit, 1, 1000)));
    }

    [HttpGet("usage")]
    [Authorize(Policies.Admin)]
    public IActionResult Usage() => Named(new { byPerson = agents.Store.Usage("person").Select(u => new { u.Key, u.Turns, u.InputTokens, u.OutputTokens, u.Cost, person = u.Key }), byAgent = agents.Store.Usage("agent"), install = agents.Store.Usage("install").FirstOrDefault() });

    // ---- model providers and slots (AG05): admin writes, anyone signed in reads what is bound (never a key) ----

    [HttpGet("models")]
    [Authorize(Policies.Viewer)]
    public IActionResult Models() => Ok(models.View(agents.List().Select(a => (a.ModelSlot, a.Name))));

    [HttpPost("models/providers/{name}/check")]
    [Authorize(Policies.Admin)]
    public IActionResult CheckProvider(string name, [FromBody] YamlBody body) =>
        ModelProviderLoader.IsName(name) ? Ok(new { diagnostics = models.CheckProvider(name, body.Yaml).Diagnostics }) : BadRequest(new { error = "A provider's name is lower case letters, digits and hyphens." });

    [HttpPut("models/providers/{name}")]
    [Authorize(Policies.Admin)]
    public IActionResult SaveProvider(string name, [FromBody] YamlBody body)
    {
        if (!ModelProviderLoader.IsName(name)) return BadRequest(new { error = "A provider's name is lower case letters, digits and hyphens." });
        var (saved, diagnostics) = models.SaveProvider(name, body.Yaml);
        if (saved) agents.Store.RecordAudit(null, null, "model-provider-saved", PersonId, "console", "1", "console", name, null, "the provider's file was saved");
        return saved ? Ok(new { saved, diagnostics }) : UnprocessableEntity(new { saved, diagnostics });
    }

    [HttpDelete("models/providers/{name}")]
    [Authorize(Policies.Admin)]
    public IActionResult DeleteProvider(string name)
    {
        if (!models.DeleteProvider(name)) return NotFound(new { error = $"There is no provider named '{name}'." });
        agents.Store.RecordAudit(null, null, "model-provider-removed", PersonId, "console", "1", "console", name, null, "the provider's file was removed");
        return NoContent();
    }

    /// <summary>Keeps a pasted key in the console's secret store under the provider's name. The key is in no answer and no audit row.</summary>
    [HttpPut("models/providers/{name}/key")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetKey(string name, [FromBody] KeyBody body)
    {
        if (!ModelProviderLoader.IsName(name)) return BadRequest(new { error = "A provider's name is lower case letters, digits and hyphens." });
        if (string.IsNullOrWhiteSpace(body.Key)) return BadRequest(new { error = "Type the key." });
        await models.StoreKeyAsync(name, body.Key.Trim());
        agents.Store.RecordAudit(null, null, "model-key-set", PersonId, "console", "1", "console", name, null, $"a key was stored for the provider, as {ConsoleModels.ReferenceFor(name)}");
        return Ok(new { stored = true, reference = ConsoleModels.ReferenceFor(name) });
    }

    [HttpPost("models/providers/{name}/test")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> TestProvider(string name, [FromBody] TestModelBody body, CancellationToken cancellation)
    {
        var result = await models.TestAsync(name, body.Model, cancellation);
        agents.Store.RecordAudit(null, null, "model-provider-tested", PersonId, "console", "1", "console", name, null, result.Ok ? $"{body.Model} answered" : result.Message);
        return Ok(result);
    }

    [HttpPost("models/slots/check")]
    [Authorize(Policies.Admin)]
    public IActionResult CheckSlots([FromBody] YamlBody body) => Ok(new { diagnostics = models.CheckSlots(body.Yaml).Diagnostics });

    [HttpPut("models/slots")]
    [Authorize(Policies.Admin)]
    public IActionResult SaveSlots([FromBody] YamlBody body)
    {
        var (saved, diagnostics) = models.SaveSlots(body.Yaml);
        if (saved) agents.Store.RecordAudit(null, null, "model-slots-saved", PersonId, "console", "1", "console", null, null, "the slot bindings were saved");
        return saved ? Ok(new { saved, diagnostics }) : UnprocessableEntity(new { saved, diagnostics });
    }

    // ---- the tokens an outside agent signs in with (AG29): a person's own, shown once ----

    [HttpGet("tokens")]
    [Authorize(Policies.Viewer)]
    public IActionResult Tokens() => Ok(agents.Store.Tokens(PersonId).Select(t => new { t.Id, t.Name, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt, active = t.IsActiveAt(DateTimeOffset.UtcNow) }));

    [HttpPost("tokens")]
    [Authorize(Policies.Viewer)]
    public IActionResult MakeToken([FromBody] TokenBody body)
    {
        try
        {
            var (row, secret) = agents.Store.CreateToken(PersonId, body.Name ?? "", body.Days is { } d ? TimeSpan.FromDays(d) : null);
            agents.Store.RecordAudit(null, null, "token-made", PersonId, "console", "1", "console", null, null, $"a token named '{row.Name}' was made for an outside agent");
            return Ok(new { token = secret, user = PersonId, record = new { row.Id, row.Name, row.CreatedAt, row.ExpiresAt } });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("tokens/{id}")]
    [Authorize(Policies.Viewer)]
    public IActionResult RevokeToken(string id)
    {
        if (!agents.Store.RevokeToken(id, PersonId)) return NotFound(new { error = "You have no token with that id that is not already revoked." });
        agents.Store.RecordAudit(null, null, "token-revoked", PersonId, "console", "1", "console", null, null, $"the token {id} was revoked");
        return NoContent();
    }

    // ---- an outside agent's half (AG29): `ui mcp` posts what it asked and did as the person whose token it holds ----

    [HttpPost("outside/ask")]
    [Authorize(Policies.Viewer)]
    public IActionResult OutsideAsk([FromBody] OutsideAskBody body) =>
        Ok(new { id = agents.AskForOutside(PersonId, body.Agent, body.AgentVersion, body.Tool, body.Preview, body.Arguments, body.Traits) });

    [HttpGet("outside/ask/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult OutsideStatus(Guid id) =>
        agents.Store.Approval(id) is { Origin: "mcp" } row && row.Person == PersonId ? Ok(new { row.Id, row.Status, row.AnsweredBy, row.Note }) : NotFound();

    /// <summary>What an outside agent's server may report: the kinds the gate writes. It cannot write a kind of the console's own (a policy change).</summary>
    private static readonly HashSet<string> OutsideKinds = new(StringComparer.Ordinal)
    {
        AgentEventKinds.Asked, AgentEventKinds.Approved, AgentEventKinds.Denied, AgentEventKinds.ToolCalled, AgentEventKinds.ToolRefused, AgentEventKinds.ToolFailed, AgentEventKinds.Stopped, "session-started",
    };

    [HttpPost("outside/events")]
    [Authorize(Policies.Viewer)]
    public IActionResult OutsideEvents([FromBody] OutsideEventsBody body)
    {
        foreach (var e in body.Events.Where(e => OutsideKinds.Contains(e.Kind)).Take(200))
            agents.Store.RecordAudit(null, null, e.Kind, PersonId, e.Agent, e.AgentVersion, "mcp", e.Tool, e.Arguments, e.Detail);
        return Accepted();
    }
}
