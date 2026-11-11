using System.Globalization;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Agents;

/// <summary>
/// The kinds <c>agent</c>, <c>skill</c> and <c>agent-policy</c> in plan 30's registry (plan 27a AG13, plan 27 section 3): what a publish checks, the slots a version needs bound in a channel (an agent's <b>model slot</b>, so a promotion with the slot unbound is refused naming
/// it), the target types that accept them, and what each depends on. One version per publish; the instructions an agent names and the files of a skill are artifacts of the version, kept by hash; publishing the same content again changes nothing (the publisher's rule).
/// <list type="bullet">
/// <item><b>agent</b>: the manifest is <c>agent.yml</c> (<see cref="AgentDefinitionLoader"/>, strict, file and line). Every instruction file it names is among the version's files. Depends on the skills and subagents it names (<c>skill:name</c>, <c>agent:name</c>). Slot: <c>model:&lt;slot&gt;</c>.</item>
/// <item><b>skill</b>: the manifest is <c>SKILL.md</c> (<see cref="SkillReader"/>): a name, a description and a body. No slots.</item>
/// <item><b>agent-policy</b>: the manifest is <c>agent-policy.yml</c> (<see cref="AgentPolicyLoader"/>). No slots, no dependencies.</item>
/// </list>
/// All three are read, not run: they serve at once on a serving target and a runner target accepts agents and skills (an unattended agent is a dependency of a package, plan 27a AG27); none is trusted code.
/// </summary>
public static class AgentDefinitions
{
    public const string Agent = "agent";
    public const string Skill = "skill";
    public const string Policy = "agent-policy";

    public static IReadOnlyList<string> Kinds { get; } = [Agent, Skill, Policy];

    /// <summary>The name of the model slot's binding in a channel, as the registry lists the slot: <c>model:default</c>.</summary>
    public static string SlotName(string slot) => "model:" + slot;

    public static DefinitionKinds Register(DefinitionKinds kinds)
    {
        kinds.Register(new DefinitionKind(Agent, ["serving", "runner"], PublishMode.Serve)
        {
            Formats = new DefinitionFormats(AgentDefinitionLoader.Version, AgentDefinitionLoader.Version),
            FormatOf = text => FormatNumber(text, "agent"),
            Validator = ValidateAgent,
            SlotsOf = ModelSlots,
        });
        kinds.Register(new DefinitionKind(Skill, ["serving", "runner"], PublishMode.Serve) { Validator = ValidateSkill });
        kinds.Register(new DefinitionKind(Policy, ["serving"], PublishMode.Serve)
        {
            Formats = new DefinitionFormats(AgentPolicyLoader.Version, AgentPolicyLoader.Version),
            FormatOf = text => FormatNumber(text, "agent-policy"),
            Validator = c => Say(AgentPolicyLoader.Load(c.Manifest, "agent-policy.yml").Diagnostics),
        });
        return kinds;
    }

    /// <summary>A registry of the three kinds alone.</summary>
    public static DefinitionKinds Kind() => Register(new DefinitionKinds());

    /// <summary>The <c>agent: N</c> or <c>agent-policy: N</c> a manifest says, or null when it does not say.</summary>
    public static int? FormatNumber(string manifest, string key)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        return StrictYamlReader.Read(manifest.TrimStart('﻿'), "manifest.yml", diagnostics) is YamlMapping map && map.Get(key) is YamlScalar { Quoted: false } s
            && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : null;
    }

    /// <summary>The slots an agent needs bound in a channel: the model slot it names.</summary>
    public static IReadOnlyList<DefinitionSlot> ModelSlots(string manifest) =>
        AgentDefinitionLoader.Load(manifest, "agent.yml").Definition is { } d ? [new DefinitionSlot(SlotName(d.ModelSlot), $"The model the agent's slot `{d.ModelSlot}` is bound to in this channel: a model-provider connection and a model.")] : [];

    /// <summary>The <c>depends_on</c> of an agent: <c>skill:name</c> for each skill and <c>agent:name</c> for each subagent it names.</summary>
    public static IReadOnlyList<string> DependsOn(string manifest) =>
        AgentDefinitionLoader.Load(manifest, "agent.yml").Definition is { } d ? [.. d.Skills.Select(s => $"{Skill}:{s}"), .. d.Subagents.Select(s => $"{Agent}:{s}")] : [];

    private static string? ValidateAgent(DefinitionCandidate candidate)
    {
        var loaded = AgentDefinitionLoader.Load(candidate.Manifest, $"agents/{candidate.Name}/agent.yml");
        if (loaded.Definition is not { } definition) return Say(loaded.Diagnostics);
        var problems = new List<string>();
        if (!string.Equals(definition.Name, candidate.Name, StringComparison.Ordinal)) problems.Add($"The agent says its name is `{definition.Name}` and it is published as `{candidate.Name}`.");
        foreach (var file in definition.Instructions)
            if (candidate.Artifacts.All(a => !string.Equals(a.Path, file, StringComparison.Ordinal)))
                problems.Add($"The instruction file `{file}` is not among the files of this version. Publish it with the agent: it is kept by its hash.");
        foreach (var skill in definition.Skills)
            if (!candidate.DependsOn.Contains($"{Skill}:{skill}", StringComparer.Ordinal) && !candidate.DependsOn.Any(d => d.StartsWith($"{Skill}:{skill}@", StringComparison.Ordinal)))
                problems.Add($"The agent names the skill `{skill}` and does not depend on it. Publish it with depends_on `{Skill}:{skill}`.");
        foreach (var sub in definition.Subagents)
            if (!candidate.DependsOn.Contains($"{Agent}:{sub}", StringComparer.Ordinal) && !candidate.DependsOn.Any(d => d.StartsWith($"{Agent}:{sub}@", StringComparison.Ordinal)))
                problems.Add($"The agent names the subagent `{sub}` and does not depend on it. Publish it with depends_on `{Agent}:{sub}`.");
        return problems.Count == 0 ? null : string.Join(" | ", problems);
    }

    private static string? ValidateSkill(DefinitionCandidate candidate)
    {
        var (skill, problem) = SkillReader.Read(candidate.Manifest, $"skills/{candidate.Name}/SKILL.md");
        if (skill is null) return problem;
        return string.Equals(skill.Name, candidate.Name, StringComparison.Ordinal) ? null : $"The skill says its name is `{skill.Name}` and it is published as `{candidate.Name}`.";
    }

    private static string? Say(IEnumerable<ConfigDiagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == ConfigSeverity.Error).ToList();
        return errors.Count == 0 ? null : string.Join(" | ", errors.Select(d => d.ToString()));
    }
}
