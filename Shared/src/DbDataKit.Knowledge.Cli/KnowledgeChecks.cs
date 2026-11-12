using DbDataKit.Agents;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Knowledge.Cli;

/// <summary>What the check needs to know about one agent to say whether knowledge would push it over its budget: the agent, what it is given before any knowledge (its instructions, tool definitions and skill descriptions), its model's window and the knowledge it already draws on.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="WindowTokens">The context window of the model its slot is bound to.</param>
/// <param name="FixedTokens">The tokens of what the agent is given before any knowledge: its instructions, tool definitions and skill descriptions, counted as <see cref="ModelSpace"/> counts them.</param>
/// <param name="OtherSources">The knowledge the agent has besides the project being checked (the install's, the workspace's project).</param>
public sealed record AgentBudgetInput(AgentDefinition Agent, int FixedTokens, int WindowTokens, IReadOnlyList<KnowledgeSource> OtherSources);

/// <summary>What a project would add to one agent's always-given text.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Budget">What it may be given always: a share of its model's window.</param>
/// <param name="PushesOver">The project takes it over the budget (it was under before).</param>
/// <param name="Items">The items of the project that reach it and are always given.</param>
/// <param name="Adds">Tokens of the instructions, guardrails and always-given references of the project that reach the agent.</param>
/// <param name="Used">Tokens the agent is given before the project (its other knowledge included).</param>
public sealed record AgentReach(string Agent, int Adds, int Used, int Budget, bool PushesOver, IReadOnlyList<string> Items);

/// <summary>The result of checking a knowledge project: every diagnostic with its file and line, and what each agent would be given.</summary>
public sealed record KnowledgeCheckReport(KnowledgeProject Project, IReadOnlyList<ConfigDiagnostic> Diagnostics, IReadOnlyList<AgentReach> Reaches)
{
    public bool Ok => !Diagnostics.HasErrors();
}

/// <summary>
/// <c>knowledge check</c> and the checks of a publish (plan 27a KN10, KN17): the strict reader's diagnostics (format, roles, a bad value), every subject against the catalog (<c>DBD-KNW-010</c>, at the subject's line), the owner of an instruction or guardrail against the groups that
/// exist (<c>DBD-KNW-030</c>) and, for every agent the project reaches, the budget of what is always given (<c>DBD-KNW-040</c>: an error that names the agent and the largest item the project adds; <c>DBD-KNW-041</c>: a warning when the agent is over before the project adds anything).
/// </summary>
public static class KnowledgeChecks
{
    public const string UnknownOwner = "DBD-KNW-030";
    public const string OverBudget = "DBD-KNW-040";
    public const string AlreadyOver = "DBD-KNW-041";

    /// <param name="project">The project as read (its own diagnostics are part of the result).</param>
    /// <param name="sourceName">What the agents call the project in <c>knowledge.from</c>: <c>install</c> or <c>project</c>.</param>
    /// <param name="catalog">What exists to be about; null checks no subject.</param>
    /// <param name="ownerExists">Whether a group of the identity home exists; null checks no owner.</param>
    /// <param name="agents">The agents whose budget is checked; none checks none.</param>
    /// <param name="counter">How tokens are counted; the estimate of plan 27 SD15 by default.</param>
    /// <param name="translate">Translates the semantic SQL of a verified query so it can be compared with its request (<c>DBD-KNW-051</c>, <c>-052</c>); null checks no SQL.</param>
    public static KnowledgeCheckReport Check(KnowledgeProject project, string sourceName, ISubjectCatalog? catalog, Func<string, bool>? ownerExists, IReadOnlyList<AgentBudgetInput> agents, ITokenCounter? counter = null, SqlToRequest? translate = null)
    {
        counter ??= EstimateCounter.Instance;
        var diagnostics = new List<ConfigDiagnostic>(project.Diagnostics);
        foreach (var item in project.Items)
        {
            // a verified query: the semantic SQL translates and says what the request says (the request is the truth, KN21)
            if (item.Verified is { SemanticSql: not null } verified && translate is not null && VerifiedQueries.Resolve(verified, translate) is { Ok: false } refused)
                diagnostics.Add(new ConfigDiagnostic(refused.Error!.StartsWith("The semantic SQL and", StringComparison.Ordinal) ? VerifiedQueries.SqlDisagrees : VerifiedQueries.SqlTranslates, ConfigSeverity.Error, item.File, 1, 1, refused.Error, "Make the SQL say what the request says, or remove the SQL."));
            if (catalog is not null) diagnostics.AddRange(SubjectResolver.CheckOnSave(item, catalog));
            if (ownerExists is not null && item.Owner is { Length: > 0 } owner && !ownerExists(owner))
                diagnostics.Add(new ConfigDiagnostic(UnknownOwner, ConfigSeverity.Error, item.File, 1, 1, $"The owner '{owner}' of '{item.Id}' is not a group of the identity home.", "Name a group that exists, or create it."));
        }

        var reaches = new List<AgentReach>();
        foreach (var input in agents)
        {
            var reach = Reach(project, sourceName, catalog, input, counter, out var largest);
            if (reach is null) continue;
            reaches.Add(reach);
            if (reach.PushesOver)
            {
                var item = project.Find(largest!)!;
                diagnostics.Add(new ConfigDiagnostic(OverBudget, ConfigSeverity.Error, item.File, item.BodyLine, 1,
                    $"The agent '{input.Agent.Name}' would be over its budget of {reach.Budget} tokens: it is given {reach.Used} before this project and the project adds {reach.Adds}. The largest item it adds is '{item.Id}' ({counter.Count(Text(item))} tokens).",
                    "Shorten the instruction, make it a reference (found by search, never always given), or narrow its scope or audience."));
            }
            else if (reach.Used > reach.Budget && reach.Adds > 0)
                diagnostics.Add(new ConfigDiagnostic(AlreadyOver, ConfigSeverity.Warning, project.Root, 0, 0, $"The agent '{input.Agent.Name}' is over its budget of {reach.Budget} tokens before this project adds anything ({reach.Used} tokens)."));
        }
        return new KnowledgeCheckReport(project, diagnostics, reaches);
    }

    private static string Text(KnowledgeItem item) => $"### {item.Title}\n{item.Body.Trim()}";

    private static AgentReach? Reach(KnowledgeProject project, string sourceName, ISubjectCatalog? catalog, AgentBudgetInput input, ITokenCounter counter, out string? largest)
    {
        largest = null;
        var inPlay = AgentScopes.InPlay(input.Agent);
        IReadOnlyList<ContextSource> Space(IReadOnlyList<KnowledgeSource> sources) =>
            ModelSpace.AlwaysGiven([], [], [], BoundKnowledgeProvider.AlwaysGiven(input.Agent, sources), BoundKnowledgeProvider.Bind(input.Agent, sources, inPlay, catalog));
        int Tokens(IEnumerable<ContextSource> parts) => input.FixedTokens + parts.Sum(p => counter.Count(p.Text));

        var before = Space([.. input.OtherSources.Where(s => s.Name != sourceName)]);
        var withProject = Space([.. input.OtherSources.Where(s => s.Name != sourceName), new KnowledgeSource(sourceName, project.Items)]);
        var budget = (int)(input.WindowTokens * ContextBudget.DefaultShare);
        var without = Tokens(before);
        var with = Tokens(withProject);
        var ours = withProject.Where(p => project.Find(p.Id) is not null && p.Kind is "bound instruction" or "guardrail" or "reference").Select(p => (p.Id, Tokens: counter.Count(p.Text))).ToList();
        if (ours.Count == 0) return null;
        largest = ours.OrderByDescending(o => o.Tokens).ThenBy(o => o.Id, StringComparer.Ordinal).First().Id;
        return new AgentReach(input.Agent.Name, with - without, without, budget, with > budget && without <= budget, [.. ours.Select(o => o.Id)]);
    }
}
