using System.Text.Json.Nodes;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents;

/// <summary>What the host gives the runtime to run a subagent: its definition and its version, the model its slot is bound to, the instructions it was given, and the tools its definition names (resolved against the product's commands and actions).</summary>
public sealed record SubagentSetup(AgentDefinition Definition, string Version, IChatClient Client, string Instructions, IReadOnlyList<AgentTool> DeclaredTools);

/// <summary>
/// Subagents (plan 27a AG20, plan 27 section 3.12): an agent that another agent calls as a tool (<c>agent_&lt;name&gt;</c>, with a <c>task</c> and what the caller passes as <c>context</c>). It is the <b>same person</b>; its reach is the person's access narrowed by the parent's tools (it can
/// never call a tool its parent could not) and then by its own definition; it has its own instructions and sees only the task and the context the parent passed; every step that asks, asks through the <b>parent's conversation</b> (the gate is the parent's, so one approval
/// desk answers); its turns, tool calls and tokens count to the parent (<see cref="ToolOutcome.Spent"/>) and, with them, its cost; and it nests at most <see cref="MaximumDepth"/> deep. Its events carry its own name, the agent that called it and its depth, so the transcript nests.
/// </summary>
public static class SubagentTools
{
    /// <summary>The levels of subagents below the agent a person talks to: an agent calls a subagent that calls one, and no further.</summary>
    public const int MaximumDepth = 2;

    public const string Prefix = "agent_";

    public static string ToolName(string agent) => Prefix + agent.Replace('-', '_');

    /// <summary>
    /// The tools of the subagents <paramref name="parent"/> declares. <paramref name="parentTools"/> is what the parent can call (its reach); <paramref name="setup"/> resolves a subagent's definition, version, model and declared tools; <paramref name="gateFor"/> gives
    /// the gate for a subagent (the parent's approvers, with the subagent's own prompting settings). A subagent's subagents are offered from <see cref="SubagentSetup.Definition"/> the same way, down to the depth limit.
    /// </summary>
    public static IReadOnlyList<AgentTool> For(AgentDefinition parent, IReadOnlyList<AgentTool> parentTools, AgentRunContext parentContext, Func<string, SubagentSetup?> setup, Func<AgentDefinition, IToolGate?> gateFor, IToolGate? parentGate = null, IRunLimits? limits = null, IAgentRecorder? recorder = null)
    {
        var tools = new List<AgentTool>();
        foreach (var name in parent.Subagents)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("task"),
                ["properties"] = new JsonObject
                {
                    ["task"] = new JsonObject { ["type"] = "string", ["description"] = "What the subagent should do, in words it can act on without your conversation." },
                    ["context"] = new JsonObject { ["type"] = "string", ["description"] = "What from your work it needs. It sees nothing else of this conversation." },
                },
            };
            var tool = new AgentTool(ToolName(name), $"Ask the agent '{name}' to do a task and give you its answer. It acts as the same person with no more access than you have; it asks the person through this conversation; its use counts to yours.",
                Traits.ReadsDefinitions, schema, $"subagent {name}", async (arguments, cancellation) =>
                {
                    var depth = parentContext.Depth + 1;
                    if (depth > MaximumDepth)
                    {
                        recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, AgentEventKinds.ToolRefused, parentContext, 0, ToolName(name), $"subagents nest at most {MaximumDepth} deep"));
                        return new ToolOutcome($"The agent '{name}' was not asked: subagents nest at most {MaximumDepth} levels deep and this is level {depth}. Do the task yourself or answer with what you have.", true);
                    }
                    if (setup(name) is not { } resolved)
                        return new ToolOutcome($"The agent '{name}' is not available: it does not exist or its model is not bound.", true);

                    var context = parentContext.ForSubagent(resolved.Definition.Name, resolved.Version);
                    // its reach: what it names, narrowed by what the parent can call, then the subagents it declares in turn
                    var reach = parentTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                    var narrowed = resolved.DeclaredTools.Where(t => reach.Contains(t.Name)).ToList();
                    var dropped = resolved.DeclaredTools.Where(t => !reach.Contains(t.Name)).Select(t => t.Name).ToList();
                    if (dropped.Count > 0)
                        recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, "subagent-narrowed", context, 0, null, $"not given, the caller cannot call them: {string.Join(", ", dropped)}"));
                    var own = For(resolved.Definition, narrowed, context, setup, gateFor, parentGate, limits, recorder);
                    var subTools = narrowed.Concat(own).ToList();

                    var message = string.IsNullOrWhiteSpace((string?)arguments["context"])
                        ? (string)arguments["task"]!
                        : $"{(string)arguments["task"]!}\n\nContext from {parentContext.Agent}:\n{(string)arguments["context"]!}";
                    recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, "subagent-started", context, 0, ToolName(name), $"asked by {parentContext.Agent}"));
                    var options = new AgentRunOptions { Instructions = resolved.Instructions, Gate = gateFor(resolved.Definition) ?? parentGate, Limits = limits, Recorder = recorder };
                    var run = await new AgentRunner(resolved.Client, subTools).RunAsync([new ChatMessage(ChatRole.User, message)], context, options, cancellation).ConfigureAwait(false);
                    recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, "subagent-finished", context, 0, ToolName(name), $"{run.Stop}; {run.Usage.ModelTurns} turns, {run.Usage.ToolCalls} tool calls, {run.Usage.Tokens} tokens"));
                    var text = run.Stop == AgentStop.Completed ? run.Text : $"The agent '{name}' stopped before it finished ({run.Stop}): {run.StopMessage}";
                    return new ToolOutcome(text, run.Stop != AgentStop.Completed) { Spent = run.Usage };
                })
            { DeclaredPrompting = Prompting.DoNotAsk };
            tools.Add(tool);
        }
        return tools;
    }
}
