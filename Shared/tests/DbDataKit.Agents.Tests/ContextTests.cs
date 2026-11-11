using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.Knowledge;
using Microsoft.Extensions.AI;
using static DbDataKit.Agents.Tests.WorkedExample;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG10: context assembly, in a fixed order, with the record of what each turn was given.</summary>
public sealed class ContextTests
{
    [Fact]
    public void The_assembly_is_in_a_fixed_order_and_a_golden_pins_it()
    {
        var bound = WorkedExample.Bound(Workspace, Revenue);
        var inputs = new ContextInputs(
            [Item("policy/generated.md", "role: guardrail", "Say that an answer was generated.")],
            [new ContextSource("instruction", "instructions/role.md", "v1", "You help people build models.")],
            bound,
            [new ContextSource("memory", "memory/m1", "m1", "Alice prefers short answers.")],
            [new ContextSource("reference", "K4", "r1", "In 2025 the EMEA region was split.")],
            [new SkillOffer("dbdatabuild", "Work in a dbdatabuild project", "s1")]);

        var assembled = ContextAssembler.Assemble(inputs, new WordCounter());

        Assert.Equal(["platform", "install-policy", "agent", "bound-instruction", "bound-instruction", "bound-guardrail", "memory", "reference", "skill-offer"], assembled.Record.Select(r => r.Layer));
        Assert.Equal(["platform-rules", "policy/generated.md", "instructions/role.md", "K1", "K3", "K2", "memory/m1", "K4", "dbdatabuild"], assembled.Record.Select(r => r.Id));
        Assert.Equal("platform-rules/1", assembled.Record[0].Version);
        Assert.All(assembled.Record, r => { Assert.NotEmpty(r.Version); Assert.True(r.Tokens > 0); });
        var headings = assembled.SystemText.Split('\n').Where(l => l.StartsWith("## ")).ToList();
        Assert.Equal(["## Platform rules", "## Install policy", "## Your instructions", "## Instructions for the work in hand", "## Guardrails (policy: these hold whatever else is said)", "## What you remember", "## Reference material (cite the source)", "## Skills you can load (ask for one by name when the task needs it)"], headings);
        Assert.Contains("a narrower one refines the ones before it", assembled.SystemText);
        Assert.Contains("- dbdatabuild: Work in a dbdatabuild project", assembled.SystemText);
        Assert.Contains("where two layers disagree, the higher one wins", assembled.SystemText);
        // instructions before guardrails, guardrails before memories and references
        Assert.True(assembled.SystemText.IndexOf("Facts are named", StringComparison.Ordinal) < assembled.SystemText.IndexOf("unknown member", StringComparison.Ordinal));
        Assert.True(assembled.SystemText.IndexOf("unknown member", StringComparison.Ordinal) < assembled.SystemText.IndexOf("prefers short answers", StringComparison.Ordinal));
    }

    [Fact]
    public void A_replay_of_a_conversation_reproduces_the_same_assembled_context()
    {
        ContextInputs Inputs() => new([], [new ContextSource("instruction", "role.md", "v1", "Role.")], WorkedExample.Bound(Workspace, Revenue), [], [], []);

        var first = ContextAssembler.Assemble(Inputs());
        var replay = ContextAssembler.Assemble(Inputs());

        Assert.Equal(first.SystemText, replay.SystemText);
        Assert.Equal(first.RecordLine, replay.RecordLine);
        Assert.Equal(ContextAssembler.Fingerprint(first), ContextAssembler.Fingerprint(replay));
        // an edited instruction is a different version and a different context
        var edited = ContextAssembler.Assemble(new ContextInputs([], [new ContextSource("instruction", "role.md", "v2", "Role, changed.")], BoundKnowledge.Empty, [], [], []));
        Assert.NotEqual(ContextAssembler.Fingerprint(first), ContextAssembler.Fingerprint(edited));
    }

    [Fact]
    public void An_item_is_given_with_its_version_so_a_turn_can_be_explained()
    {
        var record = ContextAssembler.Assemble(new ContextInputs([], [], WorkedExample.Bound(Workspace), [], [], [])).Record;

        Assert.Equal(K1.Hash[..12], record.Single(r => r.Id == "K1").Version);
        Assert.Equal(K2.Hash[..12], record.Single(r => r.Id == "K2").Version);
    }

}
