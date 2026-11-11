using DbDataKit.Knowledge;

namespace DbDataKit.Agents;

/// <summary>A place knowledge is kept for an agent: <c>install</c> (the install's knowledge project), <c>project</c> (the project its workspace is on), <c>agent</c> (the agent's own <c>knowledge/</c> folder). An agent's <c>knowledge.from</c> names them.</summary>
public sealed record KnowledgeSource(string Name, IReadOnlyList<KnowledgeItem> Items);

/// <summary>
/// Which knowledge an agent is given for what is in play (plan 27a KN34, plan 27 section 3.5): the agent's <c>knowledge: { from, exclude, always }</c> narrows the sources, <see cref="ScopeMatcher"/> chooses by scope and audience, and the install may give
/// an item to every agent whatever its own <c>for</c> says.
/// </summary>
public static class BoundKnowledgeProvider
{
    /// <param name="agent">The agent: its audience and name decide which items are for it, its <c>knowledge</c> which sources it draws on.</param>
    /// <param name="sources">Where knowledge is kept.</param>
    /// <param name="inPlay">What is in play now.</param>
    /// <param name="catalog">When given, orphaned items are left out.</param>
    /// <param name="audience">Who asks, when it is not the agent's own audience and name.</param>
    /// <param name="giveToEveryone">Items the install policy gives every agent whatever their own <c>for</c> says (ids of items in <paramref name="sources"/>).</param>
    public static BoundKnowledge Bind(AgentDefinition agent, IReadOnlyList<KnowledgeSource> sources, IReadOnlyList<InPlay> inPlay, ISubjectCatalog? catalog = null, ScopeAudience? audience = null, IReadOnlySet<string>? giveToEveryone = null)
    {
        audience ??= new ScopeAudience(agent.Audience == AgentAudience.BackEnd ? "back-end" : "front-end", agent.Name);
        var from = agent.Knowledge.From;
        var chosen = sources
            .Where(s => from.Count == 0 || from.Contains(s.Name, StringComparer.Ordinal))
            .Where(s => !agent.Knowledge.Exclude.Contains(s.Name, StringComparer.Ordinal))
            .SelectMany(s => s.Items)
            .Where(i => !agent.Knowledge.Exclude.Contains(i.Id, StringComparer.Ordinal))
            .ToList();
        var bound = ScopeMatcher.Match(chosen, inPlay, audience, catalog);
        if (giveToEveryone is null || giveToEveryone.Count == 0) return bound;

        // an item the install gives to everyone applies whatever its own audience says; it is still scoped by what it is about
        var everyone = chosen.Where(i => giveToEveryone.Contains(i.Id))
            .Select(i => i with { For = KnowledgeAudience.Everyone }).ToList();
        var extra = ScopeMatcher.Match(everyone, inPlay, audience, catalog);
        return Merge(bound, extra, inPlay);
    }

    private static BoundKnowledge Merge(BoundKnowledge a, BoundKnowledge b, IReadOnlyList<InPlay> inPlay)
    {
        List<KnowledgeItem> Both(IEnumerable<KnowledgeItem> x, IEnumerable<KnowledgeItem> y) => x.Concat(y).GroupBy(i => i.Id).Select(g => g.First())
            .OrderBy(i => ScopeMatcher.RankOf(i, inPlay) ?? 0).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        return new BoundKnowledge(Both(a.Instructions, b.Instructions), Both(a.Guardrails, b.Guardrails));
    }

    /// <summary>The references an agent is always given (its <c>knowledge.always</c>), as context parts counted against its budget.</summary>
    public static IReadOnlyList<ContextSource> AlwaysGiven(AgentDefinition agent, IReadOnlyList<KnowledgeSource> sources) =>
        [.. agent.Knowledge.Always.Select(id => sources.SelectMany(s => s.Items).FirstOrDefault(i => i.Id == id)).Where(i => i is not null)
            .Select(i => new ContextSource("reference", i!.Id, i.Hash[..12], $"### {i.Title}\n{i.Body.Trim()}"))];
}

/// <summary>
/// What is in play, worked out from what the agent does, never from a guess about what the person meant (plan 27 section 3.5a): the workspace's project is in play from the start, and a tool call or its result that names a model or a member puts it in play
/// from that turn on. A host registers what each tool's call and result name; the tracker keeps the union.
/// </summary>
public sealed class InPlayTracker
{
    private readonly List<InPlay> _inPlay = [];
    private readonly List<Func<ToolCall, ToolOutcome, IEnumerable<InPlay>>> _extractors = [];

    public InPlayTracker(IEnumerable<InPlay>? start = null) => _inPlay.AddRange(start ?? []);

    public IReadOnlyList<InPlay> Current => [.. _inPlay];

    /// <summary>Says what a call and its result put in play (for example, a Build command that shows a semantic model names it and its measures).</summary>
    public InPlayTracker When(Func<ToolCall, ToolOutcome, IEnumerable<InPlay>> extractor) { _extractors.Add(extractor); return this; }

    /// <summary>Takes in what a finished call put in play.</summary>
    public void Observe(ToolCall call, ToolOutcome outcome)
    {
        foreach (var extractor in _extractors)
            foreach (var play in extractor(call, outcome))
                if (!_inPlay.Any(p => p.Subject.Unpositioned() == play.Subject.Unpositioned() && p.Whole == play.Whole)) _inPlay.Add(play);
    }
}
