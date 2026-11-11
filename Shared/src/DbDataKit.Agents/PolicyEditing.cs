using System.Globalization;
using System.Text;
using DbDataKit.Cli;

namespace DbDataKit.Agents;

/// <summary>One difference between two policies, said in a sentence: what changes for the agents of the install, and whether it makes the install less strict.</summary>
public sealed record PolicyChange(string Setting, bool Loosens, string Text);

/// <summary>
/// The editor of the install policy (plan 27a AG17): writes an <see cref="AgentPolicy"/> as the <c>agent-policy/1</c> file the loader reads (the round trip is a test) and says what a change means. A change that makes the install <b>less strict</b> (a lower
/// minimum, a limit raised or removed, a data rule relaxed) is listed with its effect in a sentence, and the host asks the person to confirm it and records who did (plan 27 section 4, the security posture).
/// </summary>
public static class PolicyEditing
{
    public static string Write(AgentPolicy policy)
    {
        var text = new StringBuilder("agent-policy: 1\n");
        var minimums = policy.Prompting.Minimums.OrderBy(m => (int)m.Key).ToList();
        var tools = policy.Prompting.Tools.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        if (minimums.Count > 0 || tools.Count > 0)
        {
            text.Append("prompting:\n");
            if (minimums.Count > 0)
            {
                text.Append("  minimums:\n");
                foreach (var (trait, setting) in minimums) text.Append("    ").Append(trait.Names().First()).Append(": ").Append(ToolPromptingReport.Name(setting)).Append('\n');
            }
            if (tools.Count > 0)
            {
                text.Append("  tools:\n");
                foreach (var (tool, setting) in tools) text.Append("    ").Append(tool).Append(": ").Append(ToolPromptingReport.Name(setting)).Append('\n');
            }
        }

        var limits = policy.Limits;
        var scopes = new[] { ("conversation", limits.Conversation), ("person", limits.Person), ("install", limits.Install) }.Where(s => !s.Item2.IsNone).ToList();
        if (scopes.Count > 0 || limits.NotifyAtPercent != 80)
        {
            text.Append("limits:\n");
            foreach (var (name, set) in scopes)
            {
                text.Append("  ").Append(name).Append(":\n");
                if (set.Turns is { } turns) text.Append("    turns: ").Append(turns.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (set.ToolCalls is { } calls) text.Append("    tool-calls: ").Append(calls.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (set.Tokens is { } tokens) text.Append("    tokens: ").Append(tokens.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (set.Cost is { } cost) text.Append("    cost: ").Append(cost.ToString("0.00##", CultureInfo.InvariantCulture)).Append('\n');
            }
            if (limits.NotifyAtPercent != 80) text.Append("  notify-at: ").Append(limits.NotifyAtPercent.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        if (policy.CloudReadsData != DataRule.Allow)
            text.Append("data-rules:\n  cloud-providers:\n    reads-data: ").Append(AgentPolicyLoader.DataRuleNames[(int)policy.CloudReadsData]).Append('\n');
        return text.ToString();
    }

    /// <summary>What changes from <paramref name="before"/> to <paramref name="after"/>, loosenings first.</summary>
    public static IReadOnlyList<PolicyChange> Describe(AgentPolicy before, AgentPolicy after)
    {
        var changes = new List<PolicyChange>();

        foreach (var trait in before.Prompting.Minimums.Keys.Concat(after.Prompting.Minimums.Keys).Distinct().OrderBy(t => (int)t))
        {
            var name = trait.Names().First();
            before.Prompting.Minimums.TryGetValue(trait, out var was);
            after.Prompting.Minimums.TryGetValue(trait, out var now);
            var hadBefore = before.Prompting.Minimums.ContainsKey(trait);
            var hasNow = after.Prompting.Minimums.ContainsKey(trait);
            if (hadBefore == hasNow && was == now) continue;
            var from = hadBefore ? ToolPromptingReport.Name(was) : "none";
            var to = hasNow ? ToolPromptingReport.Name(now) : "none";
            var loosens = hadBefore && (!hasNow || PromptingResolver.Strictness(now) < PromptingResolver.Strictness(was));
            changes.Add(new PolicyChange($"minimum for {name}", loosens,
                loosens ? $"Lowers the minimum for {name} from {from} to {to}: {Effect(now, hasNow, name)}"
                : hadBefore ? $"Raises the minimum for {name} from {from} to {to}: {ToolPromptingReport.Meaning(now)}" : $"Sets a minimum for {name}: {ToolPromptingReport.Meaning(now)}"));
        }

        foreach (var tool in before.Prompting.Tools.Keys.Concat(after.Prompting.Tools.Keys).Distinct().OrderBy(t => t, StringComparer.Ordinal))
        {
            var hadBefore = before.Prompting.Tools.TryGetValue(tool, out var was);
            var hasNow = after.Prompting.Tools.TryGetValue(tool, out var now);
            if (hadBefore == hasNow && was == now) continue;
            var loosens = hadBefore && (!hasNow || PromptingResolver.Strictness(now) < PromptingResolver.Strictness(was));
            changes.Add(new PolicyChange($"setting for {tool}", loosens,
                !hasNow ? $"Removes the install's setting for {tool} ({ToolPromptingReport.Name(was)}): the tool's own default and the minimums for its traits apply."
                : loosens ? $"Lowers the install's setting for {tool} from {ToolPromptingReport.Name(was)} to {ToolPromptingReport.Name(now)}: {ToolPromptingReport.Meaning(now)}"
                : $"Sets {tool} to {ToolPromptingReport.Name(now)}: {ToolPromptingReport.Meaning(now)}"));
        }

        foreach (var (scope, was, now) in new[] { ("conversation", before.Limits.Conversation, after.Limits.Conversation), ("person", before.Limits.Person, after.Limits.Person), ("install", before.Limits.Install, after.Limits.Install) })
        {
            Compare(changes, scope, "model turns", was.Turns, now.Turns);
            Compare(changes, scope, "tool calls", was.ToolCalls, now.ToolCalls);
            Compare(changes, scope, "tokens", was.Tokens, now.Tokens);
            Compare(changes, scope, "cost", was.Cost, now.Cost);
        }

        if (before.Limits.NotifyAtPercent != after.Limits.NotifyAtPercent)
        {
            var loosens = after.Limits.NotifyAtPercent > before.Limits.NotifyAtPercent;
            changes.Add(new PolicyChange("notify at", loosens, loosens
                ? $"Raises the point at which administrators are told from {before.Limits.NotifyAtPercent}% to {after.Limits.NotifyAtPercent}% of a limit: they hear later."
                : $"Lowers the point at which administrators are told from {before.Limits.NotifyAtPercent}% to {after.Limits.NotifyAtPercent}% of a limit: they hear sooner."));
        }

        if (before.CloudReadsData != after.CloudReadsData)
        {
            var loosens = after.CloudReadsData < before.CloudReadsData;   // Allow = 0 is the loosest
            var rule = AgentPolicyLoader.DataRuleNames[(int)after.CloudReadsData];
            changes.Add(new PolicyChange("data rule for cloud providers", loosens, after.CloudReadsData switch
            {
                DataRule.Allow => "Lets an agent bound to a cloud provider run the commands that read data, as any other agent: rows and values can reach the provider.",
                DataRule.Ask => "An agent bound to a cloud provider asks the person every time before it runs a command that reads data.",
                _ => "An agent bound to a cloud provider cannot run a command that reads data: those tools are left out.",
            } + $" (reads-data: {rule})"));
        }

        return [.. changes.OrderByDescending(c => c.Loosens).ThenBy(c => c.Setting, StringComparer.Ordinal)];
    }

    private static string Effect(Prompting setting, bool has, string trait) => !has
        ? $"agents may then set what any tool with that trait asks, down to the tool's own default."
        : setting == Prompting.AskOnce ? $"a tool that {Verb(trait)} then asks once per conversation instead of every time."
        : $"a tool that {Verb(trait)} then runs without asking.";

    private static string Verb(string trait) => trait switch
    {
        "reads-definitions" => "reads definitions",
        "reads-data" => "reads data",
        "writes-definitions" => "writes definitions or files",
        "writes-data" => "writes data",
        "changes-structure" => "changes structure",
        "deletes" => "deletes",
        "outward" => "sends something outside the suite",
        "elevated" => "holds elevated access",
        _ => $"has the trait {trait}",
    };

    private static void Compare<T>(List<PolicyChange> changes, string scope, string unit, T? was, T? now) where T : struct, IComparable<T>
    {
        if (Nullable.Equals(was, now)) return;
        var loosens = was is not null && (now is null || now.Value.CompareTo(was.Value) > 0);
        var text = now is null ? $"Removes the {scope} limit on {unit} ({Show(was)}): there is no limit on {unit} for the {scope}."
            : was is null ? $"Sets a {scope} limit on {unit}: {Show(now)}."
            : $"{(loosens ? "Raises" : "Lowers")} the {scope} limit on {unit} from {Show(was)} to {Show(now)}.";
        changes.Add(new PolicyChange($"{scope} limit on {unit}", loosens, text));
    }

    private static string Show<T>(T? value) where T : struct => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
