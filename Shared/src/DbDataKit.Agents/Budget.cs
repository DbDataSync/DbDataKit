using Microsoft.Extensions.AI;

namespace DbDataKit.Agents;

/// <summary>One part of what is always given, with its size.</summary>
public sealed record BudgetPart(string Name, int Tokens);

/// <summary>How the always-given text compares with the agent's budget.</summary>
public sealed record BudgetReport(int Window, int BudgetTokens, int TotalTokens, IReadOnlyList<BudgetPart> Parts, string Basis)
{
    public bool Over => TotalTokens > BudgetTokens;

    /// <summary>The parts from the largest, as a refusal names them.</summary>
    public string Largest(int count = 3) => string.Join(", ", Parts.OrderByDescending(p => p.Tokens).Take(count).Select(p => $"{p.Name} ({p.Tokens} tokens)"));

    /// <summary>The sentence of a refusal at publish or promotion: what is over, by how much, and what is largest.</summary>
    public string Refusal(string agent) =>
        $"The agent '{agent}' gives {TotalTokens} tokens of instructions, tool definitions, skill descriptions and bound instructions before the conversation starts, over its budget of {BudgetTokens} tokens ({100 * BudgetTokens / Math.Max(Window, 1)}% of the model's {Window}-token window; counted with {Basis}). The largest parts are: {Largest()}.";
}

/// <summary>
/// The model's space (plan 27a AG11, plan 27 section 3.5b): what is always given (the agent's instructions, its tool definitions, its skill descriptions, the instructions and guardrails bound to its scopes) is counted and compared with a share of the model's
/// window; over it, a publish or promotion is refused naming what is largest. At run time only references and old conversation give way, never an instruction (<see cref="ContextFitter"/>).
/// </summary>
public static class ContextBudget
{
    /// <summary>The default share of the window for what is always given.</summary>
    public const double DefaultShare = 0.20;

    /// <param name="parts">What is always given, by name.</param>
    /// <param name="counter">Counts the tokens of each part.</param>
    /// <param name="windowTokens">The bound model's context window.</param>
    /// <param name="share">The share of the window the agent may fill with what is always given.</param>
    public static BudgetReport Check(IEnumerable<ContextSource> parts, int windowTokens, ITokenCounter counter, double share = DefaultShare)
    {
        var counted = parts.Select(p => new BudgetPart($"{p.Kind} {p.Id}", counter.Count(p.Text))).ToList();
        return new BudgetReport(windowTokens, (int)(windowTokens * share), counted.Sum(p => p.Tokens), counted, counter.Basis);
    }

    /// <summary>The tool definitions as the model is sent them: name, description and the schema of the arguments.</summary>
    public static IEnumerable<ContextSource> ToolDefinitions(IEnumerable<AgentTool> tools) =>
        tools.Select(t => ContextSource.Of("tool", t.Name, $"{t.Name}: {t.Description}\n{t.InputSchema.ToJsonString()}"));
}

/// <summary>What was kept of a context that did not fit, and what gave way.</summary>
public sealed record FitResult(IReadOnlyList<ContextSource> References, IReadOnlyList<ChatMessage> Conversation, string? Summary, IReadOnlyList<string> Dropped, bool InstructionsFit);

/// <summary>
/// What gives way when a turn does not fit the window (plan 27a AG11): first the references (the last found first), then the oldest turns of the conversation, summarised into one message that is recorded; never an instruction, a guardrail, a tool definition or
/// a skill description. When the instructions alone do not fit, nothing is dropped and <see cref="FitResult.InstructionsFit"/> says so: that is a refusal, not a cut.
/// </summary>
public static class ContextFitter
{
    /// <param name="alwaysTokens">The always-given text: instructions, tool definitions and so on.</param>
    /// <param name="windowTokens">The model's window.</param>
    /// <param name="answerReserve">Room left for the model's answer.</param>
    /// <param name="references">The references found for this turn, best first.</param>
    /// <param name="conversation">The conversation so far, oldest first.</param>
    /// <param name="counter">Counts the tokens.</param>
    /// <param name="summarize">Writes one message that stands for the turns that were cut (a model call in a host; a function in a test).</param>
    public static FitResult Fit(int alwaysTokens, int windowTokens, int answerReserve, IReadOnlyList<ContextSource> references, IReadOnlyList<ChatMessage> conversation, ITokenCounter counter, Func<IReadOnlyList<ChatMessage>, string> summarize)
    {
        var room = windowTokens - answerReserve - alwaysTokens;
        if (room < 0) return new FitResult(references, conversation, null, [], InstructionsFit: false);

        var keptReferences = references.ToList();
        var keptConversation = conversation.ToList();
        var dropped = new List<string>();
        int Used() => keptReferences.Sum(r => counter.Count(r.Text)) + keptConversation.Sum(m => counter.Count(m.Text ?? ""));

        while (Used() > room && keptReferences.Count > 0)
        {
            dropped.Add($"reference {keptReferences[^1].Id}");
            keptReferences.RemoveAt(keptReferences.Count - 1);
        }

        string? summary = null;
        var cut = new List<ChatMessage>();
        while (Used() > room && keptConversation.Count > 1)
        {
            cut.Add(keptConversation[0]);
            keptConversation.RemoveAt(0);
        }
        if (cut.Count > 0)
        {
            summary = summarize(cut);
            keptConversation.Insert(0, new ChatMessage(ChatRole.System, $"Summary of the earlier conversation: {summary}"));
            dropped.Add($"{cut.Count} earlier message(s), summarised");
        }
        return new FitResult(keptReferences, keptConversation, summary, dropped, InstructionsFit: true);
    }
}
