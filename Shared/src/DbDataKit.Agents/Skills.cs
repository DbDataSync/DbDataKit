using System.Text.Json.Nodes;
using DbDataKit.Cli;

namespace DbDataKit.Agents;

/// <summary>A skill (plan 27a AG12, plan 27 section 3.13): a <c>SKILL.md</c> with a name and a description in its header and instructions in its body.</summary>
public sealed record Skill(string Name, string Description, string Body, string Version, string File)
{
    public SkillOffer Offer => new(Name, Description, Version);
}

/// <summary>
/// Reads a skill from the text of its <c>SKILL.md</c>, the convention outside agents read too (a front matter block with <c>name</c> and <c>description</c>), as Build's agent kit writes it. A file that does not have both is refused with a sentence that names the file.
/// </summary>
public static class SkillReader
{
    public static (Skill? Skill, string? Problem) Read(string text, string file)
    {
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0].TrimEnd() != "---") return (null, $"The skill '{file}' starts with no header: a SKILL.md begins with `---`, a `name` and a `description`, and `---`.");
        var end = Array.FindIndex(lines, 1, l => l.TrimEnd() == "---");
        if (end < 0) return (null, $"The skill '{file}' has a header that never closes with `---`.");
        string? name = null, description = null;
        for (var i = 1; i < end; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            var key = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim().Trim('"', '\'');
            if (key == "name") name = value;
            else if (key == "description") description = value;
        }
        if (string.IsNullOrWhiteSpace(name)) return (null, $"The skill '{file}' has no `name` in its header.");
        if (string.IsNullOrWhiteSpace(description)) return (null, $"The skill '{file}' has no `description` in its header: the agent chooses a skill by it.");
        var body = string.Join('\n', lines[(end + 1)..]).Trim('\n');
        return (new Skill(name, description, body, DbDataKit.Knowledge.KnowledgeItemLoader.HashOf(text)[..12], file), null);
    }

    /// <summary>A published skill as the files an outside agent reads, the way <c>dbdatabuild project agent-kit</c> writes Build's: <c>.claude/skills/&lt;name&gt;/SKILL.md</c>.</summary>
    public static IReadOnlyDictionary<string, string> Export(Skill skill, string originalText) =>
        new Dictionary<string, string> { [$".claude/skills/{skill.Name}/SKILL.md"] = originalText };
}

/// <summary>The skills an agent may load, by name. <see cref="Tool"/> is the tool that loads one, <see cref="Offers"/> what the agent sees every turn.</summary>
public sealed class SkillCatalog(IEnumerable<Skill> skills)
{
    private readonly Dictionary<string, Skill> _skills = skills.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<Skill> Skills => _skills.Values;

    public IReadOnlyList<SkillOffer> Offers => [.. _skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => s.Offer)];

    public Skill? Find(string name) => _skills.GetValueOrDefault(name);

    /// <summary>The tool <c>skill_load</c>: the body of the skill named, as the agent's own instructions, recorded with its version. An unknown name says which skills there are.</summary>
    public AgentTool Tool(AgentRunContext context, IAgentRecorder? recorder = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("name"),
            ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The name of the skill, as offered." } },
        };
        return new AgentTool("skill_load", "Load a skill's instructions when the task needs it. The skills you can load are offered by name and description.", Traits.ReadsDefinitions, schema, "skill", (arguments, _) =>
        {
            var name = (string?)arguments["name"] ?? "";
            if (!_skills.TryGetValue(name, out var skill))
                return Task.FromResult(new ToolOutcome($"There is no skill named '{name}'. The skills are: {string.Join(", ", _skills.Keys.Order())}.", true));
            recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, AgentEventKinds.SkillLoaded, context, 0, "skill_load", $"{skill.Name}@{skill.Version}"));
            return Task.FromResult(new ToolOutcome(skill.Body, false, skill.Name, skill.Version));
        })
        { ResultKind = ToolResultKind.Skill };
    }

    /// <summary>
    /// The check at publish (plan 27a AG12): the descriptions count against the always-given budget, and the largest body must fit in the room that is left, so loading any one skill never forces an instruction out. Returns a refusal naming the skill, or null.
    /// </summary>
    public string? CheckRoom(int windowTokens, int alwaysGivenTokens, int answerReserve, ITokenCounter counter)
    {
        var room = windowTokens - alwaysGivenTokens - answerReserve;
        foreach (var skill in _skills.Values.OrderByDescending(s => counter.Count(s.Body)))
        {
            var size = counter.Count(skill.Body);
            if (size > room)
                return $"The skill '{skill.Name}' is {size} tokens (counted with {counter.Basis}) and only {Math.Max(room, 0)} tokens are left in the model's {windowTokens}-token window once the always-given text and the room for an answer are counted: loading it would push instructions out. Shorten it or split it into two skills.";
        }
        return null;
    }
}
