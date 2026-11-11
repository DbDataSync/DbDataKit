using System.Text;
using DbDataKit.Agents;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Agents.Tests;

/// <summary>
/// Plan 27a AG13: the kinds <c>agent</c>, <c>skill</c> and <c>agent-policy</c> in plan 30's registry. The life cycle (publish, stage, activate, roll back), the refusal of a promotion with the model slot unbound (naming it) and the rule that the same content again changes nothing
/// run on the state database of every engine (SQLite, and SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> / <c>DBDATAKIT_TEST_POSTGRES</c> name a server).
/// </summary>
public sealed class AgentPublishingTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-11T12:00:00Z");

    private const string Slot = "model:default";

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private static string Agent(string instruction = "instructions/role.md", string extra = "") =>
        $"agent: 1\nname: helper\naudience: back-end\nmodel: {{ slot: default }}\ninstructions: [{instruction}]\nskills: [dbdatabuild]\n{extra}";

    private const string SkillText = "---\nname: dbdatabuild\ndescription: How to work with a Build project.\n---\nRun `project compile` before you deploy.\n";

    private const string PolicyText = "agent-policy: 1\nprompting:\n  minimums:\n    writes-data: ask\n";

    private sealed record Setup(DefinitionPublisher Publisher, PayloadStore Payloads);

    private Setup Open(string engine)
    {
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"), time: _clock);
        var payloads = new PayloadStore(database);
        return new Setup(new DefinitionPublisher(database, AgentDefinitions.Kind(), payloads, "serving"), payloads);
    }

    private static PublishRequest Request(string kind, string name, string text, string channel = "prod", PublishMode mode = PublishMode.Serve, string[]? depends = null, ArtifactRef[]? files = null) =>
        new(kind, name, text, "ada", channel, mode, depends, Artifacts: files);

    private static ArtifactRef Role(Setup s, string text = "You help with Build projects. Be brief.") =>
        new PublishArtifacts(s.Payloads, s.Publisher, "ada").Add("instructions/role.md", Encoding.UTF8.GetBytes(text), "text/markdown");

    // ---- no database ----

    [Fact]
    public void The_registry_has_the_three_kinds_with_the_target_types_and_the_agents_model_slot()
    {
        var kinds = AgentDefinitions.Kind();
        Assert.Equal(["agent", "agent-policy", "skill"], kinds.All.Select(k => k.Name));
        Assert.Equal(["serving", "runner"], kinds.Require("agent").TargetTypes);
        Assert.Equal(["serving"], kinds.Require("agent-policy").TargetTypes);
        Assert.All(kinds.All, k => Assert.Equal(PublishMode.Serve, k.DefaultMode));
        Assert.All(kinds.All, k => Assert.False(k.RequiresApproval));
        Assert.Equal([Slot], kinds.Require("agent").SlotsFor(Agent()).Select(s => s.Name));
        Assert.Empty(kinds.Require("skill").SlotsFor(SkillText));
        Assert.Equal(["skill:dbdatabuild", "agent:reader"], AgentDefinitions.DependsOn(Agent(extra: "subagents: [reader]\n")));
        Assert.Equal(1, kinds.Require("agent").FormatOf!(Agent()));
        Assert.Equal(1, kinds.Require("agent-policy").FormatOf!(PolicyText));
    }

    [Fact]
    public void The_validators_refuse_a_damaged_file_a_missing_instruction_a_name_that_differs_and_a_dependency_not_named()
    {
        var kinds = AgentDefinitions.Kind();
        string? Check(string kind, string name, string text, string[]? depends = null, params ArtifactRef[] files) =>
            kinds.Require(kind).Validator!(new DefinitionCandidate(kind, name, text, files, depends ?? [], 1));
        var role = new ArtifactRef("instructions/role.md", new string('a', 64), 10, "text/markdown");

        Assert.Null(Check("agent", "helper", Agent(), ["skill:dbdatabuild"], role));
        Assert.Contains("agent.yml:2", Check("agent", "helper", "agent: 1\nname: Bad Name\naudience: back-end\nmodel: { slot: d }")!);
        Assert.Contains("`instructions/role.md` is not among the files", Check("agent", "helper", Agent(), ["skill:dbdatabuild"])!);
        Assert.Contains("published as `other`", Check("agent", "other", Agent(), ["skill:dbdatabuild"], role)!);
        Assert.Contains("does not depend on it", Check("agent", "helper", Agent(), [], role)!);
        Assert.Contains("subagent `reader`", Check("agent", "helper", Agent(extra: "subagents: [reader]\n"), ["skill:dbdatabuild"], role)!);
        Assert.Null(Check("skill", "dbdatabuild", SkillText));
        Assert.Contains("starts with no header", Check("skill", "dbdatabuild", "just text")!);
        Assert.Contains("published as `other`", Check("skill", "other", SkillText)!);
        Assert.Null(Check("agent-policy", "install", PolicyText));
        Assert.Contains("not a trait", Check("agent-policy", "install", "agent-policy: 1\nprompting:\n  minimums:\n    writes-stuff: ask")!);
    }

    // ---- on every engine: the life cycle ----

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_three_kinds_are_published_staged_activated_and_rolled_back_and_the_same_content_again_changes_nothing(string engine)
    {
        var s = Open(engine);
        var skill = s.Publisher.Publish(Request("skill", "dbdatabuild", SkillText));
        var policy = s.Publisher.Publish(Request("agent-policy", "install", PolicyText));
        Assert.True(skill.Changed && policy.Changed);

        // the agent's model slot is bound in the channel first; the instructions are a file of the version, kept by hash
        s.Publisher.Bind("prod", "agent", "helper", Slot, "claude/claude-sonnet-4-5", "ada");
        var role = Role(s);
        var one = s.Publisher.Publish(Request("agent", "helper", Agent(), depends: ["skill:dbdatabuild"], files: [role]));
        Assert.True(one.Changed);
        Assert.Equal((1, 1), (one.Version.Version, one.Version.Format));
        Assert.Equal(["skill:dbdatabuild"], one.Version.DependsOn);
        Assert.Equal(1, s.Publisher.Head("agent", "helper")!.Active);
        Assert.Equal(role.Sha256, s.Publisher.Artifacts("agent", "helper", 1).Single().Sha256);

        // the same content again changes nothing: no new version, the same hash
        var again = s.Publisher.Publish(Request("agent", "helper", Agent(), depends: ["skill:dbdatabuild"], files: [role]));
        Assert.False(again.Changed);
        Assert.Equal(1, again.Version.Version);
        Assert.False(s.Publisher.Publish(Request("skill", "dbdatabuild", SkillText)).Changed);
        Assert.False(s.Publisher.Publish(Request("agent-policy", "install", PolicyText)).Changed);

        // a new version is staged, not served, until it is activated; rolling back restores the first
        var changed = Role(s, "You help with Build projects. Always say which project you used.");
        var two = s.Publisher.Publish(Request("agent", "helper", Agent(), mode: PublishMode.Stage, depends: ["skill:dbdatabuild"], files: [changed]));
        Assert.Equal(2, two.Version.Version);
        Assert.Equal((1, 2), (s.Publisher.Head("agent", "helper")!.Active, s.Publisher.Head("agent", "helper")!.Staged));
        s.Publisher.Activate("agent", "helper", "ada");
        Assert.Equal(2, s.Publisher.Head("agent", "helper")!.Active);
        Assert.Equal(1, s.Publisher.Rollback("agent", "helper", "ada").Version);
        Assert.Equal(1, s.Publisher.Head("agent", "helper")!.Active);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_promotion_with_the_model_slot_unbound_is_refused_naming_it_and_a_binding_lets_it_through(string engine)
    {
        var s = Open(engine);
        s.Publisher.Publish(Request("skill", "dbdatabuild", SkillText));
        var role = Role(s);

        var refused = Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Request("agent", "helper", Agent(), channel: "prod", depends: ["skill:dbdatabuild"], files: [role])));
        Assert.Equal(PublishRefusal.UnboundSlot, refused.Reason);
        Assert.Contains("'model:default'", refused.Message);
        Assert.Null(s.Publisher.Head("agent", "helper"));

        // another channel binds it differently: a test model there, the real one in prod
        s.Publisher.Publish(Request("skill", "dbdatabuild", SkillText, channel: "test"));
        s.Publisher.Publish(Request("skill", "dbdatabuild", SkillText));
        s.Publisher.Bind("test", "agent", "helper", Slot, "local/llama3.1:8b", "ada");
        Assert.True(s.Publisher.Publish(Request("agent", "helper", Agent(), channel: "test", depends: ["skill:dbdatabuild"], files: [role])).Changed);
        Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Request("agent", "helper", Agent(), channel: "prod", depends: ["skill:dbdatabuild"], files: [role])));
        s.Publisher.Bind("prod", "agent", "helper", Slot, "claude/claude-sonnet-4-5", "ada");
        Assert.True(s.Publisher.Publish(Request("agent", "helper", Agent(), channel: "prod", depends: ["skill:dbdatabuild"], files: [role])).Version.Version >= 1);
        Assert.Equal("local/llama3.1:8b", s.Publisher.Bindings("test", "agent", "helper").Single().BoundTo);
        Assert.Equal("claude/claude-sonnet-4-5", s.Publisher.Bindings("prod", "agent", "helper").Single().BoundTo);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_runner_target_takes_agents_and_skills_and_refuses_the_policy_and_a_newer_format_is_refused(string engine)
    {
        var s = Open(engine);
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "runner"), time: _clock);
        var runner = new DefinitionPublisher(database, AgentDefinitions.Kind(), new PayloadStore(database), "runner");
        Assert.True(runner.Publish(Request("skill", "dbdatabuild", SkillText, mode: PublishMode.Stage)).Changed);
        var refused = Assert.Throws<PublishRefusedException>(() => runner.Publish(Request("agent-policy", "install", PolicyText)));
        Assert.Equal(PublishRefusal.WrongTarget, refused.Reason);

        var newer = Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Request("agent-policy", "install", PolicyText.Replace("agent-policy: 1", "agent-policy: 2"))));
        Assert.Contains("format 2", newer.Message);
    }
}
