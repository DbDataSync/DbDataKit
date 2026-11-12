using DbDataKit.Knowledge;

namespace DbDataKit.Agents;

/// <summary>What an agent's declared scopes put in play before it has done anything (plan 27a KN34, KN10): each model, project, connection, app or integration it names, whole.</summary>
public static class AgentScopes
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["semantic-models"] = "semantic-model", ["store-models"] = "store-model", ["build-projects"] = "build-project", ["connections"] = "connection", ["apps"] = "app", ["integrations"] = "integration",
    };

    public static IReadOnlyList<InPlay> InPlay(AgentDefinition definition) =>
        [.. definition.Scopes.Where(s => !s.All && Types.ContainsKey(s.Kind)).SelectMany(s => s.Names.Select(n => new InPlay(new KnowledgeSubject(Types[s.Kind], [n.Split(':')[0].Split('.')[0]]), Whole: true)))];
}
