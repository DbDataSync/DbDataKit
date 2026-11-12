using System.Text;

namespace DbDataKit.Cli;

/// <summary>
/// The suite-wide rule for every command a product's router offers (plan 27a AG01, plan 16's "no feature without a spec"): a verb has a summary a person can read in <c>--help</c> and an agent as the tool's description, a usage line, a JSON schema for its data (<see cref="DataSchemas.None"/>
/// when it has none) and at least one trait, so what it may touch is declared and nothing is offered to an agent by accident. <see cref="Audit(string, NounRouter)"/> lists what breaks the rule naming the product and the verb; <see cref="Golden"/> is the list of what the router offers to MCP, one line per tool with its
/// traits, which a product's test keeps equal to a file in its repository so that offering a command to agents, or changing what it may touch, is a change someone sees in a review.
/// </summary>
public static class CommandSurface
{
    /// <summary>
    /// The same audit of a router a product makes: when the router refuses to be made (the router itself refuses a verb with no data schema, a verb that is its own noun's twin and so on), the refusal is the problem, with the product's name in front of it.
    /// </summary>
    public static IReadOnlyList<string> Audit(string product, Func<NounRouter> router)
    {
        NounRouter made;
        try { made = router(); }
        catch (ArgumentException ex) { return [$"{product}: {ex.Message}"]; }
        return Audit(product, made);
    }

    /// <summary>What is wrong with the verbs of a router, one sentence each, naming the product and the verb. Empty when every verb has what the rule asks.</summary>
    public static IReadOnlyList<string> Audit(string product, NounRouter router)
    {
        var problems = new List<string>();
        foreach (var noun in router.Nouns)
        {
            if (string.IsNullOrWhiteSpace(noun.Summary)) problems.Add($"{product}: the noun `{noun.Name}` has no summary.");
            foreach (var verb in noun.Verbs)
            {
                var command = $"{product} {noun.Name} {verb.Name}";
                if (string.IsNullOrWhiteSpace(verb.Summary)) problems.Add($"{command}: has no summary (the help line and the tool's description).");
                if (verb.Usage is null) problems.Add($"{command}: has no usage text (an empty one says it takes no arguments).");
                if (string.IsNullOrWhiteSpace(verb.DataSchema)) problems.Add($"{command}: has no schema for its data (DataSchemas.None says it has none).");
                if (verb.EffectiveTraits == Traits.None) problems.Add($"{command}: declares no trait, so what it may touch is not said.");
            }
        }
        return problems;
    }

    /// <summary>
    /// The tools the router offers to MCP under <paramref name="options"/>, one line each in the router's order: <c>tool_name  traits  [verb summary]</c>. A product's test compares it with a file; <see cref="McpServer.Offered"/> is the rule the MCP server and the agent runtime both use.
    /// </summary>
    public static string Golden(NounRouter router, McpOptions options)
    {
        var text = new StringBuilder();
        foreach (var (name, _, verb) in McpServer.Offered(router, options))
            text.Append(name).Append("  ").Append(verb.EffectiveTraits.Names().Count == 0 ? "-" : string.Join(',', verb.EffectiveTraits.Names())).Append('\n');
        return text.ToString();
    }
}
