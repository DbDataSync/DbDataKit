using DbDataKit.Cli;

namespace DbDataKit.Agents;

/// <summary>
/// What one tool will ask, and from which layer (plan 27a AG30). <see cref="Effective"/> is what <see cref="PromptingResolver.Resolve"/> gives, the same function the runtime's gate uses, so the screen cannot say something the run does not do.
/// <see cref="Layer"/> names the layer that sets it (<c>install policy: tool</c>, <c>install policy: minimum for writes-data</c>, <c>agent</c>, <c>tool's own default</c>, <c>traits</c>); <see cref="Warning"/> is set when the agent's own setting is below what a higher layer needs, so it has no effect.
/// </summary>
public sealed record ToolPromptingRow(string Tool, string Source, IReadOnlyList<string> Traits, Prompting Own, string OwnFrom, Prompting? PolicyForTool, Prompting? PolicyMinimum, string? PolicyMinimumTrait, Prompting? Agent,
    Prompting Effective, string Layer, string? Warning);

public static class ToolPromptingReport
{
    /// <summary>One row per tool, in the order given.</summary>
    public static IReadOnlyList<ToolPromptingRow> Build(IEnumerable<AgentTool> tools, PromptingPolicy policy, IReadOnlyDictionary<string, Prompting>? agentSettings)
    {
        var rows = new List<ToolPromptingRow>();
        foreach (var tool in tools)
        {
            var own = tool.DeclaredPrompting ?? PromptingResolver.ByTraits(tool.Traits);
            Prompting? forTool = policy.Tools.TryGetValue(tool.Name, out var byName) ? byName : null;
            var floors = policy.Minimums.Where(m => tool.Traits.HasFlag(m.Key)).OrderByDescending(m => PromptingResolver.Strictness(m.Value)).ThenBy(m => (int)m.Key).ToList();
            Prompting? minimum = floors.Count > 0 ? floors[0].Value : null;
            var minimumTrait = floors.Count > 0 ? string.Join(", ", floors[0].Key.Names()) : null;
            Prompting? agent = agentSettings is not null && agentSettings.TryGetValue(tool.Name, out var s) ? s : null;
            var effective = PromptingResolver.Resolve(tool, policy, agent);
            var strength = PromptingResolver.Strictness(effective);

            // the layer that gets the credit: the highest authority that already asks this much
            string layer;
            if (forTool is { } t && PromptingResolver.Strictness(t) == strength) layer = "install policy: this tool";
            else if (minimum is { } m && PromptingResolver.Strictness(m) == strength) layer = $"install policy: minimum for {minimumTrait}";
            else if (agent is { } a && PromptingResolver.Strictness(a) == strength) layer = "agent";
            else layer = tool.DeclaredPrompting is not null ? "the tool's own default" : "traits";

            string? warning = null;
            if (agent is { } mine && PromptingResolver.Strictness(mine) < strength)
            {
                var from = layer.StartsWith("install policy", StringComparison.Ordinal) ? $"the {layer}" : layer == "agent" ? "the agent" : layer;
                warning = $"The agent's setting '{Name(mine)}' for {tool.Name} is below what {from} needs ({Name(effective)}), so '{Name(effective)}' applies. Remove the setting or raise it.";
            }
            rows.Add(new ToolPromptingRow(tool.Name, tool.Source, [.. tool.Traits.Names()], own, tool.DeclaredPrompting is not null ? "declared by the tool" : "its traits", forTool, minimum, minimumTrait, agent, effective, layer, warning));
        }
        return rows;
    }

    /// <summary>The setting as the files write it.</summary>
    public static string Name(Prompting setting) => AgentDefinitionLoader.PromptingNames[(int)setting];

    /// <summary>What each setting means, in a sentence a person reads beside it.</summary>
    public static string Meaning(Prompting setting) => setting switch
    {
        Prompting.Ask => "The person is asked every time before it runs.",
        Prompting.AskOnce => "The person is asked the first time in a conversation; after that it runs.",
        _ => "It runs without asking.",
    };
}

/// <summary>One kind of text in the always-given part of a context, with its size and its share of the model's window (plan 27a AG31).</summary>
public sealed record ModelSpaceGroup(string Name, int Tokens, double Share);

/// <summary>The model's space for one agent: what is always given, per kind and per part, against the budget, how it was counted and, when over, the refusal that names what is largest (plan 27a AG31, AG11).</summary>
public sealed record ModelSpaceView(string Agent, int Window, int BudgetTokens, int TotalTokens, bool Over, double UsedShareOfBudget, string Basis, IReadOnlyList<ModelSpaceGroup> Groups, IReadOnlyList<BudgetPart> Parts, string? Refusal);

public static class ModelSpace
{
    /// <summary>The always-given parts of an agent as the budget counts them: its instructions, its tool definitions, its skill descriptions, the references it always has, and the instructions and guardrails bound to what it works on.</summary>
    public static IReadOnlyList<ContextSource> AlwaysGiven(IEnumerable<ContextSource> instructions, IEnumerable<AgentTool> tools, IEnumerable<SkillOffer> skills, IEnumerable<ContextSource> always, DbDataKit.Knowledge.BoundKnowledge bound) =>
    [
        .. instructions,
        .. ContextBudget.ToolDefinitions(tools),
        .. skills.Select(s => new ContextSource("skill", s.Name, s.Version, $"- {s.Name}: {s.Description}")),
        .. always,
        .. bound.Instructions.Select(i => new ContextSource("bound instruction", i.Id, i.Hash[..12], $"### {i.Title}\n{i.Body.Trim()}")),
        .. bound.Guardrails.Select(i => new ContextSource("guardrail", i.Id, i.Hash[..12], $"### {i.Title}\n{i.Body.Trim()}")),
    ];

    public static ModelSpaceView Build(string agent, IEnumerable<ContextSource> parts, int windowTokens, ITokenCounter counter, double share = ContextBudget.DefaultShare)
    {
        var list = parts.ToList();
        var report = ContextBudget.Check(list, windowTokens, counter, share);
        var groups = list.GroupBy(p => p.Kind, StringComparer.Ordinal)
            .Select(g => new ModelSpaceGroup(g.Key, g.Sum(p => counter.Count(p.Text)), windowTokens == 0 ? 0 : (double)g.Sum(p => counter.Count(p.Text)) / windowTokens))
            .OrderByDescending(g => g.Tokens).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        return new ModelSpaceView(agent, windowTokens, report.BudgetTokens, report.TotalTokens, report.Over, report.BudgetTokens == 0 ? 0 : (double)report.TotalTokens / report.BudgetTokens, counter.Basis, groups,
            [.. report.Parts.OrderByDescending(p => p.Tokens).ThenBy(p => p.Name, StringComparer.Ordinal)], report.Over ? report.Refusal(agent) : null);
    }
}

/// <summary>One skill as the screen shows it: what the model sees every turn (name and description), the size of its body against the room left, its version and the files that export it, path to text (plan 27a AG32).</summary>
public sealed record SkillView(string Name, string Description, string Version, string File, int BodyTokens, int DescriptionTokens, bool Fits, string? Refusal, IReadOnlyDictionary<string, string> ExportFiles);

public static class SkillViews
{
    /// <param name="skills">Each skill with the text of its file as it is exported.</param>
    /// <param name="windowTokens">The model's window.</param>
    /// <param name="alwaysGivenTokens">What is always given, the descriptions included.</param>
    /// <param name="answerReserve">Room for the answer.</param>
    /// <param name="counter">Counts the tokens.</param>
    public static IReadOnlyList<SkillView> Build(IEnumerable<(Skill Skill, string Original)> skills, int windowTokens, int alwaysGivenTokens, int answerReserve, ITokenCounter counter)
    {
        var room = windowTokens - alwaysGivenTokens - answerReserve;
        var views = new List<SkillView>();
        foreach (var (skill, original) in skills.OrderBy(s => s.Skill.Name, StringComparer.Ordinal))
        {
            var size = counter.Count(skill.Body);
            var fits = size <= room;
            var refusal = fits ? null : new SkillCatalog([skill]).CheckRoom(windowTokens, alwaysGivenTokens, answerReserve, counter);
            views.Add(new SkillView(skill.Name, skill.Description, skill.Version, skill.File, size, counter.Count($"- {skill.Name}: {skill.Description}"), fits, refusal, SkillReader.Export(skill, original)));
        }
        return views;
    }
}
