using DbDataKit.Cli;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Agents;

/// <summary>
/// What exists, for <see cref="AgentChecker"/>: the tools (by the name the runtime gives them, with their traits), the skills and agents by name, and a way to ask whether a name in a scope exists (<c>semantic-models</c> and <c>Sales</c>).
/// </summary>
public sealed record AgentCatalog(IReadOnlyDictionary<string, Traits> Tools, IReadOnlySet<string> Skills, IReadOnlySet<string> Agents, Func<string, string, bool> ScopeNameExists)
{
    /// <summary>A catalog of the tools a router offers (with <c>--allow-writes</c>: every command exists, the gate decides what runs), plus the skills and agents named.</summary>
    public static AgentCatalog Of(NounRouter router, IEnumerable<string>? skills = null, IEnumerable<string>? agents = null, Func<string, string, bool>? scopeNameExists = null) =>
        new(McpServer.Offered(router, new McpOptions(new HashSet<string>(), AllowWrites: true)).ToDictionary(t => t.Name, t => t.Verb.EffectiveTraits, StringComparer.Ordinal),
            new HashSet<string>(skills ?? [], StringComparer.Ordinal), new HashSet<string>(agents ?? [], StringComparer.Ordinal), scopeNameExists ?? ((_, _) => true));
}

/// <summary>
/// <c>agent check</c> (plan 27a AG09): before anyone talks to an agent, everything that can be checked is. Every tool, skill and subagent it names exists, every scope resolves, and no tool's prompting setting is below the install policy's minimum.
/// Each problem is a diagnostic at the line where the agent file names it, with a suggestion. Codes: <c>DBD-AGT-020</c> a tool that does not exist, <c>-021</c> a prompting setting below the policy's minimum, <c>-022</c> a skill that does not exist,
/// <c>-023</c> a subagent that does not exist (or is the agent itself), <c>-024</c> a scope that does not resolve.
/// </summary>
public static class AgentChecker
{
    public const string UnknownTool = "DBD-AGT-020";
    public const string BelowMinimum = "DBD-AGT-021";
    public const string UnknownSkill = "DBD-AGT-022";
    public const string UnknownSubagent = "DBD-AGT-023";
    public const string UnresolvedScope = "DBD-AGT-024";

    public static IReadOnlyList<ConfigDiagnostic> Check(AgentDefinition definition, string file, AgentCatalog catalog, AgentPolicy? policy = null)
    {
        policy ??= AgentPolicy.None;
        var diags = new List<ConfigDiagnostic>();
        ConfigDiagnostic At(string code, int line, int column, string message, string? fix = null) => DefaultDiagnosticCatalog.Instance.Create(code, file, Math.Max(line, 1), Math.Max(column, 1), message, fix);

        foreach (var tool in definition.Tools)
        {
            if (!catalog.Tools.TryGetValue(tool.ToolName, out var traits))
            {
                var near = DidYouMean.Closest(tool.ToolName, catalog.Tools.Keys);
                diags.Add(At(UnknownTool, tool.Line, tool.Column, $"The {tool.Kind} `{tool.Name}` does not exist." + (near is not null ? $" Did you mean `{near.Replace('_', ' ')}`?" : ""), "Check the spelling against the command's help, or publish the action first."));
                continue;
            }
            if (tool.Prompting is { } asked)
            {
                Prompting? named = policy.Prompting.Tools.TryGetValue(tool.ToolName, out var byName) ? byName : null;
                var floor = PromptingResolver.Strictest([named, .. policy.Prompting.Minimums.Where(m => traits.HasFlag(m.Key)).Select(m => (Prompting?)m.Value)]);
                if (PromptingResolver.Strictness(asked) < PromptingResolver.Strictness(floor))
                    diags.Add(At(BelowMinimum, tool.Line, tool.Column, $"`{tool.Name}` asks for `{Name(asked)}`, which is below the install policy's minimum `{Name(floor)}` for what it does ({string.Join(", ", traits.Names())}).", $"Use `{Name(floor)}` or stricter; only an administrator can lower the minimum."));
            }
        }
        foreach (var (skill, index) in definition.Skills.Select((s, i) => (s, i)))
            if (!catalog.Skills.Contains(skill))
                diags.Add(At(UnknownSkill, 1, 1, $"The skill `{skill}` does not exist." + (DidYouMean.Closest(skill, catalog.Skills) is { } n ? $" Did you mean `{n}`?" : ""), $"Skills: {string.Join(", ", catalog.Skills.Order())}.") with { KeyPath = $"skills[{index}]" });
        foreach (var (sub, index) in definition.Subagents.Select((s, i) => (s, i)))
        {
            if (string.Equals(sub, definition.Name, StringComparison.Ordinal))
                diags.Add(At(UnknownSubagent, 1, 1, $"The agent `{sub}` cannot be its own subagent.") with { KeyPath = $"subagents[{index}]" });
            else if (!catalog.Agents.Contains(sub))
                diags.Add(At(UnknownSubagent, 1, 1, $"The subagent `{sub}` does not exist." + (DidYouMean.Closest(sub, catalog.Agents) is { } n ? $" Did you mean `{n}`?" : ""), $"Agents: {string.Join(", ", catalog.Agents.Order())}.") with { KeyPath = $"subagents[{index}]" });
        }
        foreach (var scope in definition.Scopes)
            foreach (var name in scope.Names)
            {
                var bare = scope.Kind == "store-models" && name.Contains(':') ? name[..name.LastIndexOf(':')] : name;
                if (!catalog.ScopeNameExists(scope.Kind, bare))
                    diags.Add(At(UnresolvedScope, scope.Line, scope.Column, $"The scope `{scope.Kind}` names `{bare}`, which does not exist (or this install cannot see it).", "Check the name against the catalog, or use `all`."));
            }
        return diags;
    }

    private static string Name(Prompting setting) => AgentDefinitionLoader.PromptingNames[(int)setting];
}
