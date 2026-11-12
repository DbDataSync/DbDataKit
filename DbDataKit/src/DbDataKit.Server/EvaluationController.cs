using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record MinimumBody(decimal? Minimum);

/// <summary>
/// The evaluation sets of the console's back-end agents (plan 27a AG23, AG24): the cases an agent has, the runs with the score per case, the cost and what the agent was given, a run started by an administrator, the minimum score before a promotion
/// as a recorded choice, and what a promotion of the agent's version would be told. Anyone signed in reads; an administrator starts a run and sets the minimum.
/// </summary>
[ApiController]
[Route("api/agents/{name}/evaluation")]
public sealed class EvaluationController(ConsoleAgents agents, ConsoleEvaluation evaluation) : ControllerBase
{
    private static JsonNode? Json(string? text) => string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);

    private static object Summary(EvaluationRunRow r) => new
    {
        r.Id, r.Agent, r.AgentVersion, r.Channel, r.Model, knowledgeVersions = Json(r.KnowledgeVersions), r.Trigger, score = r.Score, r.Passed, r.Total, cost = r.Cost, startedAt = r.StartedAt, finishedAt = r.FinishedAt,
    };

    [HttpGet("")]
    [Authorize(Policies.Viewer)]
    public IActionResult Get(string name)
    {
        if (agents.Read(name, withReports: false) is null) return NotFound(new { error = $"There is no agent named '{name}'." });
        var (cases, diagnostics) = evaluation.Cases(name);
        var version = evaluation.VersionOf(name)!;
        var runs = agents.Evaluations.Runs(name, ConsoleEvaluation.Channel, 30);
        var minimum = agents.Evaluations.Minimum(name, ConsoleEvaluation.Channel);
        return Ok(new
        {
            agent = name,
            version,
            channel = ConsoleEvaluation.Channel,
            cases = ConsoleEvaluation.Listing(cases),
            diagnostics,
            judgeBound = evaluation.JudgeBound,
            running = evaluation.Running(name),
            runs = runs.Select(Summary).ToList(),
            minimum = minimum is null ? null : new { minimum.Minimum, minimum.SetBy, minimum.SetAt },
            promotion = new { version, refusal = evaluation.PromotionRefusal(name, version) },
        });
    }

    [HttpPost("run")]
    [Authorize(Policies.Admin)]
    public IActionResult Run(string name)
    {
        var (started, problem) = evaluation.Start(name);
        return started ? Accepted(new { started = true }) : Conflict(new { error = problem });
    }

    [HttpGet("runs/{id}")]
    [Authorize(Policies.Viewer)]
    public IActionResult RunDetail(string name, string id)
    {
        if (agents.Evaluations.Run(id) is not { } run || run.Agent != name) return NotFound(new { error = $"There is no run '{id}' of {name}." });
        return Ok(new
        {
            run = Summary(run),
            cases = run.Cases.Select(c => new { name = c.Name, c.Task, score = c.Score, c.Passed, c.Answer, checks = Json(c.Checks), c.InputTokens, c.OutputTokens, cost = c.Cost, c.Problem }).ToList(),
        });
    }

    [HttpPut("minimum")]
    [Authorize(Policies.Admin)]
    public IActionResult SetMinimum(string name, [FromBody] MinimumBody body)
    {
        if (agents.Read(name, withReports: false) is null) return NotFound(new { error = $"There is no agent named '{name}'." });
        if (body.Minimum is < 0 or > 1) return UnprocessableEntity(new { error = "A minimum score is from 0 to 1." });
        var person = User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";
        agents.Evaluations.SetMinimum(name, ConsoleEvaluation.Channel, body.Minimum, person);
        var version = evaluation.VersionOf(name)!;
        return Ok(new { minimum = body.Minimum, promotion = new { version, refusal = evaluation.PromotionRefusal(name, version) } });
    }
}
