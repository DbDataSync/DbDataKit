using System.Globalization;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Agents;

/// <summary>What an agent bound to a cloud provider may do with data (plan 27 section 3.7): <see cref="Allow"/> it as any, <see cref="Ask"/> every time, or <see cref="Refuse"/> every verb that reads data.</summary>
public enum DataRule
{
    Allow,
    Ask,
    Refuse,
}

/// <summary>The limits of an install by scope, and the percentage of any limit at which administrators are told.</summary>
public sealed record PolicyLimits(LimitSet Conversation, LimitSet Person, LimitSet Install, int NotifyAtPercent)
{
    public static readonly PolicyLimits None = new(LimitSet.None, LimitSet.None, LimitSet.None, 80);
}

/// <summary>
/// The install policy, <c>agent-policy/1</c> (plan 27a AG09, plan 27 section 3.5): the settings that are not text. Policy <i>text</i> is a guardrail item of the install knowledge project, not part of this.
/// </summary>
public sealed record AgentPolicy(PromptingPolicy Prompting, PolicyLimits Limits, DataRule CloudReadsData)
{
    public static readonly AgentPolicy None = new(PromptingPolicy.None, PolicyLimits.None, DataRule.Allow);
}

public sealed class AgentPolicyResult(AgentPolicy? policy, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public AgentPolicy? Policy { get; } = policy;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Policy is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of the install policy, <c>agent-policy/1</c> (plan 27a AG09): prompting minimums per trait and settings for named tools, limits per scope, and the data rule for agents on a cloud provider. Codes: <c>DBD-AGT-010</c> a value or the shape,
/// <c>-011</c> the format number.
/// </summary>
public static class AgentPolicyLoader
{
    public const string BadPolicy = "DBD-AGT-010";
    public const string FormatNumber = "DBD-AGT-011";

    public const int Version = 1;

    public const string Kind = "agent-policy";

    public static readonly string[] Keys = ["agent-policy", "prompting", "limits", "data-rules"];

    public static readonly string[] PromptingKeys = ["minimums", "tools"];

    public static readonly string[] LimitKeys = ["conversation", "person", "install", "notify-at"];

    public static readonly string[] DataRuleKeys = ["cloud-providers"];

    public static readonly string[] CloudKeys = ["reads-data"];

    public static readonly string[] DataRuleNames = ["allow", "ask", "refuse"];

    public static string Schema()
    {
        using var stream = typeof(AgentPolicyLoader).Assembly.GetManifestResourceStream("DbDataKit.Agents.schemas.agent-policy.schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Refusal(string file, int format) => format > Version
        ? $"the {Kind} definition '{file}' is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
        : $"the {Kind} definition '{file}' is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";

    public static AgentPolicyResult Load(string yaml, string file)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml.TrimStart('﻿'), file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadPolicy, file, 1, 1, "The policy file is empty.", "Write `agent-policy: 1` and the settings."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var policy = new Reader(file, diags).Read(root);
        return new(diags.HasErrors() ? null : policy, diags);
    }

    private sealed class Reader(string fileName, List<ConfigDiagnostic> diags) : AgentFileReader(fileName, diags, BadPolicy)
    {
        public AgentPolicy? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(root, "A policy is a mapping with `agent-policy: 1`."); return null; }
            if (!CheckFormat(map)) return null;
            CheckKeys(map, Keys, "the install policy");
            return new AgentPolicy(PromptingPart(map), Limits(map), CloudRule(map));
        }

        private bool CheckFormat(YamlMapping map)
        {
            var node = map.Get("agent-policy");
            if (node is null) { Add(FormatNumber, map, $"`agent-policy` is missing: the file says which format of {Kind} it is in.", "Add `agent-policy: 1`."); return false; }
            if (node is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var format) || format < 1)
            { Add(FormatNumber, node, "`agent-policy` must be a positive whole number."); return false; }
            if (format == Version) return true;
            Add(FormatNumber, node, Refusal(File, format), format > Version ? $"Upgrade the tool, or write the file in format {Version}." : null);
            return false;
        }

        private Prompting? Setting(YamlNode node)
        {
            if (node is YamlScalar s && Array.IndexOf(AgentDefinitionLoader.PromptingNames, s.Value) is var i and >= 0) return (Prompting)i;
            Bad(node, $"`{(node as YamlScalar)?.Value}` is not a prompting setting.", $"Use one of: {string.Join(", ", AgentDefinitionLoader.PromptingNames)}.");
            return null;
        }

        private PromptingPolicy PromptingPart(YamlMapping map)
        {
            var minimums = new Dictionary<Traits, Prompting>();
            var tools = new Dictionary<string, Prompting>(StringComparer.Ordinal);
            if (map.Get("prompting") is not { } node) return PromptingPolicy.None;
            if (node is not YamlMapping m) { Bad(node, "`prompting` is a mapping: `minimums` (per trait) and `tools` (per tool)."); return PromptingPolicy.None; }
            CheckKeys(m, PromptingKeys, "`prompting`");
            if (m.Get("minimums") is YamlMapping mins)
                foreach (var e in mins.Entries)
                {
                    var trait = TraitTable.Parse(e.Key.Value);
                    if (trait is null)
                    {
                        var near = DidYouMean.Closest(e.Key.Value, Enum.GetValues<Traits>().Where(t => t != Traits.None).SelectMany(t => t.Names()));
                        Add(SharedCodes.UnknownKey, e.Key, $"`{e.Key.Value}` is not a trait." + (near is not null ? $" Did you mean `{near}`?" : ""), $"Traits: {string.Join(", ", Enum.GetValues<Traits>().Where(t => t != Traits.None).SelectMany(t => t.Names()))}.");
                    }
                    else if (Setting(e.Value) is { } setting) minimums[trait.Value] = setting;
                }
            else if (m.Get("minimums") is { } bad) Bad(bad, "`minimums` is a mapping of a trait to a setting: `writes-data: ask`.");
            if (m.Get("tools") is YamlMapping named)
            {
                foreach (var e in named.Entries)
                    if (Setting(e.Value) is { } setting) tools[e.Key.Value] = setting;
            }
            else if (m.Get("tools") is { } bad2) Bad(bad2, "`tools` is a mapping of a tool name to a setting: `connection_deploy: ask`.");
            return new PromptingPolicy(minimums, tools);
        }

        private PolicyLimits Limits(YamlMapping map)
        {
            if (map.Get("limits") is not { } node) return PolicyLimits.None;
            if (node is not YamlMapping m) { Bad(node, "`limits` is a mapping: `conversation`, `person`, `install` and `notify-at`."); return PolicyLimits.None; }
            CheckKeys(m, LimitKeys, "`limits`");
            LimitSet Scope(string key)
            {
                if (m.Get(key) is not { } n) return LimitSet.None;
                if (n is not YamlMapping sm) { Bad(n, $"`{key}` is a mapping: `{{ turns: 100, cost: 20.00 }}`."); return LimitSet.None; }
                CheckKeys(sm, AgentDefinitionLoader.LimitKeys, $"`{key}`");
                return ReadLimits(sm);
            }
            var notify = 80;
            if (m.Get("notify-at") is { } at)
            {
                if (at is YamlScalar s && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 99) notify = v;
                else Bad(at, "`notify-at` is a percentage from 1 to 99: the share of a limit at which administrators are told.");
            }
            return new PolicyLimits(Scope("conversation"), Scope("person"), Scope("install"), notify);
        }

        private DataRule CloudRule(YamlMapping map)
        {
            if (map.Get("data-rules") is not { } node) return DataRule.Allow;
            if (node is not YamlMapping m) { Bad(node, "`data-rules` is a mapping: `cloud-providers: { reads-data: refuse }`."); return DataRule.Allow; }
            CheckKeys(m, DataRuleKeys, "`data-rules`");
            if (m.Get("cloud-providers") is not { } cloud) return DataRule.Allow;
            if (cloud is not YamlMapping c) { Bad(cloud, "`cloud-providers` is a mapping: `{ reads-data: refuse }`."); return DataRule.Allow; }
            CheckKeys(c, CloudKeys, "`cloud-providers`");
            if (c.Get("reads-data") is not { } rule) return DataRule.Allow;
            if (rule is YamlScalar s && Array.IndexOf(DataRuleNames, s.Value) is var i and >= 0) return (DataRule)i;
            Bad(rule, $"`{(rule as YamlScalar)?.Value}` is not a data rule.", $"Use one of: {string.Join(", ", DataRuleNames)}.");
            return DataRule.Allow;
        }
    }
}
