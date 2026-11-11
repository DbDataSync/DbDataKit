using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents;

/// <summary>Who runs and what: the person the agent acts as, the agent and the version it runs, the conversation. The record of everything that happens names them.</summary>
public sealed record AgentRunContext(string Person, string Agent, string AgentVersion, string ConversationId = "c1", string? ParentAgent = null, int Depth = 0)
{
    /// <summary>The context of a subagent's run (plan 27a AG20): the same person and conversation, its own name and version, and the agent that called it, so the record nests.</summary>
    public AgentRunContext ForSubagent(string agent, string version) => this with { Agent = agent, AgentVersion = version, ParentAgent = Agent, Depth = Depth + 1 };
}

/// <summary>One thing that happened in a run, for the record of a turn and the audit (plan 27a AG06 keeps them in the <c>agents</c> module). <see cref="Kind"/> is one of <see cref="AgentEventKinds"/>.</summary>
public sealed record AgentEvent(DateTimeOffset At, string Kind, AgentRunContext Context, int Turn, string? Tool = null, string? Detail = null);

public static class AgentEventKinds
{
    public const string ModelAnswered = "model-answered";
    public const string ToolCalled = "tool-called";
    public const string ToolRefused = "tool-refused";
    public const string ToolFailed = "tool-failed";
    public const string Asked = "approval-asked";
    public const string Approved = "approval-approved";
    public const string Denied = "approval-denied";
    public const string LimitReached = "limit-reached";
    public const string Stopped = "stopped";
    public const string ContextGiven = "context-given";
    public const string SkillLoaded = "skill-loaded";
}

public interface IAgentRecorder
{
    void Record(AgentEvent entry);
}

/// <summary>A recorder that keeps the events in memory: tests, and a host that persists them afterwards.</summary>
public sealed class InMemoryRecorder : IAgentRecorder
{
    private readonly List<AgentEvent> _events = [];

    public IReadOnlyList<AgentEvent> Events { get { lock (_events) return [.. _events]; } }

    public void Record(AgentEvent entry) { lock (_events) _events.Add(entry); }
}

/// <summary>A tool call the model made, as the gate and the limits see it.</summary>
public sealed record ToolCall(string CallId, string Tool, JsonObject Arguments);

/// <summary>The answer of a gate to one call: let it run, or not (and then say why in words the model can act on, and whether the run ends).</summary>
public sealed record GateDecision(bool Allowed, string? Message = null, bool EndsRun = false)
{
    public static readonly GateDecision Allow = new(true);
}

/// <summary>Decides, before a tool runs, whether it may (plan 27a AG07: the person is asked as the tool's traits and the layers say).</summary>
public interface IToolGate
{
    Task<GateDecision> BeforeCallAsync(ToolCall call, AgentTool tool, AgentRunContext context, int turn, CancellationToken cancellation);
}

/// <summary>What a run has used so far.</summary>
public sealed record RunUsage(int ModelTurns, int ToolCalls, long InputTokens, long OutputTokens)
{
    public static readonly RunUsage None = new(0, 0, 0, 0);

    public long Tokens => InputTokens + OutputTokens;
}

/// <summary>Checked before each model turn and each tool call (plan 27a AG08).</summary>
public interface IRunLimits
{
    /// <summary>Null when the run may go on; else the sentence that says which limit is reached.</summary>
    string? Reached(RunUsage usage, AgentRunContext context);
}

public enum AgentStop
{
    /// <summary>The model answered with no more tool calls.</summary>
    Completed,

    /// <summary>A limit was reached.</summary>
    LimitReached,

    /// <summary>A step was not approved and the run ended.</summary>
    Denied,

    /// <summary>The model refused (its provider's content filter).</summary>
    Refused,
}

public sealed record AgentRunResult(string Text, IReadOnlyList<ChatMessage> Messages, AgentStop Stop, string? StopMessage, RunUsage Usage);

/// <summary>What a run is made of besides the model and the tools.</summary>
public sealed record AgentRunOptions
{
    /// <summary>The agent's instructions, as the system message (plan 27a AG10 assembles them from the four homes).</summary>
    public string Instructions { get; init; } = "";

    /// <summary>The instructions as they should be for a turn, when they change with what is in play (plan 27a AG10, KN34): asked before each model turn, the system message is replaced by what it returns.</summary>
    public Func<int, string>? InstructionsForTurn { get; init; }

    /// <summary>Told after each tool call that ran, with its result (an in-play tracker works out from it what is now in play).</summary>
    public Action<ToolCall, ToolOutcome>? AfterCall { get; init; }

    public IToolGate? Gate { get; init; }

    public IRunLimits? Limits { get; init; }

    public IAgentRecorder? Recorder { get; init; }

    /// <summary>A guard against a loop of model turns that no configured limit stops.</summary>
    public int MaximumModelTurns { get; init; } = 25;
}

/// <summary>
/// The agent loop (plan 27a AG03), on <see cref="IChatClient"/>: the model is asked, the tool calls it makes are checked against the tool's schema, passed through the gate and the limits, run, and their results given back marked as data, until the model
/// answers with no more calls. A bad call is not an exception: it is a result the model can act on. Nothing here knows a provider, a host or a person's screen.
/// </summary>
public sealed class AgentRunner(IChatClient client, IReadOnlyList<AgentTool> tools)
{
    private readonly IReadOnlyDictionary<string, AgentTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public async Task<AgentRunResult> RunAsync(IEnumerable<ChatMessage> conversation, AgentRunContext context, AgentRunOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new AgentRunOptions();
        var messages = new List<ChatMessage>();
        if (options.Instructions.Length > 0) messages.Add(new ChatMessage(ChatRole.System, options.Instructions));
        messages.AddRange(conversation);
        var chatOptions = new ChatOptions
        {
            Tools = [.. tools.Select(t => (AITool)AIFunctionFactory.CreateDeclaration(t.Name, t.Description, JsonSerializer.SerializeToElement(t.InputSchema)))],
            ToolMode = ChatToolMode.Auto,
        };
        var usage = RunUsage.None;

        void Record(string kind, int turn, string? tool = null, string? detail = null) =>
            options.Recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, kind, context, turn, tool, detail));
        AgentRunResult Stop(AgentStop stop, string message, string kind, int turn)
        {
            Record(kind, turn, detail: message);
            Record(AgentEventKinds.Stopped, turn, detail: stop.ToString());
            return new AgentRunResult("", messages, stop, message, usage);
        }

        for (var turn = 1; ; turn++)
        {
            if (turn > options.MaximumModelTurns) return Stop(AgentStop.LimitReached, $"The run reached its guard of {options.MaximumModelTurns} model turns.", AgentEventKinds.LimitReached, turn);
            if (options.Limits?.Reached(usage, context) is { } reached) return Stop(AgentStop.LimitReached, reached, AgentEventKinds.LimitReached, turn);

            if (options.InstructionsForTurn is { } instructionsFor)
            {
                var text = instructionsFor(turn);
                if (messages.Count > 0 && messages[0].Role == ChatRole.System) messages[0] = new ChatMessage(ChatRole.System, text);
                else messages.Insert(0, new ChatMessage(ChatRole.System, text));
            }
            var response = await client.GetResponseAsync(messages, chatOptions, cancellation).ConfigureAwait(false);
            usage = usage with
            {
                ModelTurns = usage.ModelTurns + 1,
                InputTokens = usage.InputTokens + (response.Usage?.InputTokenCount ?? 0),
                OutputTokens = usage.OutputTokens + (response.Usage?.OutputTokenCount ?? 0),
            };
            messages.AddRange(response.Messages);
            Record(AgentEventKinds.ModelAnswered, turn, detail: $"{response.Usage?.InputTokenCount ?? 0} in, {response.Usage?.OutputTokenCount ?? 0} out");

            if (response.FinishReason == ChatFinishReason.ContentFilter)
                return Stop(AgentStop.Refused, string.IsNullOrWhiteSpace(response.Text) ? "The model refused to answer." : response.Text, AgentEventKinds.Stopped, turn);

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                Record(AgentEventKinds.Stopped, turn, detail: nameof(AgentStop.Completed));
                return new AgentRunResult(response.Text, messages, AgentStop.Completed, null, usage);
            }

            var results = new List<AIContent>();
            foreach (var call in calls)
            {
                if (options.Limits?.Reached(usage, context) is { } limit)
                {
                    if (results.Count > 0) messages.Add(new ChatMessage(ChatRole.Tool, results));
                    return Stop(AgentStop.LimitReached, limit, AgentEventKinds.LimitReached, turn);
                }
                var (outcome, endsRun) = await HandleAsync(call, context, options, turn, Record, cancellation).ConfigureAwait(false);
                if (outcome.Counted) usage = usage with { ToolCalls = usage.ToolCalls + 1 };
                if (outcome.Spent is { } spent) usage = usage with { ModelTurns = usage.ModelTurns + spent.ModelTurns, ToolCalls = usage.ToolCalls + spent.ToolCalls, InputTokens = usage.InputTokens + spent.InputTokens, OutputTokens = usage.OutputTokens + spent.OutputTokens };
                if (outcome.Call is { } ran) options.AfterCall?.Invoke(ran, new ToolOutcome(outcome.Text, outcome.IsError));
                results.Add(new FunctionResultContent(call.CallId, outcome.Skill is { } skill && !outcome.IsError ? ToolResults.AsSkill(skill.Name, skill.Version, outcome.Text) : ToolResults.AsData(outcome.Tool, outcome.Source, outcome.Text, outcome.IsError)));
                if (endsRun)
                {
                    messages.Add(new ChatMessage(ChatRole.Tool, results));
                    return Stop(AgentStop.Denied, outcome.Text, AgentEventKinds.Stopped, turn);
                }
            }
            messages.Add(new ChatMessage(ChatRole.Tool, results));
        }
    }

    private sealed record Handled(string Tool, string Source, string Text, bool IsError, bool Counted, (string Name, string Version)? Skill = null, ToolCall? Call = null, RunUsage? Spent = null);

    private async Task<(Handled, bool)> HandleAsync(FunctionCallContent call, AgentRunContext context, AgentRunOptions options, int turn, Action<string, int, string?, string?> record, CancellationToken cancellation)
    {
        const string Platform = "platform";
        if (call.Exception is not null)
        {
            record(AgentEventKinds.ToolRefused, turn, call.Name, "malformed call");
            return (new Handled(call.Name, Platform, $"The call to '{call.Name}' was not run: its arguments could not be read ({call.Exception.Message}). Send the arguments as one JSON object that fits the tool's schema.", true, false), false);
        }
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            record(AgentEventKinds.ToolRefused, turn, call.Name, "unknown tool");
            return (new Handled(call.Name, Platform, $"There is no tool named '{call.Name}'. The tools are: {string.Join(", ", _tools.Keys)}.", true, false), false);
        }

        JsonObject arguments;
        try { arguments = JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>()) as JsonObject ?? new JsonObject(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            record(AgentEventKinds.ToolRefused, turn, call.Name, "arguments not JSON");
            return (new Handled(tool.Name, tool.Source, $"The call to '{tool.Name}' was not run: its arguments could not be read as JSON ({ex.Message}).", true, false), false);
        }
        var problems = tool.Validate(arguments);
        if (problems.Count > 0)
        {
            record(AgentEventKinds.ToolRefused, turn, tool.Name, "arguments do not fit the schema");
            return (new Handled(tool.Name, tool.Source, $"The call to '{tool.Name}' was not run: the arguments do not fit its schema ({string.Join("; ", problems)}). The tool is described as: {tool.Description}", true, false), false);
        }

        var toolCall = new ToolCall(call.CallId, tool.Name, arguments);
        if (options.Gate is not null)
        {
            var decision = await options.Gate.BeforeCallAsync(toolCall, tool, context, turn, cancellation).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                record(AgentEventKinds.ToolRefused, turn, tool.Name, decision.Message);
                var text = decision.Message ?? $"The call to '{tool.Name}' was not approved and did not run.";
                return (new Handled(tool.Name, Platform, text, true, false), decision.EndsRun);
            }
        }

        try
        {
            var outcome = await tool.InvokeAsync(arguments, cancellation).ConfigureAwait(false);
            record(outcome.IsError ? AgentEventKinds.ToolFailed : AgentEventKinds.ToolCalled, turn, tool.Name, arguments.ToJsonString());
            return (new Handled(tool.Name, tool.Source, outcome.Text, outcome.IsError, true, tool.ResultKind == ToolResultKind.Skill ? (outcome.Name ?? tool.Name, outcome.Version ?? "") : null, toolCall, outcome.Spent), false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            record(AgentEventKinds.ToolFailed, turn, tool.Name, ex.Message);
            return (new Handled(tool.Name, tool.Source, $"The tool '{tool.Name}' failed: {ex.Message}", true, true), false);
        }
    }
}
