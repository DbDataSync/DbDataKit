using System.Text.Json.Nodes;
using DbDataKit.Cli;

namespace DbDataKit.Agents;

/// <summary>
/// What an install policy says about prompting (plan 27a AG07): a minimum per trait that no agent can go below, and a setting for named tools. It is read from <c>agent-policy/1</c> (the policy reader of AG09); an install with no policy has none.
/// </summary>
public sealed record PromptingPolicy(IReadOnlyDictionary<Traits, Prompting> Minimums, IReadOnlyDictionary<string, Prompting> Tools)
{
    public static readonly PromptingPolicy None = new(new Dictionary<Traits, Prompting>(), new Dictionary<string, Prompting>());
}

/// <summary>
/// Which of the three settings a tool gets (plan 27 section 3.3): the layers are the tool's own default (the one its author declared, else the one its traits give), the install policy (a minimum per trait, and a setting for the tool by name) and the
/// agent definition's setting for the tool. <b>The strictest of them applies</b>; no layer can go below another's. Strictest means: ask, then ask once per conversation, then do not ask.
/// </summary>
public static class PromptingResolver
{
    public static int Strictness(Prompting setting) => setting switch { Prompting.Ask => 2, Prompting.AskOnce => 1, _ => 0 };

    public static Prompting Strictest(params Prompting?[] settings) =>
        settings.Where(s => s is not null).Select(s => s!.Value).OrderByDescending(Strictness).DefaultIfEmpty(Prompting.DoNotAsk).First();

    /// <summary>The setting of one traits set by the table of plan 27 section 3.3: the strictest of its traits.</summary>
    public static Prompting ByTraits(Traits traits) => McpGate.Default(traits);

    /// <param name="tool">The tool.</param>
    /// <param name="policy">The install policy's prompting part.</param>
    /// <param name="agentSetting">What the agent definition says for this tool, if anything.</param>
    public static Prompting Resolve(AgentTool tool, PromptingPolicy policy, Prompting? agentSetting)
    {
        var own = tool.DeclaredPrompting ?? ByTraits(tool.Traits);
        Prompting? named = policy.Tools.TryGetValue(tool.Name, out var byName) ? byName : null;
        var floor = policy.Minimums.Where(m => tool.Traits.HasFlag(m.Key)).Select(m => (Prompting?)m.Value);
        return Strictest([own, agentSetting, named, .. floor]);
    }
}

/// <summary>What the person is asked: the tool, what it was asked to do, what it may touch, who is asking. <see cref="Preview"/> is the one line they read.</summary>
public sealed record ApprovalPrompt(string Tool, string Source, JsonObject Arguments, Traits Traits, string Person, string Agent, string AgentVersion)
{
    /// <summary>The command or action with its arguments and its traits: <c>command workspace commit "message"  [writes-definitions]</c>.</summary>
    public string Preview
    {
        get
        {
            var shown = Arguments["args"] is JsonArray args
                ? string.Join(' ', args.Select(a => (string?)a ?? "").Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
                : Arguments.ToJsonString();
            return $"{Source}{(shown.Length > 0 ? " " + shown : "")}  [{string.Join(", ", Traits.Names())}]";
        }
    }
}

/// <summary>Asks the person. The console's chat shows the prompt and answers (plan 27a AG15); a test answers from a script.</summary>
public interface IApprover
{
    Task<ApprovalDecision> AskAsync(ApprovalPrompt prompt, CancellationToken cancellation);
}

/// <summary>An approver that answers from a list (tests, and the unattended case that answers no).</summary>
public sealed class ScriptedApprover(params bool[] answers) : IApprover
{
    private readonly Queue<bool> _answers = new(answers);

    public List<ApprovalPrompt> Asked { get; } = [];

    public Task<ApprovalDecision> AskAsync(ApprovalPrompt prompt, CancellationToken cancellation)
    {
        Asked.Add(prompt);
        if (!_answers.TryDequeue(out var yes)) throw new InvalidOperationException("The scripted approver was asked more often than it has answers.");
        return Task.FromResult(new ApprovalDecision(yes, yes ? null : "the person said no"));
    }
}

/// <summary>Where a step an unattended run could not ask about is left (plan 27a AG27: a draft change, a work item or a notification).</summary>
public interface IProposalSink
{
    void Propose(ToolCall call, AgentTool tool, AgentRunContext context);
}

/// <summary>
/// The gate that asks (plan 27a AG07). Before a tool runs the setting is worked out by <see cref="PromptingResolver"/>; <i>do not ask</i> runs it, <i>ask once</i> asks the first time in a conversation, <i>ask</i> asks every time, and every ask, approval and
/// denial is recorded naming the person and the agent. A denied step does not run and changes nothing; the model is told in words and may go on. An <b>unattended</b> run asks no one: only <i>do not ask</i> runs, the rest are left as proposals.
/// An approval comes only from the approver: nothing in a model's message or a tool's result can stand for one.
/// </summary>
public sealed class ApprovalGate(IApprover? approver, PromptingPolicy policy, IReadOnlyDictionary<string, Prompting>? agentSettings = null, IAgentRecorder? recorder = null, IProposalSink? proposals = null) : IToolGate
{
    private readonly HashSet<string> _approvedOnce = new(StringComparer.Ordinal);

    /// <summary>When true a denied step ends the run (the MCP workflow does); by default the model is told and may try something else.</summary>
    public bool EndRunOnDenial { get; init; }

    /// <summary>The setting a tool gets under this gate's policy and agent settings.</summary>
    public Prompting SettingFor(AgentTool tool) => PromptingResolver.Resolve(tool, policy, agentSettings is not null && agentSettings.TryGetValue(tool.Name, out var s) ? s : null);

    public async Task<GateDecision> BeforeCallAsync(ToolCall call, AgentTool tool, AgentRunContext context, int turn, CancellationToken cancellation)
    {
        var setting = SettingFor(tool);
        if (setting == Prompting.DoNotAsk) return GateDecision.Allow;
        var key = $"{context.ConversationId}\n{tool.Name}";
        if (setting == Prompting.AskOnce && _approvedOnce.Contains(key)) return GateDecision.Allow;

        var prompt = new ApprovalPrompt(tool.Name, tool.Source, call.Arguments, tool.Traits, context.Person, context.Agent, context.AgentVersion);
        if (approver is null)
        {
            proposals?.Propose(call, tool, context);
            Record(AgentEventKinds.ToolRefused, context, turn, tool.Name, "unattended: left as a proposal");
            return new GateDecision(false, $"'{tool.Name}' needs a person's approval ({setting.ToString().ToLowerInvariant()}) and nobody is here to give it, so it did not run. It has been left as a proposal for a person to decide.");
        }

        Record(AgentEventKinds.Asked, context, turn, tool.Name, prompt.Preview);
        var decision = await approver.AskAsync(prompt, cancellation).ConfigureAwait(false);
        if (decision.Approved)
        {
            if (setting == Prompting.AskOnce) _approvedOnce.Add(key);
            Record(AgentEventKinds.Approved, context, turn, tool.Name, decision.Note);
            return GateDecision.Allow;
        }
        Record(AgentEventKinds.Denied, context, turn, tool.Name, decision.Note);
        return new GateDecision(false, $"The person did not approve '{tool.Name}'{(string.IsNullOrWhiteSpace(decision.Note) ? "" : $" ({decision.Note})")}. It did not run and nothing was changed. Do not try it again unless the person asks; say what you could not do.", EndRunOnDenial);
    }

    private void Record(string kind, AgentRunContext context, int turn, string tool, string? detail) =>
        recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, kind, context, turn, tool, detail));
}
