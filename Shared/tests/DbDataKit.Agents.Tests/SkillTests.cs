using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG12: skills.</summary>
public sealed class SkillTests
{
    private static readonly AgentRunContext Context = new("alice", "build-helper", "3");

    private static string Words(int n, string word = "word") => string.Join(' ', Enumerable.Repeat(word, n));

    private static ChatMessage Turn(string text) => new(ChatRole.User, text);


    private static string RepositoryFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DbDataKit.slnx"))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException("The repository root (DbDataKit.slnx) is above the test's folder.");
    }

    private const string Small = "---\nname: widget-care\ndescription: Look after widgets when a task mentions them\n---\n# Widgets\n\nOil the hinge before painting. Never paint a hinge.\n";

    private static SkillCatalog SmallCatalog() => new([SkillReader.Read(Small, ".claude/skills/widget-care/SKILL.md").Skill!]);

    [Fact]
    public void Builds_agent_kit_is_a_skill_with_a_name_and_a_description()
    {
        var text = File.ReadAllText(RepositoryFile("DbDataBuild/src/DbDataBuild.Cli/AgentKit/SKILL.md"));

        var (skill, problem) = SkillReader.Read(text, "AgentKit/SKILL.md");

        Assert.Null(problem);
        Assert.Equal("dbdatabuild", skill!.Name);
        Assert.StartsWith("Work in a dbdatabuild project", skill.Description);
        Assert.Contains("Rules that are never negotiable", skill.Body);
        Assert.DoesNotContain("name: dbdatabuild", skill.Body);                                            // the header is not the body
        Assert.Equal(12, skill.Version.Length);
        var export = SkillReader.Export(skill, text);
        Assert.Equal([".claude/skills/dbdatabuild/SKILL.md"], export.Keys);                                 // the outside agents' own place
        Assert.Equal(text, export[".claude/skills/dbdatabuild/SKILL.md"]);
    }

    [Theory]
    [InlineData("no header", "starts with no header")]
    [InlineData("---\nname: a\n", "never closes")]
    [InlineData("---\ndescription: d\n---\nbody", "no `name`")]
    [InlineData("---\nname: a\n---\nbody", "no `description`")]
    public void A_skill_without_a_proper_header_is_refused_naming_the_file(string text, string words)
    {
        var (skill, problem) = SkillReader.Read(text, "skills/x/SKILL.md");

        Assert.Null(skill);
        Assert.Contains("skills/x/SKILL.md", problem);
        Assert.Contains(words, problem);
    }

    [Fact]
    public async Task The_agent_sees_names_and_descriptions_and_loads_the_body_only_when_the_task_needs_it()
    {
        var catalog = SmallCatalog();
        var recorder = new InMemoryRecorder();
        var turns = new TurnContext(_ => new ContextInputs([], [], DbDataKit.Knowledge.BoundKnowledge.Empty, [], [], catalog.Offers), Context, recorder);

        // a task that does not need the skill
        var plain = new ScriptedChatClient(ScriptStep.Say("Four."));
        await new AgentRunner(plain, [catalog.Tool(Context, recorder)]).RunAsync([Turn("what is two plus two")], Context, new AgentRunOptions { InstructionsForTurn = turns.ForTurn });
        var system = plain.Requests[0][0].Text;
        Assert.Contains("- widget-care: Look after widgets when a task mentions them", system);
        Assert.DoesNotContain("Oil the hinge", system);                                                    // the body is not given until it is loaded
        Assert.DoesNotContain(recorder.Events, e => e.Kind == AgentEventKinds.SkillLoaded);

        // a task that does
        var needing = new ScriptedChatClient(ScriptStep.Call("skill_load", new { name = "widget-care" }), ScriptStep.Say("Oiled."));
        await new AgentRunner(needing, [catalog.Tool(Context, recorder)]).RunAsync([Turn("fix the widget hinge")], Context, new AgentRunOptions { InstructionsForTurn = turns.ForTurn, Recorder = recorder });
        var loaded = Assert.Single(Transcript.ResultTexts(needing.Requests[1]));
        Assert.StartsWith("<skill name=\"widget-care\" version=\"", loaded);                               // given as the agent's instructions, not as data from outside
        Assert.Contains("Oil the hinge before painting", loaded);
        Assert.DoesNotContain("trust=\"data\"", loaded);
        var recorded = Assert.Single(recorder.Events, e => e.Kind == AgentEventKinds.SkillLoaded);
        Assert.Equal($"widget-care@{catalog.Find("widget-care")!.Version}", recorded.Detail);
    }

    [Fact]
    public async Task An_unknown_skill_says_which_skills_there_are()
    {
        var client = new ScriptedChatClient(ScriptStep.Call("skill_load", new { name = "nope" }), ScriptStep.Say("ok"));

        await new AgentRunner(client, [SmallCatalog().Tool(Context)]).RunAsync([Turn("x")], Context);

        Assert.Contains("There is no skill named 'nope'. The skills are: widget-care.", Assert.Single(Transcript.ResultTexts(client.Requests[1])));
    }

    [Fact]
    public void A_skill_whose_body_does_not_fit_the_room_left_is_refused_at_publish_naming_it()
    {
        var big = $"---\nname: big-skill\ndescription: A very long skill\n---\n{Words(500)}";
        var catalog = new SkillCatalog([SkillReader.Read(Small, "a").Skill!, SkillReader.Read(big, "skills/big/SKILL.md").Skill!]);

        // window 1000, always given 300, reserve 100: 600 words are left; the big body is 500 words
        Assert.Null(catalog.CheckRoom(1000, 300, 100, new WordCounter()));

        var refusal = catalog.CheckRoom(1000, 450, 100, new WordCounter());                              // 450 words left: the big body no longer fits
        Assert.NotNull(refusal);
        Assert.Contains("'big-skill'", refusal);
        Assert.Contains("500 tokens", refusal);
        Assert.Contains("only 450 tokens are left", refusal);
    }
}
